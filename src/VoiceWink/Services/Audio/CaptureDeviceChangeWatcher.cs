using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace VoiceWink.Services.Audio;

/// <summary>AUD-20: endpoint notifications for the Settings Microphone list; callbacks run on MMDevAPI threads, so onChange only hands off.</summary>
internal sealed class CaptureDeviceChangeWatcher : IDisposable
{
    private static ILogger Logger => Log.ForContext<CaptureDeviceChangeWatcher>();

    private readonly object _gate = new();
    private readonly Client _client;
    private MMDeviceEnumerator? _enumerator; // published only once registration has finished
    private bool _disposed;

    private CaptureDeviceChangeWatcher(Action onChange) => _client = new Client(onChange);

    /// <summary>Returns at once; registration runs on the thread pool, never the UI thread.</summary>
    internal static CaptureDeviceChangeWatcher Start(Action onChange)
    {
        var watcher = new CaptureDeviceChangeWatcher(onChange);
        _ = Task.Run(watcher.Register);
        return watcher;
    }

    private void Register()
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            lock (_gate) if (_disposed) return;
            enumerator = new MMDeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(_client);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _enumerator = enumerator;
                    enumerator = null;
                }
            }
            if (enumerator is null)
            {
                // A change between the page's first enumeration and this registration went unseen.
                _client.Signal();
                return;
            }
            Release(enumerator, _client); // disposed while registering
        }
        catch (Exception ex)
        {
            Logger.Warning("Device-change notifications unavailable: {ExType} — the Microphone list refreshes on dropdown open only",
                ex.GetType().Name);
            if (enumerator is not null) Release(enumerator, null);
        }
    }

    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            enumerator = _enumerator;
            _enumerator = null;
        }
        // Off the caller's (UI) thread: a wedged audio service must not stall navigation.
        if (enumerator is not null) _ = Task.Run(() => Release(enumerator, _client));
    }

    private static void Release(MMDeviceEnumerator enumerator, Client? client)
    {
        try { if (client is not null) enumerator.UnregisterEndpointNotificationCallback(client); }
        catch (Exception ex) { Logger.Debug(ex, "Unregistering device-change notifications failed"); }
        try { enumerator.Dispose(); }
        catch (Exception ex) { Logger.Debug(ex, "Disposing device-change enumerator failed"); }
    }

    /// <summary>Every add, remove and state change signals: reading their data flow would be a COM call here.</summary>
    internal sealed class Client : IMMNotificationClient
    {
        private readonly Action _onChange;
        internal Client(Action onChange) => _onChange = onChange;

        internal void Signal()
        {
            try { _onChange(); }
            catch (Exception ex) { Logger.Debug(ex, "Device-change handling failed"); }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Signal();
        public void OnDeviceAdded(string pwstrDeviceId) => Signal();
        public void OnDeviceRemoved(string deviceId) => Signal();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == AudioDeviceManager.SystemDefaultCaptureRole) Signal();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
