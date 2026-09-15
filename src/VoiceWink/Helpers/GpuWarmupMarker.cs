namespace VoiceWink.Helpers;

/// <summary>Which local engine a self-test verdict or warmed flag belongs to. Two on purpose: the
/// two engines warm, test and pin independently (Whisper in-process, Parakeet as a child).</summary>
internal enum GpuSelfTestEngine
{
    Whisper,
    Parakeet,
}

/// <summary>One engine's persisted self-test state, as read back — validated, never raw JSON.</summary>
internal readonly record struct GpuSelfTestRecord(GpuSelfTestOutcome Outcome, string? GpuName)
{
    internal bool Failed => Outcome == GpuSelfTestOutcome.Fail;

    /// <summary>TRN-64: does this verdict put the engine on the CPU? The pre-boot pin, the row's
    /// advisory and the in-session Whisper gate all key on THIS, never on <see cref="Failed"/>
    /// alone — <see cref="GpuSelfTestOutcome.Inconclusive"/> pins too (Kimi r2 B3 named the
    /// predicate and its five read sites), and so does <see cref="GpuSelfTestOutcome.Slower"/>
    /// since TRN-64 PR 2: a correct-but-slow GPU moves to the CPU at the next start.</summary>
    internal bool PinsCpu => GpuWarmupMarker.PinsCpu(Outcome);

    /// <summary>TRN-64 PR 2: does this verdict refuse a decode in this process — Fail or
    /// Inconclusive, never Slower (see <see cref="GpuWarmupMarker.RefusesDecode"/>).</summary>
    internal bool RefusesDecode => GpuWarmupMarker.RefusesDecode(Outcome);
}

/// <summary>
/// TRN-49: the once-per-machine gate on the GPU warm-up decodes. The shader/pipeline compile the
/// warm-up pays for persists in the driver's ON-DISK cache across process restarts (measured:
/// 12.51 s first decode ever, 0.90 s as the FIRST decode of a fresh process) — so warming on
/// every launch would pay real cost (a ~1 GB warm-up server child plus a second ~900 MB model
/// read, measured live on the laptop) to buy nothing after launch one. This marker records which
/// engines have been warmed for the CURRENT APP VERSION; absent, unparsable, or written by a
/// different version ⇒ warm again.
///
/// <para><b>TRN-50 (2026-09-03): the same file carries each engine's GPU SELF-TEST verdict.</b> The
/// warm-up now decodes a golden speech clip and judges the words that come back
/// (<see cref="GpuSelfTestVerdict"/>); a driver that loads cleanly and decodes real speech to
/// nothing — the owner's ARM64 laptop, Adreno X1-85, 3/3 dictations empty on the GPU and 3/3 fine
/// on the CPU — is what this records. Keyed on the SAME app version, deliberately: a FAIL pins that
/// engine to CPU for every launch of this version (read pre-boot, before any native load) and is
/// re-tested once per update; the user re-arms it sooner by turning the GPU toggle off and back
/// on. A FAIL and a warmed flag never coexist (<see cref="RecordVerdict"/> clears the flag —
/// nothing was warmed on the path the app will now use) and a re-arm clears verdict, name AND
/// flag in ONE write, so the next launch re-runs the self-test rather than skipping it on a stale
/// "warmed" (Codex plan round, Blocker 1). Every persisted field is validated on read — an
/// out-of-range verdict, a non-string or over-long name — and reads as Unknown/null rather than
/// reaching the pre-boot pin (Codex plan round, advisory).</para>
///
/// <para><b>Why the key is the app version:</b> driver shader caches key on the application, and
/// a Velopack update changes the executable's path every version — so an app update plausibly
/// re-exposes the compile, and re-warming once per update is cheap insurance that also covers
/// upstream runtime bumps. <b>Accepted residual, recorded on the TRN-49 card:</b> a driver-cache
/// eviction re-exposes exactly one slow first decode — today's behaviour, self-limited.</para>
///
/// <para><b>TRN-63 (2026-09-03): the display driver's signature rides the key too.</b> The v1
/// reason it did not — "reading it takes WMI" — was wrong: <see cref="DisplayDriverSignature"/>
/// reads the display class's <c>DriverDesc</c>/<c>DriverVersion</c> slots from the registry in a
/// millisecond. The signature is persisted with the state and compared on read exactly like the
/// app version: a DIFFERENT non-null signature discards the persisted state — both engines'
/// verdicts, names and warmed flags — so a driver update re-runs the self-test and re-warms on
/// its own (a driver update was already the recorded residual for the compile, so re-warming on
/// it is the intended shape), and a driver that fixes a FAIL lifts the CPU pin at the next start
/// instead of at the next release. A null current signature (unreadable registry) compares as
/// "nothing known": it never discards, and a write under it keeps whatever signature the file
/// already carries. <see cref="TryAdoptDriverChange"/> is the composition root's one-shot: it
/// persists the reset under the new signature so the change is reported once and the warm-up's
/// own marker instance already reads the fresh state.</para>
///
/// <para>Failures read as "not warmed" / "no verdict" and writes are best-effort — a marker that
/// cannot be written costs one redundant warm-up next launch, never capability. The file lives
/// beside settings.json and deliberately outside it: warm-up and self-test state are per-machine
/// cache bookkeeping about THIS driver, not a user preference, so they must not ride settings
/// export/import or reset.</para>
/// </summary>
internal sealed class GpuWarmupMarker
{
    private sealed class State
    {
        public string? AppVersion { get; set; }
        // TRN-63: the display driver signature the state was written under; null when the
        // registry was unreadable at that write. Compared on read like AppVersion (see the doc).
        public string? DriverSignature { get; set; }
        public bool WhisperWarmed { get; set; }
        public bool ParakeetWarmed { get; set; }
        // Verdicts persist as enum NAMES, not numbers: a hand-edited or torn number outside the
        // enum would deserialize as an undefined member and slip past a range check.
        public string? WhisperVerdict { get; set; }
        public string? ParakeetVerdict { get; set; }
        public string? WhisperGpuName { get; set; }
        public string? ParakeetGpuName { get; set; }
        // TRN-64: Whisper's verdicts are PER MODEL — the incident's garbage came from a model the
        // engine had never tested (Kimi r1 K3) — while WhisperVerdict above is the STICKY ENGINE
        // PIN (Codex r2 F1): set by any pinning outcome on any model, cleared only by a re-arm, an
        // app update or a driver change, and never by a later Pass on another model.
        public List<TestedEntry>? WhisperTested { get; set; }
    }

