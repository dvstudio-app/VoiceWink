namespace VoiceWink.Services.Transcription;

/// <summary>
/// REL-15 response lever (Batch A6): the single named flag deciding whether the Silero-VAD
/// no-speech gate runs. Enabled in every ordinary build; an incident build opts out with
/// <c>-p:VadEnabled=false</c> (see <c>Directory.Build.props</c>), after which
/// <see cref="VoiceActivityDetectionService"/> returns
/// <see cref="Helpers.NoSpeechVerdict.Unavailable"/> without touching the native path and the
/// caller falls back to the legacy whole-file RMS gate.
///
/// <para>Exists because PR #240's mitigation for the vcomp140/OpenMP access violation
/// (<c>VadTuning.DetectThreads = 1</c>) removes the SUSPECTED path without proving it — no
/// native stack was ever captured. If the crash recurs on a mitigated build, this is the
/// tested, already-wired way to ship without the Silero gate.
/// Pulled with <c>-p:VadEnabled=false</c>; a lever build is identifiable by the
/// <c>vad-disabled.marker</c> file, and at runtime by a <c>VAD disabled by build</c>
/// line with no <c>Silero VAD initialized:</c> beside it.</para>
///
/// <para>A <c>const</c> here, unlike
/// <see cref="VoiceWink.Services.AIEnhancement.ImageGenerationFeature"/>'s deliberate
/// <c>static readonly</c>: that flag is read directly in <c>if</c> bodies at call sites, where a
/// compile-time constant would make one branch provably unreachable (<c>CS0162</c>). This one is
/// never read at a call site — it is passed as a constructor argument at the composition root
/// (mirroring <see cref="VoiceWink.Services.Updates.UpdateCheckFeature"/> feeding
/// <c>UpdateService(featureEnabled:)</c>), which is warning-free AND lets the disabled behaviour
/// be tested in an ordinary default build. Keep it that way: reading this const inside
/// <c>VoiceActivityDetectionService</c> would reintroduce CS0162 and cost that coverage.</para>
/// </summary>
internal static class VadFeature
{
    public const bool IsEnabled =
#if VAD_DISABLED
        false;
#else
        true;
#endif
}
