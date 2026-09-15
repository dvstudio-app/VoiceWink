using NAudio.CoreAudioApi;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Audio;

/// <summary>
/// How a recording device was resolved for a recording (AUD-1).
/// </summary>
public enum RecordingDeviceResolutionKind
{
    /// <summary>No pinned selection — the system default path (today's behavior).</summary>
    SystemDefault,
    /// <summary>The user's pinned device resolved and is active.</summary>
    SelectedDevice,
    /// <summary>A pinned device exists but did not resolve (unplugged/disabled/timeout) —
    /// the system default is used for this recording instead.</summary>
    FallbackToDefault,
}

/// <summary>The outcome of recording-device resolution. <see cref="Device"/> may be null on any
/// path (no mic at all / bounded timeout) — the recorder tolerates null by falling through to
/// WASAPI's own default resolution, unchanged from pre-AUD-1 behavior.
///
/// <para><b><see cref="DeviceTag"/> has NO default value, deliberately (AUD-16).</b> Every
/// construction site must decide what identity it is publishing. The one that proves the point is
/// <c>StandingCaptureService</c>'s record rebuild, which passes <c>Device: null</c> because the
/// MMDevice never rides a claim — with a default, the mechanical fix there would have been to omit
/// the tag, silently degrading the WARM path (the default path, since instant recording ships on)
/// to <c>unknown</c> forever. A compile error is what forces the copy.</para>
///
/// <para><b><see cref="EndpointId"/> (AUD-18) rides the same rule for the same reason:</b> no
/// default, so every construction site decides. It is the RAW Windows endpoint id — captured by
/// the SAME bounded observe read that derives the tag (one COM read serves both) — and exists
/// only to address the capture-effects query in-process. It is never logged: the id is
/// device-identifying, which is exactly why AUD-16 hashes it into the tag for log use.</para></summary>
public sealed record ResolvedRecordingDevice(
    MMDevice? Device,
    RecordingDeviceResolutionKind Kind,
    string? SelectedId,
    string? SelectedName,
    string DeviceTag,
    string? EndpointId);

/// <summary>
/// AUD-1: the single owner of "which microphone does a recording use" — preference parsing,
/// bounded resolution, fallback classification, the one-shot fallback-notice latch, and the
/// selected-device activation-retry policy. Extracted per the launch-freeze hub rule:
/// MainViewModel swaps its direct AudioDeviceManager call for
/// <see cref="ResolveForRecordingAsync"/>; App's warm-up asks <see cref="ResolveForWarmUpAsync"/>.
///
/// <para><b>Selection contract:</b> <see cref="AppDefaults.RecordingDeviceId"/> is the
/// AUTHORITATIVE selection; <see cref="AppDefaults.RecordingDeviceName"/> is optional display
/// metadata — an ID-only record is a valid selection. Absence (or empty/whitespace id) means
/// system default. A pinned-but-absent device falls back to the system default FOR THAT
/// RECORDING and never fails the start; a genuine no-mic failure behaves exactly as before.</para>
///
/// <para><b>BoundedComCall contract:</b> every bounded lambda returns the MMDevice DIRECTLY
/// (never inside a composite), so the helper's abandonment continuation can dispose a
/// late-arriving device. Worst case a missing selected device costs two sequential 500 ms
/// bounds; the no-selection path stays exactly one bounded call.</para>
/// </summary>
public sealed class RecordingDeviceSelectionService
{
    private static ILogger Logger => Log.ForContext<RecordingDeviceSelectionService>();

    private static readonly TimeSpan ResolveBound = TimeSpan.FromMilliseconds(500);

    // Warm-up keeps the pre-AUD-1 2 s bound (App's startup budget tolerates it; recording
    // starts stay on the tight 500 ms bound so a wedged service can't delay the pill).
    private static readonly TimeSpan WarmUpBound = TimeSpan.FromSeconds(2);

    // AUD-16 note, corrected 2026-08-19 (self-review, out-of-scope defect): a separate tighter
    // TagReadBound (250 ms) was declared here and NEVER wired — the id read runs inside the
    // resolve factory's own bound (ResolveBound / WarmUpBound), so a wedged IMMDevice::GetId
    // consumes the resolve's budget rather than a smaller one of its own. The dead constant is
    // deleted rather than wired: wiring a second bound inside the observe callback would need a
    // second worker, which is exactly the abandoned-worker shape AUD-16's review rejected.