    private sealed class TestedEntry
    {
        public string? Model { get; set; }
        public string? Outcome { get; set; }
    }

    /// <summary>TRN-64: how many per-model Whisper entries the marker keeps (the catalog has five
    /// Whisper rows; oldest evicted first).</summary>
    internal const int MaxTestedModels = 8;

    /// <summary>TRN-64: the outcomes that put an engine on the CPU. One definition, used by the
    /// record, the write path (a pinning outcome also clears the warmed flag) and the pre-boot pin.</summary>
    internal static bool PinsCpu(GpuSelfTestOutcome outcome)
        => outcome is GpuSelfTestOutcome.Fail or GpuSelfTestOutcome.Inconclusive or GpuSelfTestOutcome.Slower;

    /// <summary>TRN-64 PR 2 (self-review, concurrency lens): does this verdict REFUSE a decode in
    /// THIS process? Fail and Inconclusive do; Slower does not — its text is right, only slow. The
    /// two predicates split here because a Slower pin must keep OTHER models' correctness tests
    /// running: the gate lets Slower through, so an engine that stopped testing on a Slower pin
    /// would let a never-judged second model decode on the GPU the incident is about. Every
    /// pinning verdict still moves the next start to the CPU (<see cref="PinsCpu"/>).</summary>
    internal static bool RefusesDecode(GpuSelfTestOutcome outcome)
        => outcome is GpuSelfTestOutcome.Fail or GpuSelfTestOutcome.Inconclusive;

    private readonly string _path;
    private readonly string _appVersion;
    private readonly string? _driverSignature;

    /// <summary>The two engines' marks arrive from independent pool tasks against ONE file; an
    /// unsynchronised read-modify-write loses whichever lands first (or throws a swallowed
    /// sharing violation), and the silent cost is the exact ~1 GB re-warm this file exists to
    /// prevent (self-review, concurrency lens). Both warm-ups share one marker instance
    /// (composition root), so an instance lock suffices. TRN-63 adds a second writer — the
    /// pre-boot pin's instance, whose one adopt write runs in <c>ConfigureServices</c>, on the
    /// launch thread, strictly before the warm-up's instance exists or any warm-up task is
    /// queued — so the two instances never write concurrently; the lock stays per instance.</summary>
    private readonly object _writeLock = new();

    /// <param name="driverSignature">TRN-63: the live display driver signature
    /// (<see cref="DisplayDriverSignature.Read"/>), or null when it could not be read — null
    /// never invalidates persisted state.</param>
    public GpuWarmupMarker(string path, string appVersion, string? driverSignature = null)
    {
        _path = path;
        _appVersion = appVersion;
        _driverSignature = driverSignature;
    }

