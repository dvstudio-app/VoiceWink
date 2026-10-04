using System.IO;
using System.Text.Json;
using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// This PC's measured speed for each local speech model: the time one speed check
/// (<see cref="Services.Transcription.SpeechSpeedCheck"/>) took to transcribe the bundled test clip,
/// per (model, compute class). The Models page rates Speed from it, and calibrates the estimate of
/// every unmeasured row of the same engine from it, so the stars change together and at one clear
/// moment (owner rule, 2026-10-03: stars must never jump around without the user knowing why).
///
/// <para><c>%LOCALAPPDATA%\VoiceWink\transcription-speed.json</c>, beside <c>gpu-warmup.json</c>:
/// model ids (canonical catalog names), the compute class and milliseconds only. Keyed by the
/// display-driver signature and <see cref="CurrentCheckVersion"/>: a changed driver or a changed check
/// discards every measurement, so the checks run again (and the change is announced). Loaded once,
/// then held in memory; every record writes through (write-then-move, a transient file fault retried).
/// A malformed file reads as empty; a file that cannot be READ is never replaced — nothing is recorded
/// until a later read succeeds, so a locked file never costs the saved measurements. GDPR erasure stops
/// the writes (<see cref="StopWrites"/>) and then deletes the file (<c>DataErasureService</c>).</para>
///
/// <para>Process-wide through <see cref="Configure"/> at the composition root, the
/// <see cref="LocalComputeSnapshot"/> pattern: unconfigured (tests, harnesses) means no measurements,
/// so every star is the estimate's.</para>
/// </summary>
internal sealed class TranscriptionSpeedStore
{
    internal static string DefaultPath => Path.Combine(AppPaths.RootDir, "transcription-speed.json");

    /// <summary>Which check produced the stored times; a different value reads as no measurement.</summary>
    internal const int CurrentCheckVersion = 1;

    /// <summary>A time above this is not recorded (a stalled check, a suspended PC).</summary>
    internal const int MaxMs = 10 * 60 * 1000;

    private const int MaxModelIdLength = 128;

    private static TranscriptionSpeedStore? s_current;

    /// <summary>The process-wide store, or null when none is configured.</summary>
    internal static TranscriptionSpeedStore? Current => Volatile.Read(ref s_current);

    internal static void Configure(TranscriptionSpeedStore store)
        => Volatile.Write(ref s_current, store ?? throw new ArgumentNullException(nameof(store)));

    /// <summary>Test seam: back to no store.</summary>
    internal static void ResetForTests() => Volatile.Write(ref s_current, null);

    private readonly string _path;
    private readonly string? _driverSignature;
    private readonly object _lock = new();
    private List<Row>? _rows;
    private bool _stopped;

    internal TranscriptionSpeedStore(string path, string? driverSignature)
    {
        _path = path;
        _driverSignature = driverSignature;
    }

    /// <summary>The wait before retry <c>n</c> of a transient file fault (<see cref="TransientFileRetry"/>).</summary>
    internal Action<int> Backoff { get; init; } = TransientFileRetry.DefaultBackoff;

    /// <summary>The measured time for a local catalog model on one compute class, or null.</summary>
    internal int? MeasuredMs(string? model, LocalCompute compute)
    {
        if (CanonicalLocalModel(model) is not string key) return null;
        lock (_lock)
        {
            return Rows()?.FirstOrDefault(r => r.Matches(key, compute))?.Ms;
        }
    }

    /// <summary>A read-only copy of the measurements for ONE page render (Codex diff round): read
    /// once, so a check finishing mid-render can never show changed stars beside an unchanged
    /// fingerprint, or two different stars for one model. Never writes.</summary>
    internal TranscriptionSpeedStore Snapshot()
    {
        lock (_lock)
        {
            return new TranscriptionSpeedStore(_path, _driverSignature)
            {
                _rows = Rows()?.Select(r => new Row { Model = r.Model, Compute = r.Compute, Ms = r.Ms }).ToList() ?? [],
                _stopped = true,
            };
        }
    }

