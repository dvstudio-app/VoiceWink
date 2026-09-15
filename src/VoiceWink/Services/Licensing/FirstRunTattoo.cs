using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace VoiceWink.Services.Licensing;

/// <summary>
/// LIC-11 (owner request 2026-08-17): a first-run timestamp that survives the two reset gestures an
/// AVERAGE user performs — deleting <c>%LOCALAPPDATA%\VoiceWink</c>, and uninstall + reinstall.
/// Before LIC-21 PR A that timestamp lived only in <c>settings.json</c>, so either gesture
/// restarted the try-without-a-key grace window; PR A wired the registry leg through
/// <see cref="TrialWindow"/>.
///
/// <para><b>The bar is deliberately not "unbeatable".</b> Owner, 2026-08-17: an IT-savvy user always
/// wins, and the GPL source build is the honest free path anyway. Anyone willing to clear a registry
/// key beats this by design; that is not a defect to fix.</para>
///
/// <para><b>WIRED since LIC-21 PR A (2026-09-06) — the registry leg only.</b> The orchestrator
/// (<c>read → merge → clock-guard → reseed → write</c>) is <see cref="TrialWindow"/>, reached through
/// the <see cref="ITrialStampStore"/> seam (<see cref="RegistryTrialStampStore"/> in production;
/// tests and the LS harness pass no store or an in-memory fake, so no test ever touches the
/// developer's real HKCU). This type keeps the PURE decisions and the store primitives. The two
/// wiring hazards the LIC-11 card recorded were answered like this:</para>
/// <list type="number">
/// <item><b>The "earliest-wins re-seed resurrects a deliberately cleared grace" fail-open is
/// dissolved by REMOVING the deliberate clears, not by tombstoning.</b> Since PR A the trial start
/// is device state that licensing never writes (the owner's VoiceInk rule, 2026-09-06): the grace
/// key left <c>LicenseStateStringKeys</c>, the activation persist block and
/// <c>ClearLocalLicenseState</c>, so there is nothing a re-seed could resurrect. Activate on day 2,
/// deactivate on day 3 ⇒ the remaining days. <see cref="ClearAll"/> remains for support recovery.</item>
/// <item><b>Erasure.</b> Settled by the owner (addendum A23, 2026-08-23): the tattoo survives an
/// uninstall AND a GDPR erasure, no disclosure sentence is added, and <see cref="ClearAll"/> is never
/// called from the erase path — the value is application state identifying nobody.</item>
/// </list>
///
/// <para><b>Storage.</b> ONE store primitive lives here — the registry value
/// (<c>HKCU\Software\VoiceWink\InstallEpoch</c>, WIRED). The settings key is the other leg and belongs
/// to <see cref="TrialWindow"/>, which reads and writes it beside this store. A roaming file store
/// (<c>%APPDATA%\VoiceWink\install.dat</c>) existed here as unwired insurance until 2026-09-07, when
/// the field check it waited for ran on the signed v1.78.370 (Hyper-V VM): owner UAT 180.12 —
/// uninstall through Windows Apps + reinstall from the same Setup.exe — RESUMED the trial at the same
/// remaining time. That row proves the WINDOW survived the reinstall (INS-1 keeps the data folder
/// across an uninstall, so the settings leg was still there); the proof for THIS store is 180.11,
/// the data-folder wipe, which resumed the trial from the registry alone. The roaming store was
/// retired rather than wired because it was never additive: under CurrentUser DPAPI a second store on
/// the same machine adds nothing, and a new <c>%APPDATA%\VoiceWink</c> root would collide with
/// <c>AppPaths</c>' rule that every app-owned data root be erasure- and export-covered.</para>
///
/// <para><b>Missing versus unavailable is the store's contract</b> (Codex plan round, PR A): a
/// registry read that finds no value, an undecryptable blob or a malformed one is <c>Missing</c> —
/// re-seeded silently, the LIC-11 polarity — while a registry that cannot be opened, or a write that
/// cannot protect or land, is <c>Unavailable</c>, which <see cref="TrialWindow"/> logs ONCE per
/// process and never retries (the "a dead DPAPI retries forever at Warning" trap). That is why the
/// store primitives below are <c>Try*</c> methods that log at Debug and report failure, rather than
/// the earlier fail-soft void methods that made the two cases indistinguishable.</para>
///
/// <para><b>DPAPI CurrentUser</b>, with entropy of its own (the repo's own pattern — a license blob and
/// an API-key blob are deliberately not interchangeable). Two consequences, both intended: the value is
/// not a human-readable date inviting an edit, and it is machine+user-bound, so a copied blob decrypts
/// nowhere else. <b>Undecryptable ALWAYS reads as "no tattoo", never as "expired"</b> — the reverse
/// polarity would lock a user out of a free tryout by reinstalling Windows or moving machines.</para>
/// </summary>
internal static class FirstRunTattoo
{
    private static ILogger Logger => Log.ForContext(typeof(FirstRunTattoo));

