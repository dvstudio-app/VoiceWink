using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// UI thread heartbeat for diagnosing freezes. When verbose logging is enabled,
/// logs a timestamp every 500ms on the UI thread. Any gap > 1 second in the log
/// reveals exactly when a freeze occurred and how long it lasted.
/// Also provides timing helpers for critical operations.
/// </summary>
public sealed class DebugHeartbeat : IDisposable
{
    private static ILogger Logger => Log.ForContext<DebugHeartbeat>();

    private readonly SettingsService _settings;
    private DispatcherTimer? _heartbeatTimer;
    private long _beatCount;

    public bool IsEnabled => _settings.GetBool(AppDefaults.VerboseLoggingEnabled, false);

    public DebugHeartbeat(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Start the heartbeat timer on the UI thread. Call from App.OnLaunched after window is created.
    /// </summary>
    public void Start(DispatcherQueue dispatcher)
    {
        if (!IsEnabled) return;

        dispatcher.TryEnqueue(() =>
        {
            _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _heartbeatTimer.Tick += (_, _) =>
            {
                _beatCount++;
                // Log every 10th beat (5 seconds). Freeze detection relies on timestamp gaps:
                // if expected entry at T+5s arrives at T+15s, a 10s freeze is visible.
                if (_beatCount % 10 == 0)
                    Logger.Information("[heartbeat] #{Count}", _beatCount);
            };
            _heartbeatTimer.Start();
            Logger.Information("[debug] UI thread heartbeat started (500ms interval)");
        });
    }

    /// <summary>
    /// Stop the heartbeat timer.
    /// </summary>
    public void Stop()
    {
        _heartbeatTimer?.Stop();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// Re-check the setting and start/stop accordingly. Call when the setting changes.
    /// </summary>
    public void Refresh(DispatcherQueue dispatcher)
    {
        if (IsEnabled && _heartbeatTimer == null)
            Start(dispatcher);
        else if (!IsEnabled && _heartbeatTimer != null)
            Stop();
    }

    /// <summary>
    /// Time a synchronous operation and log if verbose mode is on.
    /// </summary>
    public void Time(string label, Action action)
    {
        if (!IsEnabled)
        {
            action();
            return;
        }

        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        if (sw.ElapsedMilliseconds > 5)
            Logger.Information("[timing] {Label}: {Elapsed}ms", label, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Time a synchronous operation that returns a value and log if verbose mode is on.
    /// </summary>
    public T Time<T>(string label, Func<T> func)
    {
        if (!IsEnabled)
            return func();

        var sw = Stopwatch.StartNew();
        var result = func();
        sw.Stop();
        if (sw.ElapsedMilliseconds > 5)
            Logger.Information("[timing] {Label}: {Elapsed}ms", label, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Time an async operation and log if verbose mode is on.
    /// </summary>
    public async Task TimeAsync(string label, Func<Task> func)
    {
        if (!IsEnabled)
        {
            await func().ConfigureAwait(false);
            return;
        }

        var sw = Stopwatch.StartNew();
        await func().ConfigureAwait(false);
        sw.Stop();
        if (sw.ElapsedMilliseconds > 50)
            Logger.Information("[timing] {Label}: {Elapsed}ms", label, sw.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        Stop();
    }
}
