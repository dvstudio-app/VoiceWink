namespace VoiceWink.Helpers;

/// <summary>
/// AUD-8 lever: the single named flag deciding whether the capture path's anti-alias low-pass runs.
/// Enabled in every ordinary build; a build opts out with <c>-p:AntiAliasEnabled=false</c> (see
/// <c>Directory.Build.props</c>), after which <c>AudioRecorderService</c> takes the pre-AUD-8
/// resample path verbatim — <see cref="AudioResampler.ResampleInto"/>, per-buffer, unfiltered,
/// including its remainder-drop behaviour at an indivisible buffer size. A kill switch that restores
/// today's bytes, not a variant of the new path.
///
/// <para><b>This lever is deliberately NARROWER than <c>VadFeature</c>'s, and the asymmetry is
/// recorded here rather than left to look like an omission.</b> The VAD lever is wired end to end
/// through <c>ci.yml</c>, <c>release.yml</c>, <c>release-update.ps1</c>, a publish marker and a
/// runbook, because REL-15 guards a native access violation in <c>vcomp140</c> whose root cause was
/// never captured — there is no fix to ship, so SHIPPING THE DISABLED BUILD *is* the mitigation.
/// This is managed C# DSP with full unit coverage: if it regresses, the mitigation is reverting the
/// commit, which is faster and safer than cutting a lever release. So the scope here is a build-time
/// A/B and diagnostic lever, and the release path is intentionally absent. Adding four release
/// surfaces that must stay correct forever, to serve an incident that cannot arise, would be the
/// worse trade.</para>
///
/// <para>A <c>const</c>, following <see cref="VoiceWink.Services.Transcription.VadFeature"/>: it is
/// read ONLY in <c>AudioRecorderService</c>'s public parameterless constructor (the one DI calls)
/// and passed to the internal constructor, never at a call site — a compile-time constant in an
/// <c>if</c> body would make one branch provably unreachable (<c>CS0162</c>), and constructor
/// injection is also what lets the disabled behaviour be tested in an ordinary default build.</para>
/// </summary>
internal static class CaptureFilterFeature
{
    public const bool IsEnabled =
#if ANTIALIAS_DISABLED
        false;
#else
        true;
#endif
}
