using System.Security.Cryptography;
using System.Text;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.System;

/// <summary>
/// DPAPI-encrypted API key storage.
/// Keys are stored as base64 DPAPI blobs in settings JSON.
/// </summary>
public sealed class ApiKeyManager
{
    private static ILogger Logger => Log.ForContext<ApiKeyManager>();

    // App-specific entropy makes it harder for other apps running as the same user to decrypt.
    // Internal rather than private so ApiKeyManagerTests can seed a legacy PADDED blob directly
    // and pin GetApiKey's trim-on-read — SetApiKey normalizes now, so that state can no longer
    // be produced through the public API. Same test-seam convention as _protect below.
    internal static readonly byte[] Entropy = "VoiceWink-DPAPI-2024"u8.ToArray();

    private readonly SettingsService _settings;

    // ENH-17: serializes every slot write and carries the write-generation map. Leaf-most lock —
    // KeyChanged is always raised outside it, because subscribers take _modelListLock and
    // _imageCapabilityLock and the "no reentrant key writes" rule must keep holding.
    private readonly object _writeOrderLock = new();
    private readonly Dictionary<string, int> _writeGenerations = new(StringComparer.Ordinal);

    // Test seam mirroring SettingsService's _readFile/_writeFile convention: DPAPI cannot be
    // made to throw deterministically across environments, so tests inject a throwing protect
    // function to pin the failure contract. Production default is the real DPAPI call.
    internal Func<byte[], byte[], DataProtectionScope, byte[]> _protect = ProtectedData.Protect;

    /// <summary>
    /// Raised AFTER a stored key actually changed — successful set, clear, or remove; never on an
    /// encryption failure (the stored key did not change, which is F28/F44's whole point). Carries
    /// the provider string AS THE CALLERS PASS IT, which in production is always LOWERCASE
    /// (every key-save site does <c>provider.ToString().ToLowerInvariant()</c>) — subscribers must
    /// parse case-insensitively or they silently never match (IMG-10b plan review). This is the ONE
    /// key-write funnel, so subscribing here covers every save and clear site at once;
    /// per-call-site notification is the shape that under-notifies the next site. Subscribers must
    /// not write keys reentrantly — the fail-soft raise isolates THROWS, not recursion.
    /// </summary>
    public event Action<string>? KeyChanged;

    /// <summary>
    /// Per-subscriber fail-soft raise (same pattern as
    /// <c>TranscriptionHistoryService.RaiseHistoryChanged</c>): the key mutation has already
    /// COMMITTED when this runs, so a throwing subscriber must neither flip
    /// <see cref="SetApiKey"/>'s return to false (the caller would roll back a key that WAS
    /// saved — the exact F28/F44 corruption, Codex diff review) nor escape clear/remove. The
    /// publisher cannot make a public event's subscribers non-throwing, so it isolates them.
    /// </summary>
    private void RaiseKeyChanged(string provider)
    {
        if (KeyChanged is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try { handler(provider); }
            catch (Exception ex)
            {
                // Type + HResult only, never the exception object — subscriber messages can
                // carry arbitrary content into Sentry breadcrumbs, and Warning events ride
                // there (the RaiseHistoryChanged rule, re-flagged by Codex here).
                Logger.Warning("KeyChanged subscriber threw for {Provider}: {ErrorType} (HResult=0x{HResult:X8})",
                    provider, ex.GetType().Name, ex.HResult);
            }
        }
    }

