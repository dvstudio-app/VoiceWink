using System.Diagnostics;
using System.IO;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Services.Maintenance;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// 2026-10-03 (owner decision): a local speech model's speed is MEASURED on this PC by one timed
/// check, the same principle as the built-in models' first-use check — never learned slowly from
/// dictations, which moved the stars at an unannounced moment. The check transcribes the bundled
/// GPU self-test clip played <see cref="ClipRepeats"/> times (about 11 s, close to the reference
/// table's per-dictation basis) through the model's own service, the same <c>TranscribeAsync</c> a
/// dictation uses, so emulation, the graphics card and the driver are all in the figure.
///
/// <para><b>When.</b> Offered whenever a model has been prepared (<see cref="LocalModelPreparer"/>):
/// onboarding's download-then-select, a Models-page selection, the startup preload. It runs only for
/// a (model, compute class) this PC has not measured, waits for an idle app (the maintenance gate
/// clear, no GPU self-test running) and holds a cleanup-pass lease while it runs, so an update apply
/// or a data erasure waits for it. A recording or an Audio Transcribe start cancels it through the
/// engines' ordinary cancellation path (<see cref="CancelForUserWork"/>); the next prepare offers it
/// again. A Whisper model downloaded but never selected stays estimated: measuring it would need a
/// second native model loaded beside the active one.</para>
///
/// <para><b>What counts.</b> On the GPU an untimed first decode pays the per-shape pipeline compile
/// (and any GPU self-test wait the decode gate adds); the second decode is timed. A run whose compute
/// class changed between the two reads, or whose transcript came back empty, records nothing.</para>
/// </summary>
internal sealed class SpeechSpeedCheck
{
    internal const int ClipRepeats = 4;

    /// <summary>How many times the clip plays for this engine. Parakeet hears it once (about 3 s)
    /// and its time is scaled to the four-play basis: its decode cannot be cancelled mid-request,
    /// so a Retry or a file transcription that arrives during the check waits for it, and a short
    /// clip keeps that wait well under a second (owner decision, 2026-10-03). The scaling carries
    /// the request's fixed cost four times, a slight over-estimate the star bands absorb.</summary>
    internal static int RepeatsFor(LocalRuntimeKind runtime) => runtime == LocalRuntimeKind.Parakeet ? 1 : ClipRepeats;

    /// <summary>Runs per (model, compute class) per session: a check that keeps standing down (a
    /// busy dictation habit, a refusing GPU) stops being offered rather than decoding the clip at
    /// every recording's prepare.</summary>
    internal const int MaxAttemptsPerSession = 3;

    private static ILogger Logger => Log.ForContext<SpeechSpeedCheck>();
    private static SpeechSpeedCheck? s_current;

    internal static SpeechSpeedCheck? Current => Volatile.Read(ref s_current);

    internal static void Configure(SpeechSpeedCheck check)
        => Volatile.Write(ref s_current, check ?? throw new ArgumentNullException(nameof(check)));

    internal static void ResetForTests() => Volatile.Write(ref s_current, null);

    private readonly TranscriptionSpeedStore _store;
    private readonly Func<string, ITranscriptionService?> _serviceFor;
    private readonly Func<string, bool> _isLoaded;
    private readonly Func<LocalComputeSnapshot> _snapshot;
    private readonly IMaintenanceGate _gate;
    private readonly Func<LocalRuntimeKind, bool> _selfTestRunning;
    private readonly Func<float[]?> _clip;
    private readonly object _lock = new();
    private string? _pending;
    private Task? _loop;
    private CancellationTokenSource? _runCts;
    private string? _runModel;
    private readonly Dictionary<(string, LocalCompute), int> _attempts = [];

    /// <param name="isLoaded">Whether the model is the one its service has loaded now — a Whisper
    /// selection can change between the offer and the run, and the time must be recorded under the
    /// model that produced it.</param>
    internal SpeechSpeedCheck(TranscriptionSpeedStore store, Func<string, ITranscriptionService?> serviceFor,
        Func<string, bool> isLoaded, Func<LocalComputeSnapshot> snapshot, IMaintenanceGate gate, Func<LocalRuntimeKind, bool> selfTestRunning,
        Func<float[]?> clip)
    {
        _store = store;
        _serviceFor = serviceFor;
        _isLoaded = isLoaded;
        _snapshot = snapshot;
        _gate = gate;
        _selfTestRunning = selfTestRunning;
        _clip = clip;
    }

