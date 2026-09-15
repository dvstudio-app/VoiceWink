using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Enums;

namespace VoiceWink.Services.Audio;

/// <summary>
/// Audio input device enumeration and selection.
/// All COM audio calls run on a dedicated background STA thread to prevent blocking the UI
/// thread when devices are transitioning (e.g., headset connecting while Teams has mic focus).
/// </summary>
public sealed class AudioDeviceManager : IDisposable
{
    private static ILogger Logger => Log.ForContext<AudioDeviceManager>();

    private readonly Thread _staThread;
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly TaskCompletionSource _threadReady = new();
    private MMDeviceEnumerator? _enumerator;
    // No long-lived enumerator on the UI thread — COM device-change notifications
    // dispatched through a long-lived enumerator can deadlock the UI message pump
    // when audio devices disconnect (e.g., Jabra headset powered off).
    private List<AudioDeviceInfo>? _availableDevices;
    private volatile bool _devicesLoaded;

    public IReadOnlyList<AudioDeviceInfo> AvailableDevices
    {
        get
        {
            EnsureDevicesLoaded();
            return _availableDevices ?? [];
        }
    }

    public AudioInputMode Mode { get; set; } = AudioInputMode.SystemDefault;

    public event Action? DevicesChanged;

    public AudioDeviceManager()
    {
        _staThread = new Thread(StaThreadProc)
        {
            Name = "AudioDeviceManager-STA",
            IsBackground = true
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void StaThreadProc()
    {
        _enumerator = new MMDeviceEnumerator();
        _threadReady.SetResult();

        // Pre-warm: enumerate devices in the background so USB audio drivers
        // are initialized before the first recording (avoids cold-start delay).
        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var d in devices) d.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "STA pre-warm device enumeration failed (non-critical)");
        }