    public ApiKeyManager(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Store <paramref name="apiKey"/> DPAPI-encrypted, after normalizing and validating it.
    ///
    /// <para>Returns <see cref="ApiKeySaveResult.Saved"/> when a key was written,
    /// <see cref="ApiKeySaveResult.Cleared"/> when an empty or whitespace-only input cleared the
    /// stored key, <see cref="ApiKeySaveResult.InvalidFormat"/> when the value cannot form an
    /// HTTP header, and <see cref="ApiKeySaveResult.EncryptionFailed"/> when DPAPI threw. Use
    /// <see cref="ApiKeySaveResultExtensions.IsSuccess"/> rather than comparing to a single
    /// member — clearing is a success, and reading it as a failure would misreport a clear that
    /// worked.</para>
    ///
    /// <para>Both failure results leave the previously stored key untouched (F28/F44: this used
    /// to be a silent void, so callers proceeded as if the key were saved and the UI reported
    /// success on a key that evaporated at restart).</para>
    ///
    /// <para><b>ENH-15 — this is where malformed keys are stopped.</b> It is the ONE key-write
    /// funnel (see <c>KeyChanged</c> above), so validating here covers all seven save sites
    /// across Settings, Models and Onboarding at once; a per-site check is the shape that misses
    /// the next site. Validation runs BEFORE any encoding, so a rejected key never reaches a byte
    /// array and there is nothing extra to scrub.</para>
    /// </summary>
    public ApiKeySaveResult SetApiKey(string provider, string apiKey)
    {
        bool changed;
        ApiKeySaveResult result;
        lock (_writeOrderLock)
        {
            // ENH-17: an UNCLAIMED write still supersedes earlier claims — stated as the standing
            // INVARIANT rather than as a fact about particular callers, because the callers changed
            // inside this very PR (all five flows claim now, so no production code reaches here
            // except RemoveApiKey; Kimi caught the comment still naming ModelsPage). The invariant
            // is what matters and outlives them: any write through this door, from any future
            // caller or the test seam, must beat a claim made before it. Without it, a slow claimed
            // save could commit its older generation over a later unclaimed write — "first click
            // wins", the exact defect the slot ordering exists to prevent.
            AdvanceKeyWriteGenerationLocked(provider);
            changed = WriteApiKeyLocked(provider, apiKey, out result);
        }

        if (changed)
            RaiseKeyChanged(provider);
        return result;
    }

    /// <summary>
    /// The one write. Caller must hold <see cref="_writeOrderLock"/>. Returns whether the stored
    /// value actually changed — i.e. whether <c>KeyChanged</c> is owed, which the caller raises
    /// after releasing the lock.
    /// </summary>
    private bool WriteApiKeyLocked(string provider, string apiKey, out ApiKeySaveResult result)
    {
        // Normalize first: a trailing newline or an NBSP picked up from a web console is a
        // paste artifact, not a malformed key, and healing it silently is strictly better than
        // rejecting a key the user typed correctly.
        var normalized = Helpers.ApiKeyFormat.Normalize(apiKey);

        if (string.IsNullOrEmpty(normalized))
        {
            _settings.SetString(AppDefaults.ApiKey(provider), "");
            result = ApiKeySaveResult.Cleared;
            return true;
        }

        var verdict = Helpers.ApiKeyFormat.Validate(normalized);
        if (verdict != Helpers.ApiKeyFormatVerdict.Ok)
        {
            // Verdict only, never the value — a Warning here rides to Sentry as a breadcrumb,
            // and LogRedactionEnricher's shape-based scrubbing cannot match a prefix-less key.
            Logger.Warning("API key for {Provider} rejected: {Verdict}", provider, verdict);
            result = ApiKeySaveResult.InvalidFormat;
            return false;
        }

        // plainBytes cleared in finally — a DPAPI throw must not leave the plaintext
        // lingering in the heap any longer than the success path does.
        var plainBytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            var encrypted = _protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            var base64 = Convert.ToBase64String(encrypted);
            _settings.SetString(AppDefaults.ApiKey(provider), base64);
            Logger.Information("API key stored for provider: {Provider}", provider);
            result = ApiKeySaveResult.Saved;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to encrypt API key for {Provider}", provider);
            result = ApiKeySaveResult.EncryptionFailed;
            return false;
        }
        finally
        {
            global::System.Array.Clear(plainBytes);
        }
    }

    /// <summary>
    /// Claim the next write generation for a slot. Call SYNCHRONOUSLY at click, before the first
    /// await, and pass the result to <see cref="TryCommitApiKey"/>. The LAST claim wins.
    /// </summary>
    /// <remarks>
    /// <para><b>ENH-17. Ordering lives HERE, at the slot, because this is the level the slots
    /// actually share.</b> <c>AppDefaults.ApiKey(provider)</c> is one flat <c>apikey_{provider}</c>
    /// namespace, so the Enhancement page, the Models page and Onboarding all write the SAME slot
    /// for a given provider. A generation kept per SURFACE cannot see the others' claims: a slow
    /// enhancement save would pass its own check and commit over a Models-page save the user made
    /// afterwards — the FIRST click winning, which is worse than the accidental last-completer
    /// behaviour it replaced (Kimi, ENH-17 plan review). Per-surface maps are also just ENH-15's
    /// six scattered guards rebuilt one level up.</para>
    ///
    /// <para><b>Save AND clear both claim.</b> A clear owns the slot exactly as a save does; a
    /// clear that did not claim would be silently overwritten by an in-flight save's commit.</para>
    ///
    /// <para>The generation may only ever GATE — commit or skip. If it is ever used to choose what
    /// to write, it has become the rollback this change exists to delete.</para>
    /// </remarks>
    public int BeginKeyWrite(string provider)
    {
        lock (_writeOrderLock)
            return AdvanceKeyWriteGenerationLocked(provider);
    }

