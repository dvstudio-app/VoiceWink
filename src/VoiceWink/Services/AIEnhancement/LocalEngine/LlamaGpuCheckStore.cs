using System.Text.Json;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LAI-2: the persisted GPU self-test verdicts, <c>%LOCALAPPDATA%\VoiceWink\llama-gpu-check.json</c>
/// beside <c>gpu-warmup.json</c> (per machine, outside settings, for the same reason). Keyed by
/// (llama build, display-driver signature, model identity): a new llama.cpp build or a changed
/// driver discards every verdict — the TRN-63 rule — so a driver update gets a fresh test.
/// A malformed file reads as empty. Writes are write-then-move so a kill mid-write cannot leave a
/// torn file; a transient fault (an on-access scanner holding the file) is retried, and a write
/// whose read of the existing file faults gives up rather than erase it (so a file that stays
/// unreadable but replaceable is never rewritten: the self-test simply runs again). A FAIL pins
/// that model to the CPU; a PASS carries the ROUTE the timing comparison chose (GPU or CPU) and
/// the two measurements, and a Pass row without a route - written before the comparison existed -
/// reads as no verdict, so the check runs once more. <c>Unknown</c> is never stored. GDPR erasure
/// deletes the file (<c>DataErasureService</c>).
/// </summary>
internal sealed class LlamaGpuCheckStore
{
    internal static string DefaultPath => Path.Combine(AppPaths.RootDir, "llama-gpu-check.json");

    /// <summary>Verdicts kept per (build, driver); the oldest is dropped past this.</summary>
    internal const int MaxModels = 20;

    private readonly string _path;
    private readonly string _build;
    private readonly string? _driverSignature;
    private readonly object _lock = new();

    internal LlamaGpuCheckStore(string path, string build, string? driverSignature)
    {
        _path = path;
        _build = build;
        _driverSignature = driverSignature;
    }

    /// <summary>The wait before retry <c>n</c> of a transient file fault (<see cref="TransientFileRetry"/>).
    /// A test seam: a test uses it to release a handle it holds, so the retry path is deterministic.</summary>
    internal Action<int> Backoff { get; init; } = TransientFileRetry.DefaultBackoff;

    /// <summary><paramref name="Route"/> is set on every Pass this returns, never on a Fail.</summary>
    internal readonly record struct Entry(LlamaSelfTestVerdict Verdict, string? Adapter, LlamaRoute? Route = null);

    /// <summary>The stored verdict for <paramref name="modelIdentity"/> under this build and driver,
    /// or null. An unreadable driver signature (null) never matches a stored one, so it reads as
    /// "no verdict" and the test runs again.</summary>
    internal Entry? Get(string modelIdentity)
    {
        lock (_lock)
        {
            var row = ReadCurrent()?.Models.FirstOrDefault(m => string.Equals(m.Model, modelIdentity, StringComparison.Ordinal));
            // Member names only: Enum.TryParse would also accept "1" or "Unknown".
            return (row?.Verdict, row?.Route) switch
            {
                (nameof(LlamaSelfTestVerdict.Pass), nameof(LlamaRoute.Gpu)) => new Entry(LlamaSelfTestVerdict.Pass, row!.Adapter, LlamaRoute.Gpu),
                (nameof(LlamaSelfTestVerdict.Pass), nameof(LlamaRoute.Cpu)) => new Entry(LlamaSelfTestVerdict.Pass, row!.Adapter, LlamaRoute.Cpu),
                (nameof(LlamaSelfTestVerdict.Fail), _) => new Entry(LlamaSelfTestVerdict.Fail, row!.Adapter),
                _ => null,
            };
        }
    }

    /// <summary>Record a Fail, or a Pass with the route its timing comparison chose; Unknown and a
    /// Pass without a route are ignored. False when nothing was written (best effort — the test
    /// simply runs again next time). The timings are kept for support, never read back.</summary>
    internal bool Record(string modelIdentity, LlamaSelfTestVerdict verdict, string? adapter,
        LlamaRoute? route = null, int? gpuMs = null, int? cpuMs = null)
    {
        if (verdict == LlamaSelfTestVerdict.Unknown || _driverSignature is null
            || (verdict == LlamaSelfTestVerdict.Pass && route is null))
        {
            return false;
        }
        lock (_lock)
        {
            try
            {
                // The whole read-modify-write is the retried unit, so a retry never persists a
                // state read before the fault. ReadCurrentOrThrow, never ReadCurrent: a read that
                // faulted must abandon the write — standing in for "no file" it would persist a
                // fresh state and erase every other model's verdict, a CPU pin included.
                return TransientFileRetry.Run(() =>
                {
                    var state = ReadCurrentOrThrow() ?? new State { Build = _build, Driver = _driverSignature };
                    state.Models.RemoveAll(m => string.Equals(m.Model, modelIdentity, StringComparison.Ordinal));
                    state.Models.Add(new ModelRow
                    {
                        Model = modelIdentity,
                        Verdict = verdict.ToString(),
                        Adapter = adapter,
                        Route = verdict == LlamaSelfTestVerdict.Pass ? route.ToString() : null,
                        GpuMs = verdict == LlamaSelfTestVerdict.Pass ? gpuMs : null,
                        CpuMs = verdict == LlamaSelfTestVerdict.Pass ? cpuMs : null,
                    });
                    if (state.Models.Count > MaxModels)
                    {
                        state.Models.RemoveRange(0, state.Models.Count - MaxModels);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(state));
                    File.Move(tmp, _path, overwrite: true);
                    return true;
                }, Backoff);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary><see cref="ReadCurrentOrThrow"/> with transient faults retried; a fault that
    /// persists reads as null (no verdict — the test runs again).</summary>
    private State? ReadCurrent()
    {
        try
        {
            return TransientFileRetry.Run(ReadCurrentOrThrow, Backoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One read: the file's state when it belongs to this build and driver; null otherwise
    /// (absent, malformed, another build, another driver, or an unreadable driver signature). A
    /// file-system fault THROWS, so the write path can tell "nothing to keep" from "could not look".</summary>
    private State? ReadCurrentOrThrow()
    {
        if (_driverSignature is null || !File.Exists(_path))
        {
            return null;
        }
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (FileNotFoundException)
        {
            return null; // removed since the check: there is nothing to keep
        }
        State? state;
        try
        {
            state = JsonSerializer.Deserialize<State>(text);
        }
        catch (JsonException)
        {
            return null;
        }
        return state is not null
               && string.Equals(state.Build, _build, StringComparison.Ordinal)
               && string.Equals(state.Driver, _driverSignature, StringComparison.Ordinal)
               && state.Models is not null
               && !state.Models.Contains(null!)
            ? state
            : null;
    }

    private sealed class State
    {
        public string? Build { get; set; }
        public string? Driver { get; set; }
        public List<ModelRow> Models { get; set; } = [];
    }

    private sealed class ModelRow
    {
        public string? Model { get; set; }
        public string? Verdict { get; set; }
        public string? Adapter { get; set; }
        public string? Route { get; set; }
        public int? GpuMs { get; set; }
        public int? CpuMs { get; set; }
    }
}
