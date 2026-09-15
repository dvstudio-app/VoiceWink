using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AppMode;

/// <summary>
/// Foreground window detection.
/// Uses P/Invoke GetForegroundWindow + GetWindowThreadProcessId.
/// </summary>
public sealed class ActiveWindowService : IActiveWindowService
{
    private static ILogger Logger => Log.ForContext<ActiveWindowService>();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Dictionary<string, Regex> PatternRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Get the process name of the currently focused window.
    /// </summary>
    public string? GetActiveProcessName()
    {
        try
        {
            var hwnd = NativeInterop.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            NativeInterop.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            return GetProcessNameById((int)pid);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to get active window process");
            return null;
        }
    }

    /// <summary>
    /// Slow path extracted from <see cref="GetActiveProcessName"/>. Wrap this
    /// in <see cref="Helpers.BoundedComCall.RunBoundedAsync{T}"/> when calling
    /// from the UI thread — under heavy CPU contention .NET's
    /// <c>Process.GetProcessById</c> has been observed taking multiple
    /// seconds (a 2.25 s spike was logged on 2026-05-21 with Excel + OBS +
    /// Teams active). The PID must be captured by the caller at recording-
    /// start time from the target window; re-reading <c>GetForegroundWindow</c>
    /// on a worker would race with focus changes and match the wrong process.
    /// </summary>
    public string? GetProcessNameById(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException ex)
        {
            // Process exited between PID capture and lookup (PID race).
            Logger.Warning(ex, "Active window process exited before lookup (pid={Pid})", pid);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to get process name (pid={Pid})", pid);
            return null;
        }
    }

    /// <summary>
    /// Check if a process name matches any of the given patterns.
    /// Patterns can use * wildcards.
    /// </summary>
    public static bool MatchesPatterns(string processName, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            if (pattern.Contains('*'))
            {
                var regex = GetOrCreatePatternRegex(pattern);
                if (regex.IsMatch(processName))
                    return true;
            }
            else if (processName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static Regex GetOrCreatePatternRegex(string pattern)
    {
        lock (PatternRegexCache)
        {
            if (PatternRegexCache.TryGetValue(pattern, out var regex))
            {
                return regex;
            }

            regex = new Regex(
                "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                RegexTimeout);
            PatternRegexCache[pattern] = regex;
            return regex;
        }
    }
}