    /// <summary>Caller must hold <see cref="_writeOrderLock"/>.</summary>
    private int AdvanceKeyWriteGenerationLocked(string provider)
    {
        _writeGenerations.TryGetValue(provider, out var current);
        var next = current + 1;
        _writeGenerations[provider] = next;
        return next;
    }

    /// <summary>
    /// Is <paramref name="generation"/> still the latest claim for the slot? A READ-ONLY check,
    /// for the paths that write nothing but still repaint the row.
    /// </summary>
    /// <remarks>
    /// A rejected candidate is never written, so those paths have no <see cref="TryCommitApiKey"/>
    /// call to gate them — yet they still set the status to Invalid and reload the masked key. A
    /// superseded save reaching its 401 arm would otherwise repaint "Invalid" over the newer save's
    /// validated key: no data lost, but the user is told their good key is bad. Caught by
    /// <c>ApiKeySaveSupersessionTests</c> during the ENH-17 rewrite.
    /// </remarks>
    public bool IsCurrentKeyWrite(string provider, int generation)
    {
        lock (_writeOrderLock)
        {
            _writeGenerations.TryGetValue(provider, out var current);
            return generation == current;
        }
    }

    /// <summary>
    /// Write <paramref name="apiKey"/> only if <paramref name="generation"/> is still the latest
    /// claim for the slot. Returns <c>false</c> when the write was superseded — nothing was
    /// written and <paramref name="result"/> is meaningless in that case.
    /// </summary>
    /// <remarks>
    /// The check and the write are ONE critical section. ENH-15 relied on UI-thread affinity for
    /// this and its own comment admitted continuations have no synchronization context under test,
    /// so the atomicity was borrowed rather than held. <c>KeyChanged</c> is raised OUTSIDE the lock
    /// deliberately: subscribers take <c>_modelListLock</c> / <c>_imageCapabilityLock</c>, so this
    /// lock must stay leaf-most for the existing "no reentrant key writes" rule to hold.
    /// </remarks>
    public bool TryCommitApiKey(string provider, string apiKey, int generation, out ApiKeySaveResult result)
    {
        bool changed;
        lock (_writeOrderLock)
        {
            _writeGenerations.TryGetValue(provider, out var current);
            if (generation != current)
            {
                result = default;
                return false;
            }

            changed = WriteApiKeyLocked(provider, apiKey, out result);
        }

        if (changed)
            RaiseKeyChanged(provider);
        return true;
    }

    public string? GetApiKey(string provider)
    {
        var base64 = _settings.GetString(AppDefaults.ApiKey(provider), "");
        if (string.IsNullOrEmpty(base64))
            return null;

        try
        {
            var encrypted = Convert.FromBase64String(base64);
            var plainBytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var result = Encoding.UTF8.GetString(plainBytes);
            global::System.Array.Clear(plainBytes);
            // ENH-15: heal padding written by a build that predates SetApiKey's normalize.
            // Deliberately modest — this fixes padded-ASCII keys only, and a padded key largely
            // works on the wire anyway. It is NOT what makes a legacy malformed key surface
            // honestly; the pre-send guard in AIEnhancementService.BuildConfig is. Note the
            // consequence: the stored blob no longer equals the served value, so the next write
            // of that same key persists the trimmed form.
            return Helpers.ApiKeyFormat.Normalize(result);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to decrypt API key for {Provider}", provider);
            return null;
        }
    }

    public bool HasApiKey(string provider)
    {
        return !string.IsNullOrEmpty(GetApiKey(provider));
    }

    public void RemoveApiKey(string provider)
    {
        // Through the same lock + generation advance as every other write: the class comment
        // promises "serializes every slot write", and a removal is a write (Kimi ENH-17 review).
        SetApiKey(provider, "");
    }
}
