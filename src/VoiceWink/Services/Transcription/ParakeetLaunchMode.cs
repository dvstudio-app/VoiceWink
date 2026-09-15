namespace VoiceWink.Services.Transcription;

/// <summary>
/// TRN-53/TRN-51: which compute device the `parakeet-server` child is asked to use, expressed as
/// the typed launch mode the launcher turns into a CHILD-SPECIFIC environment block — never a
/// process-global environment mutation (codex plan round, Blocker 2: a global variable cannot
/// express the per-child CPU retry, and it leaks into every other child the app ever spawns).
///
/// <para><see cref="Auto"/> lets upstream pick: the server auto-selects the first GPU device the
/// ggml registry reports, and with a loader present but no driver ICD it serves on CPU (measured
/// on both dev machines, byte-identical output to forced CPU). <see cref="Cpu"/> pins
/// <c>PARAKEET_DEVICE=cpu</c> — the GPU-acceleration toggle OFF, the loader-missing fast path,
/// and the once-per-session latch after an Auto child died before health.</para>
/// </summary>
public enum ParakeetLaunchMode
{
    Auto,
    Cpu,
}
