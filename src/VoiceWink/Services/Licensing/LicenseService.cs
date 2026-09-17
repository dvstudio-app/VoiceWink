using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Http;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Licensing;

/// <summary>
/// Validates and manages the app's license against the LemonSqueezy License API
/// (<c>https://api.lemonsqueezy.com/v1/licenses/{activate,validate,deactivate}</c>).
///
/// State is persisted in <see cref="SettingsService"/> (license key DPAPI-encrypted
/// via the same pattern as <see cref="ApiKeyManager"/>; fingerprint + timestamps
/// stored in the clear). Revalidates online every 24 hours; allows 30 days offline
/// grace from the last successful validation; treats the time-limited first-run window (the
/// free trial, the only try path since LIC-21) as <see cref="LicenseStatus.FirstRunGrace"/> once
/// the user picks "Start free trial". Expiring keys (LIC-4 built the mechanism for the retired
/// 7-day trial key; a refunded key arrives the same way) activate like any key; their
/// <c>expires_at</c> is persisted and enforced locally at every cache-trust point so an
/// expired key can't ride the validation cache or the offline grace.
///
/// Machine binding uses <see cref="HardwareFingerprint"/> with 2-of-3 component
/// matching so common failure modes (OS reinstall regenerating MachineGuid, NIC
/// swap) do not lock out paying users.
/// </summary>
public sealed class LicenseService
{
    private const string DefaultBaseUrl = "https://api.lemonsqueezy.com/v1";
    private const int MinimumMatchingComponents = 2;

    // Substring LemonSqueezy embeds in activate-refusal errors when the license's
    // activation_limit is already fully consumed. Case-insensitive substring match —
    // brittle to LS rewording / localization, but the only signal available on an
    // activated:false response (the response omits the license_key object). Recovery
    // path on false-negative: user still sees the raw LS error, just without the
    // targeted "deactivate or upgrade" CTA substitution.
    private const string ActivationLimitErrorMarker = "activation limit";

    // Must match DPAPI entropy prefix pattern used elsewhere; a separate context keeps the
    // license blob un-decryptable by an attacker who only grabs an API-key-scoped blob.
    private static readonly byte[] LicenseKeyEntropy = "VoiceWink-License-2026"u8.ToArray();

    // internal since LIC-23 (2026-09-06): the in-session timer (LicenseRevalidationScheduler) ticks
    // at this cadence and the privacy policy says "about once a day" — one constant for the cache
    // short-circuit, the timer and the sentence, so none of the three can drift from the others.
    internal static readonly TimeSpan RevalidationInterval = TimeSpan.FromHours(24);
    // internal, not private, for the same reason FirstRunGraceDuration is: the License page states
    // this window to the user, and a page that types its own "30 days" is a second source of truth
    // that goes stale the moment this constant moves (LIC-13).
    internal static readonly TimeSpan OfflineGraceDuration = TimeSpan.FromDays(30);
    // The free trial: 7 days (LIC-21 PR A, owner decision 2026-09-06 — VoiceInk's model; the
    // LNC-6 3650-day tester scaffold that stood here since the friends/testers window is retired,
    // and the 72 h of the earlier LIC-4 plan never shipped). Every sentence derives its length from
    // this constant through TryoutWindowCopy, so no copy carries a number of its own. The window's
    // start is tattooed (TrialWindow + the registry stamp): a folder wipe does not restart it
    // (test-pinned), and a reinstall by design, pending the VM uninstall + reinstall row.
    // internal (not private) so VoiceWink.Tests can derive grace-window boundaries from this single
    // source of truth instead of hardcoding day counts that silently desync on every value change.
    internal static readonly TimeSpan FirstRunGraceDuration = TimeSpan.FromDays(7);

    private static ILogger Logger => Log.ForContext<LicenseService>();

    // Factory delegate rather than a stored HttpClient so production can resolve a
    // fresh, factory-managed wrapper on every HTTP call. Holding a singleton's
    // HttpClient pins the underlying SocketsHttpHandler for the process lifetime
    // and defeats IHttpClientFactory's handler rotation (default 2-minute HandlerLifetime) —
    // DNS changes for api.lemonsqueezy.com would never propagate. The wrapper
    // returned by CreateClient("licensing") is cheap (not disposing is fine per MS docs).
    private readonly Func<HttpClient> _httpFactory;
    private readonly SettingsService _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<HardwareFingerprint> _fingerprintProvider;
    // Memoized snapshot of the fingerprint for this service instance. Hardware doesn't
    // change at runtime, but `Compute()` does live WMI + NIC enumeration (~30-150ms cold)
    // and the gate path runs on every hotkey press. Resolved lazily on first access.
    // Tests inject their own provider via the ctor; each test creates a fresh service so
    // cache reuse across tests is impossible.
    private HardwareFingerprint? _cachedFingerprint;
    private readonly object _fingerprintCacheLock = new();
    // Serializes every license-state WRITE section — validate-response application
    // (ApplyValidateOutcome), activation persist, deactivation clear — on this
    // DI-singleton service. Never held during HTTP: concurrent CheckAsync /
    // ActivateAsync / DeactivateAsync interleave freely at their network awaits, but
    // their state mutations are atomic, which is what makes the stale-response
    // identity guard sound rather than a TOCTOU check (LIC-4, Codex diff review).
    private readonly object _stateWriteLock = new();
    // Monotonic validate-request stamp + last-APPLIED marker (LIC-4, Codex diff round 2).
    // The identity guard can't order two overlapping checks for the SAME key (startup
    // reconcile vs. a user's force refresh), and completion-order application would let a
    // slow, earlier-started response overwrite the newer verdict — including a restored
    // expiry or disabled flag. Responses therefore apply only in request-START order.
    // The increment is Interlocked (taken before the network call, outside the lock);
    // compare/advance happen under _stateWriteLock.
    private int _validateGeneration;
    private int _lastAppliedValidateGeneration;
    // Bumped (under _stateWriteLock) whenever a NEW license identity is persisted by
    // ActivateAsync. DeactivateAsync snapshots it before its HTTP call and skips the
    // local clear when a newer activation landed meanwhile — its server-side effect
    // targeted the OLD key, and erasing the new key's local state would sign the user
    // out of a license the server still considers active (Codex diff round 3).
    private int _stateEpoch;
    private readonly string _baseUrl;
    // Product-binding policy (LIC-4): which LS store/product identities are OURS. Shipped
    // staged (empty, enforcing nothing) until Batch C armed it on 2026-09-10. Injectable so
    // tests exercise the binding against fixture ids without touching Production.
    private readonly LicenseIdentityManifest _identityManifest;
    // LIC-21 PR A: the free-trial window as device state — its own store, its own lock, never
    // written by activation or deactivation. Observed (last-seen advanced) only from the UNLOCKED
    // entry points (GetCachedStatus, CheckWithOutcomeAsync); classified lock-free everywhere else,
    // so its lock is never nested inside _stateWriteLock (Codex plan round).
    private readonly TrialWindow _trialWindow;

    /// <summary>
    /// Public constructor — the pre-PR-A shape, for every caller outside the assembly's own DI
    /// wiring (the LS harness, direct constructions): NO durable trial store, so nothing they run
    /// touches a real registry. Pass a factory that resolves an <see cref="HttpClient"/> from
    /// <see cref="IHttpClientFactory"/> on each call.
    /// </summary>
    public LicenseService(
        Func<HttpClient> httpFactory,
        SettingsService settings,
        Func<DateTimeOffset>? clock = null,
        Func<HardwareFingerprint>? fingerprintProvider = null,
        string? baseUrl = null,
        LicenseIdentityManifest? identityManifest = null)
        : this(httpFactory, settings, clock, fingerprintProvider, baseUrl, identityManifest, trialStampStore: null)
    {
    }