    public static string DefaultPath => Path.Combine(AppPaths.RootDir, "gpu-warmup.json");

    /// <summary>The version key production uses: the entry assembly's version, i.e. the csproj
    /// <c>&lt;Version&gt;</c> — the same value a Velopack update changes.</summary>
    public static string CurrentAppVersion()
        => global::System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static readonly Lazy<string?> s_currentDriverSignature = new(DisplayDriverSignature.Read);

    /// <summary>TRN-63: the display driver signature production keys on — read from the registry
    /// once per process (the value cannot change under a running process in a way that matters
    /// here: a driver installed mid-session takes effect at the next start, which is also the
    /// next read).</summary>
    public static string? CurrentDriverSignature() => s_currentDriverSignature.Value;

    /// <summary>TRN-63: true when the file was written by this app version under a DIFFERENT
    /// (non-null) driver signature than this instance carries — the one condition under which the
    /// persisted verdicts and warmed flags are about a driver that is no longer installed.</summary>
    public bool DriverChangedSinceLastWrite()
    {
        var raw = ReadRaw();
        return raw is not null
            && string.Equals(raw.AppVersion, _appVersion, StringComparison.Ordinal)
            && DriverChanged(raw);
    }

    /// <summary>TRN-63: the composition root's one-shot. When the driver changed, persist the
    /// reset under the new signature in one write and return true, so the caller logs the change
    /// and every later read — including the warm-up's own marker instance — sees the fresh
    /// state. False (and no write) otherwise. True reports the DETECTED change, not a confirmed
    /// write: marker writes are best-effort like every other, so an unwritable file re-detects
    /// and re-logs at the next start — the cheap direction (one redundant warm-up), never a
    /// stale verdict honoured (Kimi diff r1, advisory).</summary>
    public bool TryAdoptDriverChange()
    {
        if (!DriverChangedSinceLastWrite()) return false;
        Write(static _ => { });
        return true;
    }

    public bool WhisperNeedsWarmup() => !Read().WhisperWarmed;

    /// <summary>TRN-64: does <paramref name="model"/> still owe a self-test? False once the engine
    /// is pinned by a REFUSING verdict (the process runs on the CPU — there is nothing to test) or
    /// the model has an entry. A Slower pin keeps other models' tests owed (PR 2): the gate lets
    /// Slower through, so a model nobody judged must still be judged before it decodes. A model the catalog does not know reads true, but <see cref="RecordWhisperVerdict"/>
    /// records it at the engine level only, so it never loops (Kimi r2 C3): the caller gates on
    /// the catalog name it was handed, not on this.</summary>
    public bool WhisperNeedsTest(string model)
        => !ReadVerdict(GpuSelfTestEngine.Whisper).RefusesDecode
           && WhisperModelVerdict(model) == GpuSelfTestOutcome.Unknown;

    /// <summary>TRN-64: the persisted per-model Whisper outcome, validated entry by entry;
    /// Unknown when absent, malformed, or the model is not a catalog Whisper row.</summary>
    public GpuSelfTestOutcome WhisperModelVerdict(string model)
    {
        var key = ValidModelName(model);
        if (key is null) return GpuSelfTestOutcome.Unknown;
        var entry = Read().WhisperTested?.LastOrDefault(e => string.Equals(ValidModelName(e.Model), key, StringComparison.Ordinal));
        return entry is null ? GpuSelfTestOutcome.Unknown : ParseOutcome(entry.Outcome);
    }

    /// <summary>TRN-64: record a Whisper self-test outcome FOR A MODEL. The model's entry is
    /// written (bounded, oldest evicted); a pinning outcome also sets the sticky engine pin and
    /// clears the warmed flag; a Pass never touches an existing pin and fills the engine record
    /// only while none stands (so the row can say "works on your graphics card"). A model the
    /// catalog does not know falls back to the engine-level <see cref="RecordVerdict"/>. Returns
    /// durability like <see cref="RecordVerdict"/>.</summary>
    public bool RecordWhisperVerdict(string? model, GpuSelfTestOutcome outcome, string? gpuName)
    {
        if (outcome == GpuSelfTestOutcome.Unknown) return false;
        var key = ValidModelName(model);
        if (key is null) return RecordVerdict(GpuSelfTestEngine.Whisper, outcome, gpuName);
        var name = ValidName(gpuName);
        return Write(s =>
        {
            s.WhisperTested ??= new List<TestedEntry>();
            s.WhisperTested.RemoveAll(e => string.Equals(ValidModelName(e.Model), key, StringComparison.Ordinal));
            s.WhisperTested.Add(new TestedEntry { Model = key, Outcome = outcome.ToString() });
            while (s.WhisperTested.Count > MaxTestedModels) s.WhisperTested.RemoveAt(0);
            if (PinsCpu(outcome))
            {
                // PR 2: a Slower never DOWNGRADES a refusing pin — Fail/Inconclusive on one model
                // outranks "correct but slow" on another (self-review, concurrency lens).
                if (!(outcome == GpuSelfTestOutcome.Slower && RefusesDecode(ParseOutcome(s.WhisperVerdict))))
                {
                    s.WhisperVerdict = outcome.ToString();
                    s.WhisperGpuName = name;
                }
                s.WhisperWarmed = false;
            }
            else if (ParseOutcome(s.WhisperVerdict) == GpuSelfTestOutcome.Unknown)
            {
                s.WhisperVerdict = outcome.ToString();
                s.WhisperGpuName = name;
            }
        });
    }

