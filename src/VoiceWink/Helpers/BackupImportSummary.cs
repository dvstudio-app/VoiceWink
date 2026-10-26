using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// What the import dialog says after a backup import — one place for both pages, counts only
/// (never a word from the file).
/// </summary>
public static class BackupImportSummary
{
    public static string Describe(ImportExportService.ImportResult result, BackupParts parts)
    {
        var lines = new List<string>();
        if (parts.HasFlag(BackupParts.Settings))
            lines.Add($"{result.AppliedCount} setting(s) applied. Restart VoiceWink for all changes to take effect.");

        if (result.DictionaryFailed)
            lines.Add("The Dictionary couldn't be imported. Nothing was added to it.");
        else if (result.Dictionary is { } d)
            lines.Add($"Dictionary: {d.WordsAdded} word(s) and {d.RulesAdded} replacement(s) added, {d.AlreadyPresent} already here.");

        if (result.DictionarySkipped > 0)
            lines.Add($"{result.DictionarySkipped} damaged Dictionary entr{(result.DictionarySkipped == 1 ? "y was" : "ies were")} skipped.");

        return string.Join("\n\n", lines);
    }
}
