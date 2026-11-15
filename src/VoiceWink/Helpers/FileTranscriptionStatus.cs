using System.Globalization;

namespace VoiceWink.Helpers;

/// <summary>
/// TRN-72: the Audio Transcribe status line while a file is in work — the step, the share of the
/// audio decoded when the engine reports one, and the elapsed time ("Transcribing... 42% · 2:15").
/// </summary>
internal static class FileTranscriptionStatus
{
    /// <summary>The line for <paramref name="phase"/>; <paramref name="decodedFraction"/> is null
    /// when the engine reports no progress (every cloud provider).</summary>
    internal static string Format(string phase, TimeSpan elapsed, double? decodedFraction)
    {
        var time = Elapsed(elapsed);
        return decodedFraction is double fraction && !double.IsNaN(fraction)
            ? $"{phase} {Percent(fraction)}% · {time}"
            : $"{phase} {time}";
    }

    /// <summary>Whole percent, never 100 while the job still runs (the text pipeline and the
    /// clipboard come after the decode).</summary>
    internal static int Percent(double fraction)
        => double.IsNaN(fraction) ? 0 : (int)Math.Clamp(Math.Floor(fraction * 100), 0, 99);

    /// <summary>"0:07", "12:30", "1:02:05".</summary>
    internal static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Minutes}:{elapsed.Seconds:00}");
    }
}
