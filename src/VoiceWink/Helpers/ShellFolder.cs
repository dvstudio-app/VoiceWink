using System.Diagnostics;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Thin wrapper over the shell for the three "show me this on disk" gestures the app needs:
/// open a folder, open a folder with one file pre-selected, or open a shipped text file.
/// Centralizes the <see cref="Process"/> launch + fail-soft logging so callers don't hand-roll
/// <c>explorer.exe</c> invocations (previously duplicated in the Report-a-problem dialog).
/// </summary>
public static class ShellFolder
{
    private static ILogger Logger => Log.ForContext(typeof(ShellFolder));

    /// <summary>
    /// Opens <paramref name="path"/> in Explorer, creating it first so a folder that has never
    /// been written to (e.g. no image generated yet) still opens cleanly instead of erroring.
    /// </summary>
    /// <returns><c>true</c> if Explorer was launched, <c>false</c> if it could not be.</returns>
    public static bool Open(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to open folder {Path}", path);
            return false;
        }
    }

    /// <summary>Opens the parent folder of <paramref name="filePath"/> with that file selected.</summary>
    /// <returns><c>true</c> if Explorer was launched, <c>false</c> if it could not be.</returns>
    public static bool Reveal(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to reveal {Path} in Explorer", filePath);
            return false;
        }
    }

    /// <summary>
    /// Opens a shipped text/markdown file in Notepad. Notepad rather than the shell's default
    /// handler because the default for an unassociated <c>.md</c> is the "How do you want to open
    /// this file?" picker, and Notepad is on every default Windows install. When Notepad cannot be
    /// launched the file is revealed in Explorer instead (<see cref="Reveal"/>), so the reader
    /// still lands on it. A missing file is refused up front — Notepad would otherwise offer to
    /// create it.
    /// </summary>
    /// <returns><c>true</c> if Notepad or Explorer was launched, <c>false</c> if neither could be.</returns>
    public static bool OpenTextFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Logger.Warning("Text file to open does not exist: {Path}", filePath);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{filePath}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to open {Path} in Notepad; revealing it in Explorer instead", filePath);
            return Reveal(filePath);
        }
    }
}
