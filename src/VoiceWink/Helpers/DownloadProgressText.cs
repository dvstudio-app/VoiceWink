namespace VoiceWink.Helpers;

/// <summary>
/// How a model download's progress reads on the Models page, the recorder pill and the Downloads
/// card. All the
/// bytes in is not the end: the download is then checked and installed, which takes a while on a
/// large model, so the end reads "Verifying…" instead of a number that stops moving.
/// </summary>
/// <remarks>
/// The line is 99%, not 100%: a multi-file model (Parakeet, the built-in AI models) holds its
/// progress at 99% until it is checked and installed, and reports 100% only once it is in
/// (<c>ModelDownloadManager</c>'s bundle install; pinned there). The cost is that the last 1% of the
/// bytes also reads "Verifying…".
/// </remarks>
internal static class DownloadProgressText
{
    internal const string Verifying = "Verifying…";

    /// <summary>True from 99%, where a multi-file model's progress waits while it is checked.</summary>
    internal static bool IsVerifying(double fraction) => RoundedPercent(fraction) >= 99;

    /// <summary>"45%", or "Verifying…" from 99%.</summary>
    internal static string Percent(double fraction)
        => IsVerifying(fraction) ? Verifying : $"{RoundedPercent(fraction)}%";

    /// <summary>
    /// The Downloads card's line under a bar: "45% · 1.2 of 2.7 GB" (or "45% · 420 of 941 MB"), in
    /// the size's own unit; "45%" with no size; "Verifying…" from 99%. The downloaded part rounds
    /// down, like the percentage.
    /// </summary>
    internal static string Progress(double fraction, long? totalBytes)
    {
        if (IsVerifying(fraction))
            return Verifying;
        var percent = Percent(fraction);
        if (totalBytes is not { } total || total <= 0)
            return percent;

        var done = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1) * total;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return Math.Round(total / 1_000_000.0) >= 1000
            ? $"{percent} · {(Math.Floor(done / 100_000_000.0) / 10).ToString("0.0", inv)} of {Size(total)}"
            : $"{percent} · {Math.Floor(done / 1_000_000.0).ToString("0", inv)} of {Size(total)}";
    }

    /// <summary>
    /// A download size the way the Models page quotes it: decimal megabytes under a gigabyte
    /// ("941 MB"), one-decimal gigabytes above ("2.7 GB").
    /// </summary>
    internal static string Size(long bytes)
    {
        var megabytes = Math.Round(bytes / 1_000_000.0);
        return megabytes >= 1000
            ? (bytes / 1_000_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : megabytes.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }

    // Down, like a progress bar, so the number never says 100 before every byte is in; the small
    // epsilon keeps 0.29 (28.999… in binary) reading 29.
    private static int RoundedPercent(double fraction)
        => (int)Math.Floor(Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1) * 100 + 1e-9);
}
