using global::System.Security.Cryptography;
using global::System.Text;
using Sentry;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// Gates Sentry SDK init on the <see cref="AppDefaults.CrashReportingOptIn"/> setting
/// and a non-empty DSN (from <c>VOICEWINK_SENTRY_DSN</c> env var or the build-time-baked <c>SentryConfig.EmbeddedDsn</c>).
///
/// <para>BeforeSend runs <see cref="LogRedactionEnricher.RedactString"/> over event message,
/// tag values, <c>Extra</c> entries and every <c>SentryException.Value</c>, folds stack-frame
/// paths, and clears <c>ServerName</c> so the machine hostname never leaves the user's box. Even
/// though Serilog events already pass through the enricher on the way to the Sentry sink, direct
/// <c>SentrySdk.CaptureException</c> calls bypass Serilog — BeforeSend is the belt that catches
/// those.</para>
///
/// <para><b>What that belt does NOT cover — the two gaps REL-31 considered, NOT an exhaustive
/// list:</b> <c>Contexts</c> (a deliberate decision, recorded at its point in
/// <see cref="BeforeSendEvent"/>) and the inside of a non-string <c>Extra</c> value. Other event
/// fields are equally untouched — <c>TransactionName</c>, <c>Fingerprint</c>, <c>Modules</c>,
/// <c>User</c>/<c>Request</c> (kept empty by <c>SendDefaultPii=false</c> rather than by scrubbing),
/// <c>DebugImages</c> (whose <c>CodeFile</c>/<c>DebugFile</c> are the same path class
/// <see cref="RedactFrames"/> folds), and per-exception <c>Type</c>/<c>Module</c>/<c>Mechanism</c>
/// — all low-risk today for the same reason <c>Contexts</c> is: nothing in this app writes them.
/// The scoping words are load-bearing. REL-31 existed because "the belt that catches those" read
/// as complete while <c>Extra</c> was scrubbed nowhere, so a second partial list styled as complete
/// would repeat the exact failure this paragraph names (self-review).</para>
///
/// <para>No-op when opt-in is false or the DSN is empty — safe to call at startup before any
/// DSN is provisioned.</para>
/// </summary>
internal static class SentryInitializer
{
    private static ILogger Logger => Log.ForContext(typeof(SentryInitializer));

    // The embedded DSN comes from the build-time-generated SentryConfig.EmbeddedDsn
    // (scripts/generate-sentry-config.ps1): baked into signed release builds from the
    // gitignored installer/.sentry-dsn, EMPTY in dev/CI builds so crash reporting stays
    // inert there (which also keeps SentryInitializerTests.IsInitialized_Initial_IsFalse
    // valid). For local dev, set the VOICEWINK_SENTRY_DSN env var instead.
    private const string DsnEnvVar = "VOICEWINK_SENTRY_DSN";

    // Single lock guarding _sdkHandle mutations in TryInit and Shutdown. A concurrent
    // TryInit + Shutdown without this lock could leave IsInitialized=true pointing at a
    // disposed SDK (Shutdown nulls the handle, then TryInit's assignment races in after).
    // Expected call sites are the app ctor and UI-thread settings toggle — single-threaded
    // in practice — but the static surface doesn't enforce that.
    private static readonly object _lock = new();
    private static IDisposable? _sdkHandle;

    public static bool IsInitialized
    {
        get { lock (_lock) return _sdkHandle != null; }
    }

