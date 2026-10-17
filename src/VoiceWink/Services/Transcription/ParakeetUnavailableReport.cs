using System.Globalization;
using System.Text;
using Serilog;
using Serilog.Events;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// Why the Parakeet engine cannot serve right now. Each member has its own pill sentence
/// (<see cref="EngineUnavailableMessage"/>) and its own key in the Sentry event.
///
/// <para>Exists because every one of these used to reach the user as "That transcription model can't
/// run on this version of VoiceWink" and reach Sentry as one generic Error (VOICEWINK-J/K/M,
/// 2026-10-15) — while the actual case there was an engine child that died a few seconds after launch
/// on a working build.</para>
/// </summary>
public enum ParakeetUnavailableCause
{
    /// <summary>This build was compiled without Parakeet (the release lever).</summary>
    BuildLever,

    /// <summary>The processor lacks what the engine needs (the startup probe said no).</summary>
    CpuUnsupported,

    /// <summary>The in-process engine's native library would not load.</summary>
    NativeLoadFailed,

    /// <summary>The engine's executable is not on disk.</summary>
    EngineFileMissing,

    /// <summary>The engine's executable is neither the pinned build nor signed by us (damaged, or
    /// altered by antivirus) — the provenance gate refused it.</summary>
    EngineFileDamaged,

    /// <summary>The engine process did not start or never became healthy.</summary>
    EngineFailedToStart,

    /// <summary>The engine failed often enough this session that it is stopped until a restart.</summary>
    EngineStoppedForSession,

    /// <summary>A previous engine process has not confirmed its exit yet; transient.</summary>
    StartingUp,

    /// <summary>The downloaded model is still being checked; transient.</summary>
    VerificationPending,

    /// <summary>The model's files are missing or failed their check (seen at transcription time;
    /// preparation answers NotDownloaded for this and the download path repairs it).</summary>
    ModelMissing,
}

/// <summary>How one engine launch ended without becoming healthy.</summary>
public enum ParakeetAttemptOutcome
{
    /// <summary>The process could not be created at all.</summary>
    LaunchFailed,

    /// <summary>The process exited on its own before it answered.</summary>
    ExitedBeforeHealth,

    /// <summary>The process stayed alive but never answered within the budget.</summary>
    NeverHealthy,

    /// <summary>A health probe never returned within the budget.</summary>
    HungProbe,

    /// <summary>A health probe threw.</summary>
    ProbeThrew,
}

/// <summary>Why an acquire returned no lease before (or instead of) launching anything.</summary>
public enum ParakeetAcquireFailureKind
{
    /// <summary>Every launch this acquire made failed; see the attempts.</summary>
    AttemptsFailed,

    /// <summary>A previous child has not confirmed its exit (one child at a time).</summary>
    PredecessorExiting,

    /// <summary>The crash-storm fuse is tripped for this session.</summary>
    StormTripped,

    /// <summary>The executable failed the provenance gate (neither the pinned hash nor our signature).</summary>
    SpawnRefused,
}