    private readonly SettingsService _settings;

    // Bounded-resolution seams (AUD-1 diff review: the deterministic test matrix needs the
    // COM-touching legs injectable — timeout = delegate returns null, cancellation = delegate
    // throws OCE, found = delegate returns a device). Production wires BoundedComCall over
    // AudioDeviceManager; each delegate returns the MMDevice DIRECTLY (the abandonment-disposal
    // contract lives inside BoundedComCall, not here).
    // AUD-16 added the `observe` parameter, and its position is the whole safety argument. The
    // callback runs INSIDE the bounded factory, on the same worker that produced the device and
    // before that device is returned — so the endpoint-id read is bounded by the SAME timeout, and
    // exactly one thread ever touches the device.
    //
    // The two shapes that were tried and are WRONG (Codex diff rounds 1 and 2):
    //   * reading `.ID` after the await, inline — unbounded COM on the recording start path, and on
    //     the standing path that await happens under `_lifecycleLock`, so a wedged endpoint stalls
    //     the whole standing lifecycle;
    //   * reading `.ID` in a SECOND `BoundedComCall` closing over the resolved device — bounded, but
    //     an abandoned worker keeps a reference to the very MMDevice that is handed to capture, so a
    //     timeout can race a native use against the recorder's dispose, and an OCE during the read
    //     throws AFTER resolution and leaks the device entirely.
    // Both are closed by construction here: no second worker exists, and on abandonment
    // BoundedComCall disposes the device this factory produced.
    private readonly global::System.Func<string, TimeSpan, string, global::System.Action<MMDevice>?, CancellationToken, Task<MMDevice?>> _resolveById;
    private readonly global::System.Func<TimeSpan, string, global::System.Action<MMDevice>?, CancellationToken, Task<MMDevice?>> _resolveDefault;

    // AUD-16: reading the endpoint id is COM, so it rides the same injection pattern as the two
    // resolve legs above — otherwise the tag's whole test matrix (populated / sentinel-on-throw /
    // sentinel-on-null) would need a real device, which AGENTS.md forbids tests from opening.
    private readonly global::System.Func<MMDevice, string?> _getEndpointId;

    // One-shot fallback-notice latch: device ids whose fallback notice already showed this run.
    private readonly HashSet<string> _noticeShown = new();
    private readonly object _noticeGate = new();

    public RecordingDeviceSelectionService(SettingsService settings, AudioDeviceManager deviceManager)
        : this(
            settings,
            (id, bound, label, observe, ct) => BoundedComCall.RunBoundedAsync(
                () => { var d = deviceManager.GetDeviceById(id); if (d != null) observe?.Invoke(d); return d; },
                bound, label, dependency: null, ct: ct),
            (bound, label, observe, ct) => BoundedComCall.RunBoundedAsync(
                () => { var d = deviceManager.GetCurrentDevice(); if (d != null) observe?.Invoke(d); return d; },
                bound, label, dependency: null, ct: ct),
            // AUD-16: `MMDevice.ID` is IMMDevice::GetId — a LIVE COM call into the audio service,
            // not a cached managed value. It is cheaper than the property-store open that AUD-11's
            // FriendlyName was (which is why a tag is affordable where a name is not), but "cheaper"
            // is not "bounded", and this repo has been burned precisely by assuming a COM call is
            // safe. The read therefore happens INSIDE the bounded resolve factory above (via observe), so
            // it shares that timeout and no second worker ever touches the device.
            d => d.ID)
    {
    }

    internal RecordingDeviceSelectionService(
        SettingsService settings,
        global::System.Func<string, TimeSpan, string, global::System.Action<MMDevice>?, CancellationToken, Task<MMDevice?>> resolveById,
        global::System.Func<TimeSpan, string, global::System.Action<MMDevice>?, CancellationToken, Task<MMDevice?>> resolveDefault,
        global::System.Func<MMDevice, string?>? getEndpointId = null)
    {
        _settings = settings;
        _resolveById = resolveById;
        _resolveDefault = resolveDefault;
        _getEndpointId = getEndpointId ?? (d => d.ID);
    }

