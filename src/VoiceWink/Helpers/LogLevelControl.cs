using Serilog.Core;
using Serilog.Events;

namespace VoiceWink.Helpers;

/// <summary>
/// The ONE minimum-level switch behind the application log (launch defaults audit, 2026-09-13). The Log Viewer's
/// "Verbose debug logging" toggle drives it: ON lowers the sink to Debug, OFF keeps Information.
/// </summary>
/// <remarks>
/// <para>Until this existed the logger was built with a hard-coded <c>MinimumLevel.Information()</c>
/// and no switch, so the ~150 <c>Logger.Debug</c> lines app-wide (half in the capture, clipboard and pill code,
/// exactly the ones a support case needs) were dropped unconditionally, verbose toggle or not;
/// the toggle only added a heartbeat and a few timings. A user told to "turn on verbose logging,
/// reproduce, send a report" sent a log with none of that in it.</para>
///
/// <para>One process-global switch, deliberately: the level is a property of the SINK, and the
/// logger is rebuilt in three places (startup, the Log Viewer's Clear, the crash-consent sink
/// attach) — a switch object outlives every rebuild, so every configuration reads the same
/// current level and a rebuild can never silently reset it. Read before Serilog exists through
/// <see cref="VerboseLoggingReader"/>, applied again from the toggle. Debug is the floor: the
/// Verbose level below it is never used by this app, and the Sentry sink keeps its own
/// Information breadcrumb / Error event floors regardless.</para>
/// </remarks>
internal static class LogLevelControl
{
    public static readonly LoggingLevelSwitch Switch = new(LogEventLevel.Information);

    /// <summary>The level <see cref="Apply"/> selects for a verbose flag. Pure; pinned by tests.</summary>
    internal static LogEventLevel LevelFor(bool verbose) => verbose ? LogEventLevel.Debug : LogEventLevel.Information;

    public static void Apply(bool verbose) => Switch.MinimumLevel = LevelFor(verbose);

    public static bool IsVerbose => Switch.MinimumLevel <= LogEventLevel.Debug;
}