    /// <summary>Every measurement, for the calibration of the unmeasured rows.</summary>
    internal IReadOnlyList<(string Model, LocalCompute Compute, int Ms)> All()
    {
        lock (_lock)
        {
            return Rows()?.Select(r => (r.Model!, Enum.Parse<LocalCompute>(r.Compute!), r.Ms)).ToList()
                ?? [];
        }
    }

    /// <summary>Record one check's time. Ignored for a cloud or unknown model, for a time outside
    /// (0, <see cref="MaxMs"/>], after <see cref="StopWrites"/>, and while the file cannot be read.
    /// False when nothing was persisted.</summary>
    internal bool Record(string? model, LocalCompute compute, TimeSpan elapsed)
    {
        if (CanonicalLocalModel(model) is not string key) return false;
        var ms = elapsed.TotalMilliseconds;
        if (!(ms > 0) || ms > MaxMs) return false;
        lock (_lock)
        {
            if (_stopped || Rows() is not { } rows) return false;
            rows.RemoveAll(r => r.Matches(key, compute));
            rows.Add(new Row { Model = key, Compute = compute.ToString(), Ms = Math.Max(1, (int)Math.Round(ms)) });
            return Write(rows);
        }
    }

    /// <summary>GDPR erasure: no write after this returns. Taken under the store's lock, so a write
    /// already running finishes first and a later one finds the flag; the in-memory measurements keep
    /// answering until the process exits.</summary>
    internal void StopWrites()
    {
        lock (_lock) _stopped = true;
    }

    /// <summary>The table's canonical name for a LOCAL catalog row (either Parakeet spelling maps
    /// to the active row), or null for a cloud or unknown model.</summary>
    internal static string? CanonicalLocalModel(string? model)
    {
        var rating = ModelRatings.Find(model);
        return rating is not null && PredefinedModels.RuntimeOf(rating.Model) is not null ? rating.Model : null;
    }

    /// <summary>The measurements, loaded on first use; null while the file cannot be read (not
    /// cached, so the next call reads again).</summary>
    private List<Row>? Rows() => _rows ??= Load();

    private List<Row>? Load()
    {
        try
        {
            // Read directly, never behind File.Exists: that answers false for an access error too,
            // and caching that as empty let the next record overwrite the saved measurements (Codex
            // diff round, #1134). Only the two not-found exceptions mean "no file yet".
            var text = TransientFileRetry.Run(() => File.ReadAllText(_path), Backoff);
            var state = JsonSerializer.Deserialize<State>(text);
            if (state is null || state.CheckVersion != CurrentCheckVersion
                || !string.Equals(state.Driver, _driverSignature, StringComparison.Ordinal))
            {
                return [];
            }
            var valid = new List<Row>();
            foreach (var row in state.Rows ?? [])
            {
                if (row?.Model is not { Length: > 0 and <= MaxModelIdLength } model
                    || !Enum.TryParse<LocalCompute>(row.Compute, ignoreCase: false, out var compute)
                    || !Enum.IsDefined(compute) || row.Compute != compute.ToString()
                    || row.Ms is <= 0 or > MaxMs
                    || CanonicalLocalModel(model) != model
                    || valid.Any(r => r.Matches(model, compute)))
                {
                    continue;
                }
                valid.Add(new Row { Model = model, Compute = row.Compute, Ms = row.Ms });
            }
            return valid;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Write(List<Row> rows)
    {
        try
        {
            return TransientFileRetry.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new State
                {
                    CheckVersion = CurrentCheckVersion,
                    Driver = _driverSignature,
                    Rows = rows,
                }));
                File.Move(tmp, _path, overwrite: true);
                return true;
            }, Backoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class State
    {
        public int CheckVersion { get; set; }
        public string? Driver { get; set; }
        public List<Row> Rows { get; set; } = [];
    }

    private sealed class Row
    {
        public string? Model { get; set; }
        public string? Compute { get; set; }
        public int Ms { get; set; }

        public bool Matches(string model, LocalCompute compute)
            => string.Equals(Model, model, StringComparison.Ordinal)
               && string.Equals(Compute, compute.ToString(), StringComparison.Ordinal);
    }
}