    /// <summary>
    /// Primary constructor — used by DI (<c>App.xaml.cs</c>) and by tests through
    /// <c>InternalsVisibleTo</c>. <paramref name="trialStampStore"/> is the durable leg of the
    /// free-trial window (the registry in production, LIC-21 PR A); <c>null</c> keeps the window on
    /// the settings leg alone. Internal because the seam's types are internal.
    /// </summary>
    internal LicenseService(
        Func<HttpClient> httpFactory,
        SettingsService settings,
        Func<DateTimeOffset>? clock,
        Func<HardwareFingerprint>? fingerprintProvider,
        string? baseUrl,
        LicenseIdentityManifest? identityManifest,
        ITrialStampStore? trialStampStore)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _trialWindow = new TrialWindow(settings, trialStampStore, _clock, FirstRunGraceDuration);
        _fingerprintProvider = fingerprintProvider ?? HardwareFingerprint.Compute;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _identityManifest = identityManifest ?? LicenseIdentityManifest.Production;
    }

    /// <summary>
    /// Returns the current machine's fingerprint, computed on first call and cached for
    /// the lifetime of this service instance. The lock guards two-thread races on first
    /// access; subsequent reads hit the cached field directly.
    /// </summary>
    private HardwareFingerprint GetFingerprint()
    {
        if (_cachedFingerprint is { } fp) return fp;
        lock (_fingerprintCacheLock)
        {
            return _cachedFingerprint ??= _fingerprintProvider();
        }
    }

    /// <summary>
    /// Test-friendly overload — pass a specific <see cref="HttpClient"/> once and
    /// reuse it for every call. Not for production: the DI pipeline should use
    /// the <see cref="Func{HttpClient}"/> overload so handler rotation works.
    /// </summary>
    public LicenseService(
        HttpClient http,
        SettingsService settings,
        Func<DateTimeOffset>? clock = null,
        Func<HardwareFingerprint>? fingerprintProvider = null,
        string? baseUrl = null,
        LicenseIdentityManifest? identityManifest = null)
        : this(() => http, settings, clock, fingerprintProvider, baseUrl, identityManifest, trialStampStore: null)
    {
    }

    /// <summary>
    /// Observe the current license state, refreshing from LemonSqueezy if the cached
    /// validation is older than 24 hours. Safe to call from startup gate or from
    /// periodic revalidation.
    ///
    /// <para>When <paramref name="force"/> is true, the 24-hour cache is bypassed and
    /// a network validate is always attempted. Use this path for the user-initiated
    /// "Check now" button — the label promises a network check and a cached hit leaves
    /// the user with no visible feedback. Ordinary page/lifecycle refreshes should leave
    /// <paramref name="force"/> at false; the LIC-22 startup reconcile and LIC-23's daily
    /// in-session timer intentionally force a validation (startup once after setup; then
    /// once per <see cref="RevalidationInterval"/> while the app runs).</para>
    /// </summary>
    public async Task<LicenseStatus> CheckAsync(bool force = false, CancellationToken ct = default)
        => (await CheckWithOutcomeAsync(force, ct).ConfigureAwait(false)).Status;

    /// <summary>
    /// <see cref="CheckAsync"/>, additionally reporting whether the server was actually consulted —
    /// see <see cref="LicenseCheckResult"/> for why a status alone cannot support an honest UI
    /// message. Every decision, every write and every returned status is IDENTICAL to
    /// <see cref="CheckAsync"/>'s: this method IS that body, with an outcome attached to each return.
    /// <c>internal</c> because the only caller that needs the distinction is the License page's view
    /// model; `MainWindow`'s startup reconcile keeps using the plain status.
    /// </summary>
    internal async Task<LicenseCheckResult> CheckWithOutcomeAsync(bool force = false, CancellationToken ct = default)
    {
        // The device was seen running — the trial window's clock guard advances whether or not a
        // key is stored (LIC-21 PR A). Outside every lock: this is an unlocked entry point.
        _trialWindow.Observe(_clock());

        var storedKey = GetStoredLicenseKey();

        // 1. No license key yet: check first-run grace window.
        if (string.IsNullOrEmpty(storedKey))
            return NotAsked(IsInFirstRunGrace() ? LicenseStatus.FirstRunGrace : LicenseStatus.Unlicensed);

        // 2. Fingerprint mismatch: machine looks different from the one we activated on.
        if (!FingerprintMatchesStored())
        {
            Logger.Warning("License fingerprint mismatch — treating as Invalid until user re-activates");
            // NotAttempted, and the distinction is load-bearing: this return happens with NO HTTP on a
            // machine that may be perfectly online, and it is reachable by a FORCED check (unlike the
            // four flags below, which force skips). A UI that assumed "forced check + nothing changed"
            // meant a network failure would blame the user's connection for a hardware change.
            return NotAsked(LicenseStatus.Invalid);
        }

        // 3. Disabled signal persisted from an earlier CheckAsync (LIC-2a). Short-circuit
        //    to DisabledReadOnly without a network call — the server already told us this
        //    license is dead, and until the user activates a new key we shouldn't flap
        //    the UI back to Activated on a cache hit.
        //    EXCEPT when the caller explicitly forced a revalidate (user clicked "Check
        //    now"): give the server a chance to say it recovered. If a transient LS
        //    incident briefly flipped the key to disabled, the user's recovery path is
        //    to click Check now, let the validate succeed, and have the success branch
        //    below clear the flag. Without this bypass the flag would be write-once and
        //    a valid license would stay locked on DisabledReadOnly forever.
        if (!force && IsLicenseDisabled())
            return NotAsked(LicenseStatus.DisabledReadOnly);

        // 3a. Foreign-identity verdict persisted from an earlier online response (LIC-4
        //     product binding). Same shape as the disabled flag: unforced paths trust the
        //     persisted server verdict; "Check now" (force) still reaches the validate so a
        //     mistaken flag can recover via a matching response (which clears it).
        if (!force && IsIdentityMismatchFlagged())
            return NotAsked(LicenseStatus.Invalid);

        // 3a-bis. The server's last recognized verdict was a refusal (revoked / inactive /
        //     expired). Same trust model as the two flags above: unforced paths honor the
        //     persisted verdict without a network call; "Check now" (force) still reaches
        //     the server so a reinstated key recovers.
        if (!force && IsValidationRefused())
            return NotAsked(LicenseStatus.Invalid);

        // 3b. Locally-known key expiry (LIC-4 — only trial keys carry an expiry in the LS
        //     catalog). Same shape as the disabled flag: unforced paths trust the persisted
        //     server verdict without a network call; a user-clicked "Check now" (force) still
        //     falls through to the validate so a merchant-side extension or trial→paid fix
        //     can recover the key (the response then overwrites the stale expiry).
        if (!force && HasExpiredPersistedExpiry())
            return NotAsked(LicenseStatus.Invalid);

        var instanceId = _settings.GetString(AppDefaults.LicenseInstanceId, "");
        var lastValidatedUtc = GetLastValidatedUtc();
        var now = _clock();

        // 4. Cache still fresh (<24h since last validate): trust it — unless the caller
        //    explicitly asked to force a revalidation (user clicked "Check now"). A
        //    force=true caller always falls through to the network validate below,
        //    where the disabled flag (if set) will be re-evaluated against the
        //    server's fresh verdict rather than trusted from local cache.
        //    Binding proof (LIC-4): when the manifest is armed, cache trust additionally
        //    requires the stored key to have MATCHED this manifest online at least once —
        //    a pre-arm activation (foreign or legitimate) has no proof, so it revalidates
        //    online even inside the 24h window instead of riding the cache.
        if (!force && BindingProofSatisfied()
            && lastValidatedUtc.HasValue && now - lastValidatedUtc.Value < RevalidationInterval)
        {
            return NotAsked(LicenseStatus.Activated);
        }

        // 5. Cache stale: try to revalidate online.
        try
        {
            // Stamped BEFORE the network call so responses can only apply in
            // request-start order (see the _validateGeneration field doc).
            var generation = Interlocked.Increment(ref _validateGeneration);
            var (ok, keyStatus, limit, usage, expiry, identity) = await CallValidateAsync(storedKey, instanceId, ct).ConfigureAwait(false);
            // Completed: a response arrived and ApplyValidateOutcome processed it — validated,
            // refused, or deliberately discarded as stale/unreadable. The Status it returns is the
            // current verdict in every one of those cases, so a caller can truthfully report "no
            // change" when it matches what it held before. That is why the outcome enum does not
            // split this further: doing so would mean threading a second return value through the
            // method that holds the identity re-check and every response-derived write.
            return new LicenseCheckResult(
                ApplyValidateOutcome(generation, storedKey, instanceId, now, ok, keyStatus, limit, usage, expiry, identity),
                LicenseCheckOutcome.Completed);
        }
        // IOException is in the filter because CallValidateAsync reads the response body with
        // ResponseHeadersRead: a connection reset mid-body throws IOException, which used to
        // escape this method entirely — the startup reconcile logged an unobserved-task fault
        // and "Check now" failed silently (Codex round 13). For grace purposes a body that
        // never arrived is an unreachable server, the same as the other three.
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException
                                   || ex is JsonException || ex is IOException)
        {
            // Network trouble — or a malformed validate body (F35): CallValidateAsync parses
            // the response, and a captive portal / proxy error page / LS incident serving
            // non-JSON with a 200 would otherwise throw JsonException PAST this catch, out of
            // the fire-and-forget startup reconcile and the LicenseViewModel. For grace
            // purposes an unparseable server is an unreachable server — same fallback chain.
            // (ActivateAsync keeps its own dedicated JsonException arm; contract drift on a
            // VALID response stays handled inside ApplyValidateOutcome.)
            //
            // Fall back to the grace window.
            //
            // When the caller force-revalidated despite a still-fresh cache (user clicked
            // "Check now" from the Activated panel), a transient network blip shouldn't
            // demote the UI from Activated → OfflineGrace. The cache we deliberately
            // bypassed is still trustworthy; return Activated to avoid a jarring flip.
            // Past the 24h revalidation window, fall through to the same offline/expired
            // behavior as the unforced path.
            //
            // If the disabled flag was set going in (force=true revalidate attempt), the
            // cache is NOT trustworthy — return DisabledReadOnly rather than flapping
            // back to Activated. The user's recovery path remains "click Check now when
            // online"; until then, keep the dead-license surface.
            // Every return in this catch is Unreachable: the request went out and could not be
            // completed, so the server's opinion is unknown and the local grace chain answers
            // instead. This is the ONE case where a UI may truthfully blame connectivity.
            if (IsLicenseDisabled())
                return Unreachable(LicenseStatus.DisabledReadOnly);
            // A persisted foreign-identity verdict is a server verdict too (LIC-4 binding) —
            // an unreachable server can't overturn it.
            if (IsIdentityMismatchFlagged())
                return Unreachable(LicenseStatus.Invalid);
            // Likewise a recognized refusal: offline can't reinstate a revoked key.
            if (IsValidationRefused())
                return Unreachable(LicenseStatus.Invalid);
            // Expired keys get no offline grace (LIC-4) — the persisted expiry IS a server
            // verdict, just delivered earlier. Also covers force-revalidate-while-offline:
            // the force path skipped the 3b short-circuit to give the server a recovery
            // chance, but an unreachable server can't extend anything.
            if (HasExpiredPersistedExpiry())
                return Unreachable(LicenseStatus.Invalid);
            // No binding proof under an armed manifest ⇒ no offline trust of any kind (LIC-4):
            // the key has never matched THIS policy online, so neither the fresh-cache return
            // below nor the grace chain may run. GraceExpired is the honest classification —
            // "needs revalidation" — and heals in one online validate.
            if (!BindingProofSatisfied())
                return Unreachable(LicenseStatus.GraceExpired);
            if (force && lastValidatedUtc.HasValue && now - lastValidatedUtc.Value < RevalidationInterval)
                return Unreachable(LicenseStatus.Activated);
            if (lastValidatedUtc.HasValue && now - lastValidatedUtc.Value < OfflineGraceDuration)
                return Unreachable(LicenseStatus.OfflineGrace);
            return Unreachable(LicenseStatus.GraceExpired);
        }
    }

    // Named constructors for the two non-network outcomes, so each return site reads as a claim
    // about what happened rather than as a tuple. LIC-15.
    private static LicenseCheckResult NotAsked(LicenseStatus status)
        => new(status, LicenseCheckOutcome.NotAttempted);

    private static LicenseCheckResult Unreachable(LicenseStatus status)
        => new(status, LicenseCheckOutcome.Unreachable);

    /// <summary>
    /// Apply a validate response's outcome: the stale-response identity re-check plus
    /// every response-derived write and the resulting classification (LIC-4).
    ///
    /// <para>Runs entirely under <see cref="_stateWriteLock"/> so the re-check and the
    /// writes are ATOMIC against <see cref="ActivateAsync"/> / <see cref="DeactivateAsync"/>
    /// mutations (which take the same lock) — without it the guard is a TOCTOU check and a
    /// concurrent activation could still land between the comparison and the writes,
    /// letting a stale response clobber the new key's expiry or disabled state (Codex diff
    /// review). The service is a DI singleton, so this one lock covers all license-state
    /// writers; it is never held during HTTP — everything here is synchronous.</para>
    /// </summary>
    private LicenseStatus ApplyValidateOutcome(
        int generation, string requestKey, string requestInstanceId, DateTimeOffset now,
        bool ok, string? keyStatus, int? limit, int? usage, KeyExpiryField expiry, LicenseIdentityFields identity)
    {
        lock (_stateWriteLock)
        {
            // Stale-response guard: CheckAsync runs concurrently with user activations
            // (MainWindow's fire-and-forget startup reconcile vs. the License page). If the
            // stored key or instance changed while the request was in flight, every write
            // below would describe the OLD key — with expiry enforcement that could wrongly
            // expire the NEW key, which is recording-blocking. Discard the stale response
            // (a deactivation that emptied the key mismatches too) and classify from
            // current local state.
            if (!string.Equals(GetStoredLicenseKey(), requestKey, StringComparison.Ordinal)
                || !string.Equals(_settings.GetString(AppDefaults.LicenseInstanceId, ""), requestInstanceId, StringComparison.Ordinal))
            {
                Logger.Information("License identity changed while validating — discarding stale validate response");
                return ClassifyCachedStatus();
            }

            // Request-order guard: a response whose request started before the last APPLIED
            // one is stale by definition — the later-started check's verdict already landed
            // and must stay authoritative (same-identity checks pass the guard above).
            if (generation <= _lastAppliedValidateGeneration)
            {
                Logger.Information("Validate response superseded by a later-started check — discarding");
                return ClassifyCachedStatus();
            }

            // Product binding (LIC-4) — evaluated BEFORE the expiry guard and REGARDLESS of
            // `ok`: a conclusively foreign identity is a verdict about whose key this is,
            // independent of whether the key itself is currently valid, and it is justified
            // by the identity fields alone even when the rest of the response is drifty.
            var bindingVerdict = EvaluateBinding(_identityManifest, identity);
            LogResponseIdentity("validate", identity, bindingVerdict);
            if (bindingVerdict == LicenseBindingVerdict.ForeignIdentity)
            {
                // Persist the verdict digest-scoped: the flag blocks only under the policy
                // that produced it, so an additive manifest change that approves this
                // product retires the flag silently. Nothing else from a foreign response
                // is trusted (no stamp/counts/expiry/proof writes); the fence still
                // advances so an earlier-started response can't slip past the ordering
                // guard afterwards.
                Logger.Warning("Validate response reports a foreign store/product identity — flagging the stored key as mismatched");
                _settings.SetString(AppDefaults.LicenseIdentityMismatchUtc, now.ToString("O"));
                _settings.SetString(AppDefaults.LicenseBindingMismatchDigest, _identityManifest.PolicyDigest);
                _lastAppliedValidateGeneration = generation;
                return CommitBlockingVerdict();
            }
            if (ok && bindingVerdict == LicenseBindingVerdict.MissingMetadata)
            {
                // A VALID response with no readable product identity under an armed manifest
                // is contract drift, not proof of foreignness — same ignore-response pattern
                // as the unparseable-expiry guard below: trust nothing, advance the fence,
                // classify from local state. No durable flag (a hard block on drift could
                // brick a good customer); the binding-proof requirement bounds the exposure —
                // a response without identity never writes a proof, and without a current
                // proof there is no cache/offline trust to inherit. Error: drift must reach
                // Sentry (provider-noise log-level policy).
                Logger.Error("Validate response valid=true but carried no product identity metadata — ignoring response");
                _lastAppliedValidateGeneration = generation;
                return ClassifyCachedStatus();
            }

            // Present-but-unparseable expires_at on a VALID response = licensing contract
            // drift. Applying it would advance the validation stamp (24h cache + 30-day
            // offline anchor) for a key whose expiry we can no longer read — repeated
            // malformed responses could extend a 7-day trial indefinitely. Trust nothing
            // from this response and classify from local state — but still ADVANCE the
            // applied-generation marker as a fence: an earlier-started response completing
            // after this one must not slip its stale writes past the ordering guard
            // (Codex diff round 5). Later well-formed checks still apply — their stamps
            // are greater. Error, not Warning: contract drift must reach Sentry
            // (provider-noise log-level policy).
            if (ok && expiry.Kind == KeyExpiryKind.Unparseable)
            {
                Logger.Error("Validate response carried an unparseable expires_at — ignoring response");
                _lastAppliedValidateGeneration = generation;
                return ClassifyCachedStatus();
            }

            _lastAppliedValidateGeneration = generation;

            // A refusal with no recognizable key status is NOT a verdict (same rule the
            // disabled-flag branch below applies) — so it must not mutate license state AT
            // ALL. Without this gate the expiry rule below still ran: a malformed
            // `valid:false` carrying `expires_at: null` ERASED an expired trial's expiry
            // while setting no refusal flag, and the next network-free GetCachedStatus
            // handed back OfflineGrace off the old validation stamp — recording re-opened
            // for an expired trial (Codex diff review round 2).
            var recognizedVerdict = ok || !string.IsNullOrEmpty(keyStatus);

            // expires_at update rule (LIC-4): Value → persist; explicit JSON null → clear
            // (the server asserts the key is perpetual, e.g. a merchant-side trial
            // extension); absent/unparseable → leave unchanged. Applies on the RECOGNIZED
            // refused branch too, so a refused "expired" response carrying a past
            // expires_at back-fills keys activated before this feature existed.
            // CONTRACT ASSUMPTION (verify at the LGL-3 live test order): LS returns the
            // license_key object WITH `expires_at: null` for perpetual keys rather than
            // omitting the field. If LS ever omits it, a stale trial expiry would survive a
            // same-key-made-perpetual validate — narrow residual risk; the realistic
            // trial→paid path is a NEW key via ActivateAsync, whose reset rule clears the
            // expiry regardless.
            if (recognizedVerdict)
            {
                if (expiry.Kind == KeyExpiryKind.Value) SetKeyExpiry(expiry.Value);
                else if (expiry.Kind == KeyExpiryKind.Null) SetKeyExpiry(null);
            }

            if (ok)
            {
                SetLastValidatedUtc(now);
                if (limit.HasValue) _settings.SetInt(AppDefaults.LicenseActivationLimit, limit.Value);
                if (usage.HasValue) _settings.SetInt(AppDefaults.LicenseActivationUsage, usage.Value);
                // Server now says the license is valid — clear any previously-persisted
                // "disabled" flag. Without this, a transient LS incident that briefly
                // returned status:disabled would sticky-lock the user on DisabledReadOnly
                // even after the server recovers. The server is the source of truth; a
                // fresh valid response supersedes the flag.
                _settings.SetString(AppDefaults.LicenseDisabledUtc, "");
                // Same supersession for a stale foreign-identity verdict (LIC-4): this
                // response passed the binding gate above, so any earlier mismatch flag is
                // recovered. On an armed manifest that gate result was Match — persist it
                // as the binding PROOF, which is what re-admits this key to cache/offline
                // trust at every trust point. (Staged manifests write no proof: "proven"
                // is only meaningful against an armed policy.)
                _settings.SetString(AppDefaults.LicenseIdentityMismatchUtc, "");
                _settings.SetString(AppDefaults.LicenseBindingMismatchDigest, "");
                // ...and any earlier refusal: the server just said this key is valid.
                _settings.SetString(AppDefaults.LicenseValidationRefusedUtc, "");
                if (bindingVerdict == LicenseBindingVerdict.Match)
                    _settings.SetString(AppDefaults.LicenseBindingProofDigest, _identityManifest.PolicyDigest);
                // Post-apply consistency (LIC-4): if a past expiry survived the update rule
                // (Absent/Unparseable response shape, or the server returned an already-past
                // date), the recording gate's GetCachedStatus() classifies Invalid — never
                // report Activated here while the gate blocks.
                if (HasExpiredPersistedExpiry())
                {
                    // A blocking verdict like any other — and this response may have just
                    // persisted the past expiry that causes it, so it needs the same durability
                    // (a process exit inside the debounce window would lose the new expiry and
                    // re-open recording after restart).
                    Logger.Information("Validate returned valid but the stored key is locally expired — reporting Invalid");
                    return CommitBlockingVerdict();
                }
                return LicenseStatus.Activated;
            }

            // Server answered but refused. If the user previously activated successfully
            // (we have an instance id), surface "disabled" as its own read-only state —
            // more user-friendly than the blunt "Invalid" banner. Disabling is a manual
            // merchant action whose cause we cannot know (refund, fraud, chargeback,
            // replacement, or error) — the presentation must never claim a refund (LIC-8).
            // Persist the disabled signal so future GetCachedStatus calls and offline
            // launches surface the correct state without a network round-trip (LIC-2a).
            if (string.Equals(keyStatus, "disabled", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(requestInstanceId))
            {
                Logger.Information("License reports disabled after prior activation — entering DisabledReadOnly");
                _settings.SetString(AppDefaults.LicenseDisabledUtc, now.ToString("O"));
                return CommitBlockingVerdict();
            }

            // Any other RECOGNIZED negative verdict (inactive, expired, etc.) is the
            // server's current truth and supersedes a stale "disabled" signal. Clearing
            // the flag here prevents a force-validate that returns "inactive" from
            // leaving the flag stranded. But when the server response is missing or
            // unrecognized (null keyStatus, malformed body), preserve the flag rather
            // than blindly clearing it — a malformed response is not a verdict.
            //
            // PERSIST the refusal (Codex diff review, 2026-07-29): returning Invalid without
            // writing anything left the recording gate open. The gate reads the network-free
            // GetCachedStatus, which would still trust the last-validated stamp and answer
            // Activated for up to 24 h — then OfflineGrace for up to 30 days — while the
            // License page showed Invalid. A revoked key must stop recording everywhere, so
            // a recognized refusal is persisted exactly like the disabled flag. A MALFORMED
            // refusal writes nothing: it is not a verdict (same rule as the flag-preserve
            // branch below).
            if (string.IsNullOrEmpty(keyStatus))
            {
                // Unrecognized refusal — the ignore-response pattern, same as an unparseable
                // expires_at: the fence is already advanced, nothing is written (the disabled
                // flag is preserved rather than cleared), and the status is classified from
                // LOCAL state. Returning Invalid here instead would make this method disagree
                // with the network-free GetCachedStatus the recording gate uses — the License
                // page saying "invalid" while the hotkey still records is the same
                // split-brain the persisted-refusal fix above exists to prevent (Codex diff
                // review round 3).
                Logger.Warning("License validation refused with no recognizable keyStatus — ignoring response");
                return ClassifyCachedStatus();
            }

            _settings.SetString(AppDefaults.LicenseDisabledUtc, "");
            _settings.SetString(AppDefaults.LicenseValidationRefusedUtc, now.ToString("O"));
            Logger.Information("License validation failed (status: {Status})", keyStatus);
            return CommitBlockingVerdict();
        }
    }

    /// <summary>
    /// Exchange a license key for an activated instance on this machine. On success,
    /// persists the key (DPAPI-encrypted), instance id, fingerprint, and timestamp.
    /// </summary>
    public async Task<LicenseActivationResult> ActivateAsync(string licenseKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return new LicenseActivationResult(false, "License key is required.", LicenseStatus.Unlicensed);

        // Service-wide single flight (Codex round 14). LicenseViewModel's busy flag is
        // INSTANCE-local and the VM is transient — every navigation to the License page builds
        // a fresh one — while this service is the DI singleton. So a user could start an
        // activation, navigate away and back, and activate again before the first returned:
        // two POSTs, two LemonSqueezy instances (each activation creates a distinct one), and
        // last-writer-wins persistence keeps only ONE instance id. The other seat is then
        // unreleasable because nothing holds its id, and neither call reports a problem.
        // Refuse locally rather than sending a second request; nothing is consumed, so
        // retrying after the first finishes is safe.
        using var flight = _activationFlight.TryAcquire();
        if (flight is null)
        {
            Logger.Information("License activation refused: another activation is already in flight");
            return new LicenseActivationResult(false,
                "An activation is already in progress on this device. Wait for it to finish before trying again.",
                GetCachedStatus());
        }

        var trimmedKey = licenseKey.Trim();
        var instanceName = BuildInstanceName();

        // Refuse BEFORE the server call when this machine yields zero fingerprint components.
        // FingerprintMatches fails closed on a zero-component current fingerprint (F8), so a
        // server-side activation would consume an LS seat and then immediately classify as
        // Invalid on the very machine that activated it — and every hopeful retry would burn
        // another seat with no compensating release. Failing here costs nothing.
        if (GetFingerprint().NonEmptyComponentCount == 0)
        {
            Logger.Error("License activation refused: no hardware fingerprint components are readable on this machine");
            return new LicenseActivationResult(false,
                "This device's hardware identity can't be read, so a license can't be bound to it. " +
                $"This can happen on locked-down or virtual machines — contact {VoiceWinkUrls.SupportEmail}.",
                LicenseStatus.Unlicensed);
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["license_key"] = trimmedKey,
                ["instance_name"] = instanceName,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/licenses/activate")
            {
                Content = new FormUrlEncodedContent(form),
            };
            RetryingHandler.DisableRetries(req);
            req.Headers.Accept.Add(new global::System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            // ResponseHeadersRead for the same reason as CallValidateAsync (Codex round 9): with
            // the default option a definitive 4xx whose BODY stalls or truncates would time out
            // before the status could be classified, and the timeout lands on the ambiguous
            // seat warning — re-creating on activation exactly what round 8 fixed for the
            // mistyped-key case. The status is a verdict; the body is only ever the wording.
            using var resp = await _httpFactory()
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Status-first classification, same discipline as CallValidateAsync (Codex round 8).
            // LS documents general License API failures as 4XX bodies carrying only `error` —
            // the shape of the MOST COMMON activation failure, a mistyped key. Requiring an
            // explicit `activated` before reading `error` (round 7's tri-state fix) turned that
            // everyday case into the alarming "a seat may have been consumed, contact support,
            // do not retry" message. Status decides first; the tri-state applies within 2xx.
            if (!resp.IsSuccessStatusCode)
            {
                // ONLY 4xx is a clean refusal — a rejected request creates no instance. 5xx may
                // have created one and then failed to answer, and anything else non-success
                // (3xx that survived redirect handling, 1xx) tells us nothing about what the
                // server did, so both are ambiguous.
                var isClientRefusal = (int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500;
                if (!isClientRefusal)
                {
                    Logger.Warning("License activation returned HTTP {Status} — outcome unknown", (int)resp.StatusCode);
                    return new LicenseActivationResult(false, AmbiguousActivationMessage, LicenseStatus.Invalid,
                SeatMayBeConsumed: true);
                }

                // 4xx: the request was REJECTED — no instance exists, so this is a clean
                // refusal and the ordinary (retryable) error is honest. The body is read
                // BEST-EFFORT and bounded, purely for the server's own wording: a rejection
                // that we can't read the reason for is still a rejection, never an ambiguous
                // seat warning.
                string? errText = null;
                try
                {
                    using var errCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    errCts.CancelAfter(ResponseBodyReadTimeout);
                    errText = TryReadErrorMessage(
                        await resp.Content.ReadAsStringAsync(errCts.Token).ConfigureAwait(false));
                }
                catch (Exception bodyEx) when (bodyEx is IOException
                                               || bodyEx is HttpRequestException
                                               || (bodyEx is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    Logger.Warning(bodyEx, "Could not read the error body of an HTTP {Status} activation refusal", (int)resp.StatusCode);
                }
                var httpError = errText ?? $"Activation failed (HTTP {(int)resp.StatusCode}).";
                Logger.Information("License activation refused: {Error}", httpError);
                return new LicenseActivationResult(false, httpError, LicenseStatus.Invalid,
                    httpError.Contains(ActivationLimitErrorMarker, StringComparison.OrdinalIgnoreCase));
            }

            // 2xx from here. ResponseHeadersRead moves the content read outside
            // HttpClient.Timeout, so bound it — and unlike the 4xx path above, a body we can't
            // read here IS ambiguous: the server said something succeeded and we can't tell what.
            // The read gets its OWN catch: a transport fault here is NOT the same as one from
            // the pre-header send. Without this an IOException escaped every filter to the
            // ViewModel's "please try again", and an HttpRequestException landed on the
            // transport branch's retry invitation — both after the server already reported
            // success (Codex round 10).
            string body;
            try
            {
                using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bodyCts.CancelAfter(ResponseBodyReadTimeout);
                body = await resp.Content.ReadAsStringAsync(bodyCts.Token).ConfigureAwait(false);
            }
            catch (Exception bodyEx) when (bodyEx is IOException
                                           || bodyEx is HttpRequestException
                                           || (bodyEx is OperationCanceledException && !ct.IsCancellationRequested))
            {
                Logger.Error(bodyEx, "Could not read the body of a successful activation response — outcome unknown");
                return new LicenseActivationResult(false, AmbiguousActivationMessage, LicenseStatus.Invalid,
                SeatMayBeConsumed: true);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // A non-object root (bare `null`, an array, a scalar) parses fine as JSON but
            // makes every TryGetProperty below THROW InvalidOperationException — which no
            // catch filter here covers, so it would escape as an unhandled exception and the
            // VM's generic handler would tell the user to try again. We cannot read whether
            // an activation happened, so this is the same ambiguity as an unreadable body
            // (Codex diff review round 6).
            if (root.ValueKind != JsonValueKind.Object)
            {
                Logger.Error("Activation response root was {Kind}, not an object — outcome unknown", root.ValueKind);
                return new LicenseActivationResult(false, AmbiguousActivationMessage, LicenseStatus.Invalid,
                SeatMayBeConsumed: true);
            }

            // Tri-state, not a boolean collapse (Codex round 7): only an EXPLICIT `false` is a
            // clean refusal where no seat was consumed and an ordinary error message is honest.
            // Missing / null / wrong-kind means we cannot tell whether the POST activated
            // anything, so it takes the ambiguous no-retry path like every other unreadable
            // outcome — collapsing it into "false" made a possibly-consumed seat look safe to
            // retry.
            var activatedFlag = root.TryGetProperty("activated", out var aEl)
                ? aEl.ValueKind switch
                {
                    JsonValueKind.True => (bool?)true,
                    JsonValueKind.False => false,
                    _ => null,
                }
                : null;
            if (activatedFlag is null)
            {
                Logger.Error("Activation response carried no explicit boolean 'activated' — outcome unknown");
                return new LicenseActivationResult(false, AmbiguousActivationMessage, LicenseStatus.Invalid,
                SeatMayBeConsumed: true);
            }

            // A 2xx that explicitly says activated:false — LS's other refusal shape.
            if (!activatedFlag.Value)
            {
                var err = root.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.String
                    ? eEl.GetString()
                    : $"Activation failed (HTTP {(int)resp.StatusCode}).";
                Logger.Information("License activation refused: {Error}", err);
                var limitReached = err != null
                    && err.Contains(ActivationLimitErrorMarker, StringComparison.OrdinalIgnoreCase);
                return new LicenseActivationResult(false, err, LicenseStatus.Invalid, limitReached);
            }

            var identity = ParseLicenseIdentity(root);
            var bindingVerdict = EvaluateBinding(_identityManifest, identity);
            LogResponseIdentity("activate", identity, bindingVerdict);

            // Kind check on `instance` before member access, same rule as ParseActivationCounts /
            // ParseKeyExpiry: `"instance": null` (or any non-object) would otherwise throw a
            // wrong-kind InvalidOperationException past every catch filter — on a response that
            // just said activated:true, i.e. with a seat possibly consumed. Degrading to an
            // empty id routes it through LIC-BIND-03, which warns and does not invite a retry.
            // Trimmed, and whitespace treated as absent: a blank id is not a usable compensation
            // handle (a deactivate call with instance_id=" " cannot free anything), so it must
            // take the LIC-BIND-03 path rather than being persisted as if it were one.
            var instanceId = (root.TryGetProperty("instance", out var instEl)
                              && instEl.ValueKind == JsonValueKind.Object
                              && instEl.TryGetProperty("id", out var idEl)
                              && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? ""
                : "").Trim();

            if (string.IsNullOrEmpty(instanceId))
            {
                // Outcome 3 of the compensation matrix (LIC-BIND-03): the server said
                // activated:true but gave us no instance id — an activation may exist that
                // we can neither confirm nor release (compensation needs the id we never
                // got). A truncated success is contract drift → Error (Sentry). The copy
                // must warn the seat may be consumed and must NOT invite retry (each
                // hopeful retry could burn another seat); when the response identity is
                // conclusively foreign, recovery authority is that key's issuing merchant,
                // not DV Studio.
                Logger.Error("Activation response was missing instance.id — ambiguous remote activation (LIC-BIND-03)");
                var ambiguousMessage = bindingVerdict == LicenseBindingVerdict.ForeignIdentity
                    ? "This key belongs to a different product, and the license server's response was " +
                      "incomplete — an activation may still have been created on it. Contact the seller " +
                      "you purchased that key from. (LIC-BIND-03)"
                    : "The license server's response was incomplete, so this activation can't be " +
                      "confirmed or undone. It may still count toward your device limit — contact " +
                      $"{VoiceWinkUrls.SupportEmail} to check whether it was activated. (LIC-BIND-03)";
                return new LicenseActivationResult(false, ambiguousMessage, LicenseStatus.Invalid,
                    SeatMayBeConsumed: true);
            }

            if (bindingVerdict is LicenseBindingVerdict.ForeignIdentity or LicenseBindingVerdict.MissingMetadata)
            {
                // Fail-closed product binding (LIC-4): LS creates the activation instance
                // before its metadata can be inspected, so a rejection must COMPENSATE by
                // releasing the just-created instance — on its own bounded token (a
                // cancelled caller must not skip cleanup), like every compensation path in
                // this method. Foreign = someone's valid key for another product (user
                // action → Warning); missing metadata on an armed manifest = contract
                // drift (→ Error, Sentry).
                if (bindingVerdict == LicenseBindingVerdict.ForeignIdentity)
                    Logger.Warning("License activation rejected: response identity is foreign to this product — releasing the activation");
                else
                    Logger.Error("License activation rejected: response carried no product identity metadata — releasing the activation");

                bool bindingSeatReleased;
                using (var bindingReleaseCts = new CancellationTokenSource(CompensationReleaseTimeout))
                    bindingSeatReleased = await TryReleaseInstanceAsync(trimmedKey, instanceId, bindingReleaseCts.Token).ConfigureAwait(false);

                // Outcome 1 (LIC-BIND-01, confirmed release): retrying with the CORRECT key
                // is safe and invited. Outcome 2 (LIC-BIND-02, failed/uncertain release):
                // the seat may still be consumed — never invite a retry, and name the only
                // party who can actually free a seat: the key's ISSUING merchant (LS
                // My Orders cannot deactivate instances; DV Studio cannot administer a
                // foreign merchant's key).
                var rejectionMessage = (bindingVerdict, bindingSeatReleased) switch
                {
                    (LicenseBindingVerdict.ForeignIdentity, true) =>
                        "This key belongs to a different product, so it wasn't activated on this " +
                        "device. The activation was released — check that you pasted your VoiceWink " +
                        "key. (LIC-BIND-01)",
                    (LicenseBindingVerdict.ForeignIdentity, false) =>
                        "This key belongs to a different product, so it can't be used with VoiceWink — " +
                        "and releasing the activation failed, so it may still count toward that key's " +
                        "device limit. Contact the seller you purchased that key from. (LIC-BIND-02)",
                    (_, true) =>
                        "The license server did not identify this key's product, so it wasn't " +
                        "activated on this device. The activation was released — please try again. " +
                        "(LIC-BIND-01)",
                    _ =>
                        "This key couldn't be verified as a VoiceWink license, and releasing the " +
                        "activation failed — it may still count toward the key's device limit. Contact " +
                        $"the seller you purchased the key from (for VoiceWink keys: {VoiceWinkUrls.SupportEmail}). " +
                        "(LIC-BIND-02)",
                };
                // Only the FAILED release leaves a seat we can't account for; a confirmed one
                // is fully compensated.
                return new LicenseActivationResult(false, rejectionMessage, LicenseStatus.Invalid,
                    SeatMayBeConsumed: !bindingSeatReleased);
            }

            var (limit, usage) = ParseActivationCounts(root);

            var expiry = ParseKeyExpiry(root);
            // Present-but-unparseable expires_at = contract drift on the activation
            // response. Persisting the key WITHOUT its expiry would locally convert a
            // 7-day trial into a perpetual key (Codex diff round 2) — refuse like any
            // other malformed response; nothing is persisted and the user retries.
            // Absent/Null stay on the reset rule below (perpetual keys legitimately
            // carry no expiry).
            if (expiry.Kind == KeyExpiryKind.Unparseable)
            {
                Logger.Error("Activation response carried an unparseable expires_at — refusing to persist");
                // The server-side activation SUCCEEDED — a seat is consumed and this is the
                // only moment we hold its instance id. Release it best-effort before
                // discarding, or every retry burns another seat toward the activation
                // limit with no cleanup handle left (Codex diff round 3). Independent
                // bounded token: a cancelled caller must not skip the compensation. The
                // release result drives the message — a failed release must warn about the
                // possibly-stranded seat instead of inviting seat-burning retries (Codex R2).
                bool expirySeatReleased;
                using (var expiryReleaseCts = new CancellationTokenSource(CompensationReleaseTimeout))
                    expirySeatReleased = await TryReleaseInstanceAsync(trimmedKey, instanceId, expiryReleaseCts.Token).ConfigureAwait(false);
                return new LicenseActivationResult(false,
                    expirySeatReleased
                        ? "The license server returned an unexpected response. Please try again."
                        // No retry invitation of ANY form when the release failed — the same
                        // rule as LIC-BIND-02: each retry creates another instance before the
                        // response can be inspected, so even "…before trying again" points
                        // the user back at the action that burns seats (Codex rounds 3+4).
                        : "The license server returned an unexpected response, and releasing the " +
                          "activation failed — it may still count toward your device limit. Contact " +
                          $"{VoiceWinkUrls.SupportEmail} to free it.",
                    LicenseStatus.Invalid,
                    SeatMayBeConsumed: !expirySeatReleased);
            }

            // Persist everything — atomically w.r.t. a concurrent validate applying its
            // response (ApplyValidateOutcome takes the same lock), so a stale validate can
            // never interleave its writes with this fresh activation's.
            LicenseStatus statusAfterPersist;
            Exception? persistFailure = null;
            lock (_stateWriteLock)
            {
                // Snapshot the COMPLETE prior license state (raw stored values, taken in the
                // same lock hold as the writes) so a failed persist restores it exactly. A
                // blanket ClearLocalLicenseState here would delete state the failed activation
                // never owned — a pre-existing license, or the FirstRunGrace window (Codex
                // diff review R1 of this fix).
                var priorStrings = new Dictionary<string, string>();
                foreach (var key in LicenseStateStringKeys)
                    priorStrings[key] = _settings.GetString(key, "");
                var priorLimit = _settings.GetInt(AppDefaults.LicenseActivationLimit, -1);
                var priorUsage = _settings.GetInt(AppDefaults.LicenseActivationUsage, -1);

                try
                {
                    SetStoredLicenseKey(trimmedKey);
                    _settings.SetString(AppDefaults.LicenseInstanceId, instanceId);
                    _settings.SetString(AppDefaults.LicenseMachineId, GetFingerprint().Serialize());
                    SetLastValidatedUtc(_clock());
                    // The free-trial stamp is deliberately NOT cleared here (LIC-21 PR A): the trial
                    // is device state licensing never writes, so a key activated inside the window
                    // neither extends nor cancels it, and a later deactivation returns the remaining
                    // days rather than a fresh window.
                    // A fresh successful activation supersedes any prior "disabled" signal on this
                    // machine — e.g. user refunded their old key and bought a new one (LIC-2a).
                    _settings.SetString(AppDefaults.LicenseDisabledUtc, "");
                    // ...and any prior foreign-identity verdict — a new key is a new contract
                    // (LIC-4 binding). The binding PROOF records that THIS key matched the
                    // current armed policy online; a key activated under a staged manifest is
                    // unproven by definition (empty), so arming later forces its first online
                    // revalidation before any cache trust.
                    _settings.SetString(AppDefaults.LicenseIdentityMismatchUtc, "");
                    _settings.SetString(AppDefaults.LicenseBindingMismatchDigest, "");
                    // A fresh activation also supersedes any earlier refusal verdict.
                    _settings.SetString(AppDefaults.LicenseValidationRefusedUtc, "");
                    _settings.SetString(AppDefaults.LicenseBindingProofDigest,
                        bindingVerdict == LicenseBindingVerdict.Match ? _identityManifest.PolicyDigest : "");
                    // Only overwrite counts when LS actually returned them. A response that omits
                    // these fields (non-standard LS product shape, future API change) should leave
                    // any prior cached values intact rather than zeroing the display until the
                    // next CheckAsync reconciles. Symmetric with the guard in CheckAsync above.
                    if (limit.HasValue) _settings.SetInt(AppDefaults.LicenseActivationLimit, limit.Value);
                    if (usage.HasValue) _settings.SetInt(AppDefaults.LicenseActivationUsage, usage.Value);

                    // expires_at reset rule (LIC-4): a new key is a new contract — persist the
                    // expiry the server asserts for THIS key, and clear any leftover from a
                    // previous key on everything else (Absent/Null/Unparseable). Only trial keys
                    // carry an expiry in the LS catalog; carrying a trial's expiry onto a fresh
                    // perpetual key would wrongly expire it.
                    SetKeyExpiry(expiry.Kind == KeyExpiryKind.Value ? expiry.Value : null);

                    // Classify from local state rather than assuming Activated: activating an
                    // already-expired key must not report Activated while the recording gate
                    // (GetCachedStatus) blocks on the just-persisted expiry. Normal activations
                    // land on Activated via the fresh validation stamp above.
                    statusAfterPersist = ClassifyCachedStatus();

                    // Durability: license state is the one settings payload whose silent loss
                    // strands a paid LS seat. Set() only schedules a debounced Save(), which is
                    // FAIL-QUIET under CR-1 protect mode or any disk fault — the UI would report
                    // Activated, the state would evaporate on the next launch, and the seat would
                    // stay consumed server-side. Force the write INSIDE the state lock: snapshot →
                    // writes → flush is one atomic commit. Lock ordering is _stateWriteLock →
                    // _saveIoLock; SettingsService never calls back into LicenseService, so no
                    // inverse acquisition exists.
                    _settings.FlushOrThrow();

                    // FlushOrThrow returns SILENTLY when persistence is suppressed (the
                    // data-erasure quiesce, which a failed erasure pass can leave engaged for
                    // the rest of the session). Reporting success off it would consume an LS
                    // seat, tell the user they're activated, and lose the state at restart —
                    // so treat it as the persist failure it is and let the rollback +
                    // compensating release below run (Codex round 13; the same hole was fixed
                    // for deactivation in round 12 and this side was missed).
                    if (_settings.IsPersistenceSuppressed)
                        throw new InvalidOperationException(
                            "Settings persistence is suppressed, so the activation could not be saved.");

                    // Epoch advances ONLY on a durably-committed activation. Advancing it up
                    // front (and again on rollback) meant a FAILED activation still looked
                    // like "a newer activation landed": a concurrent deactivation would then
                    // preserve the state it had already released server-side and tell the user
                    // a healthy newer activation was kept. The whole persist block runs under
                    // _stateWriteLock, so no clear can interleave inside it — moving the bump
                    // here is safe and makes the epoch mean what its consumer assumes.
                    _stateEpoch++;
                }
                catch (Exception ex)
                {
                    // Restore the exact prior state in the SAME lock hold that wrote the doomed
                    // one — releasing the lock between failure and rollback would let a second
                    // activation snapshot the doomed state and "restore" it later (Codex R2/R3;
                    // the R2 fix that only moved the flush inside the lock still leaked the
                    // doomed state through the exception's lock exit). This also covers a throw
                    // from the writes themselves (e.g. DPAPI failure in SetStoredLicenseKey) —
                    // the seat is consumed either way and must be compensated below.
                    // The epoch is deliberately NOT advanced here: this activation never
                    // landed, so a concurrent deactivation must still see its snapshot as
                    // current and clear the (restored) prior state rather than reporting that
                    // a newer activation was preserved.
                    persistFailure = ex;
                    foreach (var (key, value) in priorStrings)
                        _settings.SetString(key, value);
                    _settings.SetInt(AppDefaults.LicenseActivationLimit, priorLimit);
                    _settings.SetInt(AppDefaults.LicenseActivationUsage, priorUsage);
                    statusAfterPersist = ClassifyCachedStatus(); // actual post-rollback state
                }
            }

            if (persistFailure != null)
            {
                Logger.Error(persistFailure, "License activation state could not be persisted — rolled back; releasing the seat");
                // Compensation must not ride the caller's token (a cancelled caller would skip
                // the release and strand the seat) — give it its own bounded budget. Runs
                // OUTSIDE the state lock: no awaits under a Monitor.
                bool released;
                using (var releaseCts = new CancellationTokenSource(CompensationReleaseTimeout))
                    released = await TryReleaseInstanceAsync(trimmedKey, instanceId, releaseCts.Token).ConfigureAwait(false);
                // Retry is invited ONLY on a confirmed release (no seat left behind). The
                // failed-release variant must not say "try again" in any form — this is the
                // PR #162 copy the launch baseline flagged for the truthfulness rule, and it
                // carries the same seat-burning hazard as LIC-BIND-02 (Codex round 4).
                var message = released
                    ? "The license was activated but couldn't be saved on this device, so the " +
                      "activation was released. Restart the app and try again."
                    : "The license was activated but couldn't be saved on this device, and releasing " +
                      "the activation failed — it may still count toward your device limit. Contact " +
                      $"{VoiceWinkUrls.SupportEmail} to free it.";
                return new LicenseActivationResult(false, message, statusAfterPersist,
                    SeatMayBeConsumed: !released);
            }

            Logger.Information("License activated (instance: {Instance}, usage: {Usage}/{Limit}, expires: {Expires})",
                instanceId, usage ?? 0, limit ?? 0,
                expiry.Kind == KeyExpiryKind.Value ? expiry.Value.ToString("O") : "never");
            // A licence is now durably activated on this device: any earlier seat warning is
            // resolved as far as the user is concerned.
            ClearSeatNotice();
            return new LicenseActivationResult(true, null, statusAfterPersist);
        }
        // Post-send failure taxonomy (Codex diff review round 5). LemonSqueezy's activate is
        // NOT idempotent — every successful call creates a new instance — so once the request
        // is on the wire, an outcome we cannot read is an outcome we cannot rule out. Retry
        // guidance is therefore reserved for the one case where nothing can have been
        // committed: the connection itself never carried the request.
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The request WAS sent and the answer never arrived (HttpClient.Timeout, not a
            // caller cancel). LemonSqueezy may have created the instance, and we hold no id
            // to release — structurally the same ambiguity as LIC-BIND-03, so no retry.
            // LOG-1 shape (NET-6): type + message, never the exception OBJECT. An
            // HttpClient.Timeout unwinds through fixed plumbing, so the frames say nothing the
            // message does not, and they reached the file log and the in-app Log Viewer. That is
            // the whole benefit. It is NOT a redaction win: the attached message went to Sentry as
            // breadcrumb exception_message under the same value-pattern scrub {ErrorMessage} gets,
            // so what Sentry sees is unchanged either way (opus self-review, verified).
            //
            // The forensic value of this line is that the event happened and when — the record a
            // customer disputing a consumed seat needs. The seat flag itself rides the returned
            // result, not this line. The catch is typed, so {ErrorType} is always
            // TaskCanceledException; it is kept for LOG-1's house shape, not because it varies.
            Logger.Warning("License activation timed out after the request was sent — outcome unknown: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            return new LicenseActivationResult(false,
                "The license server didn't respond in time, so this activation can't be confirmed. " +
                $"It may still count toward your device limit — contact {VoiceWinkUrls.SupportEmail} " +
                "to check whether it was activated.",
                LicenseStatus.Unlicensed,
                // The message warns about the seat, so the FLAG must agree — otherwise the
                // warning is never carried past the page that raised it (Codex round 15).
                SeatMayBeConsumed: true);
        }
        catch (HttpRequestException ex)
        {
            // Transport failure — dominated by "the machine is offline", where nothing reached
            // LemonSqueezy and retrying is both safe and the only sensible instruction. The
            // caveat is there because this exception can also surface a connection dropped
            // mid-exchange, which a client cannot distinguish; sending every offline user to
            // support instead would be far worse guidance for the common case.
            Logger.Warning(ex, "Network error during license activation");
            return new LicenseActivationResult(false,
                "Could not reach the license server. Check your internet connection and try again — " +
                $"if activation keeps failing, contact {VoiceWinkUrls.SupportEmail} in case an earlier " +
                "attempt was recorded.",
                LicenseStatus.Unlicensed);
        }
        catch (JsonException ex)
        {
            // A response DID come back; we just can't read it. It may have been a success body
            // (an instance exists) whose id we never parsed — ambiguous, so no retry.
            Logger.Error(ex, "Malformed response from license activation");
            return new LicenseActivationResult(false, AmbiguousActivationMessage, LicenseStatus.Invalid,
                SeatMayBeConsumed: true);
        }
    }

    /// <summary>
    /// Deactivate this machine's activation and clear local license state.
    ///
    /// <para>Local state is cleared EITHER WAY — the user asked to stop using the licence
    /// here — but the result reports whether the server actually confirmed the release, so
    /// the UI can say so. Silently presenting "Unlicensed" after an unconfirmed release
    /// taught the user their seat was free when it may still be held; they would then hit the
    /// activation limit on the next machine with no idea why (Codex round 10).</para>
    /// </summary>
    public async Task<LicenseDeactivationResult> DeactivateAsync(CancellationToken ct = default)
    {
        // Identity + epoch are captured TOGETHER under the state lock (no HTTP inside):
        // reading them separately leaves a window where an activation persists between
        // the identity reads and the epoch snapshot — the deactivate would then send the
        // OLD (or torn) identity while holding the NEW epoch, and the guarded clear below
        // would wrongly erase the new key (Codex diff rounds 3+4). If a newer activation
        // lands AFTER this snapshot, the epoch mismatch makes the clear a no-op instead.
        string key;
        string instanceId;
        int epochAtStart;
        lock (_stateWriteLock)
        {
            key = GetStoredLicenseKey();
            instanceId = _settings.GetString(AppDefaults.LicenseInstanceId, "");
            epochAtStart = _stateEpoch;
        }

        // Nothing to release (no key/instance on file) is not an unconfirmed release — there
        // was no seat to free, so it must not raise a stranded-seat warning.
        var hadRemoteSeat = !string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(instanceId);
        var serverAcknowledged = hadRemoteSeat
            && await TryReleaseInstanceAsync(key, instanceId, ct).ConfigureAwait(false);

        return new LicenseDeactivationResult(
            SeatReleaseConfirmed: !hadRemoteSeat || serverAcknowledged,
            ClearOutcome: ClearLocalLicenseState(expectedEpoch: epochAtStart));
    }

    /// <summary>
    /// Best-effort server-side release of an activation instance (the LS deactivate
    /// call). Shared by <see cref="DeactivateAsync"/> and the malformed-activation
    /// compensation path — never throws; a failed release is logged and reported as
    /// false (the user can free a stranded seat from their LemonSqueezy account).
    /// </summary>
    private async Task<bool> TryReleaseInstanceAsync(string key, string instanceId, CancellationToken ct)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["license_key"] = key,
                ["instance_id"] = instanceId,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/licenses/deactivate")
            {
                Content = new FormUrlEncodedContent(form),
            };
            RetryingHandler.DisableRetries(req);
            req.Headers.Accept.Add(new global::System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _httpFactory().SendAsync(req, ct).ConfigureAwait(false);

            // A release counts as CONFIRMED only on a 2xx that also says deactivated:true
            // (Codex round 9). Without the status check, a 4xx/5xx body carrying a stale or
            // contradictory `deactivated:true` was read as success — and every compensation
            // path treats a confirmed release as "no seat left behind", so it would tell the
            // user the activation was released and invite the retry that consumes another.
            // Unconfirmed is the safe direction here: it only ever produces a more cautious
            // message.
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warning("Instance release returned HTTP {Status} — treating the seat as NOT released", (int)resp.StatusCode);
                return false;
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("deactivated", out var dEl)
                   && dEl.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Instance release call failed (best-effort)");
            return false;
        }
    }

    /// <summary>
    /// Begin the first-run grace window — the free trial, the only try path since LIC-21. Call
    /// when the user picks "Start free trial" (onboarding or the License page). Idempotent —
    /// repeated calls do not extend the window.
    /// </summary>
    public void StartFirstRunGrace()
    {
        // The window's own lock, never _stateWriteLock (LIC-21 PR A): the trial start left
        // LicenseStateStringKeys and both clear sites, so the activation persist block and its
        // rollback no longer write or restore it — the ordering hazard LIC-13 took this lock for
        // (an in-flight activation erasing a just-started trial, or its rollback resurrecting a
        // spent one) cannot arise when licensing never writes the stamp at all. "Already started"
        // means a window exists, Active or Ended — an Ended one is never reset; a corrupt settings
        // stamp reads as never started everywhere, so a real start overwrites it (the same polarity
        // as every reader, self-review lens A 2026-09-03).
        _trialWindow.Start(_clock());
    }

    /// <summary>Returns a masked version of the stored key, e.g. <c>XXXX-****-****-ABCD</c>.</summary>
    public string? GetMaskedLicenseKey()
    {
        var key = GetStoredLicenseKey();
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Length <= 8) return new string('*', key.Length);
        return key[..4] + new string('*', key.Length - 8) + key[^4..];
    }

    /// <summary>UTC timestamp of the last successful online validation, or null if never validated.</summary>
    public DateTimeOffset? GetLastValidatedUtc()
    {
        var raw = _settings.GetString(AppDefaults.LicenseLastValidatedUtc, "");
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Pure predicate: does <paramref name="status"/> require the MainWindow startup
    /// gate to redirect the user to the License page? Extracted so the 7-status
    /// routing rule has a single, test-pinned source of truth — a merge that drops
    /// a state from the routing chain fails a theory test rather than shipping a
    /// silent UX regression.
    /// </summary>
    public static bool RequiresStartupRedirect(LicenseStatus status) => status switch
    {
        LicenseStatus.Unlicensed        => true,
        LicenseStatus.GraceExpired      => true,
        LicenseStatus.Invalid           => true,
        LicenseStatus.DisabledReadOnly  => true,
        LicenseStatus.FirstRunGrace     => false,
        LicenseStatus.Activated         => false,
        LicenseStatus.OfflineGrace      => false,
        _ => false, // defensive default for a future enum value that hasn't been classified
    };

    /// <summary>
    /// Synchronous, network-free snapshot of the license state — mirrors
    /// <see cref="CheckAsync"/>'s decision tree but never hits LemonSqueezy. Used by the
    /// startup routing gate in <c>MainWindow</c>: we need a status classification before
    /// the UI is built, and forcing an HTTP round-trip at launch would stall first-paint
    /// by hundreds of milliseconds (or longer on flaky networks). The async
    /// <see cref="CheckAsync"/> runs in the background afterwards and reconciles any
    /// cache/server divergence.
    ///
    /// <para>Semantics match <see cref="CheckAsync"/> except the network-validate step
    /// is skipped: when the cache is stale (&gt;24h) we fall directly through to the
    /// OfflineGrace / GraceExpired decision rather than trying to talk to the server.</para>
    /// </summary>
    public LicenseStatus GetCachedStatus()
    {
        // The device was seen running (LIC-21 PR A): the trial window's last-seen advances on every
        // status read, key or no key — the per-hotkey path included. This is the UNLOCKED entry
        // point; the classification itself is lock-free and shared with the callers that run it
        // inside _stateWriteLock, which must never observe (the two locks are never nested).
        _trialWindow.Observe(_clock());
        return ClassifyCachedStatus();
    }

    /// <summary>
    /// The network-free classification behind <see cref="GetCachedStatus"/>, WITHOUT the trial
    /// window observation: lock-free and safe inside <see cref="_stateWriteLock"/>, which is where
    /// validate application, the activation persist block and its rollback call it.
    /// </summary>
    private LicenseStatus ClassifyCachedStatus()
    {
        var storedKey = GetStoredLicenseKey();
        if (string.IsNullOrEmpty(storedKey))
            return IsInFirstRunGrace() ? LicenseStatus.FirstRunGrace : LicenseStatus.Unlicensed;
        if (!FingerprintMatchesStored())
            return LicenseStatus.Invalid;
        // Disabled signal persisted by CheckAsync (LIC-2a) — surface DisabledReadOnly at
        // startup without waiting on a network validate.
        if (IsLicenseDisabled())
            return LicenseStatus.DisabledReadOnly;
        // Foreign-identity verdict persisted from an online response (LIC-4 binding) —
        // mirrors CheckAsync's decision-tree position so the two classifications can't
        // diverge; the recording gate must block a foreign key without a network call.
        if (IsIdentityMismatchFlagged())
            return LicenseStatus.Invalid;
        // Recognized server refusal (revoked / inactive / expired). THIS is the check that
        // closes the recording gate: the hotkey path classifies here, so without it a
        // refused key kept recording on the still-fresh validation stamp.
        if (IsValidationRefused())
            return LicenseStatus.Invalid;
        // Expired trial key (LIC-4) — the hotkey gate and startup routing must see this
        // without a network call. Mirrors CheckAsync's decision-tree position (after the
        // disabled flag, before any cache trust) so the two classifications can't diverge.
        if (HasExpiredPersistedExpiry())
            return LicenseStatus.Invalid;
        // Binding proof (LIC-4): an armed manifest grants no cache trust to a key that has
        // never matched THIS policy online. GraceExpired = "needs revalidation" — the
        // startup reconcile's CheckAsync heals it in one online validate.
        if (!BindingProofSatisfied())
            return LicenseStatus.GraceExpired;

        var lastValidatedUtc = GetLastValidatedUtc();
        var now = _clock();
        if (lastValidatedUtc.HasValue && now - lastValidatedUtc.Value < RevalidationInterval)
            return LicenseStatus.Activated;
        if (lastValidatedUtc.HasValue && now - lastValidatedUtc.Value < OfflineGraceDuration)
            return LicenseStatus.OfflineGrace;
        return LicenseStatus.GraceExpired;
    }

    /// <summary>
    /// Activation limit and current usage reported by LemonSqueezy for the stored
    /// license (LIC-6). Both are <c>null</c> when we've never observed them (pre-LIC-6
    /// activation, or an LS response that omitted the fields). <c>(0, 0)</c> is a real
    /// "LS said zero" value and should be treated as a contract violation by callers —
    /// a legitimate license always has a positive limit.
    /// </summary>
    public (int? Limit, int? Usage) GetActivationCounts()
    {
        // Sentinel value "-1" distinguishes "unknown" (never written) from "0" (LS
        // contract violation). SettingsService.GetInt returns the default when the key
        // is absent, so using -1 as the default and mapping it back to null preserves
        // the pre-LIC-6-activation case while letting a real "0 of N" surface as itself.
        var rawLimit = _settings.GetInt(AppDefaults.LicenseActivationLimit, -1);
        var rawUsage = _settings.GetInt(AppDefaults.LicenseActivationUsage, -1);
        return (
            rawLimit < 0 ? null : rawLimit,
            rawUsage < 0 ? null : rawUsage);
    }

    /// <summary>
    /// The activation label this install sends LemonSqueezy as <c>instance_name</c> — the
    /// <c>VoiceWink-&lt;8 hex&gt;</c> string the dashboard lists per activation (LIC-30). Read-only:
    /// returns the persisted <see cref="AppDefaults.LicenseInstanceLabel"/>, or <c>null</c> when
    /// this install has never ATTEMPTED an activation (<see cref="BuildInstanceName"/> mints and
    /// persists it at the start of the activation path — before the fingerprint preflight and the
    /// request, so a refused attempt leaves a label behind too — and every activation request of
    /// this install carries exactly this value). It NEVER mints: the License page reads it on every
    /// navigation, including on the free trial with no key, and a read must not write settings.
    /// </summary>
    public string? GetInstanceLabel()
    {
        var label = _settings.GetString(AppDefaults.LicenseInstanceLabel, "");
        return string.IsNullOrEmpty(label) ? null : label;
    }

    /// <summary>
    /// Pure predicate: should a new recording be blocked by the current status (LIC-3)?
    /// Activated / FirstRunGrace / OfflineGrace let the user record; the remaining four
    /// states (Unlicensed / GraceExpired / Invalid / DisabledReadOnly) block. Extracted
    /// so the gate has a single, test-pinned source of truth.
    /// </summary>
    public static bool IsRecordingBlocked(LicenseStatus status) => status switch
    {
        LicenseStatus.Activated         => false,
        LicenseStatus.FirstRunGrace     => false,
        LicenseStatus.OfflineGrace      => false,
        LicenseStatus.Unlicensed        => true,
        LicenseStatus.GraceExpired      => true,
        LicenseStatus.Invalid           => true,
        LicenseStatus.DisabledReadOnly  => true,
        _ => true, // defensive default: a new, unclassified status blocks until reviewed
    };

    /// <summary>
    /// Remaining duration of the first-run grace window, or <c>null</c> if the
    /// window was never started or has already expired. The LicenseViewModel uses this
    /// to render the countdown on the License page for <see cref="LicenseStatus.FirstRunGrace"/>.
    /// </summary>
    public TimeSpan? GetTrialTimeRemaining() => ReadFirstRunGraceState().Remaining;

    /// <summary>
    /// True iff the free trial was started on this device AND its window has run out
    /// (onboarding License step v2, 2026-09-03). Pure read — the settings stamp plus the injected
    /// clock, no write, no network. Derived by the SAME rule as <see cref="IsInFirstRunGrace"/>
    /// (both are projections of <see cref="ReadFirstRunGraceState"/>), so the wizard's "already
    /// used" card and the recording gate can never disagree about the boundary: an elapsed time
    /// equal to <see cref="FirstRunGraceDuration"/> is Ended here exactly because it is not in
    /// grace there. Each call still performs its own read — what is shared is the derivation, not
    /// a cached snapshot.
    /// A stamp that does not parse reads as never started (<c>false</c>), never as ended — the same
    /// polarity as "not in grace": a corrupt value offers the tryout rather than refusing it.
    /// </summary>
    public bool HasFirstRunGraceEnded() => ReadFirstRunGraceState().State == FirstRunGraceState.Ended;

    // --- internals ---

    private void SetLastValidatedUtc(DateTimeOffset when)
        => _settings.SetString(AppDefaults.LicenseLastValidatedUtc, when.ToString("O"));

    private enum FirstRunGraceState { NeverStarted, Active, Ended }

    /// <summary>
    /// The ONE derivation every grace predicate is a projection of: one stamp read, one clock
    /// read, one boundary rule (Codex plan round, 2026-09-03). Callers that need two answers from
    /// one moment call this once and read both fields; a predicate called twice reads twice.
    /// <c>Remaining</c> is non-null only while Active.
    /// </summary>
    private (FirstRunGraceState State, TimeSpan? Remaining) ReadFirstRunGraceState()
    {
        // Lock-free (LIC-21 PR A): the window is an immutable snapshot, the clock guard is
        // max(now, lastSeen), and no observation happens here — so this is safe from inside
        // _stateWriteLock, where ClassifyCachedStatus reaches it.
        var (state, remaining) = _trialWindow.Read(_clock());
        return state switch
        {
            TrialWindow.State.Active => (FirstRunGraceState.Active, remaining),
            TrialWindow.State.Ended  => (FirstRunGraceState.Ended, null),
            _                        => (FirstRunGraceState.NeverStarted, null),
        };
    }

    private bool IsInFirstRunGrace() => ReadFirstRunGraceState().State == FirstRunGraceState.Active;

    private bool FingerprintMatchesStored()
    {
        var storedRaw = _settings.GetString(AppDefaults.LicenseMachineId, "");
        if (string.IsNullOrEmpty(storedRaw)) return false;
        var stored = HardwareFingerprint.Parse(storedRaw);
        var current = GetFingerprint();
        return FingerprintMatches(current, stored);
    }

    /// <summary>
    /// Pure machine-binding decision: does <paramref name="current"/> match the fingerprint we
    /// activated on (<paramref name="stored"/>)? Extracted for direct unit testing.
    /// </summary>
    internal static bool FingerprintMatches(HardwareFingerprint current, HardwareFingerprint stored)
    {
        // Fail CLOSED when the current environment yields ZERO readable components. With nothing
        // to compare, "required" would be 0 and MatchingComponents (always >= 0) would trivially
        // pass, trusting ANY stored fingerprint — so a copied settings.json on a component-less VM
        // (no MachineGuid, blocked WMI, no NIC) would bypass machine binding entirely.
        if (current.NonEmptyComponentCount == 0) return false;

        // A rich environment requires 2-of-3; a degraded one (1 or 2 readable components) requires
        // strict equality of everything available — otherwise a single shared component could match
        // an unrelated machine's stored fingerprint.
        var required = current.NonEmptyComponentCount >= 3 ? MinimumMatchingComponents : current.NonEmptyComponentCount;
        return current.MatchingComponents(stored) >= required;
    }

    private async Task<(bool Valid, string? KeyStatus, int? Limit, int? Usage, KeyExpiryField Expiry, LicenseIdentityFields Identity)> CallValidateAsync(
        string licenseKey, string instanceId, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["license_key"] = licenseKey };
        if (!string.IsNullOrEmpty(instanceId))
            form["instance_id"] = instanceId;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/licenses/validate")
        {
            Content = new FormUrlEncodedContent(form),
        };
        // License calls never retry (repo HTTP policy — the user-facing verdict must reflect
        // the LATEST server state, not a 500ms-old retry). Activate/deactivate always had
        // this; validate was the one LS call missing it (Codex plan review, LIC-4).
        RetryingHandler.DisableRetries(req);
        req.Headers.Accept.Add(new global::System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        // ResponseHeadersRead so the status is available WITHOUT waiting for the body: the
        // default completion option buffers the whole response first, so a 404 whose body
        // stalled or truncated would time out before we could classify it — and the timeout
        // falls back to "server unreachable", leaving a deleted key Activated/OfflineGrace.
        // A verdict that depends on an error body arriving is not a verdict (Codex round 7).
        using var resp = await _httpFactory()
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var httpStatus = (int)resp.StatusCode;

        // LIC-22 (owner UAT 176.23, 2026-09-06): every NON-THROWING return below passes through
        // here, so the log carries the HTTP status of every completed validate together with what
        // this method made of it — the one fact the LIC-18 200-vs-400 matrix needs from a support
        // bundle, which before this line was recoverable only by inferring from whether the
        // identity line read present=true. The verdict vocabulary is LOCAL and closed
        // (valid / refused / not_found / no_verdict): never the parsed status string, the `error`
        // text or any other body content, which a contract-drift response could fill with
        // anything, and which as an Information breadcrumb would reach opted-in Sentry
        // (Codex plan round). The pre-existing Warning/Error lines on the no-verdict paths stay.
        (bool Valid, string? KeyStatus, int? Limit, int? Usage, KeyExpiryField Expiry, LicenseIdentityFields Identity)
            Completed(string serverVerdict, (bool Valid, string? KeyStatus, int? Limit, int? Usage, KeyExpiryField Expiry, LicenseIdentityFields Identity) result)
        {
            Logger.Information("Validate completed: HTTP {HttpStatus}, verdict={ServerVerdict}", httpStatus, serverVerdict);
            return result;
        }
        (bool Valid, string? KeyStatus, int? Limit, int? Usage, KeyExpiryField Expiry, LicenseIdentityFields Identity) NoVerdict()
            => Completed("no_verdict", (false, null, null, null, new(KeyExpiryKind.Absent, default), default));

        // Normalize by HTTP STATUS before trusting a single body field (Codex diff review
        // round 6). Parsing the body first meant a non-success response that merely LOOKED
        // contract-shaped was authoritative: a 500 or 422 whose body contained
        // `license_key.status` would persist a refusal, foreign metadata would persist a
        // mismatch, and a stray `valid:true` would clear verdicts and write a binding proof.
        // "429/5xx/422 are not verdicts" stands.
        //
        // 400 is the DELIBERATE exception (LIC-18, owner UAT 176.11, 2026-09-05). The LS License
        // API overview documents errors as "a 4XX status code ... and an `error` field", with
        // 400 = "An error occurred. See the error field for details" — and that is how LS
        // answers a validate for a key DISABLED in the dashboard: 400 with `valid:false` and
        // `license_key.status:"disabled"`, the instance still registered. Discarding every 4xx
        // therefore discarded the one refusal a paying customer's revocation arrives as, and the
        // key stayed Activated on every "Check now" while the page honestly reported "no change".
        // (A refund releases the instance and arrives as 404, which is why THAT path worked.)
        // A 400 body is read with the same parser and the same guards as a 200, then narrowed
        // below to the REFUSING direction only, with its identity discarded. 422 is LS's
        // "required field invalid or missing" — a malformed request, not a verdict — and keeps
        // the no-verdict path with 429 and 5xx.
        var badRequest = resp.StatusCode == global::System.Net.HttpStatusCode.BadRequest;
        if (!resp.IsSuccessStatusCode && !badRequest)
        {
            // 404 is the documented "item not found" — the shape a DELETED key takes, and the
            // one other non-success status that IS a verdict. Everything else contributes
            // nothing: no status, no identity, no expiry, no counts, no validity, so
            // ApplyValidateOutcome takes the ignore-response path and local state stands.
            var notFound = resp.StatusCode == global::System.Net.HttpStatusCode.NotFound;
            if (!notFound)
            {
                Logger.Warning("Validate returned HTTP {Status} — treating as no verdict", httpStatus);
                return NoVerdict();
            }
            return Completed("not_found", (false, "not_found", null, null, new(KeyExpiryKind.Absent, default), default));
        }

        // ResponseHeadersRead takes the content read OUTSIDE HttpClient.Timeout, so bound it
        // explicitly — otherwise a 2xx whose body never finishes would hang the check forever.
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(ResponseBodyReadTimeout);
        string body;
        JsonDocument doc;
        try
        {
            body = await resp.Content.ReadAsStringAsync(bodyCts.Token).ConfigureAwait(false);
            doc = JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is OperationCanceledException)
        {
            // The status was known before the body failed; record it before the exception rides to
            // CheckWithOutcomeAsync's catch, which (correctly, for grace purposes) reports the
            // server as unreachable. Without this line a 200/400 whose body was cut off or not
            // JSON left no status in the log at all — the one case the LIC-18 200-vs-400 matrix
            // most needs a support bundle to show (self-review, correctness lens). Behaviour is
            // unchanged: the exception is rethrown as before.
            Logger.Warning("Validate returned HTTP {HttpStatus} but its body could not be read — treating as unreachable", httpStatus);
            throw;
        }
        using var _ = doc;
        var root = doc.RootElement;

        // A non-object 2xx root (bare `null`, an array, a scalar) makes every TryGetProperty
        // below throw a wrong-kind InvalidOperationException, which CheckAsync's catch filter
        // (network + JsonException) does NOT cover — it would escape as an unhandled exception.
        if (root.ValueKind != JsonValueKind.Object)
        {
            Logger.Error("Validate response root was {Kind}, not an object — treating as no verdict", root.ValueKind);
            return NoVerdict();
        }

        // `valid` must be an EXPLICIT boolean. Collapsing "missing" into false made a malformed
        // 2xx body a durable REFUSAL whenever it also happened to carry a status string — i.e.
        // contract drift could revoke a paying customer. Absent/wrong-kind now contributes no
        // verdict at all and local state stands.
        // NOTE: an explicit `valid:false` IS honoured whatever the status string says. A
        // whitelist of "coherent" negative statuses was considered and rejected: it would make
        // a future LS status name (say "suspended") silently ignore a real revocation, which is
        // the more dangerous direction. `valid` is the authoritative field; the status string
        // only selects disabled-vs-generic presentation.
        if (!root.TryGetProperty("valid", out var vEl)
            || (vEl.ValueKind != JsonValueKind.True && vEl.ValueKind != JsonValueKind.False))
        {
            Logger.Error("Validate response carried no explicit boolean 'valid' — treating as no verdict");
            return NoVerdict();
        }
        var valid = vEl.ValueKind == JsonValueKind.True;
        string? keyStatus = null;
        // Kind check before member access — `"license_key": null` must degrade to
        // "no status observed", not throw a wrong-kind InvalidOperationException that
        // escapes CheckAsync's network-only catch filter (same guard as
        // ParseActivationCounts / ParseKeyExpiry).
        if (root.TryGetProperty("license_key", out var lkEl)
            && lkEl.ValueKind == JsonValueKind.Object
            && lkEl.TryGetProperty("status", out var stEl)
            && stEl.ValueKind == JsonValueKind.String)
        {
            // Trimmed, and whitespace-only left as null: `"status": " "` is not a readable
            // verdict, but being merely non-empty it counted as a "recognized future status"
            // and persisted a durable refusal — locking out a payer on a malformed body, the
            // exact opposite of the malformed-response rule (Codex round 11). Trimming also
            // makes the "disabled" comparison below tolerant of stray padding.
            var rawStatus = stEl.GetString()?.Trim();
            keyStatus = string.IsNullOrEmpty(rawStatus) ? null : rawStatus;
        }

        var (limit, usage) = ParseActivationCounts(root);

        if (badRequest)
        {
            // A 400 is a verdict in the REFUSING direction only. `valid:true` on an error status
            // is contract drift, and the round-6 rule that a non-success response can never
            // UNBLOCK (clear a refusal, refresh the stamp, write a binding proof) holds for 400
            // exactly as for 500 — so it contributes nothing and local state stands.
            if (valid)
            {
                Logger.Error("Validate returned HTTP 400 claiming valid:true — contract drift, treating as no verdict");
                return NoVerdict();
            }
            // The refusal, its status string and its expiry are trusted exactly as a 200 refusal's
            // would be (`valid` is the authoritative field; the status only selects presentation —
            // see the note above). The IDENTITY is not: no error response may write a foreign-
            // identity mismatch or a binding proof, so it is discarded here. On a refusal that is
            // inert downstream — MissingMetadata matters only when `ok`.
            return Completed("refused", (false, keyStatus, limit, usage, ParseKeyExpiry(root), default));
        }

        return Completed(valid ? "valid" : "refused", (valid, keyStatus, limit, usage, ParseKeyExpiry(root), ParseLicenseIdentity(root)));
    }

    /// <summary>
    /// Best-effort read of a top-level <c>error</c> string from an LS error body. Never throws:
    /// an error response may legitimately not be JSON at all (proxy page, truncated body), and
    /// the caller only wants the server's own wording when it happens to be available.
    /// </summary>
    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("error", out var errEl)
                   && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Discriminated read of <c>license_key.expires_at</c> from an LS response (LIC-4).</summary>
    private enum KeyExpiryKind { Absent, Null, Value, Unparseable }

    private readonly record struct KeyExpiryField(KeyExpiryKind Kind, DateTimeOffset Value);

    /// <summary>
    /// Parse <c>license_key.expires_at</c> without ever throwing: a non-object root or
    /// <c>license_key</c> (null/array/string), or a missing property, map to Absent; a JSON
    /// null maps to Null; a string that round-trips <see cref="DateTimeOffset"/> maps to
    /// Value; anything else (non-string kind, malformed string) maps to Unparseable.
    /// Callers apply DIFFERENT rules per kind — activation resets on every non-Value kind,
    /// validation clears only on explicit Null (see the call sites) — which is why this
    /// deliberately does not collapse to a nullable date.
    /// </summary>
    private static KeyExpiryField ParseKeyExpiry(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("license_key", out var lk)
            || lk.ValueKind != JsonValueKind.Object
            || !lk.TryGetProperty("expires_at", out var el))
        {
            return new(KeyExpiryKind.Absent, default);
        }
        if (el.ValueKind == JsonValueKind.Null)
            return new(KeyExpiryKind.Null, default);
        if (el.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(el.GetString(), global::System.Globalization.CultureInfo.InvariantCulture,
                global::System.Globalization.DateTimeStyles.None, out var dt))
        {
            return new(KeyExpiryKind.Value, dt);
        }
        return new(KeyExpiryKind.Unparseable, default);
    }

    private void SetKeyExpiry(DateTimeOffset? expiresAt) =>
        _settings.SetString(AppDefaults.LicenseKeyExpiresAtUtc, expiresAt?.ToString("O") ?? "");

    /// <summary>
    /// The stored key's expiry as last reported by LemonSqueezy, or null for a perpetual
    /// key / never-observed / corrupt persisted value (fail-open — see the
    /// <see cref="AppDefaults.LicenseKeyExpiresAtUtc"/> doc). Feeds the License page's
    /// "This key expires …" line (LIC-4).
    /// </summary>
    public DateTimeOffset? GetKeyExpiresAt()
    {
        var raw = _settings.GetString(AppDefaults.LicenseKeyExpiresAtUtc, "");
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, global::System.Globalization.CultureInfo.InvariantCulture,
            global::System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
    }

    /// <summary>
    /// True iff a stored key exists AND its persisted LS expiry is in the past (LIC-4).
    /// Pure read — no network, no event-state — so the recording gate, startup routing,
    /// the License page, and the VM all derive "this trial ended" from the same persisted
    /// fact and can never disagree. Local-clock trust matches the existing
    /// FirstRunGrace / offline-grace model.
    /// </summary>
    public bool IsStoredKeyExpired()
        => !string.IsNullOrEmpty(GetStoredLicenseKey()) && HasExpiredPersistedExpiry();

    /// <summary>
    /// The expiry half of <see cref="IsStoredKeyExpired"/>, for decision-tree sites that
    /// have already established the stored key is non-empty — avoids a second DPAPI
    /// decrypt on the per-hotkey-press GetCachedStatus path.
    /// </summary>
    private bool HasExpiredPersistedExpiry()
    {
        var expiresAt = GetKeyExpiresAt();
        return expiresAt.HasValue && _clock() >= expiresAt.Value;
    }

    /// <summary>
    /// Extract <c>license_key.activation_limit</c> + <c>license_key.activation_usage</c> from an
    /// LS activate or validate response root element. Each field is parsed independently
    /// within the <c>license_key</c> object — a missing or unparseable <c>activation_limit</c>
    /// yields <c>null</c> for Limit while Usage may still return a value (and vice versa).
    /// Returns <c>(null, null)</c> only when the <c>license_key</c> object itself is missing.
    /// Callers treat per-field <c>null</c> as "unchanged" rather than zero.
    /// <para>Uses <c>TryGetInt32</c> rather than <c>GetInt32</c> so an unexpected numeric shape
    /// (float, out-of-Int32 range) does not throw out of a successful activate/validate flow —
    /// the counts are a UX cue, not a correctness input, so a parse failure degrades gracefully.</para>
    /// </summary>
    private static (int? Limit, int? Usage) ParseActivationCounts(JsonElement root)
    {
        // Kind check, not just presence: `"license_key": null` (or a non-object kind) must
        // degrade to "no counts observed", not throw a wrong-kind InvalidOperationException
        // out of CallValidateAsync — CheckAsync's catch filter only covers network errors.
        if (!root.TryGetProperty("license_key", out var lk) || lk.ValueKind != JsonValueKind.Object)
            return (null, null);
        int? limit = lk.TryGetProperty("activation_limit", out var lEl)
            && lEl.ValueKind == JsonValueKind.Number
            && lEl.TryGetInt32(out var l) ? l : null;
        int? usage = lk.TryGetProperty("activation_usage", out var uEl)
            && uEl.ValueKind == JsonValueKind.Number
            && uEl.TryGetInt32(out var u) ? u : null;
        return (limit, usage);
    }

    /// <summary>
    /// The three numeric identity fields of an LS activate/validate response's <c>meta</c>
    /// object (LIC-4 product binding). A field is null when absent or not a JSON number —
    /// strict by design: identity is a gate input, so drift (e.g. number→string) must read
    /// as "missing" and fail closed rather than being coerced. <c>meta</c> also carries
    /// customer fields (name/email); those are deliberately never parsed, logged, or
    /// persisted (privacy invariant).
    /// </summary>
    internal readonly record struct LicenseIdentityFields(long? StoreId, long? ProductId, long? VariantId);

    /// <summary>Binding decision for one response identity against the manifest (LIC-4).</summary>
    internal enum LicenseBindingVerdict
    {
        /// <summary>Manifest is staged (empty) — binding not enforced; everything passes.</summary>
        NotEnforced,
        /// <summary>store_id AND product_id are in the approved sets.</summary>
        Match,
        /// <summary>Both ids present but at least one is outside the approved sets.</summary>
        ForeignIdentity,
        /// <summary>store_id or product_id absent/malformed on an armed manifest — fails closed.</summary>
        MissingMetadata,
    }

    /// <summary>Non-throwing read of <c>meta.store_id/product_id/variant_id</c> — see <see cref="LicenseIdentityFields"/>.</summary>
    internal static LicenseIdentityFields ParseLicenseIdentity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object)
        {
            return new(null, null, null);
        }
        return new(ReadId(meta, "store_id"), ReadId(meta, "product_id"), ReadId(meta, "variant_id"));

        static long? ReadId(JsonElement meta, string name)
            => meta.TryGetProperty(name, out var el)
               && el.ValueKind == JsonValueKind.Number
               && el.TryGetInt64(out var value)
                ? value
                : null;
    }

    /// <summary>
    /// Pure binding decision (LIC-4): store_id + product_id are HARD gates against the
    /// manifest's approved sets; variant_id never gates (ADVISORY — owner 2026-07-28).
    /// </summary>
    internal static LicenseBindingVerdict EvaluateBinding(LicenseIdentityManifest manifest, LicenseIdentityFields identity)
    {
        if (!manifest.IsEnforced) return LicenseBindingVerdict.NotEnforced;
        if (identity.StoreId is null || identity.ProductId is null) return LicenseBindingVerdict.MissingMetadata;
        if (!manifest.ApprovedStoreIds.Contains(identity.StoreId.Value)
            || !manifest.ApprovedProductIds.Contains(identity.ProductId.Value))
        {
            return LicenseBindingVerdict.ForeignIdentity;
        }
        return LicenseBindingVerdict.Match;
    }

    /// <summary>
    /// Per-response identity observability (the variant-ADVISORY implementation). Raw ids are
    /// logged ONLY on a Match — our own public catalog ids. Foreign/unknown ids are third-party
    /// commerce metadata: Information logs become Sentry breadcrumbs when crash reporting is on,
    /// and support-bundle/GDPR exports render local log lines through RedactString, where bare
    /// numerics are un-patternable — omission is the only airtight guarantee, so non-Match
    /// verdicts log presence booleans instead of values.
    /// </summary>
    private static void LogResponseIdentity(string operation, LicenseIdentityFields identity, LicenseBindingVerdict verdict)
    {
        if (verdict == LicenseBindingVerdict.Match)
        {
            Logger.Information(
                "License {Operation} identity: store {StoreId}, product {ProductId}, variant {VariantId} — binding {Verdict}",
                operation, identity.StoreId, identity.ProductId, identity.VariantId, verdict);
        }
        else
        {
            Logger.Information(
                "License {Operation} identity: store present={StorePresent}, product present={ProductPresent}, variant present={VariantPresent} — binding {Verdict}",
                operation, identity.StoreId.HasValue, identity.ProductId.HasValue, identity.VariantId.HasValue, verdict);
        }
    }

    /// <summary>
    /// True while the stored key holds a binding proof for the CURRENT manifest policy —
    /// i.e. it matched this manifest online at least once (LIC-4). Only meaningful when the
    /// manifest is armed; a staged manifest satisfies trivially. When false on an armed
    /// manifest, no cache-trust point may grant access: the key must revalidate online
    /// (a pre-arm activation — foreign or not — has no proof by definition).
    /// </summary>
    private bool BindingProofSatisfied()
        => !_identityManifest.IsEnforced
           || _settings.GetString(AppDefaults.LicenseBindingProofDigest, "") == _identityManifest.PolicyDigest;

    /// <summary>
    /// True iff an online response conclusively reported a foreign identity for the stored
    /// key UNDER THE CURRENT POLICY (LIC-4). Timestamp parse-guarded like
    /// <see cref="IsLicenseDisabled"/>; digest-scoped so an additive manifest change that
    /// approves a formerly-foreign product retires stale verdicts without user action.
    /// </summary>
    private bool IsIdentityMismatchFlagged()
    {
        if (!_identityManifest.IsEnforced) return false;
        var raw = _settings.GetString(AppDefaults.LicenseIdentityMismatchUtc, "");
        if (string.IsNullOrEmpty(raw) || !DateTimeOffset.TryParse(raw, out _)) return false;
        return _settings.GetString(AppDefaults.LicenseBindingMismatchDigest, "") == _identityManifest.PolicyDigest;
    }

    /// <summary>
    /// Close out a validate that just persisted a BLOCKING verdict (disabled / foreign
    /// identity / recognized refusal). Two jobs, both about the two classifications never
    /// diverging (Codex diff review round 6):
    ///
    /// <para>DURABILITY — the verdict was written with debounced <c>SetString</c> calls, which
    /// are fail-quiet under CR-1 protect mode and can also be lost if the process dies inside
    /// the 250 ms window. A blocking licence verdict that never reaches disk means the next
    /// launch reloads the old Activated cache and re-opens recording. Best-effort
    /// <c>Flush()</c> (never <c>FlushOrThrow</c>: there is nothing to roll back here, and
    /// throwing out of the state lock would be worse than a stale flag that the next online
    /// check re-applies).</para>
    ///
    /// <para>PRECEDENCE — returning a hand-picked status let this method disagree with the
    /// network-free <see cref="GetCachedStatus"/> the recording gate uses (e.g. a foreign
    /// verdict returned Invalid while a surviving disabled flag made GetCachedStatus answer
    /// DisabledReadOnly). Deriving the answer from the same decision tree makes agreement
    /// structural rather than something each branch has to remember.</para>
    /// </summary>
    private LicenseStatus CommitBlockingVerdict()
    {
        try { _settings.Flush(); }
        catch (Exception ex) { Logger.Warning(ex, "Could not flush a blocking license verdict to disk"); }
        return ClassifyCachedStatus();
    }

    /// <summary>
    /// True iff the license server's last RECOGNIZED verdict was a refusal
    /// (<see cref="AppDefaults.LicenseValidationRefusedUtc"/>). Same parse-guard discipline
    /// as <see cref="IsLicenseDisabled"/>: a corrupt persisted value reads as "no signal"
    /// rather than sticky-locking the user out.
    /// </summary>
    private bool IsValidationRefused()
    {
        var raw = _settings.GetString(AppDefaults.LicenseValidationRefusedUtc, "");
        return !string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, out _);
    }

    /// <summary>
    /// True iff the persisted disabled-flag parses as a valid UTC timestamp (LIC-2a).
    /// Deliberately stricter than a null/empty check: if settings.json is hand-edited
    /// or corrupted such that the stored value isn't a parseable ISO 8601 string, we
    /// treat the flag as "not present" rather than sticky-locking the user on
    /// DisabledReadOnly forever. The service only ever writes <c>now.ToString("O")</c>
    /// or empty string, so a parse failure indicates an external mutation — safer to
    /// fall through to the normal decision tree than to trust the garbage.
    /// </summary>
    private bool IsLicenseDisabled()
    {
        var raw = _settings.GetString(AppDefaults.LicenseDisabledUtc, "");
        return !string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, out _);
    }

    // Serialises the get-or-create of the persisted instance label below. Production uses a
    // single LicenseService (DI singleton), so this lock makes the first-write atomic on the
    // only path that matters; activation is additionally UI-serialised by LicenseViewModel's
    // busy flag. Even an unserialised race would be cosmetic — each ActivateAsync sends a
    // self-consistent label and persists the instance_id LemonSqueezy returns — but the lock
    // makes the invariant explicit (Codex diff review 2026-05-29).
    // Admits exactly one in-flight activation across the whole app — see ActivateAsync.
    private readonly SingleFlight _activationFlight = new();

    // The last seat-critical outcome the user has not yet been shown a resolution for.
    // Lives on the SINGLETON because the warning must outlive the transient LicenseViewModel:
    // the messages that can be lost are exactly the ones that matter — "a seat may have been
    // consumed", "the release wasn't confirmed" — and navigating away during a slow call
    // destroyed the VM that held them, so the user would hit the activation limit later with
    // no idea why (Codex round 14). In-memory only: it describes THIS session's operation, and
    // a restart's own status routing is the honest surface after that.
    private string? _seatNotice;

    /// <summary>
    /// A seat-critical warning from an earlier operation that no longer has a live view model —
    /// surfaced by whichever <c>LicenseViewModel</c> exists next. Null when nothing is pending.
    /// </summary>
    public string? SeatNotice => Volatile.Read(ref _seatNotice);

    /// <summary>
    /// Record a seat-critical warning so it survives the transient view model that requested
    /// the operation (see <see cref="SeatNotice"/>). The caller composes the copy; the service
    /// only outlives the page.
    /// </summary>
    public void RememberSeatNotice(string message) => Volatile.Write(ref _seatNotice, message);

    /// <summary>
    /// Dismiss the pending seat warning. Called once a view model has actually displayed it,
    /// and by the service itself whenever a clean outcome supersedes it.
    /// </summary>
    public void ClearSeatNotice() => Volatile.Write(ref _seatNotice, null);

    private readonly object _instanceLabelLock = new();

    private string BuildInstanceName()
    {
        // Privacy-by-design (LIC-9): the LemonSqueezy instance_name is a random, stable
        // per-install label — NEVER the machine hostname, which is incidentally a personal
        // identifier (it often reflects the user's name, employer, or device model). The
        // label is generated once and persisted so it stays stable across the
        // activate -> re-validate -> deactivate lifecycle; identity on LemonSqueezy's side
        // is the instance_id (see DeactivateAsync / CallValidateAsync), so this name is
        // purely a cosmetic dashboard label. Kept out of ClearLocalLicenseState so a
        // re-activation reuses the same label rather than orphaning a new one.
        // See the bundled privacy policy (src/VoiceWink/Assets/Legal/privacy-v*.md) Section 3 Category A.
        lock (_instanceLabelLock)
        {
            var existing = _settings.GetString(AppDefaults.LicenseInstanceLabel, "");
            if (!string.IsNullOrEmpty(existing))
                return existing;

            var suffix = global::System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var label = $"VoiceWink-{suffix}";
            _settings.SetString(AppDefaults.LicenseInstanceLabel, label);
            return label;
        }
    }

    private string GetStoredLicenseKey()
    {
        var base64 = _settings.GetString(AppDefaults.LicenseKey, "");
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            var encrypted = Convert.FromBase64String(base64);
            var plain = ProtectedData.Unprotect(encrypted, LicenseKeyEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to decrypt stored license key");
            return "";
        }
    }

    private void SetStoredLicenseKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            _settings.SetString(AppDefaults.LicenseKey, "");
            return;
        }
        try
        {
            var plain = Encoding.UTF8.GetBytes(key);
            var encrypted = ProtectedData.Protect(plain, LicenseKeyEntropy, DataProtectionScope.CurrentUser);
            Array.Clear(plain);
            _settings.SetString(AppDefaults.LicenseKey, Convert.ToBase64String(encrypted));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to encrypt license key for storage");
            throw;
        }
    }

    // The complete set of string-typed license-state settings keys written by ActivateAsync's
    // persist block / cleared by ClearLocalLicenseState. Snapshot source for the activation
    // rollback — keep in sync with both when adding license state. The free-trial keys are NOT
    // here (LIC-21 PR A): the trial window is device state that licensing never writes, restores
    // or clears.
    private static readonly string[] LicenseStateStringKeys =
    {
        AppDefaults.LicenseKey,
        AppDefaults.LicenseInstanceId,
        AppDefaults.LicenseMachineId,
        AppDefaults.LicenseLastValidatedUtc,
        AppDefaults.LicenseDisabledUtc,
        AppDefaults.LicenseKeyExpiresAtUtc,
        AppDefaults.LicenseBindingProofDigest,
        AppDefaults.LicenseIdentityMismatchUtc,
        AppDefaults.LicenseBindingMismatchDigest,
        AppDefaults.LicenseValidationRefusedUtc,
    };

    // Budget for the best-effort seat release on the activation compensation paths. The
    // release must not ride the caller's token (a cancelled caller would skip it and strand
    // the seat), so it gets its own bounded window instead.
    private static readonly TimeSpan CompensationReleaseTimeout = TimeSpan.FromSeconds(15);

    // Bound for reading a validate response BODY. Needed because ResponseHeadersRead (used so
    // the HTTP status can be classified without waiting for content) moves the content read
    // outside HttpClient.Timeout. Matches the licensing client's own 30 s budget.
    private static readonly TimeSpan ResponseBodyReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The single message for every activation whose outcome we cannot read: an unparseable
    /// body, a non-object root, a wrong-shaped envelope. LemonSqueezy's activate is not
    /// idempotent, so an unreadable answer may still have created an instance we hold no id
    /// for — warn about the seat and give NO retry guidance (see the post-send taxonomy).
    /// </summary>
    private static readonly string AmbiguousActivationMessage =
        "The license server returned an unreadable response, so this activation can't be confirmed. " +
        $"It may still count toward your device limit — contact {VoiceWinkUrls.SupportEmail} to check " +
        "whether it was activated.";

    /// <summary>
    /// Clear this machine's license state, reporting WHICH outcome occurred — the caller's
    /// advice differs completely between "a newer activation was deliberately preserved"
    /// (healthy; never tell the user to deactivate again) and "the clear didn't reach disk"
    /// (the old activation can return after a restart). A single bool conflated them.
    /// </summary>
    private LicenseClearOutcome ClearLocalLicenseState(int? expectedEpoch = null)
    {
        // Same lock as activation persist + validate-response application: a concurrent
        // validate must either see the pre-clear identity (and get discarded by its
        // guard on the emptied key) or wait — never interleave with a half-cleared state.
        lock (_stateWriteLock)
        {
            // Epoch-guarded clear (see _stateEpoch): the caller's clear intent predates a
            // newer activation — preserve the newer key's state.
            if (expectedEpoch.HasValue && _stateEpoch != expectedEpoch.Value)
            {
                Logger.Information("A newer activation landed during deactivation — preserving its local state");
                return LicenseClearOutcome.NewerActivationPreserved;
            }
            _settings.SetString(AppDefaults.LicenseKey, "");
            _settings.SetString(AppDefaults.LicenseInstanceId, "");
            _settings.SetString(AppDefaults.LicenseMachineId, "");
            _settings.SetString(AppDefaults.LicenseLastValidatedUtc, "");
            // The free-trial stamp survives a deactivation (LIC-21 PR A): the user returns to the
            // REMAINING trial, or to "has ended" — never to a fresh window.
            _settings.SetString(AppDefaults.LicenseDisabledUtc, "");
            _settings.SetString(AppDefaults.LicenseKeyExpiresAtUtc, "");
            _settings.SetString(AppDefaults.LicenseBindingProofDigest, "");
            _settings.SetString(AppDefaults.LicenseIdentityMismatchUtc, "");
            _settings.SetString(AppDefaults.LicenseBindingMismatchDigest, "");
            _settings.SetString(AppDefaults.LicenseValidationRefusedUtc, "");
            // Reset counts to the "unknown" sentinel (-1), not 0 — after a deactivate we
            // have no active license and the counts are genuinely unknown. Writing 0 would
            // let GetActivationCounts return (0, 0), which under the new nullable contract
            // means "LS said zero" (contract violation) rather than "never observed".
            _settings.SetInt(AppDefaults.LicenseActivationLimit, -1);
            _settings.SetInt(AppDefaults.LicenseActivationUsage, -1);

            // Durability, and it must be VERIFIED, not attempted: if the server released the
            // seat but the local clear never reached disk (crash inside the 250 ms debounce,
            // or CR-1 protect mode), the next launch reloads the old Activated state and keeps
            // recording on a seat the server has already freed — which someone else may by
            // then be using. FlushOrThrow, not Flush: Flush is FAIL-QUIET (Save swallows IO
            // errors and protect mode skips the write silently), so reporting success off it
            // would be a durability claim we never checked (Codex round 11). The caller
            // surfaces the failure; nothing is rolled back — the user asked to deactivate and
            // the server may already have released.
            try
            {
                _settings.FlushOrThrow();
                // FlushOrThrow ALSO returns silently when persistence is suppressed (the
                // data-erasure quiesce), and a failed erasure pass can leave the app running
                // in that state with settings.json still on disk. Absence of an exception is
                // therefore not proof of a write — ask (Codex round 12).
                if (_settings.IsPersistenceSuppressed)
                {
                    Logger.Error("Cleared license state was not written: settings persistence is suppressed");
                    return LicenseClearOutcome.PersistenceFailed;
                }
                return LicenseClearOutcome.Cleared;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Cleared license state could not be persisted — it may return after a restart");
                return LicenseClearOutcome.PersistenceFailed;
            }
        }
    }
}

