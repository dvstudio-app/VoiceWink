namespace VoiceWink.Services.Transcription;

/// <summary>
/// TRN-72: a transcriber that can say how much of the file it has decoded — the local engines,
/// whose decode runs in steps over the audio. Cloud clients send one request and get no such
/// figure, so they do not implement it and the Audio Transcribe page shows elapsed time only.
/// </summary>
public interface IProgressReportingTranscriber : ITranscriptionService
{
    /// <summary>Same contract as <see cref="ITranscriptionService.TranscribeAsync"/>, plus
    /// <paramref name="progress"/>: the share of the audio decoded so far, 0..1, rising — back to 0 when the engine restarts the decode.</summary>
    Task<string> TranscribeAsync(string audioFilePath, string? language, Models.TranscriptionHints? hints,
        bool diarize, IProgress<double> progress, CancellationToken ct);
}