    /// <summary>Its own DPAPI context, so a tattoo blob and a license/API-key blob are never
    /// interchangeable (mirrors <c>LicenseService.LicenseKeyEntropy</c>).</summary>
    private static readonly byte[] Entropy = "VoiceWink-FirstRun-2026"u8.ToArray();

    /// <summary>Format version, carried INSIDE the protected payload (a decimal text field, not a
    /// literal byte - the outcome is what matters). A future format change fails soft to "no tattoo"
    /// rather than misparsing an old blob into a wrong date.</summary>
    private const byte FormatVersion = 1;

    internal const string RegistryKeyPath = @"Software\VoiceWink";
    internal const string RegistryValueName = "InstallEpoch";

    /// <summary>One store's answer. <c>null</c> = absent or unreadable — the two are deliberately
    /// indistinguishable to every caller, because both must produce the same "no tattoo" outcome.</summary>
    internal readonly record struct Reading(DateTimeOffset FirstRun, DateTimeOffset LastSeen);

    // Seam: DPAPI ONLY. The registry store calls Registry directly and is NOT seamed, so the pure
    // decisions below are unit-tested and the two store methods are not.
    // An earlier version of this comment claimed "every side effect is injectable", which was
    // false - exactly the kind of claim that makes a coverage gap invisible. Seam them if the
    // wiring needs their matrix covered.
    internal static Func<byte[], byte[], DataProtectionScope, byte[]> Protect { get; set; } = ProtectedData.Protect;
    internal static Func<byte[], byte[], DataProtectionScope, byte[]> Unprotect { get; set; } = ProtectedData.Unprotect;

    /// <summary>
    /// Merge every present store: the EARLIEST first-run wins, the LATEST last-seen wins, and the
    /// first-run is then clamped against <paramref name="now"/>.
    /// <para>Earliest-first-run is what makes wiping any single store useless. Latest-last-seen is the
    /// clock-rollback guard's input - see <see cref="EffectiveNow"/>.</para>
    /// <para><b>`now` is a parameter because the dangerous incoherence is only visible here.</b> The
    /// first version never saw <c>now</c> at all, and a self-review found that A FIRST-RUN IN THE
    /// FUTURE granted unbounded grace: the clock reads 2030 at first launch (deliberately, or a dead
    /// RTC before NTP corrects it), the stores seed 2030, the clock is fixed to 2026 - and
    /// <see cref="EffectiveNow"/> then pins at 2030, so elapsed stays 0 and the window never opens.
    /// Today a folder wipe clears that; once wired, nothing an ordinary user or a support agent could
    /// do would. A first run cannot be in the future, so it clamps DOWN to <c>now</c>.</para>
    /// <para><b>That clamp is safe in the direction that matters</b> - earliest-wins means an
    /// attacker cannot future-stamp EVERY store without a full wipe (already the accepted IT-savvy
    /// defeat), and clamping only ever inflates elapsed, i.e. fail-CLOSED.</para>
    /// <para><b>A far-future LAST-SEEN is deliberately NOT filtered, and this is the second thing
    /// that must not be "improved".</b> A version of this method discarded a last-seen more than one
    /// window ahead, reasoning that the app cannot have been seen that far in the future so it must
    /// be a clock fault. BOTH diff reviewers independently showed that defeats the very guard it sits
    /// in: the fence is expressed against <c>now</c>, and <c>now</c> is exactly what an attacker
    /// moves. Wind the clock back further than one window and a legitimately-recorded last-seen is
    /// discarded as "implausible", <see cref="EffectiveNow"/> falls back to the clamped first-run,
    /// and an EXPIRED grace reopens - a fail-open licensing gate reachable by a gesture strictly
    /// easier than the folder wipe this card exists to close, and repeatable daily. There is no
    /// right place for a <c>now</c>-relative fence here.</para>
    /// <para>So the forward-clock-fault cost stands as the ACCEPTED residual it always was, and the
    /// trade is the right way round: a rare, accidental multi-day forward RTC fault costs a user
    /// their tryout (fail-CLOSED; the support recovery clears BOTH legs with the app closed —
    /// <see cref="ClearAll"/> for the registry store plus the two settings keys, because
    /// <see cref="ClearAll"/> alone leaves the settings leg, which re-seeds the durable store at the
    /// next launch), where the fence would have cost a licence to anyone willing to change their
    /// clock.</para>
    /// <para>Returns null when no store yields a reading, which is the ordinary first-ever-launch case
    /// AND the undecryptable case. Callers must treat them identically.</para>
    /// <para><b>The <c>now</c> clamp is a READ-TIME view and must never be persisted</b> (PR A
    /// self-review, correctness lens). <see cref="TrialWindow"/> keeps and re-seeds the stores from
    /// <see cref="MergeStored"/> — the raw merge — and applies the clamp through <see cref="Elapsed"/>
    /// at every read. Persisting the clamped first-run would let ONE boot under a backdated clock
    /// (a dead CMOS battery reads 2015 until NTP corrects it) rewrite a 2026 first-run to 2015 in both
    /// stores: the window reads Ended while the clock is wrong — correct, fail-closed — and would stay
    /// Ended for good after the clock was fixed. This method stays for the pure-decision tests and
    /// for any caller that wants the view in one call.</para>
    /// </summary>
    internal static Reading? Merge(IEnumerable<Reading?> readings, DateTimeOffset now)
    {
        DateTimeOffset? earliest = null, latest = null;
        foreach (var r in readings)
        {
            if (r is not { } v) continue;
            if (earliest is null || v.FirstRun < earliest) earliest = v.FirstRun;
            if (latest is null || v.LastSeen > latest) latest = v.LastSeen;
        }
        if (earliest is null) return null;

        // A first run cannot be in the future.
        var first = earliest.Value > now ? now : earliest.Value;

        // A last-seen EARLIER than the first-run is incoherent (hand-edited or a partial write);
        // clamp rather than reject, so one corrupt store cannot discard a valid first-run. Note this
        // clamp is NOT now-relative -- it compares two stored values against each other, which is
        // why it is safe where the reverted last-seen fence was not.
        var seen = latest is { } l && l > first ? l : first;
        return new Reading(first, seen);
    }

