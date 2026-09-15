using Serilog;
using VoiceWink.Helpers;
using Windows.Media.Capture;
using Windows.Media.Effects;

namespace VoiceWink.Services.Audio;

/// <summary>
/// AUD-18: logs the capture effects Windows reports for the endpoint a recording is using — one
/// line per recording, beside AUD-16's <c>dev=</c> tag, entirely OFF the recording start path.
///
/// <para><b>Placement is the safety argument.</b> The query is a WinRT/COM round-trip into the
/// audio stack, and this repo has lost a recording to an "affordable" COM call in a lock-held
/// tail (AUD-11's FriendlyName). So <see cref="LogAfterStart"/> is fire-and-forget onto the
/// thread pool: the recorder calls it AFTER the start succeeded, it touches no MMDevice (the id
/// is a string captured by AUD-16's bounded observe read), it holds no lock, and a wedged audio
/// service delays only this log line — never audio. Every exception is caught inside the task;
/// the degraded line says <c>unavailable</c> so a dead diagnostic is distinguishable from an
/// empty effects list (the CaptureEndpointClassifier boxed-uint lesson: a silent always-empty
/// diagnostic shipped dead once already).</para>
///
/// <para><b>The API choice supersedes the card's T2-COM premise, by measurement.</b> AUD-18
/// recorded "the real API is IAudioEffectsManager (Windows 11), which is COM interop" — but the
/// WinRT projection <c>Windows.Media.Effects.AudioEffectsManager</c> is directly callable under
/// the app's own TFM with no hand-rolled interop, verified live on this machine 2026-08-19: the
/// four categories probed (Other, Communications, Speech, Media) all return cleanly on the real
/// array. What it CANNOT see is also measured and documented on <see cref="CaptureEffectsProbe"/>:
/// driver DSP below the APO layer reports nothing, so "(none reported)" is a claim about the APO
/// layer only. One ASSUMPTION rides the <c>default=</c> label rather than a measurement: NAudio's
/// stock <c>WasapiCapture</c> never calls <c>IAudioClient2::SetClientProperties</c>, so our stream
/// gets the default category — true for NAudio 2.2.1 by source inspection (the AUD-3 probe's
/// finding); a future NAudio that sets a category would silently make <c>default=</c> describe a
/// chain our capture does not get. Re-check that inspection on any NAudio bump.</para>
///
/// <para>Follows the <c>RecordingStartLatencyProbe.Instance</c> precedent rather than DI — the
/// recorder is the only caller and the seam exists for tests, not composition.</para>
/// </summary>
public sealed class CaptureEffectsReader
{
    private static ILogger Logger => Log.ForContext<CaptureEffectsReader>();

    public static CaptureEffectsReader Instance { get; } = new();

    // The WinRT query, injectable so tests never touch the audio stack (the hosted CI runner has
    // no capture device, and AGENTS.md forbids tests opening real devices). Production wires
    // QueryEffectNames below.
    private readonly global::System.Func<string, MediaCategory, string[]> _query;

    // Single-flight gate (self-review A1): the query is a SYNCHRONOUS audio-stack round-trip on a
    // thread-pool worker, and a wedged audio service (this repo has measured 28 s and 238 s
    // wedges — BoundedComCall's reason to exist) would otherwise pin ONE MORE worker per
    // recording while the user re-records, starving the pool the recording path's own bounded
    // resolves run on. A timeout cannot unpin a blocked worker — abandonment leaves the thread
    // stuck — so the honest cap is concurrency: one probe in flight, and while it is stuck every
    // further recording logs an honest "probe busy" line instead of queueing another worker.
    private int _probeInFlight;

    public CaptureEffectsReader() : this(QueryEffectNames)
    {
    }

    internal CaptureEffectsReader(global::System.Func<string, MediaCategory, string[]> query)
    {
        _query = query;
    }

    /// <summary>Fire-and-forget: builds and logs the one effects line for this recording. Never
    /// throws to the caller; never blocks the recording path. <paramref name="deviceTag"/> is
    /// AUD-16's log-safe tag; <paramref name="endpointId"/> is the raw endpoint id and is used
    /// ONLY to address the query — it is never logged (it is device-identifying, the same reason
    /// AUD-16 hashes it).</summary>
    public void LogAfterStart(string? endpointId, string deviceTag)
    {
        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
        {
            // A previous probe is still running — most plausibly stuck against a wedged audio
            // service. Do not stack workers behind it — and do not spawn a worker to SAY so: a
            // Task.Run per busy line re-created the very accumulation this gate exists to cap
            // (one wedged probe + N recordings + a stalled sink = N pinned workers; Codex diff
            // round, 2026-08-19). The busy line is a pure Serilog call with preformatted strings
            // — zero audio-stack work — and both call sites already log "Recording started"
            // synchronously on this same path, so logging it inline adds no new blocking class.
            try { Logger.Information("Capture effects: {Payload} (dev={DeviceTag})", "unavailable (probe busy)", deviceTag); }
            catch { /* the diagnostic must never become a fault source */ }
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Logger.Information("Capture effects: {Payload} (dev={DeviceTag})",
                    BuildPayload(endpointId), deviceTag);
            }
            catch (Exception ex)
            {
                // The diagnostic must never become a fault source. Debug: a broken effects line
                // is an investigation detail, not an incident.
                try { Logger.Debug(ex, "Capture-effects line failed to build/log"); }
                catch { /* even the failure report may not throw — an unobserved task exception here would reach Sentry as a phantom error */ }
            }
            finally
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
            }
        });
    }

    /// <summary>The line's payload — synchronous and deterministic given the seam, which is what
    /// makes the whole decision surface testable without a device. Null/blank id ⇒ one honest
    /// "unavailable" payload (no query is attempted); a per-category failure degrades THAT
    /// category to <c>unavailable(0xHRESULT)</c> while the other still reports.</summary>
    internal string BuildPayload(string? endpointId)
    {
        var winRtId = CaptureEffectsProbe.ComposeWinRtId(endpointId);
        if (winRtId is null) return "unavailable (no endpoint id)";

        return CaptureEffectsProbe.FormatPayload(
            QueryCategory(winRtId, MediaCategory.Other),
            QueryCategory(winRtId, MediaCategory.Communications));
    }

    private string QueryCategory(string winRtId, MediaCategory category)
    {
        try
        {
            return CaptureEffectsProbe.FormatCategory(_query(winRtId, category));
        }
        catch (Exception ex)
        {
            return $"unavailable(0x{ex.HResult:X8})";
        }
    }

    private static string[] QueryEffectNames(string winRtId, MediaCategory category)
    {
        var manager = AudioEffectsManager.CreateAudioCaptureEffectsManager(winRtId, category);
        var effects = manager.GetAudioCaptureEffects();
        var names = new string[effects.Count];
        for (var i = 0; i < effects.Count; i++) names[i] = effects[i].AudioEffectType.ToString();
        return names;
    }
}