/// <summary>One launch that never became healthy: the facts that separate the crash classes.</summary>
public sealed record ParakeetAttemptFailure(
    ParakeetLaunchMode Mode,
    string? Pin,
    ParakeetAttemptOutcome Outcome,
    uint? ExitCode,
    TimeSpan Lifetime,
    int DeviceRows,
    bool BackendSeen)
{
    /// <summary><c>Auto exit=0xC000001D illegal-instruction after 3.6s devices=1 backend=no</c>.</summary>
    public string Render()
    {
        var b = new StringBuilder();
        b.Append(Mode);
        if (Pin is not null) b.Append('/').Append(LogValueSanitizer.SingleLine(Pin));
        b.Append(' ').Append(Outcome);
        if (ExitCode is { } code) b.Append(" exit=").Append(NtStatusName.Describe(code));
        b.Append(" after ").Append(Lifetime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append('s');
        b.Append(" devices=").Append(DeviceRows.ToString(CultureInfo.InvariantCulture));
        b.Append(" backend=").Append(BackendSeen ? "yes" : "no");
        return b.ToString();
    }
}

/// <summary>Why one acquire returned no lease — owned by that call, never a shared field (a
/// concurrent acquire could otherwise overwrite it before its caller reads it: Codex plan r1).</summary>
public sealed record ParakeetAcquireFailure(
    ParakeetAcquireFailureKind Kind,
    IReadOnlyList<ParakeetAttemptFailure> Attempts,
    string? Detail = null)
{
    public static ParakeetAcquireFailure Of(ParakeetAcquireFailureKind kind, string? detail = null)
        => new(kind, [], detail);
}

/// <summary>The cause of one Unavailable answer plus, for a server failure, its acquire record.</summary>
public sealed record ParakeetUnavailableReport(
    ParakeetUnavailableCause Cause,
    ParakeetAcquireFailure? Acquire = null,
    string? Detail = null)
{
    /// <summary>Longest detail kept (the spawn gate's sanitized refusal reason, an exception summary).</summary>
    internal const int MaxDetailLength = 200;

    /// <summary>Every attempt, <c>; </c>-separated, or <c>none</c>.</summary>
    public string RenderAttempts()
    {
        if (Acquire is null) return "none";
        var parts = new List<string>();
        if (Acquire.Kind != ParakeetAcquireFailureKind.AttemptsFailed) parts.Add(Acquire.Kind.ToString());
        parts.AddRange(Acquire.Attempts.Select(a => a.Render()));
        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    /// <summary>The detail as it may be logged: one line, bounded, or null.</summary>
    public string? RenderDetail()
    {
        var raw = Detail ?? Acquire?.Detail;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var line = LogValueSanitizer.SingleLine(raw);
        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength];
    }

    /// <summary>What makes two reports "the same failure" for the event bound: the cause, the
    /// detail's first token (an exception type, a refusal kind, storm vs tripwire, a legacy
    /// fallback), the acquire kind and every attempt's mode + outcome + exit code — never timings.</summary>
    internal string Key()
    {
        var b = new StringBuilder(Cause.ToString());
        var detail = RenderDetail();
        if (detail is not null)
        {
            var space = detail.IndexOf(' ');
            b.Append('|').Append(space < 0 ? detail : detail[..space]);
        }
        if (Acquire is not null)
        {
            b.Append('|').Append(Acquire.Kind);
            foreach (var a in Acquire.Attempts)
                b.Append('|').Append(a.Mode).Append(':').Append(a.Outcome).Append(':')
                    .Append(a.ExitCode?.ToString("X8", CultureInfo.InvariantCulture) ?? "-");
        }
        return b.ToString();
    }
}

/// <summary>A local engine cannot run its model on this PC right now. <see cref="Exception.Message"/>
/// IS the pill sentence for <see cref="Cause"/>; the report carries the diagnostic facts.</summary>
public sealed class LocalEngineUnavailableException : InvalidOperationException
{
    public LocalEngineUnavailableException(ParakeetUnavailableReport? report)
        : base(EngineUnavailableMessage.For(report?.Cause))
    {
        Report = report;
    }

    public ParakeetUnavailableReport? Report { get; }

    public ParakeetUnavailableCause? Cause => Report?.Cause;
}

/// <summary>
/// The ONE place a Parakeet Unavailable becomes a log event. Error — which the Sentry sink forwards —
/// the first time a failure KEY (<see cref="ParakeetUnavailableReport.Key"/>) is seen in this process,
/// Warning on every repeat: the startup preload, each dictation, Audio Transcribe and a Models-page
/// selection all prepare, so the same failure would otherwise file an event per attempt. The
/// once-per-run count is <see cref="Helpers.SessionErrorBudget"/>'s, shared with the dictation path.
/// </summary>
internal static class ParakeetUnavailableReporter
{
    internal const string Template =
        "Parakeet engine unavailable: {ParakeetCause} - {ParakeetAttempts} cpu={CpuInstructionSets} detail={ParakeetDetail}";

    /// <summary>Prefixes <see cref="ParakeetUnavailableReport.Key"/> in the shared budget, so no other
    /// site's key can collide with a Parakeet failure.</summary>
    internal const string BudgetKeyPrefix = "parakeet-unavailable:";

    private static ILogger Logger => Log.ForContext(typeof(ParakeetUnavailableReporter));

    /// <summary>Logs <paramref name="report"/>; returns the level used.</summary>
    public static LogEventLevel Report(ParakeetUnavailableReport report, ILogger? logger = null)
    {
        // The build lever is a deliberate release choice, not a fault: with it pulled, every
        // session of every user would otherwise file an event — Information, always. The two
        // transient causes (a predecessor still exiting, a hash still in flight) are passing
        // states by design, nothing to act on — Warning, always (self-review, privacy lens).
        // Every other cause spends a budget key: Error once per run, and only while the Sentry sink
        // is attached, so a failure first seen with crash reporting off still reports after an opt-in.
        var level = report.Cause switch
        {
            ParakeetUnavailableCause.BuildLever => LogEventLevel.Information,
            ParakeetUnavailableCause.StartingUp or ParakeetUnavailableCause.VerificationPending => LogEventLevel.Warning,
            _ => Helpers.SessionErrorBudget.LevelFor(BudgetKeyPrefix + report.Key()),
        };
        (logger ?? Logger).Write(level, Template,
            report.Cause, report.RenderAttempts(), CpuInstructionSets.Current.Render(), report.RenderDetail() ?? "none");
        return level;
    }

    /// <summary>Clears the shared budget — the reporter keeps no count of its own.</summary>
    internal static void ResetForTests() => Helpers.SessionErrorBudget.ResetForTests();
}
