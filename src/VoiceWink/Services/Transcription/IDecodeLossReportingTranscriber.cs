namespace VoiceWink.Services.Transcription;

/// <summary>TRN-96b: a decode's text plus how many of its chunks came back empty on non-silent audio and were not recovered.</summary>
public readonly record struct TranscriptionDecode(string Text, int LostChunks);

/// <summary>TRN-96b: a transcriber that can say a decode lost part of the recording, so the pipeline need not report it as a plain success.</summary>
public interface IDecodeLossReportingTranscriber : ITranscriptionService
{
    /// <summary>The ordinary decode (<see cref="ITranscriptionService.TranscribeAsync"/>), with the loss count of the same call.</summary>
    Task<TranscriptionDecode> TranscribeReportingLossAsync(string audioFilePath, CancellationToken ct);
}