        // Pump work items until the collection is marked complete (Dispose)
        try
        {
            foreach (var work in _workQueue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Logger.Error(ex, "STA work item failed"); }
            }
        }
        catch (OperationCanceledException) { }

        _enumerator.Dispose();
    }

    /// <summary>Run a func on the STA thread and wait for the result.</summary>
    private T RunOnSta<T>(Func<T> func)
    {
        _threadReady.Task.Wait();

        // If we're already on the STA thread, run directly to avoid deadlock
        if (Thread.CurrentThread == _staThread)
            return func();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _workQueue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            // WorkQueue completed (Dispose called) — fail gracefully
            throw new ObjectDisposedException(nameof(AudioDeviceManager));
        }
        return tcs.Task.GetAwaiter().GetResult();
    }

    private void EnsureDevicesLoaded()
    {
        if (!_devicesLoaded)
            RefreshDevices();
    }

    public void RefreshDevices()
    {
        try
        {
            _availableDevices = RunOnSta(() =>
            {
                var devices = _enumerator!.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                var result = new List<AudioDeviceInfo>();
                foreach (var d in devices)
                {
                    result.Add(new AudioDeviceInfo
                    {
                        Id = d.ID,
                        Name = d.FriendlyName,
                        IsDefault = false,
                        Kind = ClassifyEndpoint(d)
                    });
                    d.Dispose();
                }

                try
                {
                    using var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    var match = result.FirstOrDefault(d => d.Id == defaultDevice.ID);
                    if (match != null)
                        match.IsDefault = true;
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Could not determine default capture device");
                }

                return result;
            });

            _devicesLoaded = true;

            // AUD-4: the endpoint kinds ride the line that was already here, which is the whole
            // point of logging them HERE and not at recording start — classification has already
            // run during this enumeration, so it costs nothing, while a read on the resolve path
            // would add latency to exactly the span AUD-6 exists to shorten.
            //
            // This is the only consumer of the classification since the Bluetooth advisory was
            // removed 2026-08-03. It is diagnostics, not advice: a support bundle showing empty
            // or poor transcription reads very differently once the capture endpoint kind is
            // known, and this session cost hours that a line like this would have saved.
            Logger.Information("Found {Count} audio input devices ({Kinds})",
                _availableDevices.Count,
                DescribeKinds(_availableDevices));
            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate audio devices");
            _availableDevices = new List<AudioDeviceInfo>();
            _devicesLoaded = true;
        }
    }

    /// <summary>AUD-4: counts per endpoint kind for the enumeration log line, e.g.
    /// <c>2 Microphone, 1 BluetoothHandsFree</c>. Counts only — device NAMES are user-identifying
    /// and this line goes into support bundles, so it must stay content-free.</summary>
    private static string DescribeKinds(IReadOnlyCollection<AudioDeviceInfo> devices) =>
        devices.Count == 0
            ? "none"
            : string.Join(", ", devices
                .GroupBy(d => d.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}"));

    /// <summary>
    /// AUD-4: read the two signals the classifier needs — the endpoint form factor and every
    /// string-valued property — and classify. Best-effort by design: a property store that
    /// refuses to enumerate yields <see cref="CaptureEndpointKind.Unknown"/>, which produces no
    /// advice, because a wrong hint is worse than no hint. Runs inside the STA enumeration pass
    /// where the MMDevice is already open, so it costs no extra device activation.
    /// The read itself moved to <see cref="EndpointClassificationReader"/> (AUD-6) so the
    /// standing warm capture classifies its resolved device with the exact same code.
    /// </summary>
    private static CaptureEndpointKind ClassifyEndpoint(MMDevice device)
        => EndpointClassificationReader.Classify(device);

    /// <summary>
    /// Returns the MMDevice to use for recording based on current mode.
    /// </summary>
    public MMDevice? GetCurrentDevice() => GetSystemDefaultDevice();

    /// <summary>
    /// AUD-1: resolve a capture device by its WASAPI endpoint ID. Returns null when the
    /// device is absent, not active (unplugged/disabled — disposed before returning, so a
    /// rejected MMDevice never leaks a COM proxy), or the lookup fails. Same threading
    /// contract as <see cref="GetSystemDefaultDevice"/>: synchronous COM on the caller's
    /// thread — recording-path callers wrap it in <c>BoundedComCall</c>.
    /// </summary>
    public MMDevice? GetDeviceById(string id)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(id);
            if (device == null) return null;
            if (device.State != DeviceState.Active)
            {
                Logger.Information("Selected capture device is not active (state={State})", device.State);
                device.Dispose();
                return null;
            }
            return device;
        }
        catch (Exception ex)
        {
            Logger.Information("Selected capture device unavailable: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            device?.Dispose();
            return null;
        }
    }

    // AUD-1: single-flight latch for RefreshDevicesAsync — concurrent callers share one pass.
    private Task<IReadOnlyList<AudioDeviceInfo>>? _refreshInFlight;
    private readonly object _refreshGate = new();

    /// <summary>
    /// AUD-1: off-caller-thread device enumeration for UI callers — the synchronous
    /// <see cref="RefreshDevices"/> waits on the STA worker and can block a UI thread
    /// indefinitely behind a wedged audio service. Coalesced: concurrent callers await
    /// the SAME pass. Returns an immutable snapshot; also updates
    /// <see cref="AvailableDevices"/> and fires <see cref="DevicesChanged"/> like the
    /// synchronous path.
    /// </summary>
    public Task<IReadOnlyList<AudioDeviceInfo>> RefreshDevicesAsync()
    {
        lock (_refreshGate)
        {
            if (_refreshInFlight is { } inFlight)
                return inFlight;

            var pass = Task.Run<IReadOnlyList<AudioDeviceInfo>>(() =>
            {
                try
                {
                    RefreshDevices();
                    return _availableDevices?.ToArray() ?? [];
                }
                finally
                {
                    lock (_refreshGate) _refreshInFlight = null;
                }
            });
            _refreshInFlight = pass;
            return pass;
        }
    }

    /// <summary>
    /// Returns the system default capture device.
    ///
    /// <para><b>Thread / apartment notes:</b> NAudio's MMDevice and WasapiCapture
    /// wrap COM objects in the MMDevice / WASAPI APIs, which are registered as
    /// "ThreadingModel=Both" — agile across MTA apartments. The returned
    /// MMDevice can be passed to a <see cref="NAudio.CoreAudioApi.WasapiCapture"/>
    /// constructor on a different MTA worker without marshaling, and the
    /// capture's data-available callback fires on its own internal audio
    /// thread regardless of who constructed it. We do not need a dedicated
    /// owned thread for the audio capture lifecycle; the only requirement is
    /// that a single owner manages disposal (here, <c>AudioRecorderService</c>).</para>
    ///
    /// <para><b>Hang isolation:</b> this call is wrapped by
    /// <see cref="VoiceWink.Helpers.BoundedComCall"/> at every caller so a wedged
    /// audio service cannot stall the calling thread. Under heavy CPU
    /// contention this synchronous COM enumeration has been observed to take
    /// 28+ seconds (logged as <c>"GetSystemDefaultDevice took NNNNms (slow COM call)"</c>);
    /// the bounded wrapper times out long before that.</para>
    /// </summary>
    public MMDevice? GetSystemDefaultDevice()
    {
        try
        {
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            sw.Stop();
            if (sw.ElapsedMilliseconds > 50)
                Logger.Warning("GetSystemDefaultDevice took {Elapsed}ms (slow COM call)", sw.ElapsedMilliseconds);
            return device;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "No default capture device available");
            return null;
        }
    }

    public void Dispose()
    {
        _workQueue.CompleteAdding();
        _staThread.Join(timeout: TimeSpan.FromSeconds(2));
        _workQueue.Dispose();
    }
}

public class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; set; }

    /// <summary>AUD-4: how this endpoint captures. <b>Diagnostics only since 2026-08-03</b> —
    /// nothing user-facing derives from it, and it never gated or altered capture. It exists to
    /// put the endpoint kind in the enumeration log line so a support bundle can be read.</summary>
    public CaptureEndpointKind Kind { get; init; } = CaptureEndpointKind.Unknown;
}