    /// <summary>
    /// Initialize the SDK if opt-in is enabled and a DSN is available. Idempotent.
    /// Returns true when the SDK is live after the call.
    ///
    /// <para>When already initialized and fresh license context is supplied, refreshes
    /// the <c>license.status</c> / <c>license.hash</c> scope tags. <b>No in-repo caller
    /// passes license context today</b> — the signature is wired for the REL-1a consent
    /// wizard / LIC-2 onboarding rewrite, where <c>LicenseService</c> will call
    /// <c>TryInit(true, status, key)</c> after resolving license state. Until then,
    /// the <c>license.*</c> tags simply stay unset.</para>
    /// </summary>
    public static bool TryInit(bool optIn, string? licenseStatus = null, string? licenseKey = null)
    {
        lock (_lock)
        {
            if (_sdkHandle != null)
            {
                // Future-use path (REL-1a): refresh license tags without re-initializing.
                if (!string.IsNullOrEmpty(licenseStatus) || !string.IsNullOrEmpty(licenseKey))
                {
                    try
                    {
                        SentrySdk.ConfigureScope(scope =>
                        {
                            if (!string.IsNullOrEmpty(licenseStatus))
                                scope.SetTag("license.status", licenseStatus);
                            if (!string.IsNullOrEmpty(licenseKey))
                                scope.SetTag("license.hash", HashLicense(licenseKey));
                        });
                    }
                    catch (Exception ex)
                    {
                        // The SDK is still live — don't let a tag-refresh failure
                        // propagate and make the caller think init failed.
                        Logger.Warning(ex, "Sentry license tag refresh failed; SDK remains live with stale tags");
                    }
                }
                return true;
            }
            if (!optIn)
            {
                Logger.Debug("Sentry init skipped: opt-in disabled");
                return false;
            }

            var dsn = Environment.GetEnvironmentVariable(DsnEnvVar);
            if (string.IsNullOrWhiteSpace(dsn)) dsn = SentryConfig.EmbeddedDsn;
            if (string.IsNullOrWhiteSpace(dsn))
            {
                Logger.Information("Sentry init skipped: no DSN configured (set VOICEWINK_SENTRY_DSN, or build a signed release with installer/.sentry-dsn present)");
                return false;
            }

            // Stage the handle in a local so that if ConfigureScope throws after Init
            // succeeded we can dispose the live SDK instead of orphaning it.
            IDisposable? newHandle = null;
            try
            {
                newHandle = SentrySdk.Init(o =>
                {
                    o.Dsn = dsn;
                    // Keeps device.name / user / request (cookies, headers, IP) OUT of every
                    // envelope at the SDK level. It does NOT cover free-text exception content, so
                    // the redaction belts below carry that load: BeforeSend folds C:\Users\<name>\
                    // paths + key shapes in message/tags/exception values AND stack-frame paths;
                    // BeforeBreadcrumb does the same per breadcrumb. Residual: the connection source
                    // IP is visible to Sentry's INGEST endpoint regardless of this flag — drop it via
                    // the project's "Prevent Storing of IP Addresses" setting (ops, not code). Do NOT
                    // flip this to true.
                    o.SendDefaultPii = false;
                    o.MaxBreadcrumbs = 50;
                    // Single-user desktop app: use ONE global scope instead of the default
                    // async-local forked scopes. Without this, events captured outside the
                    // Serilog pipeline (WinUI/AppDomain crash handlers, threadpool
                    // exceptions) arrive with an EMPTY scope — no breadcrumbs and none of
                    // the ConfigureScope tags below (observed on VOICEWINK-5: 29 crash
                    // events, zero breadcrumbs, missing app.arch — undiagnosable).
                    // Sentry's own docs recommend global mode for desktop apps.
                    o.IsGlobalModeEnabled = true;
                    // Bound the synchronous flush in Shutdown() (invoked on the data-erase path from
                    // the UI dispatcher) so an offline machine can't stall erase for the ~2s default.
                    o.ShutdownTimeout = global::System.TimeSpan.FromSeconds(1);
                    o.AutoSessionTracking = true;
                    o.Release = GetRelease();
                    o.TracesSampleRate = 0.0;
                    o.ProfilesSampleRate = 0.0;
                    o.SetBeforeSend(BeforeSendEvent);
                    // Scrub EVERY breadcrumb (not just Serilog-sourced ones, which the enricher already
                    // redacts) — direct SentrySdk.AddBreadcrumb calls bypass Serilog, and with the disk
                    // cache below those would otherwise persist unredacted. Honors the privacy policy's
                    // "redacted breadcrumbs" promise. (Codex review 2026-06-14.)
                    o.SetBeforeBreadcrumb(BeforeBreadcrumb);
                    // Offline cache (REL-1b): persist envelopes to disk so a crash/error captured while
                    // the machine is offline is delivered on the next online launch, instead of being
                    // dropped on the failed send. Those are exactly the field cases crash reporting is
                    // for. Events are redacted by BeforeSend BEFORE they are cached, so the on-disk
                    // queue never holds unredacted data.
                    o.CacheDirectoryPath = AppPaths.SentryCacheDir;
                });

                SentrySdk.ConfigureScope(scope =>
                {
                    scope.SetTag("os.version", Environment.OSVersion.VersionString);
                    scope.SetTag("app.arch", Environment.Is64BitProcess ? "x64" : "x86");
                    if (!string.IsNullOrEmpty(licenseStatus))
                        scope.SetTag("license.status", licenseStatus);
                    if (!string.IsNullOrEmpty(licenseKey))
                        scope.SetTag("license.hash", HashLicense(licenseKey));
                });

                _sdkHandle = newHandle;
                Logger.Information("Sentry initialized (release={Release})", GetRelease());
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Sentry init failed");
                try { newHandle?.Dispose(); }
                catch (Exception disposeEx) { Logger.Warning(disposeEx, "Sentry handle dispose failed during init rollback"); }
                return false;
            }
        }
    }

