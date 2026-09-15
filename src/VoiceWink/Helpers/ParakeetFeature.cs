namespace VoiceWink.Helpers;

/// <summary>
/// Build-time on/off for the sherpa-onnx engine (TRN-1 step 3), mirroring <c>VadFeature</c>.
///
/// <para><b>Why a second native runtime gets a lever.</b> REL-15 is the standing lesson: a native
/// dependency that works on every dev machine can still fault on a user's — there, a vcomp140/OpenMP
/// access violation that no local test reproduced. The response was a build flag that ships the
/// degraded-but-working path, and the same escape hatch belongs here before the first Parakeet build
/// reaches a fleet.</para>
///
/// <para><b>Read ONLY at the composition root</b>, exactly like <c>VadFeature.IsEnabled</c> and for
/// the same reason: a <c>const</c> consumed inside an <c>if</c> makes the method tail unreachable
/// (CS0162), and it also makes the disabled behaviour untestable in an ordinary build. Injected as a
/// constructor flag instead, so a default build can exercise both states.</para>
/// </summary>
internal static class ParakeetFeature
{
#if PARAKEET_DISABLED
    internal const bool IsEnabled = false;
#else
    internal const bool IsEnabled = true;
#endif
}
