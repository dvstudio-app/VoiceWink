using Serilog;
using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// UI-11: the ONE place the GPU-acceleration preference is read and written at runtime. Flipping it
/// is one operation with three parts — persist, log the flip, re-arm the TRN-50 self-test — and
/// this class is what keeps them together.
///
/// <para><b>Why it exists.</b> Until UI-11 the toggle had exactly one writer,
/// <c>SettingsViewModel</c>'s generated change handler, and the three parts lived in its body. The
/// move to the Models page added a SECOND writer and carried only the persist across, which killed
/// the re-arm outright: <c>GpuWarmup.RearmSelfTest</c> had one caller in the whole app, the row's
/// own amber advisory says "turn this switch off and back on" to re-test, and that instruction
/// would have become inert on the only surface that renders it. An operation with side effects
/// belongs in one callable, not re-implemented per call site.</para>
///
/// <para><b>Change is detected against the FILE, never a cached field.</b> This is the second half
/// of the same defect. <c>SettingsViewModel</c> held the value in an <c>[ObservableProperty]</c>
/// backing field seeded once in its constructor, and the generated setter short-circuits on an
/// equal value — so with a second writer in play, "Reset all settings to defaults" compared the
/// default against a stale cache, matched, and silently wrote nothing. Reading the current value
/// here means there is no cache to go stale (the same reasoning
/// <c>OnboardingPage</c> records for <c>LaunchAtLogin</c>).</para>
///
/// <para><b>Nothing applies live, by construction.</b> The whisper.cpp load order is process-wide
/// and frozen at the first decode and the Parakeet child's device rides its launch environment, so
/// a flip reaches decoding only at the next start. After an INTERACTIVE flip on the Models page
/// the TRN-59 restart dialog says so and offers the restart; UI-12 removed the standing sentence
/// from the row, so the reset path and a failed dialog are deliberately silent. The log line is what
/// gives a support bundle the flip timestamp.</para>
///
/// <para>Pre-boot reads are NOT this class's job: <see cref="GpuAccelerationReader"/> owns those,
/// because its callers run before any <c>SettingsService</c> exists.</para>
/// </summary>
internal static class GpuAccelerationPreference
{
    private static ILogger Logger => Log.ForContext(typeof(GpuAccelerationPreference));

    /// <summary>The stored preference, defaulted. Live — callers re-read rather than cache.</summary>
    public static bool Read(SettingsService settings)
        => settings.GetBoolDefaulted(AppDefaults.GpuAccelerationEnabled);

    /// <summary>
    /// Persist a flip and run its side effects. A write of the value already stored is a no-op in
    /// all three parts: it must not re-arm, because re-arming discards a legitimately recorded
    /// self-test verdict and costs a fresh warm-up child at the next start. That matches the
    /// behaviour of the generated setter this replaced, which only ran on an actual change.
    /// </summary>
    /// <param name="rearm">The self-test re-arm, defaulting to the real one. A parameter rather
    /// than settable static state so a test can observe WHEN the re-arm fires without configuring
    /// the process-wide <c>GpuWarmup.Instance</c> — which every other test relies on staying
    /// unconfigured and inert. No production call site passes it.</param>
    public static void Write(SettingsService settings, bool value, Action? rearm = null)
    {
        if (Read(settings) == value) return;

        settings.SetBool(AppDefaults.GpuAccelerationEnabled, value);
        Logger.Information("GPU acceleration {State} (applies at next app start)", value ? "enabled" : "disabled");
        // TRN-50: a flip in EITHER direction re-arms the GPU self-test — verdicts, adapter names
        // and warmed flags, one write — so the next start tests the GPU again instead of trusting
        // a verdict recorded for a driver the user may since have changed. No-op when the warm-up
        // was never configured (tests).
        if (rearm is not null) rearm();
        else Services.Transcription.GpuWarmup.Instance.RearmSelfTest();
    }
}