    /// <summary>
    /// The STORED merge — earliest first-run wins, latest last-seen wins, a last-seen earlier than
    /// the first-run clamps UP to it (the coherence clamp, two stored values against each other) —
    /// and nothing else: <c>now</c> is never consulted, so what the stores are re-seeded from is
    /// exactly what they held. The read-time clamps live in <see cref="Elapsed"/>.
    /// </summary>
    internal static Reading? MergeStored(IEnumerable<Reading?> readings)
    {
        DateTimeOffset? earliest = null, latest = null;
        foreach (var r in readings)
        {
            if (r is not { } v) continue;
            if (earliest is null || v.FirstRun < earliest) earliest = v.FirstRun;
            if (latest is null || v.LastSeen > latest) latest = v.LastSeen;
        }
        if (earliest is null) return null;
        var first = earliest.Value;
        var seen = latest is { } l && l > first ? l : first;
        return new Reading(first, seen);
    }

    /// <summary>
    /// How much of the window has elapsed at <paramref name="now"/>, over the READ-TIME view of a
    /// stored reading: the first-run clamped down to <c>now</c> (a first run cannot be in the future
    /// — the LIC-11 unbounded-grace fix), the last-seen clamped up to that, and
    /// <see cref="EffectiveNow"/> applied. Never writes, never persisted. Fail-closed by construction:
    /// every clamp here can only inflate the result.
    /// </summary>
    internal static TimeSpan Elapsed(Reading stored, DateTimeOffset now)
    {
        var view = Merge(new Reading?[] { stored }, now)!.Value;
        return EffectiveNow(now, view.LastSeen) - view.FirstRun;
    }

    /// <summary>
    /// Clock-rollback guard: <c>max(now, lastSeen)</c>.
    /// <para>Winding the system clock BACK cannot stretch the window — the guard absorbs it, which is
    /// the common direction (CMOS death, dual-boot RTC drift) and is harmless here.</para>
    /// <para><b>The failure this buys, stated rather than discovered later:</b> a FORWARD clock fault
    /// larger than the window, while the app is running, stamps last-seen far in the future and
    /// permanently burns an honest user's tryout even after the clock is corrected. Accepted because
    /// multi-day forward RTC faults are rare, the window is short, and support recovery is "clear
    /// every trial store, app closed" (the registry store via <see cref="ClearAll"/> AND the two
    /// settings keys). The alternative — trusting a backward jump — hands the window to anyone who
    /// changes their clock, which is the gesture this card exists to close.</para>
    /// </summary>
    internal static DateTimeOffset EffectiveNow(DateTimeOffset now, DateTimeOffset lastSeen)
        => now > lastSeen ? now : lastSeen;

