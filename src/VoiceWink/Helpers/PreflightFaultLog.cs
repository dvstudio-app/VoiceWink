using System.Net.Http;
using Serilog;
using Serilog.Events;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Helpers;

/// <summary>
/// The warm recording path's preflight fault, logged by BOTH of its observers (the background
/// continuation and the stop-side await) through one key, so one fault is one Sentry event at
/// most (2026-10-17). The cold path reported the same faults as <c>Error(ex)</c> while the warm
/// path — the default, with instant recording on — kept only the exception type at Warning, so
/// the default path's preflight defects never reached Sentry. Kept out of <c>MainViewModel</c> by
/// the hub-class rule (Kimi diff r1).
/// </summary>
internal static class PreflightFaultLog
{
    /// <summary>Logs <paramref name="ex"/> at <see cref="LevelFor"/>'s level; the exception is
    /// attached only to the Error.</summary>
    public static void Write(ILogger logger, Exception? ex, string what)
    {
        var level = LevelFor(ex);
        if (level >= LogEventLevel.Error)
            logger.Write(level, ex, "{What} ({ExType})", what, ex!.GetType().Name);
        else
            logger.Write(level, "{What} ({ExType})", what, ex?.GetType().Name ?? "Unknown");
    }

    /// <summary>Warning for the environmental classes — a cancel, a timeout, the network, an
    /// interrupted model download (a stalled or short transfer, re-typed by the downloader — Codex
    /// diff r1), a full disk, a GPU self-test refusal; otherwise the session budget's level for the
    /// exception type: Error once per run, Warning after. Not pure — it spends the budget.</summary>
    public static LogEventLevel LevelFor(Exception? ex) => ex switch
    {
        null => LogEventLevel.Warning,
        OperationCanceledException or TimeoutException or HttpRequestException
            or ModelSourceIOException or ModelSourceEndOfStreamException
            or ModelLocalIOException or GpuSelfTestRefusedException
            => LogEventLevel.Warning,
        _ => SessionErrorBudget.LevelFor("preflight:" + ex.GetType().Name),
    };
}
