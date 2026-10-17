using Serilog;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// The model downloads running right now, for the surfaces that are not the download's own page:
/// the Downloads card (Home, and the speech step of custom setup), and the recorder pill when a
/// dictation waits for a download someone else started (Express setup starts both models in the
/// background, and a recording made before the speech model is in waits on the download manager's
/// per-model lock, which reports no progress).
/// </summary>
/// <remarks>
/// A download is a <see cref="Handle"/>: <see cref="Begin"/> registers it, <see cref="Handle.Report"/>
/// moves it and <see cref="Handle.Dispose"/> ends it. A report that arrives after the end is ignored,
/// because <c>Progress&lt;T&gt;</c> posts its callbacks, so the last one can land after the download
/// returned — without the check it would put a finished download back on Home for good.
/// <para>Several callers can download the same model at once (a recording, the Models page,
/// Express setup); the download manager lets one through and the others wait on its per-model lock,
/// reporting nothing. They share ONE entry per model, which ends when the last of them does — so a
/// caller that starts waiting never resets the entry the downloading caller keeps moving (Kimi diff
/// r1). <see cref="Cancel"/> asks every caller that gave a way to stop to stop.</para>
/// <para>A download the user cancelled can stay listed as STOPPED, with a way to start it again,
/// while its owner still wants it (<see cref="MarkStopped"/>: the selected speech model — dictation
/// needs it). It is listed only while nothing downloads that model, and dropped once its owner no
/// longer wants it.</para>
/// One instance per process (<see cref="Instance"/>), the <c>GpuWarmup.Instance</c> shape: the
/// reporters (the recording path, the Models page's view model, the built-in AI model store) and the
/// readers (the Downloads card, the recording path) are constructed in different places, and none
/// owns the others.
/// </remarks>
public sealed class BackgroundDownloads
{
    private static ILogger Logger => Log.ForContext<BackgroundDownloads>();

