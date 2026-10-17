using VoiceWink.Services.Transcription;

namespace VoiceWink.Helpers;

/// <summary>
/// What the pill says when the Parakeet engine cannot run, one sentence per cause.
///
/// <para>Until 2026-10-17 every cause read "That transcription model can't run on this version of
/// VoiceWink", which is true only of the build lever — the field case behind this type
/// (VOICEWINK-J/K/M) was an engine process dying on one PC under a build that runs everywhere else,
/// where "this version" sends the user looking for an update that changes nothing. Each sentence fits
/// the pill (<c>MainViewModel.DefaultPillMaxCodeUnits</c>, pinned by test) and names the way out and
/// where it is. "Pick one on Models" is advice where the engine cannot run here at all; the app does
/// not pick one for the user (owner decisions 2026-08-02 and TRN-10d 2026-08-15: no silent
/// substitution). The same sentence follows a fresh download: the download is kept, and the pill has
/// no room to say so beside the way out.</para>
/// </summary>
internal static class EngineUnavailableMessage
{
    public static string For(ParakeetUnavailableCause? cause) => cause switch
    {
        ParakeetUnavailableCause.BuildLever => "Parakeet isn't in this build — pick one on Models",
        ParakeetUnavailableCause.CpuUnsupported => "Parakeet can't use this processor — pick one on Models",
        ParakeetUnavailableCause.NativeLoadFailed or ParakeetUnavailableCause.EngineFailedToStart
            => "Parakeet couldn't start on this PC — pick one on Models",
        ParakeetUnavailableCause.EngineFileMissing => "Parakeet's engine file is missing — reinstall VoiceWink",
        ParakeetUnavailableCause.EngineFileDamaged => "Parakeet's engine file is damaged — reinstall VoiceWink",
        ParakeetUnavailableCause.EngineStoppedForSession => "Parakeet stopped working — restart VoiceWink",
        ParakeetUnavailableCause.StartingUp => "Parakeet is still starting — try again in a moment",
        ParakeetUnavailableCause.VerificationPending => "Parakeet is still checking its model — try again soon",
        ParakeetUnavailableCause.ModelMissing => "Parakeet's model is missing — download it on Models",
        _ => "This model can't run on this PC — pick one on Models",
    };
}
