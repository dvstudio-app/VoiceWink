using System.Text.RegularExpressions;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>REL-19: <c>settings.json.optout</c>, one consent key per line, read OFF at the next launch until a durable apply removes it.</summary>
internal static class ConsentTombstone
{
    private static ILogger Logger => Log.ForContext(typeof(ConsentTombstone));

    /// <summary>The keys <see cref="DiagnosticsConsent.Apply"/> is called with; nothing else is ever vetoed.</summary>
    internal static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        AppDefaults.PromptTraceLoggingOptIn,
        AppDefaults.PromptTraceIncludeImagePrompts,
        AppDefaults.KeepRecordingsForDebug,
        AppDefaults.CrashReportingOptIn,
        AppDefaults.AutomaticUpdateCheckEnabled,
        AppDefaults.AutomaticUpdateInstallEnabled,
    };

    /// <summary>Only a missing file is Absent; a persistent read failure or a non-key line is Unreadable, which vetoes every key.</summary>
    internal enum State { Absent, Present, Unreadable }

    // A settings-key shape; an unknown key of this shape is a newer build's and is kept, not obeyed.
    private static readonly Regex KeyShape = new("^[A-Za-z][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);

    private static readonly object Gate = new();

    internal static string PathFor(string settingsFilePath) => settingsFilePath + ".optout";

    /// <summary>Every key-shaped line in the file, allowlisted or not, so a rewrite keeps a newer build's keys.</summary>
    internal static State Read(string settingsFilePath, out IReadOnlySet<string> keys)
    {
        keys = new HashSet<string>(StringComparer.Ordinal);
        string[] lines;
        try
        {
            lines = TransientFileRetry.Run(() => File.ReadAllLines(PathFor(settingsFilePath)), TransientFileRetry.DefaultBackoff);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return State.Absent;
        }
        catch
        {
            return State.Unreadable;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!KeyShape.IsMatch(line)) return State.Unreadable;
            set.Add(line);
        }
        keys = set;
        return State.Present;
    }

    /// <summary>The consent keys that must read OFF: none when absent, the listed ones when present, all of them when unreadable.</summary>
    internal static IReadOnlySet<string> Vetoed(string settingsFilePath) =>
        Read(settingsFilePath, out var keys) switch
        {
            State.Present => keys.Where(Keys.Contains).ToHashSet(StringComparer.Ordinal),
            State.Unreadable => Keys,
            _ => new HashSet<string>(StringComparer.Ordinal),
        };

    internal static bool IsVetoed(string settingsFilePath, string key) =>
        Keys.Contains(key) && Vetoed(settingsFilePath).Contains(key);

    /// <summary>Record <paramref name="key"/>; false when it could not be recorded (unreadable marker, IO failure, not a consent key).</summary>
    internal static bool TryAdd(string settingsFilePath, string key) => Update(settingsFilePath, key, add: true);

    /// <summary>Clear <paramref name="key"/>; true when it is no longer recorded, false for a non-consent key.</summary>
    internal static bool TryRemove(string settingsFilePath, string key) => Update(settingsFilePath, key, add: false);

    private static bool Update(string settingsFilePath, string key, bool add)
    {
        if (!Keys.Contains(key)) return false;
        lock (Gate)
        {
            if (Read(settingsFilePath, out var current) == State.Unreadable) return false;
            var next = new HashSet<string>(current, StringComparer.Ordinal);
            if (add ? !next.Add(key) : !next.Remove(key)) return true;
            return Write(settingsFilePath, next);
        }
    }

    private static bool Write(string settingsFilePath, HashSet<string> keys)
    {
        var path = PathFor(settingsFilePath);
        if (keys.Count == 0)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception e)
            {
                Logger.Warning("Consent opt-out record could not be cleared: {ErrorType}", e.GetType().Name);
                return false;
            }
        }

        // GUID tmp, like the settings snapshot; still matched by erasure's settings.json.*.tmp sweep.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(tmp, keys.OrderBy(k => k, StringComparer.Ordinal));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            Logger.Warning("Consent opt-out record could not be written: {ErrorType}", e.GetType().Name);
            return false;
        }
    }
}
