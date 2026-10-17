namespace VoiceWink.Helpers;

/// <summary>
/// Whether the CURRENT logger forwards to Sentry. <c>App</c> clears it before closing a logger
/// and publishes it only once the replacement is in <c>Log.Logger</c> (startup, Clear Logs, the
/// crash-reporting consent's re-attach), so it is never true while reports go to a closed logger.
///
/// <para>The once-per-run Error budgets read it so a cause first seen while crash reporting was
/// OFF is not spent: otherwise a user who opts in to report an existing failure would never send
/// it, because every repeat after the opt-in is a Warning (Kimi diff r1, 2026-10-17). A cause is
/// counted only while this is true, and stays counted across a later off → on, so cycling the
/// consent never re-sends what Sentry already has.</para>
///
/// <para>Defaults to true: the logger is built before anything that consults this can log, and
/// the test host — which never builds the app's logger — then exercises the budget as it runs with
/// crash reporting on.</para>
/// </summary>
internal static class SentrySinkState
{
    private static volatile bool _attached = true;

    public static bool Attached
    {
        get => _attached;
        set => _attached = value;
    }
}