    /// <summary>
    /// Flush and shut down the SDK. Safe to call when uninitialized. Dispose runs outside
    /// the state lock so any internal SDK locking can't deadlock against TryInit's own
    /// acquisition of <see cref="_lock"/>.
    /// </summary>
    public static void Shutdown()
    {
        IDisposable? handle;
        lock (_lock)
        {
            handle = _sdkHandle;
            _sdkHandle = null;
        }
        if (handle == null) return;
        try { handle.Dispose(); }
        catch (Exception ex) { Logger.Warning(ex, "Sentry shutdown failed"); }
    }

    /// <summary>
    /// Internal for tests. Scrubs event message, tag values, <c>Extra</c> entries,
    /// <c>SentryException.Value</c> entries and both exception and thread stack frames, and
    /// clears <c>ServerName</c>. <b>Not</b> scrubbed, deliberately: <c>Contexts</c> — see the
    /// decision comment at that point in the method. Fails <b>closed</b>: if any step throws,
    /// the event is dropped (return null) rather than forwarded unredacted — direct
    /// <c>SentrySdk.CaptureException</c> calls bypass Serilog, so BeforeSend is the only
    /// scrub for those, and a leaked key is worse than a missed crash report.
    /// </summary>
    internal static SentryEvent? BeforeSendEvent(SentryEvent evt, SentryHint hint)
    {
        try
        {
            evt.ServerName = null;

            if (evt.Message != null)
            {
                evt.Message = new SentryMessage
                {
                    Message = Redact(evt.Message.Message),
                    Formatted = Redact(evt.Message.Formatted),
                };
            }

            foreach (var tagKey in evt.Tags.Keys.ToList())
            {
                var v = evt.Tags[tagKey];
                var redacted = Redact(v);
                if (!ReferenceEquals(redacted, v))
                    evt.SetTag(tagKey, redacted ?? string.Empty);
            }

            // REL-31: Extra is the payload channel a capture that BYPASSES Serilog would use, and
            // catching those captures is the whole reason this hook exists — yet breadcrumb Data
            // was scrubbed two-layer in BeforeBreadcrumb while event Extra was scrubbed nowhere.
            // Latent rather than live when this landed (no SetExtra call site existed, so every
            // Extra entry arrived from the Serilog sink already scrubbed by the enricher); this
            // closes the channel before something opens it.
            //
            // The two layers are spelled out here instead of calling RedactNamedValue, which would
            // read better but routes the value through LogRedactionEnricher.RedactString DIRECTLY,
            // bypassing the RedactOverride seam below — and then no test could prove this loop is
            // inside the fail-closed try (Codex plan round). The NAME layer is shared; only the
            // value call differs.
            //
            // KNOWN LIMIT: a non-string value is left alone, and that channel is LIVE, not
            // hypothetical. The first version of this comment claimed the gap was "arrived at
            // independently" of the enricher's {@}-destructuring limit because "a direct Extra
            // write never reaches the enricher" — which is wrong twice over, and the correction is
            // the useful part (self-review). Sentry.Serilog's sink unwraps a ScalarValue to its RAW
            // underlying object and passes any non-scalar LogEventPropertyValue through as an
            // object, so ordinary structured logging puts non-strings in Extra with no SetExtra
            // call anywhere: HotkeyService's hook-restart-limit Error logs an int, LicenseService's
            // activation-shape Error logs a JsonValueKind. So this is the ENRICHER'S limit
            // resurfacing on the same entries — LogRedactionEnricher.Enrich's value layer matches
            // only ScalarValue { Value: string } — and the two limits STACK rather than backstop
            // each other.
            //
            // What keeps that benign today is the NAME layer, which both the enricher and this loop
            // apply whatever the value's shape: a sensitive property name is dropped even when it
            // carries a collection. The exposure is a non-sensitive NAME carrying user data in a
            // collection or destructured object at Error level. No such site exists today (SEC-3/4
            // routed the user-data sites through string tokens), and the fix belongs in the
            // enricher rather than here, or the two halves diverge — tracked as REL-33.
            //
            // REOPEN THIS on either way a non-sensitive collection or destructured object can carry
            // user data past the string-only value branch: an Error-level structured-log argument
            // (the live channel, and the one the first version of this comment MISSED), or a direct
            // SentryEvent.SetExtra in a capture path (which the first version named as the ONLY
            // trigger — wrong as an exclusive claim, correct as one of two; Codex diff r1). Contexts
            // below additionally reopens on a Sentry SDK upgrade that changes what it populates.
            //
            // ALSO not covered, so it is not inferred from silence: Extra KEYS are never scrubbed
            // (the same posture as the tags loop and the breadcrumb Data scrub), and the string
            // layer itself is incomplete — see REL-32 for the Authorization-header hole.
            foreach (var extraKey in evt.Extra.Keys.ToList())
            {
                if (LogRedactionEnricher.IsSensitivePropertyName(extraKey))
                {
                    // Name layer: the key alone condemns the value, whatever its type — an int
                    // under "apikey" is still a secret, so this runs before the string test.
                    evt.SetExtra(extraKey, LogRedactionEnricher.RedactedMarker);
                    continue;
                }

                if (evt.Extra[extraKey] is string s)
                    evt.SetExtra(extraKey, Redact(s));
            }

            // Contexts is deliberately NOT scrubbed, and this comment is the decision rather than
            // the omission it would otherwise look like (REL-31). Three grounds: nothing in this
            // app writes Contexts — it is populated entirely by the SDK (device, os, runtime,
            // memory, thread-pool, culture, app, trace); SendDefaultPii=false already keeps
            // device.name / user / request out at SDK level; and blanket-scrubbing typed context
            // dictionaries would mangle exactly the structured diagnostics they exist for, for no
            // privacy gain — the "an over-broad token silently destroys a useful field" failure
            // mode .claude/rules/diagnostics.md records for capProcs= / appMode=. Reopen on the
            // trigger stated above.

            if (evt.SentryExceptions != null)
            {
                foreach (var sex in evt.SentryExceptions)
                {
                    sex.Value = Redact(sex.Value);
                    // Stack frames carry file paths independently of the Value scrub above, so a
                    // C:\Users\<name>\ path (or build-machine path) would otherwise survive here.
                    RedactFrames(sex.Stacktrace);
                }
            }

            // Thread stack traces carry the same frame paths and are attached independently of
            // exceptions (e.g. captured threads on a hang), so fold them too.
            if (evt.SentryThreads != null)
                foreach (var thread in evt.SentryThreads)
                    RedactFrames(thread.Stacktrace);

            return evt;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Sentry BeforeSend redaction threw; dropping event to prevent potential leak");
            return null;
        }
    }

