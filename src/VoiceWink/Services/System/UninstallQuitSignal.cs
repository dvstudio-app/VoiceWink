namespace VoiceWink.Services.System;

/// <summary>
/// INS-4: the cross-process channel that lets the uninstall hook ask a RUNNING VoiceWink to quit
/// GRACEFULLY, rather than killing it.
///
/// <para><b>Why a named event and not a window message.</b> The obvious signal —
/// <c>PostMessage(WM_CLOSE)</c>, which is what <c>Process.CloseMainWindow()</c> sends too — is
/// exactly what the title-bar X sends, and this app deliberately INTERCEPTS that: the main window's
/// <c>Closed</c> handler hides to tray and returns early unless <c>_isQuitting</c> is already set. So
/// a WM_CLOSE from another process hides the window, the process lives on, and the fallback kill
/// becomes the normal path — the precise outcome the graceful step exists to avoid. Both in-process
/// quit paths (tray Quit and the update-apply quit) set that flag FIRST, and an out-of-process
/// message cannot. This channel therefore triggers the same code they do.</para>
///
/// <para><b>Scope is LOCAL, deliberately.</b> The name is session-scoped (<c>Local\</c>), so the
/// signal can only ever reach a VoiceWink running as the SAME user in the SAME session — the
/// uninstall's own user. It cannot reach another user's instance on a shared machine, which is a
/// property the process-scan side has to work for separately and gets for free here.</para>
///
/// <para><b>Fail-soft on every path.</b> The app side never blocks startup on it, and the signal
/// side treats "no such event" as "nothing of ours is listening" — which is also the honest answer
/// when the running instance predates this build. The caller's kill fallback covers every case where
/// this does nothing.</para>
/// </summary>
internal static class UninstallQuitSignal
{
    /// <summary>Session-scoped so it cannot cross users. The GUID suffix mirrors the single-instance
    /// mutex's shape — a name collision with an unrelated product would be a cross-process defect
    /// nobody would think to look for.</summary>
    internal const string EventName = @"Local\VoiceWink-QuitRequest-6B1E9F44";

    /// <summary>
    /// App side: create the event and run <paramref name="onQuitRequested"/> when it is signalled.
    /// Returns the handle so the app can keep it alive; null when the event cannot be created, which
    /// is never fatal — the uninstall path just falls back to killing.
    /// </summary>
    internal static EventWaitHandle? Listen(Action onQuitRequested) => Listen(EventName, onQuitRequested);

    /// <summary>
    /// Name-seamed overload. <b>Tests MUST use this with a unique name</b> — exercising the channel
    /// on <see cref="EventName"/> would signal the developer's own running VoiceWink and quit it
    /// mid-test-run.
    /// </summary>
    internal static EventWaitHandle? Listen(string eventName, Action onQuitRequested)
    {
        EventWaitHandle? handle = null;
        try
        {
            // ManualReset: the signal is a latch, not a queue. If two uninstall attempts overlap,
            // the second must not be swallowed, and the app quits once either way.
            handle = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            ThreadPool.RegisterWaitForSingleObject(
                handle,
                (_, timedOut) => { if (!timedOut) SafeInvoke(onQuitRequested); },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: true);
            return handle;
        }
        catch (Exception ex)
        {
            // Dispose before giving up. An ORPHANED event is worse than none: it exists, so the
            // uninstall hook's TryOpenExisting succeeds, RequestQuit returns TRUE, and the runner
            // logs "asked to quit gracefully" with nothing listening — reintroducing exactly the
            // dishonest log line this card removed elsewhere.
            try { handle?.Dispose(); } catch { /* best effort */ }
            Serilog.Log.Warning(ex, "INS-4: quit-request listener unavailable ({ExType})", ex.GetType().Name);
            return null;
        }
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { Serilog.Log.Error(ex, "INS-4: quit-request handler threw"); }
    }

    /// <summary>
    /// Uninstall-hook side: ask any listening instance to quit. Returns true only when the event
    /// existed AND was signalled — false means nothing was listening, which the caller must treat as
    /// "no graceful attempt happened" rather than as failure.
    /// <para>Never throws: this runs inside a Velopack fast callback, where an exception must not
    /// disrupt the uninstall.</para>
    /// </summary>
    internal static bool RequestQuit() => RequestQuit(EventName);

    /// <summary>Name-seamed overload — see the warning on <see cref="Listen(string, Action)"/>.</summary>
    internal static bool RequestQuit(string eventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch
        {
            return false; // no listener, or no rights — the kill fallback covers it
        }
    }
}
