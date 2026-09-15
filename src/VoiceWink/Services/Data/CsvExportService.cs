using System.Globalization;
using System.Text;
using Serilog;
using VoiceWink.Models.Entities;

namespace VoiceWink.Services.Data;

/// <summary>
/// Export transcription history to CSV.
/// </summary>
public sealed class CsvExportService
{
    private static ILogger Logger => Log.ForContext<CsvExportService>();

    public string ExportToCsv(IEnumerable<TranscriptionRecord> transcriptions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Timestamp,Duration(s),TranscriptionModel,EnhancementModel,Language,Text,EnhancedText,WasEnhanced");

        foreach (var t in transcriptions)
        {
            sb.AppendLine(string.Join(",",
                t.Id.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(t.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                // InvariantCulture: a comma-decimal culture (nl-BE, fr-BE) would otherwise render
                // "12,3" and split the numeric field across two CSV columns, shifting every column
                // after it. Applies to both the History export and the GDPR history.csv (shared code).
                t.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture),
                EscapeCsv(t.ModelName),
                EscapeCsv(t.EnhancementModelName ?? ""),
                EscapeCsv(t.Language ?? ""),
                EscapeCsv(t.Text),
                EscapeCsv(t.EnhancedText ?? ""),
                t.WasEnhanced));
        }

        return sb.ToString();
    }

    public async Task ExportToFileAsync(IEnumerable<TranscriptionRecord> transcriptions, string filePath)
    {
        var csv = ExportToCsv(transcriptions);
        await File.WriteAllTextAsync(filePath, csv, Encoding.UTF8).ConfigureAwait(false);
        // SEC-4: user-chosen destination — see LogPathProjection.
        Logger.Information("Exported transcriptions: filePath={UserFilePath}", Helpers.LogPathProjection.FileNameOnly(filePath));
    }

    private static string EscapeCsv(string value)
    {
        // Neutralize formula injection for spreadsheet applications.
        // A leading =, +, -, or @ (possibly after whitespace) is interpreted as a formula
        // by Excel/Sheets. Prepending a single quote forces text interpretation.
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}