    public static BackgroundDownloads Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<Entry> _running = [];
    private readonly List<StoppedDownload> _stopped = [];
    private readonly HashSet<string> _cancelledByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, Action<double> OnProgress)> _listeners = [];

    /// <summary>Raised after a download starts, moves, ends or stops — on the reporting thread.</summary>
    public event Action? Changed;

    internal BackgroundDownloads()
    {
    }

    /// <summary>
    /// Registers a caller downloading <paramref name="key"/> (the model's catalog name).
    /// <paramref name="totalBytes"/> is the model's size, for the "1.2 of 2.7 GB" line;
    /// <paramref name="cancel"/> stops this caller's download. <paramref name="userStarted"/> marks a
    /// download someone asked for (a Download button, setup): it clears an earlier Cancel of that
    /// model (<see cref="WasCancelledByUser"/>).
    /// </summary>
    internal Handle Begin(string key, string displayName, long? totalBytes = null, Action? cancel = null,
        bool userStarted = false)
    {
        Handle handle;
        lock (_gate)
        {
            if (userStarted)
                _cancelledByUser.Remove(key);
            _stopped.RemoveAll(s => SameKey(s.Key, key));
            var entry = Find(key);
            if (entry is null)
            {
                entry = new Entry(key, displayName, Environment.TickCount64);
                _running.Add(entry);
            }
            entry.TotalBytes ??= totalBytes is > 0 ? totalBytes : null;
            handle = new Handle(this, entry, cancel);
            entry.Handles.Add(handle);
        }
        RaiseChanged();
        return handle;
    }

    /// <summary>
    /// The downloads to show, running ones first (oldest first), then the stopped ones still wanted
    /// and not running again. A stopped one no longer wanted is dropped here.
    /// </summary>
    internal IReadOnlyList<DownloadRow> Snapshot()
    {
        List<DownloadRow> rows;
        List<StoppedDownload> stopped;
        lock (_gate)
        {
            rows = _running
                .Select(e => new DownloadRow(e.Key, e.DisplayName, e.Fraction, e.TotalBytes,
                    e.Handles.Any(h => h.CancelAction is not null), Stopped: false, e.StartedTicks, Note: null))
                .ToList();
            stopped = _stopped.Where(s => Find(s.Key) is null).ToList();
        }

        // The owner's "still wanted" reads its own state (the selected model), so it runs outside
        // the lock.
        var unwanted = new List<StoppedDownload>();
        foreach (var s in stopped)
        {
            if (StillWanted(s))
                rows.Add(new DownloadRow(s.Key, s.DisplayName, 0, s.TotalBytes, CanCancel: false, Stopped: true, StartedTicks: 0, s.Note));
            else
                unwanted.Add(s);
        }
        if (unwanted.Count > 0)
        {
            lock (_gate)
            {
                _stopped.RemoveAll(unwanted.Contains);
            }
        }
        return rows;
    }

    /// <summary>
    /// Asks every caller downloading <paramref name="key"/> that gave a way to stop, to stop, and
    /// remembers the Cancel for this session: a dictation that needs the model does not start its
    /// download again by itself until someone asks for it (Codex diff r1 — a dictation waiting on the
    /// model's install lock, or its stop-side retry, would otherwise fetch it again from zero).
    /// </summary>
    internal void Cancel(string key)
    {
        List<Action> cancels;
        lock (_gate)
        {
            _cancelledByUser.Add(key);
            cancels = Find(key)?.Handles
                .Where(h => !h.Ended && h.CancelAction is not null)
                .Select(h => h.CancelAction!)
                .ToList() ?? [];
        }
        foreach (var cancel in cancels)
        {
            try
            {
                cancel();
            }
            catch (Exception ex)
            {
                Logger.Warning("Cancelling a download threw: {ErrorType}", ex.GetType().Name);
            }
        }
        if (CancelledByUser is { } cancelled)
        {
            try
            {
                cancelled(key);
            }
            catch (Exception ex)
            {
                Logger.Warning("A download-cancelled subscriber threw: {ErrorType}", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Raised by <see cref="Cancel"/>, on its caller's thread, after the callers were asked to stop —
    /// so the owner can keep the model listed as stopped whichever caller was downloading it
    /// (Codex diff r2: a dictation's own download has no owner that would).
    /// </summary>
    internal event Action<string>? CancelledByUser;

    /// <summary>Whether <paramref name="key"/>'s download was cancelled from the card this session
    /// and not asked for again since.</summary>
    internal bool WasCancelledByUser(string key)
    {
        lock (_gate)
        {
            return _cancelledByUser.Contains(key);
        }
    }

    /// <summary>
    /// Keeps a download the user cancelled listed as stopped, with <paramref name="restart"/> behind
    /// its Download button, while <paramref name="stillWanted"/> holds. <paramref name="note"/> says
    /// why it stays.
    /// </summary>
    internal void MarkStopped(string key, string displayName, long? totalBytes, string note,
        Action restart, Func<bool> stillWanted)
    {
        lock (_gate)
        {
            _stopped.RemoveAll(s => SameKey(s.Key, key));
            _stopped.Add(new StoppedDownload(key, displayName, totalBytes is > 0 ? totalBytes : null, note, restart, stillWanted));
        }
        RaiseChanged();
    }

    /// <summary>
    /// Starts a stopped download again, through the action its owner gave. The row stays until the
    /// download really starts (<see cref="Begin"/> drops it), so a refused restart — an update being
    /// applied — leaves it in place (self-review, 2026-10-17).
    /// </summary>
    internal void Restart(string key)
    {
        StoppedDownload? stopped;
        lock (_gate)
        {
            stopped = _stopped.FirstOrDefault(s => SameKey(s.Key, key));
        }
        if (stopped is null)
            return;
        try
        {
            stopped.Restart();
        }
        catch (Exception ex)
        {
            Logger.Warning("Restarting a download threw: {ErrorType}", ex.GetType().Name);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Forwards the progress of <paramref name="key"/>'s download to <paramref name="onProgress"/>
    /// until disposed, starting with where it is now.
    /// </summary>
    internal IDisposable Listen(string key, Action<double> onProgress)
    {
        var listener = (key, onProgress);
        double? current;
        lock (_gate)
        {
            _listeners.Add(listener);
            current = Find(key)?.Fraction;
        }
        if (current is { } now)
            Notify(onProgress, now);
        return new Subscription(() =>
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        });
    }

    private static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private Entry? Find(string key) => _running.FirstOrDefault(e => SameKey(e.Key, key));

    private static bool StillWanted(StoppedDownload stopped)
    {
        try
        {
            return stopped.StillWanted();
        }
        catch (Exception ex)
        {
            Logger.Warning("A stopped download's owner threw: {ErrorType}", ex.GetType().Name);
            return false;
        }
    }

    private void Report(Handle handle, double fraction)
    {
        List<Action<double>> listeners;
        double now;
        lock (_gate)
        {
            if (handle.Ended || !_running.Contains(handle.Entry))
                return;
            now = handle.Entry.Fraction = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
            listeners = _listeners
                .Where(l => SameKey(l.Key, handle.Entry.Key))
                .Select(l => l.OnProgress)
                .ToList();
        }
        foreach (var listener in listeners)
            Notify(listener, now);
        RaiseChanged();
    }

    private void End(Handle handle)
    {
        bool removed = false;
        lock (_gate)
        {
            if (handle.Ended)
                return;
            handle.Ended = true;
            handle.Entry.Handles.Remove(handle);
            if (handle.Entry.Handles.Count == 0)
                removed = _running.Remove(handle.Entry);
        }
        // Also when the entry stays: the card shows Cancel only while a caller can still stop.
        if (removed || handle.CancelAction is not null)
            RaiseChanged();
    }

    private static void Notify(Action<double> listener, double fraction)
    {
        try
        {
            listener(fraction);
        }
        catch (Exception ex)
        {
            Logger.Warning("A download progress listener threw: {ErrorType}", ex.GetType().Name);
        }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } changed)
            return;
        foreach (var subscriber in changed.GetInvocationList().Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch (Exception ex)
            {
                Logger.Warning("A download change subscriber threw: {ErrorType}", ex.GetType().Name);
            }
        }
    }

    /// <summary>One model being downloaded, shared by every caller downloading it. Fields are
    /// written under the owner's lock.</summary>
    internal sealed class Entry(string key, string displayName, long startedTicks)
    {
        internal string Key { get; } = key;
        internal string DisplayName { get; } = displayName;
        internal long StartedTicks { get; } = startedTicks;
        internal double Fraction { get; set; }
        internal long? TotalBytes { get; set; }
        internal List<Handle> Handles { get; } = [];
    }

    /// <summary>One caller's part in a download.</summary>
    internal sealed class Handle : IDisposable
    {
        private readonly BackgroundDownloads _owner;

        internal Handle(BackgroundDownloads owner, Entry entry, Action? cancel)
        {
            _owner = owner;
            Entry = entry;
            CancelAction = cancel;
        }

        internal Entry Entry { get; }

        internal Action? CancelAction { get; }

        /// <summary>Written under the owner's lock.</summary>
        internal bool Ended { get; set; }

        internal void Report(double fraction) => _owner.Report(this, fraction);

        public void Dispose() => _owner.End(this);
    }

    private sealed record StoppedDownload(string Key, string DisplayName, long? TotalBytes, string Note,
        Action Restart, Func<bool> StillWanted);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// One line of the Downloads card. <see cref="StartedTicks"/> is when the download began, for the
/// Cancel button's double-click guard; <see cref="Note"/> is set on a stopped one.
/// </summary>
internal sealed record DownloadRow(string Key, string DisplayName, double Fraction, long? TotalBytes,
    bool CanCancel, bool Stopped, long StartedTicks, string? Note);