    /// <summary>
    /// Folds user-profile / build-machine paths out of every stack frame's path-bearing fields.
    /// Shared by the exception and thread stack traces so neither surface can leak a C:\Users\&lt;name&gt;\ path.
    /// </summary>
    private static void RedactFrames(SentryStackTrace? stacktrace)
    {
        var frames = stacktrace?.Frames;
        if (frames == null) return;
        foreach (var frame in frames)
        {
            frame.AbsolutePath = Redact(frame.AbsolutePath);
            frame.FileName = Redact(frame.FileName);
            frame.Module = Redact(frame.Module);
            frame.Package = Redact(frame.Package);
        }
    }

    /// <summary>
    /// Internal for tests. Scrubs every breadcrumb's message + string data values before it is
    /// recorded — covers Serilog-sourced breadcrumbs AND direct <c>SentrySdk.AddBreadcrumb</c> calls
    /// that bypass the Serilog enricher. Load-bearing now that <see cref="SentryOptions.CacheDirectoryPath"/>
    /// persists envelopes (incl. breadcrumbs) to disk. Fails closed: on any error the breadcrumb is dropped.
    /// </summary>
    internal static Breadcrumb? BeforeBreadcrumb(Breadcrumb crumb, SentryHint hint)
    {
        try
        {
            IReadOnlyDictionary<string, string>? data = crumb.Data;
            if (data is { Count: > 0 })
            {
                var scrubbed = new Dictionary<string, string>(data.Count);
                foreach (var kv in data)
                    // Two layers like the enricher: a sensitive Data KEY (text/body/response/...)
                    // drops the whole value even when it matches no key shape; else value-scrub.
                    scrubbed[kv.Key] = LogRedactionEnricher.RedactNamedValue(kv.Key, kv.Value);
                data = scrubbed;
            }

            // NB: Sentry 6.4.1's public Breadcrumb ctor has no timestamp parameter (it's internal), so
            // the rebuilt crumb is stamped ~now. BeforeBreadcrumb runs at add-time, so the drift is
            // sub-millisecond and the trail order is preserved — not worth reflecting into the SDK.
            return new Breadcrumb(
                message: Redact(crumb.Message) ?? string.Empty,
                type: crumb.Type ?? "default",
                data: data,
                category: Redact(crumb.Category),
                level: crumb.Level);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Sentry BeforeBreadcrumb redaction threw; dropping breadcrumb");
            return null;
        }
    }

    /// <summary>
    /// Redaction hook, replaceable in tests so we can verify the fail-closed path
    /// (if redaction throws, BeforeSend must drop the event). Production callers always
    /// use <see cref="DefaultRedact"/>. Mutable field rather than a DI seam because
    /// the static SDK hook signature (<c>SetBeforeSend</c>) gives us no DI injection point.
    /// <para>NOT thread-safe. Tests that swap this field rely on VoiceWink.Tests
    /// disabling parallel execution via <c>AssemblyInfo.cs</c> — do not turn parallel
    /// execution on without first wrapping this seam in a lock.</para>
    /// </summary>
    internal static Func<string?, string?> RedactOverride = DefaultRedact;

    private static string? DefaultRedact(string? s) =>
        string.IsNullOrEmpty(s) ? s : LogRedactionEnricher.RedactString(s);

    private static string? Redact(string? s) => RedactOverride(s);

    private static string GetRelease()
    {
        var ver = typeof(SentryInitializer).Assembly.GetName().Version;
        return ver != null ? $"voicewink@{ver.Major}.{ver.Minor}.{ver.Build}" : "voicewink@unknown";
    }

    private static string HashLicense(string licenseKey)
    {
        var bytes = Encoding.UTF8.GetBytes(licenseKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }
}