    public bool ParakeetNeedsWarmup() => !Read().ParakeetWarmed;

    public void MarkWhisperWarmed() => Write(s => s.WhisperWarmed = true);

    public void MarkParakeetWarmed() => Write(s => s.ParakeetWarmed = true);

    /// <summary>The persisted self-test record for one engine, validated (see the class doc).</summary>
    public GpuSelfTestRecord ReadVerdict(GpuSelfTestEngine engine)
    {
        var s = Read();
        return engine == GpuSelfTestEngine.Whisper
            ? new GpuSelfTestRecord(ParseOutcome(s.WhisperVerdict), ValidName(s.WhisperGpuName))
            : new GpuSelfTestRecord(ParseOutcome(s.ParakeetVerdict), ValidName(s.ParakeetGpuName));
    }

    /// <summary>Record a self-test outcome. A FAIL also clears the engine's warmed flag in the
    /// same write: nothing was warmed on the path the app will use from here, and a warmed flag
    /// that survived a FAIL would let a later re-arm skip the very re-test it promises. An
    /// <see cref="GpuSelfTestOutcome.Unknown"/> outcome is never persisted (nothing was learned).
    /// <para>Returns whether the verdict is now DURABLE. REL-30 gates the Sentry-visible Error on
    /// that: an unwritable marker cannot pin the engine to the CPU, so the same failure is
    /// rediscovered at the next launch, and reporting it each time would put one event per start
    /// in front of every affected user. False for an Unknown outcome too — nothing was persisted
    /// because nothing was learned.</para></summary>
    public bool RecordVerdict(GpuSelfTestEngine engine, GpuSelfTestOutcome outcome, string? gpuName)
    {
        if (outcome == GpuSelfTestOutcome.Unknown)
        {
            return false;
        }
        var name = ValidName(gpuName);
        // Kimi diff r1 A2: no no-downgrade guard here, unlike RecordWhisperVerdict's model-known
        // path. Nothing can reach this with a Slower under a refusing Whisper pin — the queue's
        // pin guard stops every run under one — and Parakeet's CPU-pinned child yields no GPU
        // evidence, so it never records at all. Stated rather than guarded: a second guard whose
        // input is unreachable is a claim nothing tests.
        return Write(s =>
        {
            if (engine == GpuSelfTestEngine.Whisper)
            {
                s.WhisperVerdict = outcome.ToString();
                s.WhisperGpuName = name;
                if (PinsCpu(outcome)) s.WhisperWarmed = false;
            }
            else
            {
                s.ParakeetVerdict = outcome.ToString();
                s.ParakeetGpuName = name;
                if (PinsCpu(outcome)) s.ParakeetWarmed = false;
            }
        });
    }

    /// <summary>Forget one engine's verdict, GPU name AND warmed flag — atomically, so the next
    /// launch runs the self-test again instead of skipping it on a stale warmed flag.</summary>
    public void Rearm(GpuSelfTestEngine engine) => Write(s => Clear(s, engine));

    /// <summary>The toggle's re-arm: both engines, one write.</summary>
    public void RearmAll() => Write(s =>
    {
        Clear(s, GpuSelfTestEngine.Whisper);
        Clear(s, GpuSelfTestEngine.Parakeet);
    });

    private static void Clear(State s, GpuSelfTestEngine engine)
    {
        if (engine == GpuSelfTestEngine.Whisper)
        {
            s.WhisperVerdict = null;
            s.WhisperGpuName = null;
            s.WhisperWarmed = false;
            s.WhisperTested = null; // TRN-64: the per-model map goes with the pin
        }
        else
        {
            s.ParakeetVerdict = null;
            s.ParakeetGpuName = null;
            s.ParakeetWarmed = false;
        }
    }