    /// <summary>A per-resolution tag slot. Created fresh at each call site and handed to the bounded
    /// factory as its <c>observe</c> callback, so the endpoint-id read happens on the worker that
    /// produced the device, inside the same timeout, before the device is returned to anyone.
    ///
    /// <para>If the wait is abandoned, the late worker still writes only into THIS object, which
    /// nothing else reads, while <see cref="BoundedComCall"/> disposes the device it produced. That
    /// is what makes the abandoned-worker case inert rather than a use-after-dispose race.</para></summary>
    private sealed class TagSlot
    {
        internal string Tag = Helpers.CaptureDeviceTag.Unknown;

        // AUD-18: the raw endpoint id, captured by the same read that derives the tag. Null when
        // the read failed or returned blank — the effects line then reports "unavailable" rather
        // than querying a malformed id.
        internal string? Id;
    }

    private global::System.Action<MMDevice> Observer(TagSlot slot) =>
        d =>
        {
            var (id, tag) = IdAndTagFromReader(() => _getEndpointId(d));
            slot.Id = id;
            slot.Tag = tag;
        };

    /// <summary>The fail-soft read, over a delegate rather than an <c>MMDevice</c>.
    ///
    /// <para>The indirection is what makes this testable at all: <c>MMDevice</c> has no public
    /// constructor and <c>AGENTS.md</c> forbids tests opening a real device, so a signature taking
    /// the device could only ever be exercised on its null branch — the throwing-read branch, which
    /// is the one that must not break a recording, would ship unpinned.</para></summary>
    internal static string TagFromIdReader(global::System.Func<string?> readId)
        => IdAndTagFromReader(readId).Tag;

    /// <summary>AUD-18 widened the fail-soft read to return the RAW id alongside the tag — one
    /// COM read, two consumers (the tag for logs, the id for the in-process effects query). The
    /// failure contract is unchanged: a throwing or blank read yields (null, unknown) and never
    /// breaks a recording.</summary>
    internal static (string? Id, string Tag) IdAndTagFromReader(global::System.Func<string?> readId)
    {
        try
        {
            var id = readId();
            return (string.IsNullOrWhiteSpace(id) ? null : id, Helpers.CaptureDeviceTag.For(id));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading the capture endpoint id for its log tag threw — tagging unknown");
            return (null, Helpers.CaptureDeviceTag.Unknown);
        }
    }

