using System.Diagnostics;
using System.IO;
using VoiceWink.Helpers;
using VoiceWink.Services.Updates;

namespace VoiceWink.Services.System;

/// <summary>INS-2: removes the install root Velopack 0.0.1298's uninstall leaves behind (<c>.dead</c>, <c>Velopack.log</c>, <c>Update.exe</c>).</summary>
/// <remarks>shortcut: works around Velopack 0.0.1298, delete this class and its call in Program when TCH-4 (Velopack 1.x) lands.</remarks>
internal static class InstallRootJanitor
{
    // Polls because .dead appears only after the user closes Velopack's "uninstalled" dialog (INS-2).
    internal const int PollSeconds = 5;
    internal const int MaxPolls = 360;
    internal const int SettleSeconds = 10;

    private const string RootLeaf = @"\VoiceWinkApp";

    /// <summary>Production entry, called last from <c>OnBeforeUninstallFastCallback</c>.</summary>
    public static void Schedule() =>
        Schedule(AppContext.BaseDirectory, PrereqInstaller.LogPublic, psi =>
        {
            using var process = Process.Start(psi);
            return process is not null;
        });

    /// <summary>Spawns the detached cleanup for a Velopack install; never throws and logs no paths.</summary>
    internal static void Schedule(string? baseDirectory, Action<string> log, Func<ProcessStartInfo, bool> start)
    {
        string line;
        try
        {
            var root = InstallRoot.FromBaseDirectory(baseDirectory);
            var systemDir = Environment.SystemDirectory;
            var body = root is null ? null : BuildCommand(root, systemDir, PollSeconds, MaxPolls, SettleSeconds);
            if (body is null)
            {
                line = root is null
                    ? "INS-2: install-root removal not scheduled (not a Velopack install)"
                    : "INS-2: install-root removal not scheduled (unsafe path)";
            }
            else
            {
                // System32 as working directory: outside the root so rmdir can remove it, and no planted exe there (INS-2).
                var psi = new ProcessStartInfo(Path.Combine(systemDir, "cmd.exe"), "/D /E:ON /V:OFF /C " + body)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = systemDir,
                };
                line = start(psi)
                    ? "INS-2: install-root removal scheduled"
                    : "INS-2: install-root removal failed to start (no process)";
            }
        }
        catch (Exception ex)
        {
            line = $"INS-2: install-root removal failed to start ({ex.GetType().Name})";
        }

        try { log(line); }
        catch { /* logging must never fail the uninstall */ }
    }

    /// <summary>The cmd body, or null when the root is not one this janitor may remove.</summary>
    /// <remarks>Deletes only the three husk files, then a non-recursive rmdir, so nothing else in the root is ever removed (INS-2).</remarks>
    internal static string? BuildCommand(string root, string systemDir, int pollSeconds, int maxPolls, int settleSeconds)
    {
        if (!IsSafe(root) || !IsSafe(systemDir)) return null;
        if (!root.EndsWith(RootLeaf, StringComparison.OrdinalIgnoreCase)) return null;
        if (pollSeconds < 1 || maxPolls < 1 || settleSeconds < 1) return null;

        var q = "\"" + root;
        var ping = $"\"{systemDir.TrimEnd('\\')}\\PING.EXE\"";
        // PING as the delay: choice returns at once when stdin is closed (INS-2).
        // del empties a directory it is given, so a husk name that is a directory (or junction) stops the cleanup (INS-2).
        var guard = $"if exist {q}\\.dead\" if not exist {q}\\current\\\" if not exist {q}\\.dead\\\" "
            + $"if not exist {q}\\Update.exe\\\" if not exist {q}\\Velopack.log\\\"";
        return $"for /L %i in (1,1,{maxPolls}) do @({ping} -n {pollSeconds + 1} 127.0.0.1 >nul & {guard} "
            + $"({ping} -n {settleSeconds + 1} 127.0.0.1 >nul & {guard} "
            + $"(del /f /q /a {q}\\Update.exe\" {q}\\Velopack.log\" {q}\\.dead\" & rmdir {q}\" & exit)))";
    }

    // Quote and percent would break cmd's quoting or expand; refusing leaves the husk, the pre-INS-2 behaviour.
    private static bool IsSafe(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && path.IndexOfAny(['"', '%', '\r', '\n']) < 0;
}