    private static GpuSelfTestOutcome ParseOutcome(string? persisted)
        => persisted is not null
           && Enum.TryParse<GpuSelfTestOutcome>(persisted, ignoreCase: false, out var parsed)
           && Enum.IsDefined(parsed)
            ? parsed
            : GpuSelfTestOutcome.Unknown;

    /// <summary>TRN-64: a per-model key is the catalog's own spelling of a Whisper row, or null.
    /// Catalog-known only (Codex r2 F1: a collision-safe identity, never a file name) — the same
    /// name <c>SelectedModelName</c> stores and <c>ModelRatings.RuntimeOf</c> resolves. Bounded and
    /// path-free like every persisted field, so a hand-edited entry cannot reach a log line.</summary>
    internal static string? ValidModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128) return null;
        if (model.IndexOfAny(['\\', '/', '\r', '\n']) >= 0) return null;
        return Models.PredefinedModels.Models
            .FirstOrDefault(m => m.Runtime == Models.LocalRuntimeKind.Whisper
                                 && string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    /// <summary>A GPU name that passes the same bounds the device-line parser enforces — length,
    /// no path separators, single line — or null. The name feeds a log line and the Settings
    /// advisory, so nothing free-form may ride in from the file.</summary>
    internal static string? ValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Length > GgmlVulkanDeviceLine.MaxNameLength) return null;
        if (name.IndexOfAny(['\\', '/', '\r', '\n']) >= 0) return null;
        return name;
    }

    /// <summary>The persisted state as written, or null when there is none or it is unreadable —
    /// no version or driver check applied. <see cref="Read"/> applies them.</summary>
    private State? ReadRaw()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return global::System.Text.Json.JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TRN-63: both signatures known and different. A null on either side is "nothing
    /// known" and never a change — the direction that keeps a persisted verdict. The persisted
    /// side is validated like every other persisted field (<see cref="ValidSignature"/>): a
    /// hand-edited or torn value reads as "nothing known" rather than as a spurious change that
    /// would drop both verdicts and log a driver change that never happened.</summary>
    private bool DriverChanged(State s)
    {
        var persisted = ValidSignature(s.DriverSignature);
        return _driverSignature is not null
           && persisted is not null
           && !string.Equals(persisted, _driverSignature, StringComparison.Ordinal);
    }

    /// <summary>A persisted driver signature within the bounds <see cref="DisplayDriverSignature"/>
    /// writes — non-blank, one line, no path separators, at most the signature bound — or null.
    /// Anything else never reaches the comparison.</summary>
    internal static string? ValidSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return null;
        if (signature.Length > DisplayDriverSignature.MaxSignatureLength) return null;
        if (signature.IndexOfAny(['\\', '/', '\r', '\n']) >= 0) return null;
        return signature;
    }

    private State Fresh() => new() { AppVersion = _appVersion, DriverSignature = _driverSignature };

    private State Read()
    {
        var state = ReadRaw();
        if (state is null || !string.Equals(state.AppVersion, _appVersion, StringComparison.Ordinal))
        {
            // A different app version's warm-ups and verdicts do not count — see the class doc.
            return Fresh();
        }
        if (DriverChanged(state))
        {
            // TRN-63: a different display driver's warm-ups and verdicts do not count either.
            return Fresh();
        }
        return state;
    }

    /// <summary>Best-effort persist. Returns whether the write actually landed — REL-30 needs that
    /// answer: a self-test FAIL is reported to Sentry only once the verdict is durable, because an
    /// unwritable marker leaves the engine looking un-warmed and the same failure would be
    /// rediscovered, and re-reported, at every launch.</summary>
    private bool Write(Action<State> mutate)
    {
        try
        {
            lock (_writeLock)
            {
                var state = Read();
                state.AppVersion = _appVersion;
                // TRN-63: a readable signature is always persisted; an unreadable one (null)
                // leaves the file's signature alone rather than erasing what a later, readable
                // start would compare against — within the same app version: a version reset
                // (Read returned Fresh) has already discarded everything the signature protected.
                if (_driverSignature is not null) state.DriverSignature = _driverSignature;
                mutate(state);
                // Write-then-move so a kill mid-write cannot leave a torn file that discards a
                // previously recorded flag (torn JSON reads as "nothing warmed, no verdict").
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, global::System.Text.Json.JsonSerializer.Serialize(state));
                File.Move(tmp, _path, overwrite: true);
            }
            return true;
        }
        catch
        {
            // Best effort by design: an unwritable marker re-warms next launch, nothing worse.
            return false;
        }
    }
}
