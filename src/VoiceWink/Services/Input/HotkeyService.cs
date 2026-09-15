using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using SharpHook;
using SharpHook.Native;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Input;

/// <summary>
/// Global hotkey handler with tap/hold detection (SharpHook).
/// Uses SimpleGlobalHook so SuppressEvent works (prevents OS alt-menu activation).
/// Implements tap (hands-free) vs hold (push-to-talk) detection with 300ms threshold.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private static ILogger Logger => Log.ForContext<HotkeyService>();

    private const long TapThresholdMs = 300;
    private const long DebounceMs = 50;
    private const int MaxRestartAttempts = 5;

    // Watchdog: Windows can silently remove a low-level keyboard hook (e.g. after a
    // slow hook handler exceeds LowLevelHooksTimeout, or around user-session events).
    // SharpHook's run loop doesn't notice — the thread stays alive; we just stop
    // receiving events. We correlate our own last-event timestamp against
    // GetLastInputInfo (which reports Windows' view of when user input last landed
    // regardless of hooks) and restart when Windows is seeing input but we aren't.
    // Thresholds are tuned to recover within ~75s of hook death; the false-positive
    // case (user mousing-only for 60+ seconds) is a ~600ms invisible restart.
    private const int WatchdogIntervalSeconds = 15;
    private const int HookSilentThresholdSeconds = 60;    // 1 minute silent on our side
    private const int WindowsInputFreshSeconds = 30;      // and Windows saw input in the last 30s
    private const int MinSecondsBetweenAutoRestarts = 60; // never auto-restart more than once a minute

    private readonly SettingsService _settings;

    /// <summary>HKY-2: receives the foreground sample at each watchdog restart and emits
    /// the diagnostic line off-thread. Null in tests / the public ctor.</summary>
    private readonly WatchdogForegroundObserver? _watchdogObserver;

    private SimpleGlobalHook? _hook;
    private DispatcherQueue? _dispatcherQueue;
    private volatile bool _disposed;
    private int _restartCount;

    /// <summary>Test seam: restart attempts this hook lifetime (pins RestartDeadHookAsync's superseded no-op).</summary>
    internal int RestartAttemptCount => Volatile.Read(ref _restartCount);

    private DispatcherTimer? _watchdogTimer;
    /// <summary>UTC timestamp of the most recent keyboard event our hook received (key down/up).</summary>
    private DateTime _lastHookEventUtc = DateTime.UtcNow;
    /// <summary>GetTickCount timestamp of the most recent mouse event our all-input hook received.</summary>
    private uint _lastMouseEventTickCount;
    /// <summary>UTC timestamp of the last watchdog-triggered restart; gates subsequent restarts.</summary>
    private DateTime _lastWatchdogRestartUtc = DateTime.MinValue;
    /// <summary>UTC timestamp of the last "near-miss" watchdog log, used to throttle the diagnostic line. UI thread only.</summary>
    private DateTime _lastNearMissLogUtc = DateTime.MinValue;

    // Service-level restart serialization. SharpHook/libuiohook holds process-global
    // state, so watchdog, hook-task-continuation, and App-triggered restarts must
    // mutex with each other. App also has its own _hookRestartLock for trigger
    // coalescing (resume + unlock arriving seconds apart) — that's a separate
    // concern from native-hook safety.
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    /// <summary>Once-per-minute throttle window for the near-miss watchdog log. UI thread only.</summary>
    private static readonly TimeSpan NearMissLogThrottle = TimeSpan.FromSeconds(60);
    /// <summary>Floor for "Windows-input is fresh enough to suggest the user just typed" — must be ≤ <see cref="WindowsInputFreshSeconds"/>.</summary>
    private const int NearMissHookSilentFloorSeconds = 30;
    /// <summary>
    /// Tolerance for treating our mouse-hook event as the same input observed by
    /// GetLastInputInfo. Both use GetTickCount time; this allows hook dispatch jitter.
    /// </summary>
    private const uint MouseExplainsWindowsInputToleranceMs = 250;

    private long _lastEventTimestamp;
    private long _lastKeyDownTimestamp;
    private long _activeKeyDownTimestamp;
    private volatile bool _isHandsFreeMode;
    private volatile bool _keyDownStartedRecording;
    // HKY-3: ALL hotkey bindings live in ONE immutable snapshot behind a single volatile
    // reference. Reference assignment is atomic, so the hook thread can never observe a new
    // trigger beside an old modifier requirement — see HotkeyConfiguration's remarks for the
    // torn-publication defect that shape prevents.
    private volatile HotkeyConfiguration _config = new(
        new HotkeyBinding(HotkeyModifiers.None, KeyCode.VcRightAlt), null, null, null);

    /// <summary>
    /// Serialises the two config WRITERS. Readers stay lock-free — they take one volatile read.
    /// </summary>
    /// <remarks>
    /// <c>volatile</c> stops a TORN reference; it does not stop a LOST UPDATE. Both writers are
    /// read-modify-write (<c>ReloadHotkeys</c> replaces the roles and carries the prompts forward,
    /// <c>RegisterPromptHotkeys</c> does the reverse), so two overlapping writers can each publish
    /// a snapshot derived from the same original and one set of changes vanishes — a hotkey the
    /// user just bound, or a prompt hotkey, silently unregistered. Every caller today happens to
    /// be on the UI thread, so there is no scenario reachable in the shipped build; the lock is
    /// taken anyway because these are PUBLIC methods on a DI-resolved service, so "all callers are
    /// UI-thread" is a convention about code not yet written rather than a property of this type
    /// (Codex diff review round 1).
    /// </remarks>
    private readonly object _configWriteLock = new();

    /// <summary>The current binding snapshot. One volatile read per use — never re-read mid-decision.</summary>
    internal HotkeyConfiguration Configuration => _config;

    /// <summary>
    /// Whether any installed keyboard layout uses AltGr — see <see cref="HotkeyModifierMatch"/>
    /// for what it changes. Resolved once per hook lifetime (never per keystroke: the hook
    /// callback runs inside LowLevelHooksTimeout) and refreshed on restart, because a user can
    /// add a layout while the app runs. Fails toward true.
    /// </summary>
    private volatile bool _altGrLayout = true;

    /// <summary>Test seam over the layout probe so combo matching is testable with no keyboard.</summary>
    internal bool AltGrLayoutForTesting
    {
        get => _altGrLayout;
        set => _altGrLayout = value;
    }
    // Key-OWNED dedicated-action latches (F4 diff review R1): the key currently holding
    // each simple action, VcUndefined = none. Owning the latch by KEY rather than a bool
    // makes rebind-while-held safe with no cross-thread coordination: ReloadHotkeys never
    // touches these — only the hook thread (plus ResetHookState) writes them — so a
    // binding swap can neither strand a latch nor race a reload-time clear against the
    // new binding's key-down. A held key keeps suppressing its own repeats and release
    // until PHYSICAL release, regardless of what it is bound to now.
    private volatile KeyCode _pasteLastHeldKey = KeyCode.VcUndefined;
    private volatile KeyCode _redoLastHeldKey = KeyCode.VcUndefined;
    private volatile KeyCode _generateImageHeldKey = KeyCode.VcUndefined;

    /// <summary>Which hotkey key currently "owns" the state machine. VcUndefined = no key active.</summary>
    private volatile KeyCode _activeHotkeyCode = KeyCode.VcUndefined;

    // Serializes every mutation of the recording-gesture fields (_isHandsFreeMode,
    // _keyDownStartedRecording, _activeHotkeyCode, _activeKeyDownTimestamp) and the gesture
    // epoch: the hook thread's compound read-classify-mutate blocks in HandleKeyDown /
    // HandleKeyUp vs the UI thread's InvalidateRecordingGesture (pipeline-transition sync).
    // Without it, a release classifying concurrently with an external invalidation could
    // re-arm the hands-free latch AFTER the reset — the permanent tap/hold inversion
    // (2026-07-17 PTT bug). Leaf lock: no dispatch, no logging, no other lock taken inside
    // any locked region.
    private readonly object _gestureLock = new();

    // Monotonic stamp incremented by every semantic invalidation (under _gestureLock).
    // Queued hotkey dispatches capture it at decision time and are DISCARDED at delivery
    // when it has moved on: an action classified against state that an invalidation has
    // since killed must not act (Codex diff round 1 — a press during Transcribing whose
    // queued toggle delivered after the →Idle sweep would otherwise START a phantom
    // recording with no armed gesture to stop it).
    private int _gestureEpoch;

    /// <summary>
    /// True when a queued dispatch captured under the CURRENT gesture epoch — i.e. no
    /// invalidation happened between its decision and its delivery. Checked first thing by
    /// the dispatched lambdas; internal so tests pin the fence deterministically.
    /// </summary>
    internal bool IsQueuedActionCurrent(int capturedEpoch)
        => Volatile.Read(ref _gestureEpoch) == capturedEpoch;

    /// <summary>
    /// Test seam over the UI dispatch of the recording-gesture lambdas. Production always
    /// goes through the real <see cref="_dispatcherQueue"/>; tests inject a capturer so a
    /// queued lambda can be EXECUTED after a gesture invalidation — pinning that the
    /// delivery fence actually discards stale actions (an epoch-arithmetic assertion alone
    /// would survive the fence being deleted from the lambdas).
    /// </summary>
    internal Func<Action, bool>? DispatchSeamForTesting { get; set; }

    /// <summary>Dispatch a recording-gesture lambda to the UI thread (or the test seam).
    /// Returns null when neither exists — matching the old <c>_dispatcherQueue?.TryEnqueue</c>
    /// shape the dropped-enqueue stamp retraction keys off.</summary>
    private bool? TryDispatch(Action work)
    {
        if (DispatchSeamForTesting is { } seam) return seam(work);
        return _dispatcherQueue?.TryEnqueue(() => work());
    }

    /// <summary>
    /// What action a key-down dispatched on the hook thread (snapshot of the
    /// state at the time the press was received, captured BEFORE handing off to
    /// the dispatcher). Used by tests to verify the lambda would consume a
    /// captured action rather than re-reading live state.
    /// </summary>
    internal enum KeyDownAction { Start, StopHandsFree }

    /// <summary>The action captured by the most recent <see cref="HandleKeyDown"/> for the recording hotkey path. Test-only.</summary>
    private volatile object? _lastDispatchedActionBox;

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> stamped on the hook thread when a
    /// key-down dispatches a START action, consumed once by the UI-thread
    /// toggle path to measure hotkey→pill-visible latency. Accessed only via
    /// <see cref="Interlocked"/> (never read raw). 0 = no unconsumed stamp.
    /// A stamp left behind by a start that never ran (enqueue dropped, state
    /// machine ignored it) is discarded by the consumer's freshness check.
    /// </summary>
    private long _lastStartDispatchTimestamp;

    /// <summary>
    /// Consume-once accessor for the most recent START dispatch timestamp.
    /// Returns null when no unconsumed stamp exists. Callers must treat the
    /// value as advisory and apply their own freshness window — see
    /// <see cref="Helpers.MiniRecorderShowLatencyProbe.IsFresh"/>.
    /// </summary>
    internal long? TryConsumeStartDispatchTimestamp()
    {
        var ts = Interlocked.Exchange(ref _lastStartDispatchTimestamp, 0);
        return ts == 0 ? null : ts;
    }

    /// <summary>
    /// Watchdog decision returned by <see cref="EvaluateWatchdog"/>:
    /// Healthy = nothing to do; NearMiss = log diagnostic but don't restart;
    /// Restart = hook-silent threshold tripped while Windows saw input recently.
    /// </summary>
    internal enum WatchdogDecision { Healthy, NearMiss, Restart }

    /// <summary>
    /// Test-only accessor for the active hotkey state. Internal because the test
    /// assembly is granted <c>InternalsVisibleTo</c>; production code should not depend
    /// on this — <see cref="ResetState"/> and <see cref="InvalidateRecordingGesture"/> are
    /// the only sanctioned mutation points.
    /// </summary>
    internal KeyCode ActiveHotkeyCodeForTesting => _activeHotkeyCode;

    /// <summary>Test-only: live <see cref="_isHandsFreeMode"/> read.</summary>
    internal bool IsHandsFreeModeForTesting => _isHandsFreeMode;

    /// <summary>Test-only: paste-last key-repeat guard (true while any key holds the action).</summary>
    internal bool PasteLastKeyDownForTesting => _pasteLastHeldKey != KeyCode.VcUndefined;

    /// <summary>Test-only: redo-last key-repeat guard (true while any key holds the action).</summary>
    internal bool RedoLastKeyDownForTesting => _redoLastHeldKey != KeyCode.VcUndefined;

    /// <summary>Test-only: generate-image key-repeat guard (IMG-1; true while any key holds the action).</summary>
    internal bool GenerateImageKeyDownForTesting => _generateImageHeldKey != KeyCode.VcUndefined;

    /// <summary>Test-only: action captured by the most recent recording-hotkey key-down dispatch. Null if no key-down has run yet.</summary>
    internal KeyDownAction? LastDispatchedActionForTesting => (KeyDownAction?)_lastDispatchedActionBox;

    /// <summary>Test-only setter for hands-free state. Used to drive snapshot-vs-live-state regression tests.</summary>
    internal void SetHandsFreeModeForTesting(bool value) => _isHandsFreeMode = value;

    /// <summary>
    /// Probe to check whether a hotkey key is currently physically pressed. Defaults to
    /// <see cref="DefaultIsKeyPhysicallyDown"/> which queries Windows via GetAsyncKeyState.
    /// Tests substitute a stub so the stale-different-key branch can be exercised
    /// deterministically without a real keyboard.
    /// </summary>
    internal Func<KeyCode, bool> KeyDownProbe { get; set; } = DefaultIsKeyPhysicallyDown;

    /// <summary>True if the most recent stop was triggered by push-to-talk (long press release).</summary>
    public bool WasPushToTalk { get; private set; }

    /// <summary>
    /// When positive, prompt/paste-last hotkey keys pass through unsuppressed and don't fire
    /// events. Held during paste operations so programmatic Ctrl+V isn't caught by the hook.
    /// A refcount, not a bool (IMG-BG, 2026-07-16): a background image job's paste can overlap
    /// a live dictation's paste, and with a plain bool the first scope to end un-suppressed the
    /// hook mid-way through the other's injected keystrokes.
    /// </summary>
    private int _suppressPromptActions;

    public bool SuppressPromptActions
        => global::System.Threading.Volatile.Read(ref _suppressPromptActions) > 0;

    /// <summary>
    /// Enter a suppression scope. Dispose to leave; disposing twice releases once. Suppression
    /// holds while ANY scope is alive.
    /// </summary>
    public IDisposable BeginSuppressPromptActions()
    {
        global::System.Threading.Interlocked.Increment(ref _suppressPromptActions);
        return new SuppressPromptActionsLease(this);
    }

    private sealed class SuppressPromptActionsLease : IDisposable
    {
        private HotkeyService? _owner;

        internal SuppressPromptActionsLease(HotkeyService owner) => _owner = owner;

        public void Dispose()
        {
            var owner = global::System.Threading.Interlocked.Exchange(ref _owner, null);
            if (owner != null)
                global::System.Threading.Interlocked.Decrement(ref owner._suppressPromptActions);
        }
    }

    /// <summary>Raised on UI thread when recording should be toggled.</summary>
    public event Action? ToggleRecordingRequested;

    /// <summary>Raised on UI thread when paste-last transcription is requested.</summary>
    public event Action? PasteLastRequested;

    /// <summary>Raised on UI thread when redo-last enhancement is requested.</summary>
    public event Action? RedoLastRequested;

    /// <summary>Raised on UI thread when the new-image-generation dialog is requested (IMG-1).</summary>
    public event Action? GenerateImageRequested;

    /// <summary>Raised on UI thread when a prompt-specific hotkey is pressed. Arg = prompt ID.</summary>
    public event Action<string>? PromptHotkeyPressed;

    /// <summary>Raised on UI thread when AltGr is detected and a phantom prompt override should be cancelled.</summary>
    public event Action? CancelPromptOverride;

    public HotkeyService(SettingsService settings)
        : this(settings, watchdogObserver: null)
    {
    }

    /// <summary>HKY-2: production overload (App's explicit DI factory) supplying the
    /// watchdog foreground observer. Null (the public ctor, all test sites) = no
    /// foreground-context line; the watchdog itself behaves identically.</summary>
    internal HotkeyService(SettingsService settings, WatchdogForegroundObserver? watchdogObserver)
    {
        _settings = settings;
        _watchdogObserver = watchdogObserver;
        _config = ResolveRoleBindings(_config);
    }

    /// <summary>
    /// Re-read hotkey assignments from settings. Call after user changes hotkey in Settings UI.
    /// </summary>
    public void ReloadHotkeys()
    {
        HotkeyConfiguration previous, updated;
        lock (_configWriteLock)
        {
            previous = _config;
            updated = ResolveRoleBindings(previous);
            // ONE volatile write publishes every role together — the hook thread can never see a
            // new trigger beside an old modifier requirement (HotkeyConfiguration's remarks) —
            // and the lock keeps a concurrent prompt registration from losing either update.
            _config = updated;
        }
        // Deliberately NO latch/gesture mutation here (F4): the held-key latches are
        // KEY-owned and only the hook thread writes them — a reload-time clear would
        // race a concurrent key-down of the new binding (publish-then-clear could wipe
        // a latch the new key just set, letting its next repeat re-fire the action).
        if (!previous.Recording.Equals(updated.Recording))
        {
            Logger.Information("Hotkey changed from {Old} to {New}",
                previous.Recording.Canonical, updated.Recording.Canonical);
        }
        WarnOnShadowedRoles(updated);
    }

    /// <summary>
    /// Read all four role bindings from settings into a new snapshot, carrying the prompt map
    /// across unchanged.
    /// </summary>
    private HotkeyConfiguration ResolveRoleBindings(HotkeyConfiguration current) => current.WithRoles(
        ResolveRecordingBinding(),
        ResolveOptionalBinding(AppDefaults.PasteLastHotkeyModifier, "paste-last"),
        ResolveOptionalBinding(AppDefaults.RedoLastHotkeyModifier, "redo-last"),
        ResolveOptionalBinding(AppDefaults.GenerateImageHotkeyModifier, "generate-image"));

    /// <summary>
    /// A single-key role binding cannot be told apart from a combo on the same trigger once the
    /// hook is running: the single-key branch short-circuits its modifier check (that is the
    /// pre-HKY-3 compatibility guarantee), and role branches are tested in a FIXED order, so
    /// whichever is checked first wins both presses. The Settings page refuses to create this
    /// pairing; a hand-edited settings.json still can, so say so in the log rather than let a
    /// hotkey be silently dead.
    /// </summary>
    private static void WarnOnShadowedRoles(HotkeyConfiguration config)
    {
        var seen = new Dictionary<KeyCode, HotkeyBinding>();
        foreach (var binding in EnumerateRoleBindings(config))
        {
            if (seen.TryGetValue(binding.Trigger, out var other)
                && (binding.IsSingleKey || other.IsSingleKey)
                && !binding.Equals(other))
            {
                Logger.Warning(
                    "Hotkeys {A} and {B} share a trigger key and one requires no modifier — the one " +
                    "checked first will win both presses. Re-pick one in Settings.",
                    other.Canonical, binding.Canonical);
            }
            seen[binding.Trigger] = binding;
        }
    }

    private static IEnumerable<HotkeyBinding> EnumerateRoleBindings(HotkeyConfiguration config)
    {
        yield return config.Recording;
        if (config.PasteLast.HasValue) yield return config.PasteLast.Value;
        if (config.RedoLast.HasValue) yield return config.RedoLast.Value;
        if (config.GenerateImage.HasValue) yield return config.GenerateImage.Value;
    }

    /// <summary>
    /// Register prompt-specific hotkeys. Call when prompts are loaded or changed.
    /// </summary>
    public void RegisterPromptHotkeys(IEnumerable<(string promptId, string keyName)> bindings)
    {
        // Deliberately NO state reset (F4, 2026-07-14): gesture state is bound to the
        // PHYSICAL press lifecycle, not the binding table — the old full ResetState()
        // here cleared _activeHotkeyCode mid-hold (swallowing the stop key-up) and
        // _isHandsFreeMode mid-recording (inverting the tap/hold machine) whenever the
        // user edited prompts while dictating. Nothing else depended on it: the pending
        // prompt override is MainViewModel's captured mutable CustomPrompt reference,
        // which hotkey-service state never touched.
        //
        // The lock spans the whole read-modify-write (see _configWriteLock). It has to include
        // the loop, not just the publish: the shadowing decisions below are made against
        // `current`, so a snapshot derived from a stale one would lose a concurrent role change
        // AND register prompts judged against roles that no longer exist. Cost is irrelevant —
        // this runs on a prompt edit, never per keystroke.
        lock (_configWriteLock)
        {
            var current = _config;
            var newDict = new Dictionary<KeyCode, string>();
            foreach (var (promptId, keyName) in bindings)
            {
                // Prompt hotkeys are SINGLE-KEY until HKY-7, so a stored combo is not a prompt
                // binding this build can honour — MapKeyCode rejects it and it is skipped below,
                // exactly as any other unrecognised name is.
                var code = MapKeyCode(keyName);
                if (code.HasValue)
                {
                    // Typing keys became mappable in HKY-3 as COMBO triggers. Prompt hotkeys carry
                    // no modifiers, so a stored "Space" or "D" — which MapKeyCode used to reject
                    // outright — would now register and suppress every space or D the user types.
                    // The prompt picker cannot offer these, so this guards an imported or
                    // hand-edited prompts file, and it refuses exactly as a role binding does.
                    if (HotkeyBinding.IsTypingKey(code.Value))
                    {
                        Logger.Warning("Prompt hotkey {Key} for {PromptId} needs a modifier — skipped",
                            keyName, promptId);
                        continue;
                    }

                    // Skip only keys a role genuinely SHADOWS — a single-key role, which claims
                    // every press of its trigger. A combo role does not shadow: HandleKeyDown
                    // falls through to the prompt branch when the combo's modifiers are not held,
                    // so "prompt F6" beside "role Ctrl+F6" works and must not be dropped.
                    if (current.IsPromptBindingBlocked(code.Value))
                    {
                        Logger.Warning("Prompt hotkey {Key} for {PromptId} conflicts with a system hotkey — skipped",
                            keyName, promptId);
                        continue;
                    }
                    newDict[code.Value] = promptId;
                    Logger.Information("Prompt hotkey registered: {Key} → {PromptId}", keyName, promptId);
                }
            }
            // Single volatile write — roles and prompts publish together.
            _config = current.WithPrompts(newDict);
        }
    }

    /// <summary>
    /// All available hotkey names, grouped: modifier keys, then function keys, then extra keys.
    /// Modifier keys support tap/hold (for recording hotkey). All keys work for single-press actions.
    /// </summary>
    public static readonly string[] ModifierKeys =
        ["RightAlt", "LeftAlt", "LeftControl", "RightControl", "RightShift", "LeftShift"];

    public static readonly string[] FunctionKeys =
        ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];

    public static readonly string[] ExtraKeys =
        ["Pause", "ScrollLock", "Insert", "NumLock"];

    /// <summary>The space bar — its own picker section (owner, 2026-08-30): it is the single most
    /// likely combo trigger (Ctrl+Space is the peer-standard dictation hotkey), so it leads the
    /// typing keys rather than sitting behind 36 of them.</summary>
    public static readonly string[] SpaceKeys = ["Space"];

    /// <summary>A–Z, their own picker section.</summary>
    public static readonly string[] LetterKeys =
    [
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
    ];

    /// <summary>0–9, their own picker section.</summary>
    public static readonly string[] DigitKeys =
        ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

    /// <summary>
    /// Keys people type with. HKY-3 added these so combos like Ctrl+Space and Ctrl+Shift+D are
    /// expressible; they are USELESS on their own and <see cref="Helpers.HotkeyBinding.Validate"/>
    /// refuses to bind one without Ctrl — a bare "D" binding would suppress every D the user
    /// types, and Shift is no help because Shift+D types "D".
    /// </summary>
    /// <remarks>
    /// COMPOSED from the three picker sections rather than listed again, so the set the validator
    /// and the filters consult can never drift from the set the picker offers. Content and order
    /// are byte-identical to the pre-split list (Space, A–Z, 0–9).
    /// </remarks>
    public static readonly string[] TypingKeys = [.. SpaceKeys, .. LetterKeys, .. DigitKeys];

    /// <summary>All available hotkey names (modifiers + function + extra + typing).</summary>
    public static readonly string[] AllKeys =
        [.. ModifierKeys, .. FunctionKeys, .. ExtraKeys, .. TypingKeys];

    /// <summary>
    /// Start the global keyboard hook. Must be called from UI thread to capture DispatcherQueue.
    /// </summary>
    public async Task StartAsync(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _disposed = false; // Reset in case we're restarting after Dispose (e.g. sleep/wake)
        _restartCount = 0; // Fresh lifetime — don't inherit attempt count from prior Dispose.
        // Serialized through the same _restartLock as every other hook-lifecycle mutation
        // (F29): a hook death inside the StartHookAsync below queues the DURABLE
        // RestartDeadHookAsync continuation, which waits on this lock and recovers right
        // after we release it. Blocking-await (not WaitAsync(0)) — at cold start nothing
        // else can hold the lock, so this cannot deadlock or delay startup;
        // RestartHookAsync's fail-fast semantics for its own callers are unchanged.
        bool started;
        await _restartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            started = await StartHookAsync().ConfigureAwait(false);
        }
        finally
        {
            _restartLock.Release();
        }
        if (!started && !_disposed)
        {
            // Recovery is owned by the DURABLE dead-hook handoff (RestartDeadHookAsync),
            // already queued by the hook-ended continuation and waiting on the lock we
            // just released — restarting here as well would race it and bounce a healthy
            // replacement (Codex PR-4 R2). Only report honestly.
            Logger.Warning("Initial hotkey hook start failed — dead-hook recovery pending");
        }
        // After ConfigureAwait(false) the continuation is on the thread pool, but
        // DispatcherTimer must be created on a thread with a DispatcherQueue — marshal.
        dispatcherQueue.TryEnqueue(StartWatchdog);
    }

    /// <summary>
    /// Arm the periodic hook-health check. Must be called on the UI thread because
    /// <see cref="DispatcherTimer"/> needs the dispatcher to schedule ticks.
    /// </summary>
    private void StartWatchdog()
    {
        // Guard: Dispose may have run between StartAsync's TryEnqueue and this callback.
        if (_disposed) return;
        _watchdogTimer?.Stop();
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(WatchdogIntervalSeconds) };
        _watchdogTimer.Tick += OnWatchdogTick;
        _watchdogTimer.Start();
    }

    /// <summary>
    /// Pure decision function for watchdog action. Extracted so tests can drive it
    /// with synthetic timestamps without touching real Win32 or real hooks.
    /// </summary>
    /// <remarks>
    /// Signal mapping:
    /// <list type="bullet">
    ///   <item><c>Healthy</c>: user idle, hook event recent, or cooldown still active.</item>
    ///   <item><c>NearMiss</c>: hook silent ≥ <see cref="NearMissHookSilentFloorSeconds"/>s
    ///     but below the restart threshold, while Windows saw input recently — diagnostic only.</item>
    ///   <item><c>Restart</c>: hook silent past <see cref="HookSilentThresholdSeconds"/>s
    ///     while Windows saw input recently and the rate-limit cooldown has elapsed.</item>
    /// </list>
    /// Touch, pen, or gamepad input that updates <c>LASTINPUTINFO</c> without flowing
    /// through <c>WH_MOUSE_LL</c> may be classified as keyboard-implied input. The
    /// resulting restart is non-destructive and rate-limited.
    /// </remarks>
    internal static WatchdogDecision EvaluateWatchdog(
        DateTime nowUtc,
        DateTime lastHookEventUtc,
        uint lastMouseEventTickTime,
        uint windowsInputDwTime,
        uint tickNow,
        DateTime lastWatchdogRestartUtc)
    {
        // Unchecked subtraction handles the 49.7-day GetTickCount wrap correctly.
        var msSinceWindowsInput = unchecked(tickNow - windowsInputDwTime);
        var secsSinceWindowsInput = msSinceWindowsInput / 1000.0;
        var secsSinceHookEvent = (nowUtc - lastHookEventUtc).TotalSeconds;
        var mouseExplainsWindowsInput =
            TickDistance(lastMouseEventTickTime, windowsInputDwTime) <= MouseExplainsWindowsInputToleranceMs;
        var secsSinceLastRestart = (nowUtc - lastWatchdogRestartUtc).TotalSeconds;

        // User idle — Windows hasn't seen input recently.
        if (secsSinceWindowsInput > WindowsInputFreshSeconds) return WatchdogDecision.Healthy;
        // Keyboard hook event seen recently — definitely alive.
        if (secsSinceHookEvent < NearMissHookSilentFloorSeconds) return WatchdogDecision.Healthy;
        // Windows input was fresh, and the mouse hook saw the same input tick. This is
        // mouse-only browsing, not evidence that the keyboard hook died. The next real
        // keystroke either updates _lastHookEventUtc (proof of life) or advances
        // LASTINPUTINFO without a matching mouse tick, falling through to Restart.
        if (mouseExplainsWindowsInput) return WatchdogDecision.Healthy;
        // Approaching the restart threshold but not yet there — emit diagnostic.
        if (secsSinceHookEvent < HookSilentThresholdSeconds) return WatchdogDecision.NearMiss;
        // Past threshold but still in cooldown — skip restart, treat as near-miss
        // so the throttled diagnostic surfaces the unusual situation in the log.
        if (secsSinceLastRestart < MinSecondsBetweenAutoRestarts) return WatchdogDecision.NearMiss;
        return WatchdogDecision.Restart;
    }

    private void OnWatchdogTick(object? sender, object e)
    {
        try
        {
            if (_disposed || _hook == null) return;
            // Skip while a key is actively held — restart would leak the key-down.
            if (_activeHotkeyCode != KeyCode.VcUndefined) return;

            var info = new NativeInterop.LASTINPUTINFO
            {
                cbSize = (uint)global::System.Runtime.InteropServices.Marshal.SizeOf<NativeInterop.LASTINPUTINFO>()
            };
            if (!NativeInterop.GetLastInputInfo(ref info)) return;

            var nowUtc = DateTime.UtcNow;
            var tickNow = NativeInterop.GetTickCount();
            var lastMouseEventTickTime = GetLastMouseEventTickCount();
            var decision = EvaluateWatchdog(nowUtc, _lastHookEventUtc, lastMouseEventTickTime, info.dwTime, tickNow, _lastWatchdogRestartUtc);

            switch (decision)
            {
                case WatchdogDecision.Healthy:
                    return;

                case WatchdogDecision.NearMiss:
                    // Throttle to one log per minute. UI-thread only — DispatcherTimer
                    // ticks on the UI thread so no synchronization needed.
                    if (nowUtc - _lastNearMissLogUtc < NearMissLogThrottle) return;
                    _lastNearMissLogUtc = nowUtc;
                    var nearMissHookSilent = (nowUtc - _lastHookEventUtc).TotalSeconds;
                    var nearMissWindowsInput = unchecked(tickNow - info.dwTime) / 1000.0;
                    var nearMissMouseInput = unchecked(tickNow - lastMouseEventTickTime) / 1000.0;
                    var nearMissReason = TickDistance(lastMouseEventTickTime, info.dwTime) <= MouseExplainsWindowsInputToleranceMs
                        ? "mouse input explains Windows input"
                        : "keyboard input implied";
                    Logger.Information(
                        "Watchdog near-miss: hookSilent={HookSilent:F0}s windowsInput={WindowsInput:F0}s mouseInput={MouseInput:F0}s reason={Reason} — no restart yet",
                        nearMissHookSilent, nearMissWindowsInput, nearMissMouseInput, nearMissReason);
                    return;

                case WatchdogDecision.Restart:
                    var hookSilent = (nowUtc - _lastHookEventUtc).TotalSeconds;
                    var windowsInput = unchecked(tickNow - info.dwTime) / 1000.0;
                    var mouseInput = unchecked(tickNow - lastMouseEventTickTime) / 1000.0;
                    Logger.Warning(
                        "Watchdog: keyboard hook silent for {HookSilent:F0}s, Windows saw input {WindowsInput:F0}s ago, mouse hook silent for {MouseInput:F0}s — restarting global hook",
                        hookSilent, windowsInput, mouseInput);
                    // HKY-2: sample the foreground BEFORE the restart call below — its async
                    // body runs synchronously through lock acquisition and hook disposal
                    // before its first yield, so a post-call capture would not be a fire-time
                    // sample. Two sub-ms user32 calls; elevation and the process-name lookup
                    // happen off-thread in the observer, so recovery is never delayed.
                    var foreground = WatchdogForegroundProbe.Capture(
                        NativeInterop.GetForegroundWindow, GetWindowPid, nowUtc);
                    _lastWatchdogRestartUtc = nowUtc;
                    // Reset so we don't immediately re-fire on the next tick while the restart is in flight.
                    _lastHookEventUtc = nowUtc;
                    // Fire-and-forget the restart; observe exceptions so nothing is swallowed.
                    _ = RestartAndLogAsync();
                    _watchdogObserver?.Publish(foreground);
                    return;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Hook watchdog tick failed — continuing");
        }
    }

    private async Task RestartAndLogAsync()
    {
        try { _ = await RestartHookAsync("watchdog").ConfigureAwait(false); }
        catch (Exception ex) { Logger.Error(ex, "Watchdog-triggered hook restart failed"); }
    }

    /// <summary>HKY-2: PID half of the foreground capture (the return value of
    /// GetWindowThreadProcessId itself is the thread id, which we don't need).</summary>
    private static uint GetWindowPid(IntPtr hwnd)
    {
        NativeInterop.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static uint TickDistance(uint a, uint b)
    {
        var forward = unchecked(a - b);
        var backward = unchecked(b - a);
        return forward < backward ? forward : backward;
    }

    private uint GetLastMouseEventTickCount()
    {
        return global::System.Threading.Volatile.Read(ref _lastMouseEventTickCount);
    }

    private void MarkMouseEvent(uint tickCount)
    {
        global::System.Threading.Volatile.Write(ref _lastMouseEventTickCount, tickCount);
    }

    /// <summary>
    /// Public restart entry point for App-level callers (PowerModeChanged.Resume,
    /// SessionSwitch.SessionUnlock). Serialized via <see cref="_restartLock"/> so
    /// concurrent App / watchdog / hook-task-continuation restarts mutex with each
    /// other — SharpHook is process-global. Preserves <see cref="_isHandsFreeMode"/>
    /// across the restart so a tap immediately after wake/unlock resolves correctly.
    /// Returns true if the restart actually ran; false if it was coalesced because
    /// another restart was already in flight, the service was disposed, or the
    /// per-lifetime restart limit was hit. Caller should not update its own cooldown
    /// or log success unless this returned true.
    /// </summary>
    public Task<bool> RestartAsync(string reason) => RestartHookAsync(reason);

    /// <summary>
    /// Returns false when the hook task ended during the initialization window, so no
    /// caller logs success or resets the attempt counter over a dead hook. Recovery is
    /// NOT the caller's job: the hook-ended continuation's durable handoff
    /// (<see cref="RestartDeadHookAsync"/>) waits for the lifecycle lock and restarts
    /// once the caller releases it — a death after this method's completion sample
    /// takes that same path (Codex PR-4 R2/R3).
    /// </summary>
    private async Task<bool> StartHookAsync()
    {
        // Refresh the AltGr verdict per hook lifetime — a user can add a keyboard layout while
        // the app runs, and this is the one place cheap enough to ask (never per keystroke).
        _altGrLayout = KeyboardLayoutProfile.AnyInstalledLayoutUsesAltGr();
        Logger.Information("Starting global hotkey hook, key={Key}", _config.Recording.Canonical);

        // Reset the watchdog's last-event baseline so a freshly-started hook isn't
        // immediately judged "silent" against a timestamp that pre-dates the restart.
        // Without this, after a SessionUnlock-triggered restart the watchdog would tick
        // 15s later, see _lastHookEventUtc from before sleep (hours old) plus Windows
        // having just seen unlock-related mouse input, and falsely fire another restart.
        var nowUtc = DateTime.UtcNow;
        _lastHookEventUtc = nowUtc;
        MarkMouseEvent(NativeInterop.GetTickCount());

        // GlobalHookType.All installs a low-level mouse hook in addition to the keyboard hook,
        // so native callbacks still sit in the OS mouse pipeline. We subscribe to mouse
        // MOVE and DRAG in addition to click/release/wheel — and never read or persist payload
        // fields from the mouse event args. Movement is essential: GetLastInputInfo (the
        // watchdog's "Windows saw input" signal) advances on plain cursor movement, but movement
        // flows through neither our keyboard hook nor a click/wheel-only mouse hook. Without a
        // mouse-MOVE signal of our own, the watchdog mistook "user moving the mouse without
        // typing" for "keyboard hook died" and restarted the hook every ~60-75s (76 false
        // restarts in one ~6h session). Our handler body is allocation-free in steady state (a
        // Volatile write of GetTickCount; the priority boost is a fast-path no-op after the
        // first event per hook). SharpHook still allocates the event args it dispatches, but
        // libuiohook's native WH_MOUSE_LL processes every move regardless of our subscription,
        // so the marginal cost is just the managed dispatch plus our trivial handler.
        var hook = new SimpleGlobalHook(GlobalHookType.All);
        hook.KeyPressed += OnKeyPressed;
        hook.KeyReleased += OnKeyReleased;
        hook.MousePressed += OnMouseEvent;
        hook.MouseReleased += OnMouseEvent;
        hook.MouseMoved += OnMouseEvent;
        hook.MouseDragged += OnMouseEvent;
        hook.MouseWheel += OnMouseWheel;
        _hook = hook;

        // Observe the hook task — if the hook thread dies (native crash, Windows removed the
        // low-level hook after a timeout, unhandled exception), log it and auto-restart.
        // Capture 'hook' so the ContinueWith can verify it's still the current hook.
        var hookTask = hook.RunAsync();
        _ = hookTask.ContinueWith(t =>
        {
            if (_disposed) return; // intentional shutdown

            if (t.IsFaulted)
                Logger.Error(t.Exception, "Global hotkey hook DIED — will attempt restart");
            else
                Logger.Warning("Global hotkey hook stopped unexpectedly (no error) — will attempt restart");

            // Restart on UI thread so _dispatcherQueue is valid
            _dispatcherQueue?.TryEnqueue(async () =>
            {
                if (_disposed) return;
                // Cheap pre-check only — the AUTHORITATIVE identity revalidation runs
                // inside the lock (RestartDeadHookAsync), where it cannot race a
                // lock-holding start/restart replacing the hook.
                if (_hook != hook) return;
                try { await RestartDeadHookAsync(hook, "hook task ended").ConfigureAwait(false); }
                catch (Exception ex) { Logger.Error(ex, "Failed to restart global hotkey hook"); }
            });
        }, TaskContinuationOptions.NotOnCanceled);

        // Small delay to let the hook initialize
        await Task.Delay(100).ConfigureAwait(false);

        if (hookTask.IsCompleted)
        {
            // Died during init: report failure so no caller logs success or resets the
            // counter over a dead hook. Recovery itself is the hook-ended continuation's
            // durable handoff (RestartDeadHookAsync), which WAITS for the lifecycle lock —
            // a death AFTER this sample but before lock release takes the same path, so
            // this sample is best-effort honesty, not the recovery mechanism (Codex R2).
            Logger.Warning("Global hotkey hook ended during initialization — start reported as failed");
            return false;
        }

        _restartCount = 0; // successful start — reset the counter
        Logger.Information("Global hotkey hook started");
        return true;
    }

    /// <summary>
    /// Returns true if the restart actually ran end-to-end. Returns false if the
    /// call was coalesced (another restart in progress), the service was disposed,
    /// or the per-lifetime restart limit was hit. Callers that need to update their
    /// own cooldown or log success should branch on the return value.
    /// </summary>
    private async Task<bool> RestartHookAsync(string reason)
    {
        if (_disposed) return false;
        // Service-level mutex. SharpHook/libuiohook is process-global, so concurrent
        // App-triggered + watchdog-triggered restarts must serialize through this single
        // lock. WaitAsync(0) makes overlapping REQUESTS fail fast rather than queue —
        // appropriate for the watchdog/App callers, whose triggers re-fire. Hook DEATH is
        // not a request but a fact: it goes through RestartDeadHookAsync below, which
        // waits for the lock so the recovery can never be coalesced away (Codex PR-4 R2).
        if (!await _restartLock.WaitAsync(0).ConfigureAwait(false))
        {
            Logger.Information("Hook restart skipped ({Reason}) — another restart in progress", reason);
            return false;
        }
        try
        {
            if (_disposed) return false; // racing with Dispose between WaitAsync and here
            return await RestartHookLockedAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>
    /// Durable dead-hook recovery (Codex PR-4 R2): WAITS for the lifecycle lock (never
    /// coalesced — the old fail-fast skip could drop the only recovery for a hook that
    /// died while a start/restart held the lock, leaving a dead hook behind a success
    /// log) and revalidates the dead hook's identity INSIDE the lock, so a stale death
    /// notification can never dispose a healthy replacement. Internal for the
    /// superseded-no-op identity test; a real restart from tests would install a live
    /// native hook, so only the no-op path is unit-pinned.
    /// </summary>
    internal async Task RestartDeadHookAsync(object deadHook, string reason)
    {
        if (_disposed) return;
        await _restartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (!ReferenceEquals(_hook, deadHook))
                return; // superseded while waiting — a lock-holding lifecycle op already replaced it
            await RestartHookLockedAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    // The restart body. MUST be called with _restartLock held.
    private async Task<bool> RestartHookLockedAsync(string reason)
    {
        var attempt = Interlocked.Increment(ref _restartCount);
        if (attempt > MaxRestartAttempts)
        {
            Logger.Error("Global hotkey hook restart limit reached ({Max} attempts, reason={Reason}) — giving up. Restart the app to restore hotkey.", MaxRestartAttempts, reason);
            return false;
        }

        Logger.Information("Restarting global hotkey hook ({Reason}, attempt {N}/{Max})", reason, attempt, MaxRestartAttempts);

        // Dispose the dead hook
        if (_hook != null)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _hook.MousePressed -= OnMouseEvent;
            _hook.MouseReleased -= OnMouseEvent;
            _hook.MouseMoved -= OnMouseEvent;
            _hook.MouseDragged -= OnMouseEvent;
            _hook.MouseWheel -= OnMouseWheel;
            try { _hook.Dispose(); } catch { /* already dead */ }
            _hook = null;
        }

        // Hook-state-only reset preserves _isHandsFreeMode so a tap immediately
        // after the restart resolves to the correct action (the user's hands-free
        // recording survives the hook lifecycle).
        ResetHookState();

        // Brief delay before restarting to avoid tight restart loops
        await Task.Delay(500).ConfigureAwait(false);

        if (_disposed) return false;
        // Propagate a died-during-init start as failure: the counter already advanced
        // for this attempt, and the durable dead-hook handoff (or the watchdog's next
        // tick) retries within threshold.
        return await StartHookAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates the SEMANTIC recording-gesture state — the hands-free latch and the current
    /// gesture's started-recording mark — while PRESERVING physical key ownership
    /// (<see cref="_activeHotkeyCode"/> / <see cref="_activeKeyDownTimestamp"/>): a still-held
    /// hotkey keeps suppressing its own auto-repeats, and its eventual physical release
    /// completes inertly (the classification sees <c>startedRecording == false</c>) and clears
    /// ownership itself. Dedicated-key latches (paste-last / redo-last / generate-image) are
    /// untouched — <see cref="ResetState"/> is the broad variant. <see cref="WasPushToTalk"/>
    /// is deliberately preserved: the transcribe pipeline reads it AFTER the stop transition
    /// that routinely fires this method.
    /// <para>Called by MainViewModel's RecordingState transition hook (via
    /// <see cref="Helpers.RecordingGestureSync"/>) and its busy-state ignore branch, so the
    /// latch can never outlive the recording it describes. A latch with nothing recording
    /// permanently inverted the tap/hold machine — press-while-idle took the StopHandsFree
    /// branch and armed no gesture, so a hold's release was inert (2026-07-17 PTT bug: a
    /// pill-stop of a hands-free recording left the latch armed forever).</para>
    /// </summary>
    public void InvalidateRecordingGesture(string reason)
    {
        bool wasArmed;
        lock (_gestureLock)
        {
            wasArmed = InvalidateRecordingGestureLocked();
        }
        if (wasArmed)
            Logger.Information("Hands-free latch cleared ({Reason})", reason);
    }

    /// <summary>Caller MUST hold <see cref="_gestureLock"/>. Returns whether the hands-free
    /// latch was armed (the caller logs outside the lock). Bumps the gesture epoch so
    /// queued-but-undelivered hotkey dispatches decided before this invalidation are
    /// discarded at delivery (see <see cref="IsQueuedActionCurrent"/>).</summary>
    private bool InvalidateRecordingGestureLocked()
    {
        var wasArmed = _isHandsFreeMode;
        _isHandsFreeMode = false;
        _keyDownStartedRecording = false;
        _gestureEpoch++;
        return wasArmed;
    }

    /// <summary>
    /// Full reset of all state: the semantic gesture (via
    /// <see cref="InvalidateRecordingGesture"/>) PLUS all hook-thread bookkeeping including
    /// key ownership and the dedicated-key latches. Called when a recording start is refused
    /// or fails (error, cancel, license-block) — contexts where dropping ownership is correct.
    /// Routine pipeline-transition sync uses the narrow <see cref="InvalidateRecordingGesture"/>
    /// instead. Reconfiguration paths (<see cref="ReloadHotkeys"/>,
    /// <see cref="RegisterPromptHotkeys"/>) deliberately do NOT reset — gesture state is
    /// bound to the physical press lifecycle, not the binding table (F4, 2026-07-14).
    /// </summary>
    public void ResetState()
    {
        bool wasArmed;
        // ONE critical section (Codex diff round 1): a fresh gesture must not be able to
        // slip in between the semantic clear and the bookkeeping clear.
        lock (_gestureLock)
        {
            wasArmed = InvalidateRecordingGestureLocked();
            ResetHookStateLocked();
        }
        if (wasArmed)
            Logger.Information("Hands-free latch cleared ({Reason})", "full reset");
    }

    /// <summary>
    /// Hook-thread-only reset. Clears the bookkeeping that's bound to a particular
    /// hook lifecycle — active key, timestamps, key-repeat / paste-last / redo-last
    /// guards — while preserving <see cref="_isHandsFreeMode"/>, which is a UX-level
    /// flag whose semantics outlive any single hook restart. Called by
    /// <see cref="RestartHookAsync"/> and the lost-key-up branches in
    /// <see cref="HandleKeyDown"/>. The dispatcher-race fix in <see cref="HandleKeyDown"/>
    /// / <see cref="HandleKeyUp"/> requires that <see cref="_isHandsFreeMode"/> survive
    /// a watchdog/session-unlock restart so a tap immediately after the restart
    /// resolves to the correct action.
    /// </summary>
    internal void ResetHookState()
    {
        // Under _gestureLock: three of these fields are the recording-gesture set the
        // lock guards; clearing them mid-classification would otherwise tear a
        // HandleKeyDown/HandleKeyUp compound block. Deliberately does NOT bump the
        // gesture epoch — hook-lifecycle recovery preserves hands-free semantics (F29),
        // so queued dispatches stay deliverable.
        lock (_gestureLock)
        {
            ResetHookStateLocked();
        }
    }

    /// <summary>Caller MUST hold <see cref="_gestureLock"/>.</summary>
    private void ResetHookStateLocked()
    {
        _activeHotkeyCode = KeyCode.VcUndefined;
        _keyDownStartedRecording = false;
        _activeKeyDownTimestamp = 0;
        _pasteLastHeldKey = KeyCode.VcUndefined;
        _redoLastHeldKey = KeyCode.VcUndefined;
        _generateImageHeldKey = KeyCode.VcUndefined;
        _lastKeyDownTimestamp = 0;
    }

    /// <summary>
    /// Map a settings string to a SharpHook KeyCode. Returns null for unknown key names.
    /// Use <see cref="MapKeyCodeOrDefault"/> when a fallback is needed (e.g. recording hotkey).
    /// </summary>
    internal static KeyCode? MapKeyCode(string keyName) => keyName switch
    {
        "RightAlt" => KeyCode.VcRightAlt,
        "LeftAlt" => KeyCode.VcLeftAlt,
        "LeftControl" => KeyCode.VcLeftControl,
        "RightControl" => KeyCode.VcRightControl,
        "RightShift" => KeyCode.VcRightShift,
        "LeftShift" => KeyCode.VcLeftShift,
        "F1" => KeyCode.VcF1,
        "F2" => KeyCode.VcF2,
        "F3" => KeyCode.VcF3,
        "F4" => KeyCode.VcF4,
        "F5" => KeyCode.VcF5,
        "F6" => KeyCode.VcF6,
        "F7" => KeyCode.VcF7,
        "F8" => KeyCode.VcF8,
        "F9" => KeyCode.VcF9,
        "F10" => KeyCode.VcF10,
        "F11" => KeyCode.VcF11,
        "F12" => KeyCode.VcF12,
        "Pause" => KeyCode.VcPause,
        "ScrollLock" => KeyCode.VcScrollLock,
        "Insert" => KeyCode.VcInsert,
        "NumLock" => KeyCode.VcNumLock,
        // HKY-3 typing keys — combo triggers only (see TypingKeys).
        "Space" => KeyCode.VcSpace,
        "A" => KeyCode.VcA, "B" => KeyCode.VcB, "C" => KeyCode.VcC, "D" => KeyCode.VcD,
        "E" => KeyCode.VcE, "F" => KeyCode.VcF, "G" => KeyCode.VcG, "H" => KeyCode.VcH,
        "I" => KeyCode.VcI, "J" => KeyCode.VcJ, "K" => KeyCode.VcK, "L" => KeyCode.VcL,
        "M" => KeyCode.VcM, "N" => KeyCode.VcN, "O" => KeyCode.VcO, "P" => KeyCode.VcP,
        "Q" => KeyCode.VcQ, "R" => KeyCode.VcR, "S" => KeyCode.VcS, "T" => KeyCode.VcT,
        "U" => KeyCode.VcU, "V" => KeyCode.VcV, "W" => KeyCode.VcW, "X" => KeyCode.VcX,
        "Y" => KeyCode.VcY, "Z" => KeyCode.VcZ,
        "0" => KeyCode.Vc0, "1" => KeyCode.Vc1, "2" => KeyCode.Vc2, "3" => KeyCode.Vc3,
        "4" => KeyCode.Vc4, "5" => KeyCode.Vc5, "6" => KeyCode.Vc6, "7" => KeyCode.Vc7,
        "8" => KeyCode.Vc8, "9" => KeyCode.Vc9,
        _ => null
    };

    /// <summary>
    /// Inverse of <see cref="MapKeyCode"/> over <see cref="AllKeys"/>; null for any key that is
    /// not bindable. Built once — <see cref="Helpers.HotkeyBinding"/> renders every canonical
    /// string through it, so a name added to <see cref="AllKeys"/> becomes renderable with no
    /// second table to keep in step.
    /// </summary>
    private static readonly Dictionary<KeyCode, string> KeyCodeNames = BuildKeyCodeNames();

    private static Dictionary<KeyCode, string> BuildKeyCodeNames()
    {
        var map = new Dictionary<KeyCode, string>();
        foreach (var name in AllKeys)
        {
            var code = MapKeyCode(name);
            if (code.HasValue) map[code.Value] = name;
        }
        return map;
    }

    /// <summary>The canonical name for a bindable key, or null when it is not bindable.</summary>
    internal static string? KeyCodeToName(KeyCode keyCode)
        => KeyCodeNames.TryGetValue(keyCode, out var name) ? name : null;

    /// <summary>Map key name with fallback to RightAlt (for recording hotkey which must always have a value).</summary>
    internal static KeyCode MapKeyCodeOrDefault(string keyName) => MapKeyCode(keyName) ?? KeyCode.VcRightAlt;

    /// <summary>
    /// Map a SharpHook <see cref="KeyCode"/> to a Windows virtual-key code suitable
    /// for <see cref="NativeInterop.GetAsyncKeyState"/>. Returns 0 for unmapped keys.
    /// Only the keys we use as hotkeys need to be mapped — coverage is asserted by
    /// <c>HotkeyServiceTests.MapKeyCodeToVk_CoversEveryAllKeysEntry</c> so additions
    /// to <see cref="AllKeys"/> can't silently miss the probe mapping.
    /// </summary>
    internal static int MapKeyCodeToVk(KeyCode kc) => kc switch
    {
        KeyCode.VcRightAlt => 0xA5,      // VK_RMENU
        KeyCode.VcLeftAlt => 0xA4,       // VK_LMENU
        KeyCode.VcLeftControl => 0xA2,   // VK_LCONTROL
        KeyCode.VcRightControl => 0xA3,  // VK_RCONTROL
        KeyCode.VcRightShift => 0xA1,    // VK_RSHIFT
        KeyCode.VcLeftShift => 0xA0,     // VK_LSHIFT
        // Not bindable (AllKeys omits them) but OBSERVABLE: HotkeyModifierMatch probes the Win
        // keys so a held Win breaks an exact modifier match instead of being invisible.
        KeyCode.VcLeftMeta => 0x5B,      // VK_LWIN
        KeyCode.VcRightMeta => 0x5C,     // VK_RWIN
        KeyCode.VcF1 => 0x70, KeyCode.VcF2 => 0x71, KeyCode.VcF3 => 0x72, KeyCode.VcF4 => 0x73,
        KeyCode.VcF5 => 0x74, KeyCode.VcF6 => 0x75, KeyCode.VcF7 => 0x76, KeyCode.VcF8 => 0x77,
        KeyCode.VcF9 => 0x78, KeyCode.VcF10 => 0x79, KeyCode.VcF11 => 0x7A, KeyCode.VcF12 => 0x7B,
        KeyCode.VcPause => 0x13,         // VK_PAUSE
        KeyCode.VcScrollLock => 0x91,    // VK_SCROLL
        KeyCode.VcInsert => 0x2D,        // VK_INSERT
        KeyCode.VcNumLock => 0x90,       // VK_NUMLOCK
        // HKY-3 typing keys. VK_SPACE, then the ASCII-aligned VK ranges: 'A'..'Z' = 0x41..0x5A
        // and '0'..'9' = 0x30..0x39.
        KeyCode.VcSpace => 0x20,
        KeyCode.VcA => 0x41, KeyCode.VcB => 0x42, KeyCode.VcC => 0x43, KeyCode.VcD => 0x44,
        KeyCode.VcE => 0x45, KeyCode.VcF => 0x46, KeyCode.VcG => 0x47, KeyCode.VcH => 0x48,
        KeyCode.VcI => 0x49, KeyCode.VcJ => 0x4A, KeyCode.VcK => 0x4B, KeyCode.VcL => 0x4C,
        KeyCode.VcM => 0x4D, KeyCode.VcN => 0x4E, KeyCode.VcO => 0x4F, KeyCode.VcP => 0x50,
        KeyCode.VcQ => 0x51, KeyCode.VcR => 0x52, KeyCode.VcS => 0x53, KeyCode.VcT => 0x54,
        KeyCode.VcU => 0x55, KeyCode.VcV => 0x56, KeyCode.VcW => 0x57, KeyCode.VcX => 0x58,
        KeyCode.VcY => 0x59, KeyCode.VcZ => 0x5A,
        KeyCode.Vc0 => 0x30, KeyCode.Vc1 => 0x31, KeyCode.Vc2 => 0x32, KeyCode.Vc3 => 0x33,
        KeyCode.Vc4 => 0x34, KeyCode.Vc5 => 0x35, KeyCode.Vc6 => 0x36, KeyCode.Vc7 => 0x37,
        KeyCode.Vc8 => 0x38, KeyCode.Vc9 => 0x39,
        _ => 0
    };

    /// <summary>
    /// Default <see cref="KeyDownProbe"/> implementation. High bit of GetAsyncKeyState
    /// indicates the key is currently physically down. Returns false for unmapped keys
    /// (safe default — caller falls back to elapsed-time heuristic).
    /// </summary>
    private static bool DefaultIsKeyPhysicallyDown(KeyCode kc)
    {
        var vk = MapKeyCodeToVk(kc);
        if (vk == 0) return false;
        return (NativeInterop.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>
    /// Does <paramref name="keyCode"/> fire <paramref name="binding"/> right now — trigger key
    /// AND, for a combo, the required modifiers physically held?
    /// </summary>
    /// <remarks>
    /// A single-key binding never probes: pre-HKY-3 the hook consulted no modifier state, so
    /// RightAlt fired with Shift held, and probing now would silently kill it. A combo probes
    /// through the same <see cref="KeyDownProbe"/> seam the stuck-key recovery uses, so tests
    /// drive it with no keyboard.
    /// </remarks>
    private bool Matches(HotkeyBinding? binding, KeyCode keyCode)
    {
        if (!binding.HasValue) return false;
        var value = binding.Value;
        if (value.Trigger != keyCode) return false;
        return HotkeyModifierMatch.IsSatisfiedBy(value, KeyDownProbe, _altGrLayout);
    }

    /// <summary>Matches a non-optional binding — the recording role, which is always present.</summary>
    private bool Matches(HotkeyBinding binding, KeyCode keyCode)
        => binding.Trigger == keyCode
           && HotkeyModifierMatch.IsSatisfiedBy(binding, KeyDownProbe, _altGrLayout);

    /// <summary>
    /// Process a key-down event. Internal for testing.
    /// Returns true if the event should be suppressed.
    /// </summary>
    internal bool HandleKeyDown(KeyCode keyCode, long timestampMs)
    {
        // ── Gesture ownership FIRST (F4, 2026-07-14): an event for the key that owns
        // the in-flight press lifecycle belongs to the GESTURE, regardless of what the
        // key is currently bound to. A reconfiguration mid-hold (ReloadHotkeys /
        // RegisterPromptHotkeys) swaps the binding tables — it must not let a held
        // key's repeats leak to the foreground app or fire a newly-bound dedicated
        // action. This block therefore precedes the paste-last/redo-last/generate-image
        // branches AND the binding-membership checks below.
        if (_activeHotkeyCode != KeyCode.VcUndefined && keyCode == _activeHotkeyCode)
        {
            // Key-repeat suppression with lost-key-up detection:
            // Windows sends a first KeyDown repeat after the "repeat delay" (250–1000ms
            // depending on user settings), then subsequent repeats every ~30ms. We
            // suppress ALL repeats. If the gap since the last KeyDown exceeds 2000ms
            // (well above the max repeat delay of 1000ms + slowest repeat rate), the
            // key-up was genuinely lost (dialog stole focus, etc.) and this is a new
            // press — reset and allow it through.
            if (timestampMs - _lastKeyDownTimestamp < 2000)
            {
                _lastKeyDownTimestamp = timestampMs;
                return true; // Key-repeat or initial repeat delay — suppress
            }
            // Gap too large for any key-repeat — key-up was genuinely lost.
            Logger.Warning("Lost key-up detected ({Key}, gap {Gap}ms) — auto-resetting",
                _activeHotkeyCode, timestampMs - _lastKeyDownTimestamp);
            // Hook-only reset: the user's hands-free state (if any) survives this
            // recovery, otherwise a stuck-key would silently flip the recording mode.
            // Active becomes Undefined, so this press falls through below as a genuine
            // fresh press of whatever the key is bound to NOW.
            ResetHookState();
        }

        // A held DEDICATED key owns its repeats until physical release too (F4 diff
        // review R1): its original press was suppressed, so a mid-hold rebind must not
        // reinterpret its auto-repeats under the NEW binding table — a repeat could
        // otherwise start a recording, fire another action, or leak to the foreground.
        if (keyCode == _pasteLastHeldKey || keyCode == _redoLastHeldKey || keyCode == _generateImageHeldKey)
        {
            return true; // repeat of a suppressed dedicated press — inert until release
        }

        // ONE volatile read of the binding snapshot for this whole decision (HKY-3). Re-reading
        // per branch could straddle a ReloadHotkeys and classify one press against two different
        // configurations.
        var config = _config;

        // Check paste-last hotkey (simple press, no tap/hold)
        // When SuppressPromptActions is set (during paste), let key through so programmatic Ctrl+V works.
        // HKY-3: a trigger match whose MODIFIERS are unsatisfied falls THROUGH to the branches
        // below rather than returning — otherwise a Ctrl+F6 recording binding would be swallowed
        // by a plain-F6 paste-last binding that never fires.
        if (Matches(config.PasteLast, keyCode))
        {
            if (SuppressPromptActions) return false;
            if (_pasteLastHeldKey != KeyCode.VcUndefined) return true; // action already held — repeat guard
            _pasteLastHeldKey = keyCode;
            _dispatcherQueue?.TryEnqueue(() => PasteLastRequested?.Invoke());
            return true;
        }

        // Check redo-last hotkey (simple press, opens model picker for last enhancement)
        if (Matches(config.RedoLast, keyCode))
        {
            if (SuppressPromptActions) return false;
            if (_redoLastHeldKey != KeyCode.VcUndefined) return true; // action already held — repeat guard
            _redoLastHeldKey = keyCode;
            _dispatcherQueue?.TryEnqueue(() => RedoLastRequested?.Invoke());
            return true;
        }

        // Check generate-image hotkey (simple press, opens the new-image dialog — IMG-1)
        if (Matches(config.GenerateImage, keyCode))
        {
            if (SuppressPromptActions) return false;
            if (_generateImageHeldKey != KeyCode.VcUndefined) return true; // action already held — repeat guard
            _generateImageHeldKey = keyCode;
            _dispatcherQueue?.TryEnqueue(() => GenerateImageRequested?.Invoke());
            return true;
        }

        // Determine if this is a recording hotkey (main or prompt)
        bool isMainHotkey = Matches(config.Recording, keyCode);
        config.Prompts.TryGetValue(keyCode, out var promptId);
        bool isPromptHotkey = !isMainHotkey && promptId != null;

        // Not ours, or ours but without its modifiers held: pass the key through UNSUPPRESSED.
        // The second case is what lets a bare Space keep typing a space while Ctrl+Space records.
        if (!isMainHotkey && !isPromptHotkey) return false;
        if (isPromptHotkey && SuppressPromptActions) return false;

        // AltGr detection: on European keyboards, pressing RightAlt (AltGr) sends a phantom
        // LeftControl key-down immediately before RightAlt. If LeftControl was just caught as a
        // prompt hotkey and the main hotkey (RightAlt) arrives within a tiny window, this is AltGr.
        // Cancel the phantom prompt action and transfer ownership to the main hotkey.
        // Don't dispatch another toggle — one was already queued by the LeftControl handler.
        // Predicate + ownership transfer under ONE lock acquisition (Codex diff round 1):
        // an invalidation between the check and the write must not resurrect ownership
        // the reset just cleared.
        bool altGrTransfer;
        lock (_gestureLock)
        {
            altGrTransfer = _activeHotkeyCode == KeyCode.VcLeftControl
                && keyCode == KeyCode.VcRightAlt
                && isMainHotkey
                && (timestampMs - _activeKeyDownTimestamp) < 50;
            if (altGrTransfer)
                _activeHotkeyCode = keyCode;
        }
        if (altGrTransfer)
        {
            Logger.Information("AltGr detected (LeftControl+RightAlt) — cancelling phantom prompt override");
            _dispatcherQueue?.TryEnqueue(() => CancelPromptOverride?.Invoke());
            return true;
        }

        // A DIFFERENT hotkey arrived while one is "active" (the same-key repeat /
        // lost-key-up case is handled by the gesture-owned block at the top of this
        // method). Normally we suppress to
        // prevent two hotkeys interfering while one is held. But if the active key is
        // stuck (its key-up was eaten by a focus-stealing dialog, etc.), the state
        // would be permanently latched and would block every OTHER hotkey until the
        // stuck key happens to be pressed again. Detect "stuck" via two signals:
        //
        //  1. >2s since the last key-down event (well past any plausible auto-repeat).
        //  2. The key is NOT physically down right now (GetAsyncKeyState).
        //
        // The second check is essential because modifier hotkeys (RightAlt, modifiers
        // for AltGr layouts, etc.) often do NOT auto-repeat under Windows, so the
        // timestamp can be 5+ seconds old during a legitimate push-to-talk hold. Reset
        // ONLY when both elapsed-time AND physical-state agree the key is no longer held.
        if (_activeHotkeyCode != KeyCode.VcUndefined)
        {
            if (timestampMs - _lastKeyDownTimestamp < 2000)
                return true; // Other hotkey is genuinely held — suppress.
            if (KeyDownProbe(_activeHotkeyCode))
                return true; // Key is still physically down — modifier held for long PTT, suppress.
            Logger.Warning("Stale active hotkey ({Stale}, gap {Gap}ms, key not physically down) — resetting so {NewKey} can proceed",
                _activeHotkeyCode, timestampMs - _lastKeyDownTimestamp, keyCode);
            // Hook-only reset — preserve hands-free state across the recovery.
            ResetHookState();
            // Fall through to fresh key-down handling below
        }

        // Debounce (only for genuine new key-down, not repeats)
        if (timestampMs - _lastEventTimestamp < DebounceMs) return true;
        _lastEventTimestamp = timestampMs;

        // Snapshot-action pattern (round-3 Codex review): capture the action and
        // mutate _isHandsFreeMode on the hook thread BEFORE queuing the lambda.
        // The dispatched lambda then consumes the captured action — never reads live
        // state — eliminating the race where a subsequent fast tap mutates state
        // before the previous key-down's lambda has run. Publication + decision run
        // under _gestureLock so they can't interleave with a pipeline-transition
        // InvalidateRecordingGesture.
        KeyDownAction action;
        int capturedEpoch;
        lock (_gestureLock)
        {
            _activeHotkeyCode = keyCode;
            _activeKeyDownTimestamp = timestampMs;
            _lastKeyDownTimestamp = timestampMs;

            var wasHandsFreeAtKeyDown = _isHandsFreeMode;
            action = wasHandsFreeAtKeyDown ? KeyDownAction.StopHandsFree : KeyDownAction.Start;
            if (wasHandsFreeAtKeyDown)
            {
                _isHandsFreeMode = false;
                WasPushToTalk = false;
            }
            _keyDownStartedRecording = !wasHandsFreeAtKeyDown;
            capturedEpoch = _gestureEpoch; // decision stamped for the delivery fence
        }
        _lastDispatchedActionBox = action; // test-observable

        var capturedAction = action;
        // Gated on isPromptHotkey, NOT taken straight from the map lookup (Grok diff review r1).
        // promptId is keyed on the TRIGGER alone, and since HKY-3 a prompt may legitimately sit on
        // a trigger a COMBO role also uses ("prompt F6" beside "recording Ctrl+F6"). Capturing it
        // unconditionally would make Ctrl+F6 start a recording WITH that prompt applied instead of
        // a plain one — the two chords would stop being two actions. Before HKY-3 this was safe
        // only because a role always shadowed the prompt out of the map entirely.
        var capturedPromptId = isPromptHotkey ? promptId : null;

        // Latency probe: stamp the hook-thread dispatch instant for START
        // actions only (a StopHandsFree dispatch must never arm the
        // hotkey→pill measurement). Consumed once on the UI thread by
        // MainViewModel.ToggleRecordAsync.
        long startStamp = 0;
        if (action == KeyDownAction.Start)
        {
            startStamp = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastStartDispatchTimestamp, startStamp);
        }

        // Dispatch state logic to UI thread — unified for main and prompt hotkeys.
        // Only logging + side-effects here; state mutation already happened above.
        var enqueued = TryDispatch(() =>
        {
            // Delivery fence: the decision above was made against gesture state that an
            // InvalidateRecordingGesture may have killed while this lambda sat in the
            // queue — acting on it would start a phantom recording (press during
            // Transcribing whose delivery slips past the →Idle sweep).
            if (!IsQueuedActionCurrent(capturedEpoch))
            {
                // Retract OUR latency stamp too (CompareExchange — a newer press's fresh
                // stamp survives): a discarded Start must not leave a stamp a tray/Home
                // start could consume and misattribute as hotkey-triggered.
                if (startStamp != 0)
                    Interlocked.CompareExchange(ref _lastStartDispatchTimestamp, 0, startStamp);
                Logger.Information("Stale hotkey dispatch discarded — gesture invalidated between decision and delivery");
                return;
            }

            if (capturedAction == KeyDownAction.StopHandsFree)
            {
                Logger.Information("Hands-free mode OFF — stopping recording");
                ToggleRecordingRequested?.Invoke();
            }
            else
            {
                if (capturedPromptId != null)
                    PromptHotkeyPressed?.Invoke(capturedPromptId);
                Logger.Information(capturedPromptId != null
                    ? "Prompt hotkey pressed — starting recording"
                    : "Hotkey pressed — starting recording");
                ToggleRecordingRequested?.Invoke();
            }
        });

        // A dropped enqueue means the start will never run — retract OUR stamp
        // (CompareExchange so a faster subsequent press's fresh stamp survives).
        // Null dispatcher (unit tests drive HandleKeyDown directly) keeps the
        // stamp: tests consume it explicitly, and production always has a queue.
        if (startStamp != 0 && enqueued == false)
            Interlocked.CompareExchange(ref _lastStartDispatchTimestamp, 0, startStamp);

        return true; // Suppress the key
    }

    /// <summary>
    /// Process a key-up event. Internal for testing.
    /// Returns true if the event should be suppressed.
    /// </summary>
    internal bool HandleKeyUp(KeyCode keyCode, long timestampMs)
    {
        // ── Gesture ownership FIRST (F4, 2026-07-14): the release of the key that owns
        // the in-flight gesture COMPLETES it, regardless of what the key is currently
        // bound to — a mid-hold reconfiguration (ReloadHotkeys / RegisterPromptHotkeys)
        // must never swallow the stop release (the recording would run on and the
        // tap/hold machine would invert on the next press). This deliberately BYPASSES
        // the event debounce: a lost completion latches the machine, and after
        // completion the active code clears, so duplicate chatter ups fall into the
        // inert paths below. It also precedes the dedicated-action latch blocks — a
        // held key rebound to paste-last/redo-last/generate-image mid-gesture must
        // finish the gesture, not fire the new action's latch path.
        bool ownsGesture;
        long pressDurationMs = 0;
        var startedRecording = false;
        var enterHandsFreeMode = false;
        var isLongPressStop = false;
        var capturedEpoch = 0;
        lock (_gestureLock)
        {
            ownsGesture = _activeHotkeyCode != KeyCode.VcUndefined && keyCode == _activeHotkeyCode;
            if (ownsGesture)
            {
                _lastEventTimestamp = timestampMs;

                _activeHotkeyCode = KeyCode.VcUndefined;

                pressDurationMs = timestampMs - _activeKeyDownTimestamp;
                _activeKeyDownTimestamp = 0;

                // Capture on hook thread — guaranteed to reflect what HandleKeyDown set on
                // the hook thread, regardless of whether the UI-thread lambda from
                // HandleKeyDown has run yet. Under _gestureLock, a pipeline-transition
                // InvalidateRecordingGesture is either fully before this block (the release
                // then classifies inert — startedRecording is false) or fully after it (the
                // latch this block may set is cleared by the reset) — a stale latch can no
                // longer outlive the recording.
                startedRecording = _keyDownStartedRecording;

                // Snapshot-action pattern: classify the release on the hook thread and
                // mutate _isHandsFreeMode immediately. The dispatched lambda only logs
                // and fires events based on captured snapshots.
                enterHandsFreeMode = startedRecording && pressDurationMs < TapThresholdMs;
                isLongPressStop = startedRecording && pressDurationMs >= TapThresholdMs;
                if (enterHandsFreeMode)
                {
                    _isHandsFreeMode = true;
                    WasPushToTalk = false;
                }
                else if (isLongPressStop)
                {
                    WasPushToTalk = true;
                }
                capturedEpoch = _gestureEpoch; // decision stamped for the delivery fence
            }
        }
        if (ownsGesture)
        {
            var capturedStartedRecording = startedRecording;
            var capturedDuration = pressDurationMs;
            var capturedEnterHandsFree = enterHandsFreeMode;
            var capturedIsLongPressStop = isLongPressStop;

            TryDispatch(() =>
            {
                // Delivery fence — see HandleKeyDown's twin: a PTT-stop toggle delivered
                // after an invalidation would toggle an Idle pipeline into a phantom start.
                if (!IsQueuedActionCurrent(capturedEpoch))
                {
                    Logger.Information("Stale hotkey dispatch discarded — gesture invalidated between decision and delivery");
                    return;
                }

                // Stop action (second tap ending hands-free, or release of a hotkey
                // whose down was a Stop) — no logging, no side effect.
                if (!capturedStartedRecording) return;

                if (capturedEnterHandsFree)
                {
                    Logger.Information("Brief press ({Duration}ms) — entering hands-free mode", capturedDuration);
                }
                else if (capturedIsLongPressStop)
                {
                    Logger.Information("Long press ({Duration}ms) — push-to-talk stop", capturedDuration);
                    ToggleRecordingRequested?.Invoke();
                }
            });

            return true; // Suppress — the press that started the gesture was suppressed too
        }

        var config = _config;

        // Dedicated-action key ups. Ownership is KEY-based (F4 diff review R1): the
        // HELD key's release clears its action even if the binding moved mid-hold, and
        // the currently-BOUND key's up keeps the pre-F4 pass-through semantics. Let
        // through during paste, suppress otherwise — matching the suppressed press.
        //
        // HKY-3: the idle-release clause is restricted to SINGLE-KEY bindings, and this is a
        // correctness requirement rather than a tidy-up. Key-up must never re-probe modifiers
        // (they are routinely released before the trigger), so the only safe rule is "suppress
        // the up exactly when the down was suppressed". For a combo whose modifiers were NOT
        // held, the down passed through to the app — suppressing its up on a trigger match
        // alone would deliver a key-down with no key-up and leave the key stuck in the target.
        // The held-latch clause still covers every combo press that actually fired.
        if (keyCode == _pasteLastHeldKey || IsIdleSingleKeyRelease(config.PasteLast, keyCode))
        {
            if (keyCode == _pasteLastHeldKey) _pasteLastHeldKey = KeyCode.VcUndefined;
            return !SuppressPromptActions;
        }

        // Redo-last key up
        if (keyCode == _redoLastHeldKey || IsIdleSingleKeyRelease(config.RedoLast, keyCode))
        {
            if (keyCode == _redoLastHeldKey) _redoLastHeldKey = KeyCode.VcUndefined;
            return !SuppressPromptActions;
        }

        // Generate-image key up (IMG-1)
        if (keyCode == _generateImageHeldKey || IsIdleSingleKeyRelease(config.GenerateImage, keyCode))
        {
            if (keyCode == _generateImageHeldKey) _generateImageHeldKey = KeyCode.VcUndefined;
            return !SuppressPromptActions;
        }

        // Paste-window pass-through for modifier KEYUPs (after the paste-last/redo
        // guard resets above, before any main-hotkey state processing). During a
        // paste (SuppressPromptActions) with no press in flight, the paste path may
        // inject side-specific modifier KEYUPs to clear a physically-held modifier
        // before Ctrl+V. Suppressing those here would block the OS keyboard-state
        // update and defeat the release — and when the user's recording hotkey IS a
        // modifier (RightAlt default), the idle main-hotkey branch below would eat
        // exactly that event. Keyups are inert for the foreground app, so letting
        // user-originated modifier keyups through during the ~1 s paste window is
        // harmless too.
        if (SuppressPromptActions
            && _activeHotkeyCode == KeyCode.VcUndefined
            && IsModifierKeyCode(keyCode))
        {
            return false;
        }

        // Determine if this is a recording hotkey (main or prompt). Single-key only, for the
        // reason given on the dedicated-action branches above: a combo's idle release must
        // pass through, because its press did.
        bool isMainHotkey = config.Recording.IsSingleKey && keyCode == config.Recording.Trigger;
        bool isPromptHotkey = !isMainHotkey && config.Prompts.ContainsKey(keyCode);

        if (!isMainHotkey && !isPromptHotkey) return false;
        if (isPromptHotkey && SuppressPromptActions) return false;

        // No gesture owns this key (the owning-key release completed at the top of
        // this method). A bound recording key's idle release is suppressed, matching
        // its (suppressed) press.
        return true;
    }

    /// <summary>
    /// True for the idle release of a bound SINGLE-KEY binding — the pre-HKY-3 pass-through
    /// semantics, deliberately not extended to combos (see the call sites' comment).
    /// </summary>
    private static bool IsIdleSingleKeyRelease(HotkeyBinding? binding, KeyCode keyCode)
        => binding is { IsSingleKey: true } value && value.Trigger == keyCode;

    /// <summary>
    /// The eight side-specific modifier keys the paste path's held-modifier guard
    /// may inject KEYUPs for. Kept in sync with
    /// <c>ModifierReleasePlan.SideSpecificModifierVks</c> (VK-level twin).
    /// </summary>
    internal static bool IsModifierKeyCode(KeyCode keyCode) => keyCode
        is KeyCode.VcLeftControl or KeyCode.VcRightControl
        or KeyCode.VcLeftAlt or KeyCode.VcRightAlt
        or KeyCode.VcLeftShift or KeyCode.VcRightShift
        or KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

    /// <summary>Inverse of <see cref="MapKeyCode"/> for the six assignable modifier keys;
    /// null for anything else (function/extra keys are not phantom-prone).</summary>
    private static string? ModifierKeyCodeToName(KeyCode keyCode) => keyCode switch
    {
        KeyCode.VcRightAlt => "RightAlt",
        KeyCode.VcLeftAlt => "LeftAlt",
        KeyCode.VcLeftControl => "LeftControl",
        KeyCode.VcRightControl => "RightControl",
        KeyCode.VcRightShift => "RightShift",
        KeyCode.VcLeftShift => "LeftShift",
        _ => null
    };

    /// <summary>
    /// VKs the paste pipeline may force-release when physically reported down at
    /// send time: the modifiers currently serving as VoiceWink hotkeys — main,
    /// paste-last, redo-last, AND every registered prompt hotkey. All of these
    /// flow through this hook's suppression, which is what makes their key state
    /// phantom-prone. Reads volatile snapshots only — safe from any thread.
    /// </summary>
    /// <remarks>
    /// <b>HKY-3 adds nothing here, by construction.</b> A binding is phantom-prone only when a
    /// MODIFIER key's own events are suppressed, and that happens only when the modifier is the
    /// TRIGGER. A combo's trigger is never a modifier key
    /// (<see cref="HotkeyBinding.Validate"/>), and a combo's modifier keys are never suppressed
    /// — neither down (we cannot know the combo will complete) nor up (see
    /// <see cref="HandleKeyUp"/>). So combos leave no phantom modifier state, and
    /// <see cref="ModifierKeyCodeToName"/> correctly returns null for every one of their
    /// triggers.
    /// </remarks>
    internal ushort[] GetPhantomProneModifierVks()
    {
        var config = _config;
        var names = new List<string?>
        {
            ModifierKeyCodeToName(config.Recording.Trigger),
            config.PasteLast.HasValue ? ModifierKeyCodeToName(config.PasteLast.Value.Trigger) : null,
            config.RedoLast.HasValue ? ModifierKeyCodeToName(config.RedoLast.Value.Trigger) : null,
            config.GenerateImage.HasValue ? ModifierKeyCodeToName(config.GenerateImage.Value.Trigger) : null,
        };
        foreach (var keyCode in config.Prompts.Keys)
            names.Add(ModifierKeyCodeToName(keyCode));
        return ModifierReleasePlan.PhantomProneVks(names);
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        EnsureHookThreadPriorityBoosted();
        _lastHookEventUtc = DateTime.UtcNow;
        try
        {
            var timestampMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (HandleKeyDown(e.Data.KeyCode, timestampMs))
                e.SuppressEvent = true;
        }
        catch (Exception ex)
        {
            // Never let exceptions propagate into SharpHook's event loop — that kills the hook thread.
            Logger.Error(ex, "Unhandled exception in OnKeyPressed for {Key}", e.Data.KeyCode);
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        EnsureHookThreadPriorityBoosted();
        _lastHookEventUtc = DateTime.UtcNow;
        try
        {
            var timestampMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (HandleKeyUp(e.Data.KeyCode, timestampMs))
                e.SuppressEvent = true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unhandled exception in OnKeyReleased for {Key}", e.Data.KeyCode);
        }
    }

    private void OnMouseEvent(object? sender, MouseHookEventArgs e)
    {
        EnsureHookThreadPriorityBoosted();
        // Keep this allocation-free: it runs on the hook thread for global mouse events.
        MarkMouseEvent(NativeInterop.GetTickCount());
    }

    private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
    {
        EnsureHookThreadPriorityBoosted();
        // Keep this allocation-free: it runs on the hook thread for high-rate wheel input.
        MarkMouseEvent(NativeInterop.GetTickCount());
    }

    /// <summary>
    /// Managed thread id of the hook callback thread we most recently boosted,
    /// or <c>0</c> if no boost has been attempted yet. SharpHook's libuiohook
    /// runs callbacks on a single dedicated thread, but the watchdog can dispose
    /// the hook and start a new one (see <see cref="StartHookAsync"/>) — that
    /// spawns a fresh thread. The pure decision lives in
    /// <see cref="HookThreadBoostGate.ShouldBoost"/>; this field is the gate's
    /// "what was the last boosted thread id" state.
    /// </summary>
    internal int _boostedHookThreadId;

    /// <summary>
    /// Raises the SharpHook callback thread's priority to <c>AboveNormal</c> on
    /// the first event after each hook start (initial start + every watchdog
    /// restart). The LL keyboard hook callback has a ~300ms timeout (Windows
    /// LowLevelHooksTimeout) before Windows silently drops the hook. Under
    /// heavy CPU contention (Excel auto-calc was the original trigger) a
    /// Normal-priority hook thread can miss that deadline; AboveNormal lets
    /// it preempt normal-priority CPU hogs.
    /// </summary>
    private void EnsureHookThreadPriorityBoosted()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        // Fast path: same thread we already boosted. Delegated to a pure helper
        // for unit testability — the gate logic itself doesn't need a hook running.
        if (!HookThreadBoostGate.ShouldBoost(_boostedHookThreadId, currentThreadId))
            return;

        bool ok;
        int err = 0;
        try
        {
            var handle = NativeInterop.GetCurrentThread();
            ok = NativeInterop.SetThreadPriority(handle, NativeInterop.THREAD_PRIORITY_ABOVE_NORMAL);
            if (!ok)
                err = Marshal.GetLastWin32Error();
        }
        catch (Exception ex)
        {
            // Off-load the failure log to the thread pool too — keep the hook
            // callback path allocation- and I/O-free.
            Task.Run(() => Logger.Warning(ex, "Failed to raise hook callback thread priority"));
            _boostedHookThreadId = currentThreadId; // don't retry every event
            return;
        }

        // Record the boost result before logging so concurrent callbacks on the
        // same thread short-circuit immediately.
        _boostedHookThreadId = currentThreadId;

        // Codex 2026-05-23: don't log synchronously from the hook callback —
        // the Serilog file sink is synchronous and disk I/O on this thread
        // eats into LowLevelHooksTimeout's ~300ms budget. Hand off to the
        // thread pool. (The priority change itself is the cheap part.)
        if (ok)
        {
            Task.Run(() => Logger.Information(
                "Hook callback thread priority raised to AboveNormal on thread {Tid} (LowLevelHooksTimeout mitigation)",
                currentThreadId));
        }
        else
        {
            var capturedErr = err;
            Task.Run(() => Logger.Warning(
                "SetThreadPriority returned false on hook thread {Tid}; LastError={Err}",
                currentThreadId, capturedErr));
        }
    }

    /// <summary>
    /// The recording binding, which must always resolve to something: an unparseable setting
    /// falls back to RightAlt, preserving <c>MapKeyCodeOrDefault</c>'s pre-HKY-3 contract that
    /// the recording hotkey never ends up unbound.
    /// </summary>
    private HotkeyBinding ResolveRecordingBinding()
    {
        var stored = _settings.GetString(AppDefaults.HotkeyModifier, "RightAlt");
        if (HotkeyBinding.TryParse(stored, out var binding, out var error)) return binding;

        Logger.Warning("Recording hotkey {Stored} is not a valid binding ({Error}) — falling back to RightAlt",
            stored, error);
        return new HotkeyBinding(HotkeyModifiers.None, KeyCode.VcRightAlt);
    }

    /// <summary>
    /// An optional role binding. Empty means the user disabled it; anything unparseable is
    /// treated as disabled and logged. Neither case is ever written back to settings — a value
    /// this build cannot read may be one a later build can, and silently rewriting it would
    /// destroy the user's choice.
    /// </summary>
    private HotkeyBinding? ResolveOptionalBinding(string settingsKey, string role)
    {
        var stored = _settings.GetString(settingsKey, "");
        if (string.IsNullOrWhiteSpace(stored)) return null;
        if (HotkeyBinding.TryParse(stored, out var binding, out var error)) return binding;

        Logger.Warning("{Role} hotkey {Stored} is not a valid binding ({Error}) — treated as disabled",
            role, stored, error);
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_watchdogTimer != null)
        {
            _watchdogTimer.Stop();
            _watchdogTimer.Tick -= OnWatchdogTick;
            _watchdogTimer = null;
        }
        if (_hook != null)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _hook.MousePressed -= OnMouseEvent;
            _hook.MouseReleased -= OnMouseEvent;
            _hook.MouseMoved -= OnMouseEvent;
            _hook.MouseDragged -= OnMouseEvent;
            _hook.MouseWheel -= OnMouseWheel;
            _hook.Dispose();
            _hook = null;
            Logger.Information("Global hotkey hook disposed");
        }
        // Don't dispose _restartLock — App.OnPowerModeChanged.Resume can call
        // RestartAsync after Dispose (e.g. a wake-from-sleep mid-shutdown). The
        // _disposed guard inside RestartHookAsync makes the call a no-op in that
        // case, but only if the semaphore is still valid.
    }
}
