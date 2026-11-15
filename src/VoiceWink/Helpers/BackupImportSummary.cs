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
        {
            // SET-3: the import applies at once, so no restart sentence; the refused count says what the file held that was not applied.
            var applied = $"{result.AppliedCount} setting{(result.AppliedCount == 1 ? "" : "s")} applied";
            lines.Add(result.SettingsRefused > 0 ? $"{applied}, {result.SettingsRefused} refused." : $"{applied}.");
        }

        if (result.DictionaryFailed)
            lines.Add("The Dictionary couldn't be imported. Nothing was added to it.");
        else if (result.Dictionary is { } d)
            lines.Add($"Dictionary: {d.WordsAdded} word(s) and {d.RulesAdded} replacement(s) added, {d.AlreadyPresent} already here.");

        if (result.DictionarySkipped > 0)
            lines.Add($"{result.DictionarySkipped} damaged Dictionary entr{(result.DictionarySkipped == 1 ? "y was" : "ies were")} skipped.");

        return string.Join("\n\n", lines);
    }

    /// <summary>SET-3: no setting applied and no Dictionary merged — e.g. a JSON file that is not a backup, whose every key is refused.</summary>
    public static bool ImportedNothing(ImportExportService.ImportResult result) =>
        result.AppliedCount == 0 && result.Dictionary is null;
}
