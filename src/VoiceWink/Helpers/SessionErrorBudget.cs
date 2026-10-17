using Serilog.Events;

namespace VoiceWink.Helpers;

/// <summary>
/// The level for a failure whose CAUSE Sentry must see, bounded to one event per cause per run:
/// <see cref="LogEventLevel.Error"/> — which the Sentry sink forwards — the first time a key is seen
/// in this process, <see cref="LogEventLevel.Warning"/> on every repeat.
///
/// <para>Exists because the dictation path logged several of its failure causes only at Warning or
/// Information (2026-10-17, owner: "proper debugging information … so that we can make sure
/// VoiceWink runs on as many PCs as possible"): those reach Sentry, if at all, as one of the 50
/// breadcrumbs an unrelated Error happens to carry, and a cause logged at startup has scrolled out by
/// the time anything fails. Raising them to a plain Error would file one event per dictation on the
/// affected PC — the quota class <c>diagnostics.md</c> forbids. One per key per run is the
/// per-machine signal without the per-press cost.</para>
///
/// <para>Keys are fixed text plus an exception TYPE name, an enum name or a number — never a
/// message, a path or anything a user wrote. This type decides only the level; each site keeps its
/// own message template. Environmental classes (network, provider answers, a user's cancel) never
/// come here: they stay Warning.</para>
/// </summary>
internal static class SessionErrorBudget
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    /// <summary>Error the first time <paramref name="key"/> is seen in this process while the
    /// Sentry sink is attached, Warning after. While it is not attached the answer is Error and
    /// nothing is recorded, so the first occurrence after an opt-in still reaches Sentry
    /// (<see cref="SentrySinkState"/>).</summary>
    public static LogEventLevel LevelFor(string key)
    {
        lock (Gate)
        {
            if (!SentrySinkState.Attached)
                return Seen.Contains(key) ? LogEventLevel.Warning : LogEventLevel.Error;
            return Seen.Add(key) ? LogEventLevel.Error : LogEventLevel.Warning;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate) Seen.Clear();
        SentrySinkState.Attached = true;
    }
}
