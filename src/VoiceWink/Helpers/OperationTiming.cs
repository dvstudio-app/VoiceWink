using System.Diagnostics;
using System.IO;
using Serilog;
using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// One timing line in the log for every transcription, every AI text enhancement and every image
/// generation (owner, 2026-10-01: "we should have timings logged for everything"). Before this,
/// the current Parakeet engine wrote no duration at all and a decode time had to be worked out by
/// subtracting timestamps of two unrelated lines; Whisper, the cloud providers and the AI step
/// were the same. Each line starts with "... timing:" so one Log Viewer search finds them all.
///
/// <para>The lines carry ids, durations, counts and the exception TYPE — never transcript,
/// prompt or reply text. <c>{Model}</c> passes through <c>ModelIdEnricher</c> like every other
/// model id the app logs.</para>
///
/// <para>The transcription line times the whole call the pipeline waits on, the NET-1 connect
/// retry included, because that is the wait the user sees. The enhancement line times the
/// provider call inside the text deadline, so a dictation's enhancement and a redo both get
/// one.</para>
/// </summary>
internal static class OperationTiming
{
    private static ILogger Logger => Log.ForContext(typeof(OperationTiming));

    /// <summary>Times a transcription and logs one line on every exit, then returns or rethrows
    /// unchanged.</summary>
    internal static async Task<string> TimeTranscriptionAsync(
        string? model, string? wavPath, Func<Task<string>> run, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var text = await run().ConfigureAwait(false);
            LogTranscription(model, wavPath, sw.Elapsed, null, ct);
            return text;
        }
        catch (Exception ex)
        {
            LogTranscription(model, wavPath, sw.Elapsed, ex, ct);
            throw;
        }
    }

    /// <summary>Times one AI text-enhancement provider call.</summary>
    internal static async Task<string> TimeEnhancementAsync(
        string provider, string? model, int inputChars, Func<Task<string>> run, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await run().ConfigureAwait(false);
            Logger.Information(
                "AI enhancement timing: {Provider:l} {Model} - {ElapsedMs} ms, {InChars} chars in, {OutChars} chars out, {Outcome:l}",
                provider, model, sw.ElapsedMilliseconds, inputChars, reply?.Length ?? 0, Describe(null, ct));
            return reply!;
        }
        catch (Exception ex)
        {
            Logger.Information(
                "AI enhancement timing: {Provider:l} {Model} - {ElapsedMs} ms, {InChars} chars in, {Outcome:l}",
                provider, model, sw.ElapsedMilliseconds, inputChars, Describe(ex, ct));
            throw;
        }
    }

    /// <summary>Times one image-generation provider call (one version of a batch).</summary>
    internal static async Task<T> TimeImageAsync<T>(
        string provider, string? model, Func<Task<T>> run, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await run().ConfigureAwait(false);
            Logger.Information("Image generation timing: {Provider:l} {Model} - {ElapsedMs} ms, {Outcome:l}",
                provider, model, sw.ElapsedMilliseconds, Describe(null, ct));
            return result;
        }
        catch (Exception ex)
        {
            Logger.Information("Image generation timing: {Provider:l} {Model} - {ElapsedMs} ms, {Outcome:l}",
                provider, model, sw.ElapsedMilliseconds, Describe(ex, ct));
            throw;
        }
    }

    private static void LogTranscription(string? model, string? wavPath, TimeSpan elapsed, Exception? ex, CancellationToken ct)
    {
        var engine = EngineLabel(PredefinedModels.RuntimeOf(model), SafeSnapshot());
        var outcome = Describe(ex, ct);
        var audioSeconds = wavPath is null ? null : TryReadWavSeconds(wavPath);
        if (audioSeconds is double seconds)
        {
            Logger.Information(
                "Transcription timing: {Model} ({Engine:l}) - {AudioSeconds:l}s of audio in {ElapsedMs} ms ({Speed:l}), {Outcome:l}",
                model, engine, seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                (long)elapsed.TotalMilliseconds, SpeedText(seconds, elapsed), outcome);
        }
        else
        {
            Logger.Information("Transcription timing: {Model} ({Engine:l}) - {ElapsedMs} ms, {Outcome:l}",
                model, engine, (long)elapsed.TotalMilliseconds, outcome);
        }
    }

    /// <summary>Which engine and compute a model ran on: "cloud" for anything that is not a local
    /// catalog row, otherwise the engine and where it RESOLVED (the live snapshot, read after the
    /// call — a Parakeet child's own device token, Whisper's loaded backend).</summary>
    internal static string EngineLabel(LocalRuntimeKind? runtime, Helpers.LocalComputeSnapshot snapshot)
    {
        if (runtime is not LocalRuntimeKind kind) return "cloud";
        var name = kind == LocalRuntimeKind.Parakeet ? "Parakeet" : "Whisper";
        return snapshot.For(kind) == LocalCompute.Gpu ? $"{name} on the GPU" : $"{name} on the processor";
    }

    /// <summary>"4.9x real time"; blank-safe for a zero-length call.</summary>
    internal static string SpeedText(double audioSeconds, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0 || audioSeconds <= 0) return "speed n/a";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{audioSeconds / elapsed.TotalSeconds:F1}x real time");
    }

    /// <summary>"ok", "cancelled", "timed out" or "failed (TypeName)" — the type only, never the
    /// message, which can carry provider text. A cancellation the CALLER did not ask for is a
    /// deadline (the enhancement deadline, an HttpClient timeout), the rule
    /// <c>AIEnhancementService</c> applies when it converts one.</summary>
    internal static string Describe(Exception? ex, CancellationToken ct) => ex switch
    {
        null => "ok",
        OperationCanceledException when ct.IsCancellationRequested => "cancelled",
        OperationCanceledException or TimeoutException => "timed out",
        _ => $"failed ({ex.GetType().Name})",
    };

    private static Helpers.LocalComputeSnapshot SafeSnapshot()
    {
        try { return Helpers.LocalComputeSnapshot.Current; }
        catch { return Helpers.LocalComputeSnapshot.AllCpu; }
    }

    /// <summary>Audio length from a PCM WAV header (byte rate and data size), or null when the
    /// file cannot be read or is not a WAV this can measure. Reads at most the first 64 KB.</summary>
    internal static double? TryReadWavSeconds(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
            stream.ReadExactly(buffer);
            return WavSecondsFromHeader(buffer, stream.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static double? WavSecondsFromHeader(ReadOnlySpan<byte> header, long fileLength)
    {
        if (header.Length < 12 || !header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
            return null;

        long byteRate = 0;
        long offset = 12;
        while (offset + 8 <= header.Length)
        {
            var id = header.Slice((int)offset, 4);
            long size = BitConverter.ToUInt32(header.Slice((int)offset + 4, 4));
            long body = offset + 8;

            if (id.SequenceEqual("fmt "u8) && size >= 12 && body + 12 <= header.Length)
                byteRate = BitConverter.ToUInt32(header.Slice((int)body + 8, 4));
            else if (id.SequenceEqual("data"u8))
            {
                if (byteRate <= 0) return null;
                var dataBytes = Math.Min(size, Math.Max(0, fileLength - body));
                return dataBytes / (double)byteRate;
            }

            offset = body + size + (size & 1);
        }
        return null;
    }
}
