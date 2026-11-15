using Serilog;

namespace VoiceWink.Services.Audio;

/// <summary>HIS-11: the microphone a recording was made with; both parts null when unknown.</summary>
public readonly record struct RecordedCaptureDevice(string? Name, string? Tag);

/// <summary>
/// HIS-11: which microphone made each recording, keyed by its WAV path until the History row is written.
/// </summary>
public sealed class RecordingDeviceLedger
{
    private static ILogger Logger => Log.ForContext<RecordingDeviceLedger>();

    // Retained recordings are a handful; the bound only stops the map growing for the life of the process.
    internal const int MaxEntries = 32;

    private sealed record Entry(string? Tag, string? EndpointId, string? Name);

    private readonly Func<string, string?> _findCachedName;
    private readonly Func<Task> _refreshNames;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, string> _namesById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshAsked = new(StringComparer.Ordinal);

    /// <param name="findCachedName">Endpoint id to its name from the last device enumeration; must make no device call.</param>
    /// <param name="refreshNames">Starts a device enumeration off the caller's thread.</param>
    public RecordingDeviceLedger(Func<string, string?> findCachedName, Func<Task> refreshNames)
    {
        _findCachedName = findCachedName;
        _refreshNames = refreshNames;
    }

    /// <summary>Record the device a recording started on. Call after the start succeeded; never throws.</summary>
    public void Note(string? wavPath, string? deviceTag, string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(wavPath)) return;

        var tag = string.IsNullOrWhiteSpace(deviceTag) || deviceTag == Helpers.CaptureDeviceTag.Unknown ? null : deviceTag;
        var id = string.IsNullOrWhiteSpace(endpointId) ? null : endpointId;
        var refresh = false;

        lock (_gate)
        {
            var name = ResolveNameLocked(id);
            if (!_byPath.ContainsKey(wavPath))
            {
                _order.Enqueue(wavPath);
                while (_order.Count > MaxEntries) _byPath.Remove(_order.Dequeue());
            }
            _byPath[wavPath] = new Entry(tag, id, name);
            // One enumeration per endpoint per run: a machine whose enumeration fails must not repeat it every recording.
            refresh = name is null && id is not null && _refreshAsked.Add(id);
        }

        if (refresh) StartRefresh(id!);
    }

    /// <summary>The device noted for this recording; default (both null) when none was.</summary>
    public RecordedCaptureDevice Find(string? wavPath)
    {
        if (string.IsNullOrWhiteSpace(wavPath)) return default;

        lock (_gate)
        {
            if (!_byPath.TryGetValue(wavPath, out var entry)) return default;
            var name = entry.Name ?? ResolveNameLocked(entry.EndpointId);
            if (name is not null && entry.Name is null) _byPath[wavPath] = entry with { Name = name };
            return new RecordedCaptureDevice(name, entry.Tag);
        }
    }

    private string? ResolveNameLocked(string? endpointId)
    {
        if (endpointId is null) return null;

        string? name = null;
        try { name = _findCachedName(endpointId); }
        catch (Exception ex)
        {
            Logger.Debug("Reading the cached microphone name failed: {ErrorType}", ex.GetType().Name);
        }

        // The snapshot wins (a renamed device gets its new name); the remembered one covers a device since unplugged.
        if (string.IsNullOrWhiteSpace(name)) return _namesById.TryGetValue(endpointId, out var known) ? known : null;
        _namesById[endpointId] = name;
        return name;
    }

    private void StartRefresh(string endpointId)
    {
        try
        {
            _refreshNames().ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                        Logger.Debug("Microphone name refresh failed: {ErrorType}", t.Exception?.GetBaseException().GetType().Name);
                    // Remember the name now: this endpoint gets no second refresh, and its recording may never reach Find.
                    else if (t.IsCompletedSuccessfully)
                        lock (_gate) _ = ResolveNameLocked(endpointId);
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Debug("Microphone name refresh could not start: {ErrorType}", ex.GetType().Name);
        }
    }
}