/// <summary>What happened to this machine's local license state during a deactivation.</summary>
public enum LicenseClearOutcome
{
    /// <summary>Cleared and durably written.</summary>
    Cleared,

    /// <summary>
    /// A newer activation arrived mid-flight and the epoch guard deliberately kept it. HEALTHY:
    /// the user must never be told to "deactivate again" — that would tear down the good state.
    /// </summary>
    NewerActivationPreserved,

    /// <summary>
    /// Cleared in memory but the write did not land (disk error, protect mode, or suppressed
    /// persistence), so the old activation can return after a restart.
    /// </summary>
    PersistenceFailed,
}

/// <summary>
/// Return value of <see cref="LicenseService.DeactivateAsync"/>.
/// <paramref name="SeatReleaseConfirmed"/> is false when the server did not confirm the
/// release (unreachable, non-2xx, malformed, or timed out) and a seat may therefore still be
/// held — the UI must say so rather than presenting a clean "Unlicensed".
/// </summary>
public sealed record LicenseDeactivationResult(bool SeatReleaseConfirmed, LicenseClearOutcome ClearOutcome);

/// <summary>
/// Return value of <see cref="LicenseService.ActivateAsync"/>.
/// <paramref name="ActivationLimitReached"/> is true iff the server refused the activation
/// because every seat is taken — the UI uses that to prompt a seat-deactivation flow
/// rather than a generic "try a different key" message (LIC-6).
/// </summary>
public sealed record LicenseActivationResult(
    bool Success,
    string? ErrorMessage,
    LicenseStatus Status,
    bool ActivationLimitReached = false,
    // True when this outcome may have left a LemonSqueezy activation instance behind that
    // nothing holds an id for. The UI uses it to persist the warning past its own lifetime —
    // a transient view model would otherwise take the message to the grave when the user
    // navigates away, and they'd meet the activation limit later with no explanation.
    bool SeatMayBeConsumed = false);
