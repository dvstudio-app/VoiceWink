using System;
using Microsoft.UI.Dispatching;

namespace VoiceWink.Views;

/// <summary>
/// Production <see cref="IPillScheduler"/> for <see cref="MiniRecorderPresentationController"/>:
/// a single non-repeating <see cref="DispatcherQueueTimer"/> on the UI thread. The controller
/// re-arms it after every Reconcile, so at most one callback is pending; a stale tick after a
/// re-arm is harmless because the callback (OnScheduledTick) re-evaluates against the clock.
/// </summary>
internal sealed class DispatcherPillScheduler : IPillScheduler
{
    private readonly DispatcherQueue _queue;
    private DispatcherQueueTimer? _timer;
    private Action? _callback;

    public DispatcherPillScheduler(DispatcherQueue queue)
        => _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Arm(TimeSpan? delay, Action onFire)
    {
        _callback = onFire;
        if (_timer == null)
        {
            _timer = _queue.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => _callback?.Invoke();
        }
        _timer.Stop();
        if (delay is { } d)
        {
            // A zero/negative delay must still fire on a later turn — clamp to a tiny positive.
            _timer.Interval = d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(1);
            _timer.Start();
        }
    }
}
