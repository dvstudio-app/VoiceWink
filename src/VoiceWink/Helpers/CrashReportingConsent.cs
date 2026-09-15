using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// The ONE apply for the crash-reporting consent (the wizard's step and the Settings toggle,
/// launch defaults audit 2026-09-13): persist the choice DURABLY, then bring the Sentry SDK and
/// the logger's sink set in line with the value actually in effect — in this session, not the
/// next one.
/// </summary>
/// <remarks>
/// <para>Before this existed each site wrote the flag with a debounced <c>SetBool</c> and
/// started or stopped the SDK, and the Serilog → Sentry sink stayed wherever startup had left it:
/// the sink is attached only when <see cref="SentryInitializer.IsInitialized"/> was true at the
/// moment the logger was BUILT, so a user who opted in kept a logger with no Sentry sink until a
/// restart (the Settings row said "Takes effect after a restart." for exactly this), and a user
/// who opted out kept a logger still carrying one. Consent and behaviour disagreed in both
/// directions for the rest of the session.</para>
///
/// <para>The write goes through <see cref="DiagnosticsConsent.Apply"/> — the same durable,
/// fail-OFF rule the other three consent toggles take: a choice that could not be persisted is
/// OFF in memory, so the SDK is never started on a consent the next launch will not find
/// (<c>CrashOptInReader</c> reads the file, not the cache).</para>
///
/// <para><b>The logger is rebuilt only when the sink set it would contain CHANGED</b> — when
/// the SDK went from stopped to live or back. A rebuild closes and reopens every sink and drops
/// whatever is logged in that window, so it is not done for an opt-in that could not start the
/// SDK (no DSN in a dev or CI build) or for a repeated click on the same value. The rebuild
/// itself is injected (<c>App.ReattachLogSinks</c> in production) because it touches the real
/// <c>%LOCALAPPDATA%\VoiceWink\Logs</c> — a test never passes the real one.</para>
/// </remarks>
internal static class CrashReportingConsent
{
    /// <summary>
    /// Persist <paramref name="requested"/> durably, start or stop the SDK to match the
    /// EFFECTIVE value, and rebuild the log sinks when the SDK's liveness changed. Returns the
    /// consent result (effective value + whether it is durable) for the caller's own reporting.
    /// </summary>
    public static DiagnosticsConsent.ConsentApplyResult Apply(SettingsService settings, bool requested, Action reattachLogSinks)
    {
        var result = DiagnosticsConsent.Apply(settings, AppDefaults.CrashReportingOptIn, requested);
        ApplyRuntime(result.Effective, reattachLogSinks);
        return result;
    }

    /// <summary>The runtime half: SDK to match <paramref name="effective"/>, sinks rebuilt on a liveness change.</summary>
    internal static void ApplyRuntime(bool effective, Action reattachLogSinks)
        => ApplyRuntime(effective, reattachLogSinks, () => SentryInitializer.IsInitialized, DriveSdk);

    /// <summary>
    /// The decision with its two SDK reads injected, so the liveness-change rule and its
    /// ORDER (read → drive → read → rebuild) are testable in a host where the real SDK can never
    /// go live — a rebuild placed before the drive would compare equal readings and never fire.
    /// </summary>
    internal static void ApplyRuntime(bool effective, Action reattachLogSinks, Func<bool> isLive, Action<bool> driveSdk)
    {
        var wasLive = isLive();
        driveSdk(effective);
        if (isLive() != wasLive)
            reattachLogSinks();
    }

    private static void DriveSdk(bool effective)
    {
        if (effective)
            SentryInitializer.TryInit(true);
        else
            SentryInitializer.Shutdown();
    }
}
