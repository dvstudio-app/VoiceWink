namespace VoiceWink.Helpers;

/// <summary>
/// The Log Viewer's search: finds the lines that contain a phrase across EVERY daily application log
/// file, not only the part of today's file the page shows.
///
/// <para><b>Why every file.</b> The page shows the last 200 lines of the newest file, and a line worth
/// finding — a first-use check that ran yesterday, a GPU verdict written at the first start after an
/// update — is usually older than that. Serilog keeps 14 daily files (<c>voicewink-yyyyMMdd.log</c>,
/// plus <c>_NNN</c> size rolls); the prompt-trace files in the same folder are named differently and
/// are deliberately not searched — they are not the application log.</para>
///
/// <para>Pure apart from the file reads, so the matching, the ordering and the cap are pinned by
/// <c>LogSearchTests</c>; the page only renders what this returns.</para>
/// </summary>
internal static class LogSearch
{
    /// <summary>The application log files; the prompt-trace pair uses other names.</summary>
    internal const string FilePattern = "voicewink-*.log";

    /// <summary>How many matching lines the page shows — the newest ones. The count reports all.</summary>
    internal const int MaxShown = 500;

    /// <summary>The query to search for, or null for "no search": blank and whitespace-only mean off.</summary>
    internal static string? Normalize(string? query)
        => string.IsNullOrWhiteSpace(query) ? null : query.Trim();

    /// <summary>A line matches when it contains the query anywhere, ignoring case.</summary>
    internal static bool Matches(string line, string query)
        => line.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The line split into runs, each marked as a match or not — every occurrence of the query,
    /// ignoring case, so the page can emphasise them. Joined back, the runs are the line unchanged.
    /// </summary>
    internal static IReadOnlyList<(string Text, bool IsMatch)> Segments(string line, string query)
    {
        var segments = new List<(string, bool)>();
        var start = 0;
        while (start < line.Length)
        {
            var hit = line.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
                break;
            if (hit > start)
                segments.Add((line[start..hit], false));
            segments.Add((line.Substring(hit, query.Length), true));
            start = hit + query.Length;
        }
        if (start < line.Length)
            segments.Add((line[start..], false));
        return segments;
    }

    /// <summary>The log files in <paramref name="dir"/>, oldest first (the names sort by date, and a
    /// size roll <c>_001</c> sorts after its day's first file). Empty when the folder is absent.</summary>
    internal static IReadOnlyList<string> LogFilesOldestFirst(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir, FilePattern).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>Reads complete UTF-8 log lines and returns the byte offset after the last LF.
    /// An unfinished line is left behind the returned cursor for the next tail read.</summary>
    internal static long ReadCompleteLines(Stream stream, Action<string> accept, CancellationToken ct)
    {
        var position = stream.Position;
        var end = position;
        var firstLine = position == 0;
        var buffer = new byte[4096];
        using var pending = new MemoryStream();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0)
                return end;
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                pending.Write(buffer, start, i - start);
                var line = System.Text.Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
                if (firstLine)
                    line = line.TrimStart('\uFEFF');
                accept(line.TrimEnd('\r'));
                firstLine = false;
                pending.SetLength(0);
                start = i + 1;
                end = position + start;
            }
            pending.Write(buffer, start, count - start);
            position += count;
        }
    }

    /// <summary>What a search found. <see cref="Lines"/> are the newest <c>maxShown</c> matches in file
    /// order; <see cref="Total"/> counts every match. <see cref="LastFile"/> and
    /// <see cref="LastFileEnd"/> say how far the newest file was read, so the live tail can continue
    /// from there without showing a line twice.</summary>
    internal sealed record Result(
        IReadOnlyList<string> Lines, int Total, int Unreadable, string? LastFile, long LastFileEnd);

    /// <summary>
    /// Reads <paramref name="files"/> in order and keeps the matching lines. A file that cannot be
    /// read (deleted by the retention sweep mid-search, locked) is counted in
    /// <see cref="Result.Unreadable"/> and skipped — the others are still searched.
    /// Cancellation is checked between files and each read buffer.
    /// </summary>
    internal static Result Search(IReadOnlyList<string> files, string query, int maxShown, CancellationToken ct)
    {
        var kept = new Queue<string>();
        var total = 0;
        var unreadable = 0;
        string? lastFile = null;
        long lastEnd = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var end = ReadCompleteLines(fs, line =>
                {
                    if (!Matches(line, query))
                        return;
                    total++;
                    kept.Enqueue(line);
                    if (kept.Count > maxShown)
                        kept.Dequeue();
                }, ct);
                lastFile = file;
                lastEnd = end;
            }
            catch (IOException)
            {
                unreadable++;
            }
            catch (UnauthorizedAccessException)
            {
                unreadable++;
            }
        }
        return new Result(kept.ToArray(), total, unreadable, lastFile, lastEnd);
    }

    /// <summary>The one line under the search box.</summary>
    internal static string Summary(int total, int shown, int unreadable)
    {
        var text = total switch
        {
            0 => "No lines match.",
            1 => "1 line matches.",
            _ when shown < total => $"{total:N0} lines match — showing the newest {shown:N0}.",
            _ => $"{total:N0} lines match.",
        };
        return unreadable switch
        {
            0 => text,
            1 => text + " 1 log file could not be read.",
            _ => text + $" {unreadable} log files could not be read.",
        };
    }
}