    /// <summary>How long a prepared model waits before its check is considered (test seam).</summary>
    internal TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often the idle wait looks again, and how long it waits in all (test seams).</summary>
    internal TimeSpan IdlePoll { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan IdleBudget { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The loop currently running, for tests to await; null when idle.</summary>
    internal Task? RunningForTests { get { lock (_lock) return _loop; } }

    /// <summary>Raised on the check's thread after a measurement is stored, so an open Models
    /// page re-renders its stars and shows the "updated" line (Kimi diff r2).</summary>
    public event Action? Measured;

    /// <summary>A model was prepared. Starts its check when this PC has not measured it on the
    /// compute class it would run on now; the newest offer wins while one is running.</summary>
    internal void OfferAfterPrepare(string model)
    {
        if (PredefinedModels.RuntimeOf(model) is not LocalRuntimeKind runtime) return;
        if (_store.MeasuredMs(model, Compute(runtime)) is not null) return;
        lock (_lock)
        {
            _pending = model;
            _loop ??= Task.Run(LoopAsync);
        }
    }

    /// <summary>A dictation or a file transcription is starting: stop a running check now.</summary>
    internal void CancelForUserWork()
    {
        lock (_lock)
        {
            _pending = null;
            try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Another model is being prepared: a check for a different model stands down, so the
    /// selection never waits behind its decode (self-review).</summary>
    internal void CancelUnless(string model)
    {
        lock (_lock)
        {
            if (_runModel is not null && !string.Equals(_runModel, model, StringComparison.OrdinalIgnoreCase))
            {
                try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            string? model;
            lock (_lock)
            {
                model = _pending;
                _pending = null;
                if (model is null)
                {
                    _loop = null;
                    return;
                }
            }
            try
            {
                await RunOneAsync(model).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning("Speech speed check failed: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private LocalCompute Compute(LocalRuntimeKind runtime)
    {
        try { return _snapshot().For(runtime); }
        catch { return LocalCompute.Cpu; }
    }

    private async Task RunOneAsync(string model)
    {
        var runtime = PredefinedModels.RuntimeOf(model)!.Value;
        // Published from the start, so a recording starting during the waits cancels them too.
        using var cts = new CancellationTokenSource();
        lock (_lock)
        {
            var key = (model, Compute(runtime));
            _attempts.TryGetValue(key, out var attempts);
            if (attempts >= MaxAttemptsPerSession) return;
            _attempts[key] = attempts + 1;
            _runCts = cts;
            _runModel = model;
        }
        IDisposable? lease = null;
        var wav = Path.Combine(Path.GetTempPath(), $"voicewink-speed-check-{Guid.NewGuid():N}.wav");
        try
        {
            await Task.Delay(StartDelay, cts.Token).ConfigureAwait(false);
            if (_store.MeasuredMs(model, Compute(runtime)) is not null) return;

            var waited = TimeSpan.Zero;
            while (!(_gate.Check().CanProceed && !_selfTestRunning(runtime)))
            {
                if (waited >= IdleBudget)
                {
                    Logger.Information("Speech speed check for {Model} not run: VoiceWink was busy", model);
                    return;
                }
                await Task.Delay(IdlePoll, cts.Token).ConfigureAwait(false);
                waited += IdlePoll;
            }

            if (_clip() is not { Length: > 0 } clip || _serviceFor(model) is not { } service) return;
            if (!_gate.TryBeginCleanupPass(out lease)) return;

            var repeats = RepeatsFor(runtime);
            WriteWav(wav, clip, repeats);
            // A Parakeet decode is never cancelled mid-request (Codex diff round): a cancelled
            // in-flight request makes the service wait its 2 s grace and probe the child under the
            // service lock, longer than the one-play decode (well under a second, scaled from about
            // 2 s for four plays on the slowest processor measured). It finishes, usually while the
            // user is still speaking; the user's work then stops the check between decodes. A Whisper decode
            // (up to ~40 s on a slow processor) is cancelled at once, through whisper.cpp's abort.
            var decodeToken = runtime == LocalRuntimeKind.Parakeet ? CancellationToken.None : cts.Token;
            var before = Compute(runtime);
            if (before == LocalCompute.Gpu)
            {
                // Untimed: the per-shape pipeline compile, and any GPU self-test the decode gate waits on.
                await service.TranscribeAsync(wav, null, null, false, decodeToken).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();
            }
            if (!_isLoaded(model)) return;
            var sw = Stopwatch.StartNew();
            var text = await service.TranscribeAsync(wav, null, null, false, decodeToken).ConfigureAwait(false);
            sw.Stop();
            var after = Compute(runtime);
            if (!_isLoaded(model))
            {
                Logger.Information("Speech speed check for {Model} not recorded (another model was loaded)", model);
                return;
            }
            if (after != before || TranscriptContent.IsEmpty(text))
            {
                Logger.Information("Speech speed check for {Model} not recorded ({Reason})", model,
                    after != before ? "the compute class changed" : "empty transcript");
                return;
            }
            if (_store.Record(model, after, sw.Elapsed * ClipRepeats / repeats))
            {
                try { Measured?.Invoke(); }
                catch (Exception ex) { Logger.Warning(ex, "Speech speed check: a page refresh failed"); }
            }
            Logger.Information("Speech speed check: {Model} on the {Compute} - {ElapsedMs} ms for {AudioSeconds:l}s of audio",
                model, after == LocalCompute.Gpu ? "GPU" : "processor", (long)sw.Elapsed.TotalMilliseconds,
                (clip.Length * repeats / 16000.0).ToString("F1", global::System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Logger.Information("Speech speed check for {Model} stood down (a dictation, a file transcription or another model's load); offered again at the next load", model);
        }
        finally
        {
            lock (_lock)
            {
                _runCts = null;
                _runModel = null;
            }
            lease?.Dispose();
            try { File.Delete(wav); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The clip repeated <paramref name="repeats"/> times as a 16 kHz mono PCM16 WAV.</summary>
    internal static void WriteWav(string path, float[] samples, int repeats)
    {
        var count = samples.Length * repeats;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + count * 2);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(GpuSelfTestClip.SampleRate);
        w.Write(GpuSelfTestClip.SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(count * 2);
        for (var r = 0; r < repeats; r++)
        {
            foreach (var s in samples)
            {
                w.Write((short)Math.Clamp((int)Math.Round(s * 32767f), short.MinValue, short.MaxValue));
            }
        }
    }
}