    /// <summary>Which stores need writing to bring them all up to date — the self-healing half.
    /// A store is re-seeded when it is missing, when its first-run is LATER than the merged one (it
    /// was wiped and re-created), or when its last-seen is older.</summary>
    internal static bool NeedsReseed(Reading? store, Reading merged)
        => store is not { } s || s.FirstRun > merged.FirstRun || s.LastSeen < merged.LastSeen;

    // ── Encoding ──────────────────────────────────────────────────────────────

    /// <summary>Encode a reading as a DPAPI-protected blob. The version rides INSIDE the protected
    /// payload, so tampering is DETECTED rather than impossible: DPAPI blobs are integrity-checked, so
    /// an edited blob makes Unprotect throw and Decode return null.</summary>
    internal static byte[]? Encode(Reading reading)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{FormatVersion}|{reading.FirstRun:O}|{reading.LastSeen:O}");
            return Protect(payload, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            // Debug, not Warning: TrialWindow owns the ONE per-process Warning for a faulted store (Codex diff round, PR A -- the two together were two Sentry-visible warnings per failure).
            Logger.Debug("First-run tattoo could not be encoded ({ExType})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Decode a blob. EVERY failure — wrong machine, wrong user, corrupt bytes, unknown
    /// version, unparseable dates — returns null, i.e. "no tattoo". Never throws, and never reports
    /// "expired": see the type doc for why that polarity is load-bearing.</summary>
    internal static Reading? Decode(byte[]? blob)
    {
        if (blob is null || blob.Length == 0) return null;
        try
        {
            var text = Encoding.UTF8.GetString(Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
            var parts = text.Split('|');
            if (parts.Length != 3) return null;
            if (!byte.TryParse(parts[0], out var version) || version != FormatVersion) return null;
            if (!DateTimeOffset.TryParse(parts[1], null,
                    global::System.Globalization.DateTimeStyles.RoundtripKind, out var first)) return null;
            if (!DateTimeOffset.TryParse(parts[2], null,
                    global::System.Globalization.DateTimeStyles.RoundtripKind, out var seen)) return null;
            return new Reading(first, seen);
        }
        catch (Exception ex)
        {
            // Expected on a new machine/profile: CurrentUser DPAPI cannot decrypt another user's blob.
            Logger.Debug("First-run tattoo unreadable, treating as absent ({ExType})", ex.GetType().Name);
            return null;
        }
    }

    // ── Stores ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read the registry store. Returns <c>true</c> when the registry could be consulted —
    /// <paramref name="reading"/> is then the decoded value, or <c>null</c> for an absent,
    /// undecryptable or malformed one (all "no tattoo", the LIC-11 polarity). Returns <c>false</c>
    /// ONLY when the registry itself could not be opened or read; the caller owns the Warning and
    /// the per-process memo, so this logs at Debug.
    /// </summary>
    internal static bool TryReadRegistry(out Reading? reading)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            reading = Decode(key?.GetValue(RegistryValueName) as byte[]);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug("First-run tattoo registry read failed ({ExType})", ex.GetType().Name);
            reading = null;
            return false;
        }
    }

    /// <summary>
    /// Write the registry store. Returns <c>false</c> when the value could not be protected (DPAPI)
    /// or could not be written; the caller owns the Warning and the memo (Debug here).
    /// </summary>
    internal static bool TryWriteRegistry(Reading reading)
    {
        if (Encode(reading) is not { } blob) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (key is null) return false;
            key.SetValue(RegistryValueName, blob, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug("First-run tattoo registry write failed ({ExType})", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Remove the one store this type owns — the registry value. Fail-soft and idempotent.
    /// <para>Support recovery ONLY (a clock fault that burned an honest user's trial — the accepted
    /// residual of the clock guard). Neither of the two callers once imagined for it exists:
    /// activation and deactivation never touch a live trial since LIC-21 PR A, and the erasure path
    /// never clears the tattoo by owner decision (A23). Deliberately does NOT remove the settings
    /// keys — those belong to <see cref="TrialWindow"/> — so on its own this does NOT recover a
    /// trial: the settings leg re-seeds the durable store at the next launch. A recovery clears
    /// BOTH legs (this, plus <c>licenseFirstRunGraceStartedUtc</c> and
    /// <c>licenseFirstRunGraceLastSeenUtc</c> in <c>settings.json</c>, or the whole data folder)
    /// with the app CLOSED, because a running instance re-persists its in-memory snapshot at the
    /// next hourly write. No code path calls this today; it is a documented manual procedure.</para>
    /// </summary>
    internal static void ClearAll()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            key?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "First-run tattoo registry clear failed ({ExType})", ex.GetType().Name);
        }
    }
}
