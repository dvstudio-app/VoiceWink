using Serilog;

namespace VoiceWink.Helpers;

/// <summary>UI-26 adds the tray icon to the notification area, retrying an add Windows refused.</summary>
internal static class TrayIconRegistration
{
    /// <summary>UI-26 the wait before each retry; after the last one only Explorer's next TaskbarCreated (answered by H.NotifyIcon) adds the icon.</summary>
    internal static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    /// <summary>The first attempt runs before the first await, so a caller sees its outcome at once; never throws.</summary>
    internal static async Task<bool> RegisterAsync(
        Action add, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancel)
    {
        var attempts = RetryDelays.Length + 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                add();
                if (attempt == 1)
                    Log.Information("System tray icon set up");
                else
                    Log.Information("System tray icon set up on attempt {Attempt} of {Attempts}", attempt, attempts);
                return true;
            }
            catch (Exception ex) when (attempt == attempts)
            {
                // Error, so a persistent refusal reaches Sentry; a transient one recovers at Warning.
                Log.Error(ex, "Tray icon refused on all {Attempts} attempts; it is added when Explorer next restarts the taskbar",
                    attempts);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tray icon refused on attempt {Attempt} of {Attempts}; retrying in {Delay}",
                    attempt, attempts, RetryDelays[attempt - 1]);
            }

            try { await delay(RetryDelays[attempt - 1], cancel); }
            catch (OperationCanceledException) { return false; }
            // A delay that finished before the cancel still resumes here; the icon may be disposed by now.
            if (cancel.IsCancellationRequested) return false;
        }
    }
}