    /// <summary>The parsed pinned selection, or null when none. Pure settings read — no COM.</summary>
    public (string Id, string? Name)? ReadSelection()
    {
        var id = _settings.GetString(AppDefaults.RecordingDeviceId, "");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var name = _settings.GetString(AppDefaults.RecordingDeviceName, "");
        return (id, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>
    /// Resolve the device for a recording start. Never throws for a missing selection —
    /// classification carries the outcome; OCE propagates (caller-cancel).
    /// </summary>
    public async Task<ResolvedRecordingDevice> ResolveForRecordingAsync(CancellationToken ct)
    {
        var selection = ReadSelection();
        var slot = new TagSlot();
        if (selection is not { } sel)
        {
            var dev = await _resolveDefault(ResolveBound, "recording-GetCurrentDevice", Observer(slot), ct);
            return new ResolvedRecordingDevice(dev, RecordingDeviceResolutionKind.SystemDefault, null, null,
                dev is null ? Helpers.CaptureDeviceTag.Unknown : slot.Tag,
                dev is null ? null : slot.Id);
        }

        var selected = await _resolveById(sel.Id, ResolveBound, "recording-GetDeviceById", Observer(slot), ct);
        if (selected != null)
            return new ResolvedRecordingDevice(selected, RecordingDeviceResolutionKind.SelectedDevice, sel.Id, sel.Name, slot.Tag, slot.Id);

        // A FRESH slot: the byId attempt above may have written into the first one, and this is a
        // different device.
        var fallbackSlot = new TagSlot();
        var fallback = await _resolveDefault(ResolveBound, "recording-GetCurrentDevice", Observer(fallbackSlot), ct);
        // SEC-3: same class as the pin line — a persisted endpoint name, user-identifying, and
        // Warning is above Sentry's breadcrumb threshold. Specific property + trailing `micName=`
        // token, never the generic {Name}.
        Logger.Warning("Selected recording device unavailable — using system default for this recording (id present): micName={RecordingDeviceName}",
            Helpers.LogValueSanitizer.SingleLine(sel.Name ?? "<unset>"));
        return new ResolvedRecordingDevice(fallback, RecordingDeviceResolutionKind.FallbackToDefault, sel.Id, sel.Name,
            fallback is null ? Helpers.CaptureDeviceTag.Unknown : fallbackSlot.Tag,
            fallback is null ? null : fallbackSlot.Id);
    }

    /// <summary>One retry with the system default after a SELECTED device's activation failed
    /// (the resolve→activation race: the endpoint vanished between GetDeviceById and WASAPI
    /// init). Bounded like every recording-path resolution.
    ///
    /// <para>AUD-16: it returns the TAG alongside the device, rather than offering a separate
    /// "tag this device I already have" call. That shape is deliberate and was forced by review — a
    /// second bounded read closing over an already-returned device is exactly the abandoned-worker /
    /// use-after-dispose race described on the <c>_resolveById</c> field. Tagging is only ever safe
    /// on the worker that produced the device, so it is only ever offered there.</para>
    ///
    /// <para>The retry path needs its own tag because otherwise it would LIE: the recording starts on
    /// the fallback device while the resolution record still carries the SELECTED device's tag, so a
    /// user who pins a mic and unplugs it between resolve and activation would get a recording
    /// attributed to a device it was never captured from. Post-hoc attribution is this feature's only
    /// purpose; attributing falsely is worse than the gap it closes (Kimi plan review).</para></summary>
    public async Task<(MMDevice? Device, string Tag, string? EndpointId)> ResolveDefaultWithTagAsync(CancellationToken ct)
    {
        var slot = new TagSlot();
        var device = await _resolveDefault(ResolveBound, "recording-GetCurrentDevice-retry", Observer(slot), ct)
            .ConfigureAwait(false);
        return (device,
            device is null ? Helpers.CaptureDeviceTag.Unknown : slot.Tag,
            device is null ? null : slot.Id);
    }

    /// <summary>Retry policy for a thrown <c>StartRecordingAsync</c>: only a SELECTED device's
    /// DEVICE-SHAPED start failure earns the one default-device retry — a stale selection must
    /// never fail the recording, but a genuine no-mic failure keeps today's behavior. The
    /// allowlist is deliberate (diff review): activation failures surface as
    /// <see cref="AudioCaptureUnavailableException"/> (bounded-constructor timeout),
    /// <see cref="global::System.Runtime.InteropServices.COMException"/> (WASAPI activation/start),
    /// or <see cref="InvalidOperationException"/> (capture stopped immediately after start) —
    /// file I/O or permission failures (WAV writer) would fail identically on the default
    /// device, so they propagate unretried. OCE is caller-cancel, never retried.</summary>
    public static bool ShouldRetryWithDefault(RecordingDeviceResolutionKind kind, Exception ex) =>
        kind == RecordingDeviceResolutionKind.SelectedDevice
        && ex is not OperationCanceledException
        && ex is AudioCaptureUnavailableException
            or global::System.Runtime.InteropServices.COMException
            or InvalidOperationException;

    /// <summary>True exactly once per device id per app run — the fallback notice must inform,
    /// not nag (a train ride's every recording would otherwise re-show it).</summary>
    public bool TryClaimFallbackNotice(string deviceId)
    {
        lock (_noticeGate) return _noticeShown.Add(deviceId);
    }

    /// <summary>Selection-aware warm-up device (App startup). Best-effort: any failure returns
    /// null (warm-up proceeds against the WASAPI default); the notice latch is NOT consumed —
    /// warm-up is a background optimization, not a user action.</summary>
    public async Task<MMDevice?> ResolveForWarmUpAsync()
    {
        try
        {
            var selection = ReadSelection();
            if (selection is { } sel)
            {
                var selected = await _resolveById(sel.Id, WarmUpBound, "warmup-GetDeviceById", null, CancellationToken.None);
                if (selected != null) return selected;
            }

            return await _resolveDefault(WarmUpBound, "warmup-GetSystemDefaultDevice", null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Warm-up device resolution failed (non-critical)");
            return null;
        }
    }
}
