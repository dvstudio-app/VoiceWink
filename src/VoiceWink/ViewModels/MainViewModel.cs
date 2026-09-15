using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Models.Enums;
using VoiceWink.Services.Audio;
using VoiceWink.Services.System;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.Data;
using VoiceWink.Services.Input;
using VoiceWink.Services.Licensing;
using VoiceWink.Services.AppMode;
using VoiceWink.Services.TextProcessing;
using VoiceWink.Services.Transcription;

namespace VoiceWink.ViewModels;

/// <summary>
/// Main recording state machine.
/// States: Idle → Starting → Recording → Transcribing → Done → Idle.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<MainViewModel>();

    private readonly AudioRecorderService _recorder;
    private readonly RecordingDeviceSelectionService _deviceSelection;
    private readonly Services.Audio.StandingCaptureService _standingCapture;
    private readonly Services.Transcription.IParakeetPcppBackend? _pcppBackend;
    // The local-runtime seam, not a concrete engine: MainViewModel must not know which stack
    // serves the selected model (Parakeet 1/3). Replaced WhisperTranscriptionService one-for-one.
    private readonly LocalModelPreparer _localModels;
    private readonly TranscriptionServiceRegistry _transcriptionRegistry;
    private readonly ModelDownloadManager _modelDownloader;
    private readonly ClipboardService _clipboard;
    private readonly SettingsService _settings;
    private readonly SoundFeedbackService _soundFeedback;
    private readonly SystemAudioMuteService _systemMute;
    private readonly TextPipelineRunner _textPipeline;
    private readonly TranscriptionHistoryService _historyService;
    private readonly TranscriptionHistoryWriter _historyWriter;
    private readonly Services.Data.ReferencePersistence _referencePersistence;
    private readonly LifetimeMetricsService _lifetimeMetrics;
    private readonly AIEnhancementService _enhancement;
    private readonly CustomVocabularyService _customVocabulary;
    private readonly PromptDetectionService _promptDetection;
    private readonly HotkeyService _hotkeyService;
    private readonly AppModeManager _appModeManager;
    private readonly RedoCoordinator _redoCoordinator;
    private readonly RetainedWavLedger _wavLedger;
    private readonly LicenseService _license;
    private readonly Services.AIEnhancement.ImageGenerationJobService _imageJob;
    private readonly VoiceActivityDetectionService _voiceActivityDetection;
    private readonly Services.Audio.RecordingAudioPreparation _audioPreparation;

    // C3 (2026-07-14): single-flight guard for the stop→transcribe pipeline. Three of the
    // four recording-stop call sites (hotkey / tray / HomePage) call ToggleRecordAsync
    // DIRECTLY, bypassing the RelayCommand's built-in no-concurrent-execution guard — so two
    // overlapping stops could both enter StopAndTranscribeAsync and the second disposed the
    // first's live CancellationTokenSource (ObjectDisposedException) and deleted the WAV the
    // first retained for retry. This admits exactly one stop body at a time.
    private readonly Helpers.SingleFlight _stopSingleFlight = new();

    [ObservableProperty]
    private RecordingState _recordingState = RecordingState.Idle;

    // Thread-safe mirror of "is the recording pipeline busy?" for off-thread maintenance-gate reads.
    // IMaintenanceGate.Check() may run off the UI thread (e.g. from the update-apply path), and the
    // generated RecordingState property is backed by a non-volatile field written on the UI thread.
    // This volatile bool gives RecorderMaintenanceSource a tear-free read. Updated by the
    // OnRecordingStateChanged partial that the [ObservableProperty] generator invokes on every change.
    private volatile bool _recordingPipelineBusy;

    /// <summary>Thread-safe: true while the recording pipeline is active (RecordingState != Idle).</summary>
    public bool IsRecordingPipelineBusy => _recordingPipelineBusy;

    partial void OnRecordingStateChanged(RecordingState value)
    {
        _recordingPipelineBusy = value != RecordingState.Idle;

        // IMG-BG: a pending background-image completion presents only when the pipeline is
        // idle. The apply runs on a QUEUED dispatcher turn, never inline here — the pipeline
        // sets Idle BEFORE its own terminal presentation (e.g. StopAndTranscribeAsync arms its
        // redo after the Idle write), so an inline apply would present and be immediately
        // clobbered (Codex plan round 2). The queued apply re-checks Idle + the presentation
        // epoch on its own turn.
        if (value == RecordingState.Idle && _pendingImageCompletion != null)
            EnqueueUiTurn(ApplyPendingImageCompletionIfCurrent);
    }

    // The generator invokes BOTH partials on every change; this overload carries the old
    // value, which the gesture sync needs (transitions, not states). The hotkey machine's
    // hands-free latch may only survive while a recording is live — a latch with nothing
    // recording permanently inverts the tap/hold machine (2026-07-17 PTT bug: a pill-stop
    // of a hands-free recording never told HotkeyService). Rule in
    // RecordingGestureSync.ShouldInvalidate; every stop/abort path funnels through this
    // property, so per-call-site resets can no longer drift.
    partial void OnRecordingStateChanged(RecordingState oldValue, RecordingState newValue)
        => RecordingGestureSync.OnTransition(oldValue, newValue,
            () => _hotkeyService.InvalidateRecordingGesture("recording pipeline transition"));

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _lastTranscription = "";

    [ObservableProperty]
    private bool _isModelLoaded;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private bool _isMiniRecorderVisible;

    [ObservableProperty]
    private bool _isDownloadingModel;

    [ObservableProperty]
    private string _downloadModelName = "";

    [ObservableProperty]
    private bool _isRedoAvailable;

    /// <summary>
    /// Whether a failed transcription's retry affordance is armed (REL-12): a retained
    /// WAV + <see cref="TranscriptionRetryContext"/> exist on the coordinator. Mirrors
    /// <see cref="IsRedoAvailable"/>'s role for App.xaml.cs's PropertyChanged-driven
    /// pill rendering. Flipped LAST in <see cref="ArmRetry"/> (after StatusText /
    /// RetryStatusText / ArmedAffordance) so the availability-triggered snapshot is
    /// always internally consistent.
    /// </summary>
    [ObservableProperty]
    private bool _isRetryAvailable;

    /// <summary>
    /// Friendly name of the paste-target app captured at recording start / redo
    /// capture (PILL-1) — the MiniRecorder renders it as "→ Name" while a recording
    /// pipeline is visible, so the user can SEE where the paste will go (focus
    /// follows clicks, not eyes). Null = unresolved/none (label hidden). Published
    /// from a worker thread — UI consumers marshal. UI display only; never log it.
    /// </summary>
    [ObservableProperty]
    private string? _pasteTargetAppName;

    /// <summary>
    /// Whether the UIA element captured at recording start LOOKED editable (the
    /// paste gate's rescue predicate — Edit, or TextPattern + not-read-only +
    /// focusable). Null = unknown/no claim (capture pending, failed, or probe
    /// inconclusive). OBSERVABILITY ONLY since 2026-07-09 — no UI consumes it:
    /// the PILL-2 label tint it drove was retired (colors over-promised; the
    /// probe under-reports custom-UIA editors like PowerPoint and OOP-WebView2
    /// inputs like Copilot, and even a confirmed capture can't promise the paste
    /// — PST-4 tab switch). Kept for the diagnostic log line in
    /// <see cref="ResolvePasteTargetEditability"/>.
    /// Published from a worker thread.
    /// </summary>
    [ObservableProperty]
    private bool? _pasteTargetEditableConfirmed;

    // PILL-1 stale-publish fence: bumped at every label reset. A resolver captures the
    // generation at start and must see it UNCHANGED to publish. IsSameCapture alone
    // cannot cover the zero-target History redo — that path clears the label but
    // deliberately leaves _pasteTargetSnapshot in place (the paste gates still use it),
    // so a late resolver from the prior capture would otherwise republish into the
    // targetless redo pill.
    private int _targetLabelGeneration;

    // Serializes reset against resolver publication: a bare generation CHECK followed
    // by an ASSIGNMENT lets a reset slip between them and the stale name republish.
    // PropertyChanged fires inside the lock — safe: the only subscribers marshal via
    // DispatcherQueue.TryEnqueue (non-blocking).
    private readonly object _targetLabelLock = new();

    private void ResetPasteTargetAppName()
    {
        lock (_targetLabelLock)
        {
            _targetLabelGeneration++;
            PasteTargetAppName = null;
            PasteTargetEditableConfirmed = null;
        }
    }

    public DateTime? RecordingStartedAtUtc { get; private set; }

    /// <summary>
    /// Presentation tone of the armed redo pill: Success (green — everything worked),
    /// Warning (amber — the paste was declined but the content is safely on the clipboard),
    /// Error (red — the attempt actually failed). Set BEFORE <see cref="IsRedoAvailable"/> in
    /// <see cref="ArmRedo"/> so App.xaml.cs's IsRedoAvailable-triggered snapshot always reads
    /// a fresh tone; also raises its own PropertyChanged (App observes both — belt and braces
    /// per the Codex final check).
    /// </summary>
    public MiniRecorderTone RedoTone
    {
        get => _redoTone;
        private set => SetProperty(ref _redoTone, value);
    }
    private MiniRecorderTone _redoTone;

    /// <summary>
    /// Which affordance (redo/retry) armed MOST RECENTLY — the recency selector for
    /// both the pill (App reads it on each <c>AffordanceArmed</c> event to publish the winner to
    /// <c>MiniRecorderPresentationController</c>) and the redo-last hotkey routing when BOTH are
    /// armed (a History-page redo can arm redo while a failed dictation's retry stays armed; a fixed
    /// precedence is wrong in one direction or the other — Codex round-2/3). Plain property (no
    /// PropertyChanged): App reads it inside the AffordanceArmed handler.
    /// Written FIRST in each Arm method so an interior apply (affordance set,
    /// availability not yet flipped) falls through to the benign VisibleState/Hidden
    /// branches instead of rendering a half-armed pill.
    /// </summary>
    public ArmedAffordance ArmedAffordance { get; private set; }

    /// <summary>
    /// The redo pill's OWN status text, captured at <see cref="ArmRedo"/> time. The
    /// armed-affordance pill kinds must never render the shared <see cref="StatusText"/> —
    /// an older armed affordance re-surfacing after the newer one clears would wear
    /// whatever unrelated text was written since (Codex round-3). Plain property,
    /// written before the availability flip.
    /// </summary>
    public string? RedoStatusText { get; private set; }

    /// <summary>Retry counterpart of <see cref="RedoStatusText"/> — the error message
    /// captured at <see cref="ArmRetry"/> time.</summary>
    public string? RetryStatusText { get; private set; }

    /// <summary>Tone of the retry pill (PRM-4): Error for real failures (REL-12
    /// default), Warning for the echo-block retain — "possible echo" is a suspicion,
    /// not a failure, and must render amber (owner UAT 2026-07-18). Written BEFORE
    /// the <see cref="IsRetryAvailable"/> flip, like <see cref="RedoTone"/>.</summary>
    public MiniRecorderTone RetryTone { get; private set; } = MiniRecorderTone.Error;

    // Redo state (_lastRedoContext + dismiss timer) lives on RedoCoordinator now.
    // IsRedoAvailable and RedoTone stay here because WinUI binding paths
    // (App.xaml.cs) subscribe to this VM's PropertyChanged.

    private string? _currentRecordingPath;
    private CancellationTokenSource? _transcriptionCts;
    /// <summary>
    /// Live only while the main pipeline's TEXT enhancement call is in flight
    /// (published/cleared around the EnhanceAsync await, UI-thread only). Linked to
    /// the pipeline ct; the stop button cancels THIS instead of the pipeline so a
    /// "skip" pastes the raw transcript with history + redo intact (ENH-3).
    /// </summary>
    private CancellationTokenSource? _enhancementSkipCts;
    private CancellationTokenSource? _modelDownloadCts;

    /// <summary>
    /// Reentrancy gate for the enhancement/model-picker ContentDialog (0 = closed,
    /// 1 = open/opening). WinUI allows one ContentDialog per XamlRoot; acquired BEFORE
    /// <see cref="RequestModelPickerWithContext"/> mutates redo/focus/target state so a
    /// duplicate retry click is a true no-op (2026-07-10 incident: the second click's
    /// COMException path silently cleared the redo state the open dialog still owned).
    /// Shared with the AskImageSize options-picker site, and since TRN-17 with the
    /// transcription-RETRY picker — deliberately the same gate rather than a second one: a
    /// ContentDialog is one-per-XamlRoot, so two independent gates would let both open and throw.
    /// </summary>
    private int _enhancementPickerActive;
    private DispatcherTimer? _meterTimer;
    private IntPtr _targetWindowHandle;
    private DateTime _targetWindowCapturedAtUtc = DateTime.MinValue;
    /// <summary>
    /// Identity snapshot (hwnd/pid/tid + async-enriched class) of the paste target,
    /// captured with <see cref="_targetWindowHandle"/> at recording-start and at
    /// redo-capture. Consumed by the paste pipeline's liveness/identity + UIPI gates.
    /// Published/updated via Volatile/Interlocked — the class enrichment runs on a
    /// worker after the pill is visible (GetClassName can block under target
    /// contention and must never run on the recording-start hot path).
    /// </summary>
    private PasteTargetSnapshot? _pasteTargetSnapshot;
    private static readonly TimeSpan ImagePasteFreshnessWindow = TimeSpan.FromSeconds(10);
    /// <summary>
    /// UIA element that had keyboard focus at recording-start (or redo-time).
    /// Captured so we can restore focus to the same DOM element before
    /// SendCtrlV — Win32 SetForegroundWindow brings the OS window forward but
    /// cannot tell a Chromium-based renderer (Electron apps) which DOM
    /// editable to focus. The slot is the single owner
    /// (<see cref="ReleaseCapturedFocusedElement"/> is the only release path);
    /// ClipboardService borrows without releasing. Recording-start captures
    /// are PENDING (non-blocking — the pill must not wait on a cross-process
    /// UIA call) until a paste path materializes them via
    /// <see cref="CapturedFocusSlot.ResolveAsync"/>.
    /// </summary>
    private readonly CapturedFocusSlot _focusSlot = new();

    /// <summary>
    /// PST-11: the editable element the LAST recording pasted into, held across the redo picker so
    /// the redo's paste has a candidate that is actually a text box. Separate from
    /// <see cref="_focusSlot"/> deliberately — <c>CaptureForRedo</c> releases any non-Recording
    /// occupant, so the picker-open capture would destroy exactly what this holds.
    /// </summary>
    private readonly RedoFocusRetention _redoFocusRetention = new();

    /// <summary>
    /// UI-7: how many picker dialogs are ON SCREEN right now (0 or 1 — the pickers are serialised
    /// by <see cref="_enhancementPickerActive"/>).
    ///
    /// <para><b>Deliberately NOT <see cref="_enhancementPickerActive"/>, which is a different
    /// question.</b> That flag means "an enhancement operation owns the dialog slot" and is held
    /// until the whole handler task completes — for the redo picker that includes
    /// <c>RedoEnhancementAsync</c>, a multi-second cloud call that runs long after the dialog is
    /// gone (Codex verification round). Gating on it would suppress the paste for a dictation
    /// started while no dialog is visible at all, which is a silent downgrade to the clipboard for
    /// a user who did nothing wrong. This counts only the interval a dialog is actually up.</para>
    /// </summary>
    private int _pickerDialogVisible;

    /// <summary>UI-7: this recording started with a picker dialog on screen, so there is no honest
    /// paste target and the transcript is COPIED rather than pasted. Latched once at recording
    /// start — re-reading the gate at paste time would answer a different question (the dialog is
    /// usually gone by then) and reintroduce the defect.</summary>
    private bool _pasteSuppressedByPicker;

    private int? _lastTranscriptionHistoryId;

    /// <summary>
    /// App Mode config detected at recording start. Applied during transcription.
    /// Cleared after each recording completes (overrides are always transient).
    /// </summary>
    private AppModeConfig? _activeAppModeConfig;

    /// <summary>
    /// Background model preload task. Awaited in StartRecordingAsync to coalesce
    /// with the existing EnsureModelLoadedAsync call (which short-circuits when
    /// the same model is already loaded).
    /// </summary>
    private Task? _preloadTask;

    /// <summary>
    /// When set, the next transcription will use this prompt instead of the active one.
    /// Set by prompt-specific hotkeys; cleared after use.
    /// </summary>
    private CustomPrompt? _pendingPromptOverride;

    /// <summary>
    /// PRM-5: the speech-recognition language resolved ONCE at recording start — prompt override →
    /// App-Mode override → the global setting AS IT WAS THEN — and stashed so the local-model
    /// preload and the later <c>TranscribeAsync</c> ask for the SAME language. Concrete, never null:
    /// leaving "global" unresolved let a mid-recording Settings edit change this recording's
    /// language, and let a retry re-read Settings and recognise the same WAV differently than the
    /// attempt it replays. Set on every start (so a previous recording's value cannot leak) and
    /// consumed in <c>StopAndTranscribeAsync</c>; a retry uses its own captured value. UI-thread only.
    /// </summary>
    private string? _recordingLanguageOverride;

    /// <summary>
    /// The MODEL resolved once at recording start, stashed for exactly the reason
    /// <see cref="_recordingLanguageOverride"/> is: preparation and transcription must not each
    /// read <c>SelectedModelName</c> for themselves.
    ///
    /// <para>They used to. Recording start prepared the model it had captured locally, then
    /// <c>StopAndTranscribeAsync</c> called <c>GetService()</c> with no argument, which re-read
    /// Settings — two reads of mutable state across an await-heavy window. Changing the model
    /// mid-recording therefore prepared one model and transcribed with another. While every local
    /// model was Whisper that only loaded a different <c>.bin</c>; with a second runtime it becomes
    /// "prepared Parakeet, transcribed with whisper.cpp".</para>
    ///
    /// <para>Set on every start so a previous recording's value cannot leak; a retry captures and
    /// passes its own. UI-thread only.</para>
    /// </summary>
    private string? _recordingModelSnapshot;

    /// <summary>How the extracted recording preflight ended (AUD-6). <c>Superseded</c> carries
    /// the five supersession guards' semantics out of the extracted method: the COLD call site
    /// returns on it exactly where the old inline <c>return</c>s sat; the WARM background task
    /// treats it as a quiet no-op.</summary>
    private enum PreflightOutcome { Completed, Superseded }

    /// <summary>
    /// Serializes preflight PUBLICATION against recording-start RESET of the snapshot fields
    /// (Codex diff r2): WinUI 3 has no SynchronizationContext, so the warm preflight's
    /// continuations run on the THREAD POOL — its guard-then-write was racy against the UI
    /// thread's next-recording null-out/CTS-install. The preflight now computes into LOCALS and
    /// publishes once, under this lock, with an ownership + cancellation check; the start side
    /// resets under the same lock. A publish therefore lands entirely before a reset or is
    /// refused entirely after one — never interleaved.
    /// </summary>
    private readonly object _preflightPublishGate = new();

    /// <summary>
    /// AUD-6 warm path: the in-flight preflight (App-Mode detect → PRM-5 language → model
    /// snapshot → ensure-loaded) running CONCURRENTLY with the live recording, PLUS the startup
    /// CTS that owns it — carried together so a user cancel during the stop-side wait can cancel
    /// the preflight ITSELF (a multi-GB model download must not keep running headless after the
    /// stop aborted; Codex diff r1). Null on the cold path (which awaits the preflight inline,
    /// as it always has) and for retries. Consumed — awaited then cleared — by
    /// <c>StopAndTranscribeAsync</c> before it reads the snapshot fields; cleared at every
    /// recording start AND by <c>DiscardRecordingPreflight</c> on pre-transcription aborts, so a
    /// stale task can never be awaited by a later, unrelated stop. UI-thread only.
    /// </summary>
    private (Task<PreflightOutcome> Task, CancellationTokenSource StartupCts)? _recordingPreflight;

    /// <summary>
    /// AUD-6 (Codex diff r3): the in-flight WARM start. The state flips to Recording BEFORE the
    /// recorder owns the session (that ordering is the feature), so a stop that lands in any
    /// yield inside that stretch would stop "nothing", abort on a missing WAV, and leave the
    /// drain recording under Idle UI — a privacy leak. The stop path awaits this gate FIRST, so
    /// it always observes the settled truth (attached, or failed with the start's own catch
    /// owning cleanup). Assigned synchronously in the same UI turn as the flip; completed-task
    /// awaits are no-ops, so the common fully-synchronous warm start pays nothing. UI-thread only.
    /// </summary>
    private Task? _warmStartGate;

    /// <summary>
    /// Gate for dispatching the recording hotkey to <see cref="ToggleRecordAsync"/>.
    /// Starting is included so the user can cancel a model download via hotkey.
    /// Transcribing/Enhancing are included so the press is logged then ignored —
    /// pipeline cancellation goes through the MiniRecorder stop button.
    /// </summary>
    public bool CanProcessHotkeyAction => RecordingState is RecordingState.Idle or RecordingState.Recording or RecordingState.Starting or RecordingState.Transcribing or RecordingState.Enhancing;

    /// <summary>
    /// Set a prompt override for the next recording (called by prompt-specific hotkeys).
    /// </summary>
    public void SetPromptOverride(string promptId)
    {
        var prompt = _enhancement.GetPrompts().FirstOrDefault(p => p.Id == promptId);
        if (prompt != null)
        {
            _pendingPromptOverride = prompt;
            // {Prompt} (not {Title}) so LogRedactionEnricher redacts the user-authored title in Sentry (C8).
            Logger.Information("Prompt hotkey override set: userTerm={Prompt}", Helpers.LogValueSanitizer.SingleLine(prompt.Title));
        }
    }

    /// <summary>
    /// Clear any pending prompt override (called when AltGr is detected and the phantom
    /// LeftControl prompt action should be cancelled).
    /// </summary>
    public void ClearPromptOverride()
    {
        if (_pendingPromptOverride != null)
        {
            Logger.Information("Prompt override cleared (AltGr phantom key detected)");
            _pendingPromptOverride = null;
        }
    }

    /// <summary>
    /// Raised when recording ends due to error or cancel, so HotkeyService can reset its state
    /// (broad reset, including key ownership — see HotkeyService.ResetState). These call sites
    /// cover refusals/failures that never enter the pipeline; the routine stop-path guarantee
    /// is the RecordingState transition hook (OnRecordingStateChanged →
    /// <see cref="Helpers.RecordingGestureSync"/>), which narrowly invalidates the gesture.
    /// </summary>
    public event Action? ResetHotkeyState;

    /// <summary>
    /// Raised to show a message pill in the MiniRecorder. The tone selects BOTH the styling and the
    /// lifetime (owned by <c>MiniRecorderPresentationController</c>): Error (red — something
    /// actually failed) PERSISTS with no auto-dismiss (ERR-PERSIST — the user must always see a
    /// failure, dismissed only by the corner × or a newer presentation); Warning (amber — a normal
    /// condition needing user action, e.g. an auto-paste decline whose content is safely on the
    /// clipboard) keeps the timed attention lifetime
    /// (<see cref="MiniRecorderTimings.AttentionSeconds"/>). The third argument is what a click on
    /// the pill's body does (LNC-11): <see cref="PillMessageAction.None"/> for every message but the
    /// license gate's refusal, which opens the License page it names.
    /// </summary>
    public event Action<string, MiniRecorderTone, PillMessageAction>? ShowMiniRecorderError;

    /// <summary>
    /// LNC-11: raised at most once per session, by the recording gate, on the first press it refuses
    /// because the free trial ended (<see cref="TrialEndRedirect"/>). App restores the main window
    /// and lands on the License page — the moment the trial ends while the app is running is the
    /// one win-back moment the app owns, and a pill alone left the user to find the page.
    /// </summary>
    public event Action? OpenLicensePageRequested;

    private readonly TrialEndRedirect _trialEndRedirect = new();

    /// <summary>
    /// Raised to update model download progress in MiniRecorder.
    /// Args: (progressFraction 0-1, modelName).
    /// </summary>
    public event Action<double, string>? DownloadProgressUpdated;

    /// <summary>
    /// Raised on EVERY redo/retry arm (ArmRedo/ArmRetry), including a re-arm while the affordance is
    /// already available — e.g. a background image job completing and re-arming redo with a newer
    /// context/tone while a text redo was already armed. App reads <see cref="ArmedAffordance"/> +
    /// the matching text/tone and (re)publishes the controller's affordance with fresh content, tone,
    /// lifetime, and handle. A pure availability false→true transition would MISS this re-arm and
    /// leave the pill showing stale content that clicks through to the newer context (Codex r2 #1).
    /// </summary>
    public event Action? AffordanceArmed;

    /// <summary>
    /// Raised to request the model picker dialog. MiniRecorderWindow raises this via ViewModel
    /// so the dialog opens on the main window (which has XamlRoot).
    /// Args: (isImageGeneration, capturedContext). The dialog callback calls RedoEnhancementAsync.
    /// Context is passed as a snapshot to avoid timer races nulling the coordinator context.
    /// </summary>
    public event Func<bool, RedoContext, Task>? ShowModelPickerRequested;

    /// <summary>
    /// Raised when an image generation prompt has <c>AskImageSize</c> enabled. The handler shows
    /// the same rich dialog as the redo/regenerate flow — an editable input-text box pre-filled
    /// with the transcribed text, plus provider / model / aspect / size / quality — pre-seeded
    /// from the supplied <see cref="RedoContext"/>. Returns the user's selections, or null if the
    /// user cancels. The image dropdowns are filtered per the resolved provider+model.
    /// </summary>
    // The token is the PIPELINE's (F20): Stop during "Waiting for image options..." cancels
    // it, and the subscriber (App.ShowEnhancementOptionsDialogAsync) registers a Hide() on it
    // so the open dialog dismisses instead of the Stop click being a silent no-op.
    public event Func<RedoContext, CancellationToken, Task<EnhancementDialogSelection?>>? ShowImageOptionsPickerRequested;

    /// <summary>
    /// Raised when the user taps Retry on a failed transcription (TRN-17). The handler shows
    /// <c>TranscriptionRetryOptionsDialog</c> and RETURNS the choice — the shape of
    /// <see cref="ShowImageOptionsPickerRequested"/>, not of <see cref="ShowModelPickerRequested"/>:
    /// after the pick, <see cref="RetryTranscriptionAsync"/> must re-run REL-12's volatile guards
    /// and consume the retained WAV itself, and moving that into App would put the guard order in
    /// the hub.
    ///
    /// <para><b>No CancellationToken, deliberately.</b> The image picker takes one because a
    /// pipeline Stop tap can hit a dialog opened DURING a run and must be able to Hide() it. This
    /// dialog only ever opens at <see cref="RecordingState.Idle"/> with nothing in flight, so there
    /// is no cancel source to plumb.</para>
    /// </summary>
    public event Func<TranscriptionRetryPickerRequest, Task<TranscriptionRetrySelection?>>?
        ShowTranscriptionRetryPickerRequested;

    /// <summary>
    /// TRN-64: raised ONCE per session when a local Whisper decode was refused because the GPU
    /// failed — or never finished — its self-test in this process. The handler (App, hosted
    /// exactly like <see cref="ShowTranscriptionRetryPickerRequested"/>) shows the
    /// <c>RestartToApplyDialog</c> with the refusal's own body and RETURNS whether it was shown;
    /// false releases the one-shot so a later refusal may offer again (no XamlRoot, a throw).
    /// Raised AFTER the failure surface has armed the retry pill — the offer is an addition to
    /// the recovery, never a replacement for it.
    /// </summary>
    public event Func<Helpers.GpuSelfTestRefusedException, Task<bool>>? ShowGpuSelfTestRestartRequested;

    private readonly Helpers.GpuSelfTestRestartOffer _gpuSelfTestRestartOffer = new();

    public MainViewModel(
        AudioRecorderService recorder,
        RecordingDeviceSelectionService deviceSelection,
        LocalModelPreparer localModels,
        ModelDownloadManager modelDownloader,
        ClipboardService clipboard,
        SettingsService settings,
        SoundFeedbackService soundFeedback,
        SystemAudioMuteService systemMute,
        TextPipelineRunner textPipeline,
        TranscriptionHistoryService historyService,
        TranscriptionHistoryWriter historyWriter,
        Services.Data.ReferencePersistence referencePersistence,
        LifetimeMetricsService lifetimeMetrics,
        TranscriptionServiceRegistry transcriptionRegistry,
        AIEnhancementService enhancement,
        CustomVocabularyService customVocabulary,
        PromptDetectionService promptDetection,
        HotkeyService hotkeyService,
        AppModeManager appModeManager,
        RedoCoordinator redoCoordinator,
        RetainedWavLedger wavLedger,
        LicenseService license,
        Services.AIEnhancement.ImageGenerationJobService imageJob,
        VoiceActivityDetectionService voiceActivityDetection,
        Services.Audio.RecordingAudioPreparation audioPreparation,
        Services.Audio.StandingCaptureService standingCapture,
        // TRN-29 auto-cleanup proof: the ONLY place "the engine's text survived the machine-owned
        // pipeline" is decidable is after that pipeline, which lives here — so the ViewModel
        // carries the proof signal (criterion = engine health, owner decision 2026-08-24; see the
        // notify site). Optional with a null default (the ModelManagementViewModel pattern):
        // null in a kill-switch rebuild, the real coordinator in ordinary builds.
        Services.Transcription.IParakeetPcppBackend? pcppBackend = null)
    {
        _recorder = recorder;
        _deviceSelection = deviceSelection;
        _standingCapture = standingCapture;
        _pcppBackend = pcppBackend;
        _localModels = localModels;
        _modelDownloader = modelDownloader;
        _clipboard = clipboard;
        _settings = settings;
        _soundFeedback = soundFeedback;
        _systemMute = systemMute;
        _textPipeline = textPipeline;
        _historyService = historyService;
        _historyWriter = historyWriter;
        _referencePersistence = referencePersistence;
        _lifetimeMetrics = lifetimeMetrics;
        _transcriptionRegistry = transcriptionRegistry;
        _enhancement = enhancement;
        _customVocabulary = customVocabulary;
        _promptDetection = promptDetection;
        _hotkeyService = hotkeyService;
        _appModeManager = appModeManager;
        _redoCoordinator = redoCoordinator;
        _wavLedger = wavLedger;
        _license = license;
        _imageJob = imageJob;
        _voiceActivityDetection = voiceActivityDetection;
        _audioPreparation = audioPreparation;

        // ERR-PERSIST controller refactor (2026-07-21): the pill's dismiss timing, pause/resume, and
        // dismissal all moved to MiniRecorderPresentationController (single owner). The VM no longer
        // subscribes the coordinator's dismiss timer (gone) or the image-job StateChanged for
        // affordance-timer pause/resume — the controller pauses a TimedVisible affordance behind the
        // idle image-job pill and resumes it on its own. App subscribes the image job's StateChanged
        // to publish/clear the GeneratingImage pill on the controller.

        // Kick off UIA worker construction + COM activation now so it's ready
        // well before the user's first hotkey press. Without this, the very
        // first recording after launch would race the activator and likely
        // skip DOM-focus capture (no captured element → no UIA-based focus
        // restore at paste time → falls back to plain Ctrl+V on the wrong
        // pane in Chromium/Electron targets).
        Helpers.UiaFocusBridge.WarmUp();
    }

    /// <summary>
    /// Pre-load the selected Whisper model on a background thread so the first recording
    /// starts immediately. No-op if no model is downloaded or a cloud model is selected.
    /// Never triggers a download. Failures are logged and swallowed.
    /// </summary>
    public Task PreloadModelAsync()
    {
        var selectedModel = _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel);

        // Skip preload for cloud models — nothing to load locally
        if (Models.CloudModels.IsCloudModel(selectedModel))
        {
            return Task.CompletedTask;
        }

        // Skip if no models are downloaded at all
        var downloaded = _modelDownloader.GetDownloadedModels();
        if (downloaded.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Idempotent: if a preload is already in flight, return the same task
        if (_preloadTask is { IsCompleted: false })
        {
            return _preloadTask;
        }

        _preloadTask = LoadModelInternalAsync(selectedModel);
        return _preloadTask;
    }

    /// <summary>
    /// Preload the named local model, logging duration and swallowing exceptions.
    ///
    /// <para>Fail-soft for EVERY outcome, unchanged: this is a startup optimisation, so a model
    /// that is missing or unrecognised simply means the first recording pays the load cost and
    /// reports the problem there. It must never be the thing that surfaces an error at launch.</para>
    /// </summary>
    private async Task LoadModelInternalAsync(string modelName)
    {
        try
        {
            Logger.Debug("Preloading local model");
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            // Same constraint as every other prepare site: preloading an English-only model with a
            // non-English setting loads it for a language it cannot produce, and recording start
            // then rebuilds it — which is exactly the cost the preload exists to avoid.
            var language = Helpers.EffectiveTranscriptionLanguage
                .ForModelName(modelName, _settings.GetString(AppDefaults.SelectedLanguage, "auto")).Language;
            var outcome = await _localModels.PrepareAsync(modelName, language, CancellationToken.None);
            if (outcome == Services.Transcription.PrepareOutcome.Loaded)
            {
                IsModelLoaded = true;
                sw.Stop();
                Logger.Debug("Local model preloaded in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            }
            else
            {
                Logger.Debug("Preload skipped ({Outcome}) — recording start will handle it", outcome);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Model preload failed");
        }
    }

    /// <summary>
    /// Toggle recording.
    /// </summary>
    [RelayCommand]
    public async Task ToggleRecordAsync()
    {
        Logger.Information("ToggleRecord called, state={State}", RecordingState);

        // Consume any hotkey-dispatch timestamp unconditionally at entry — even on
        // branches that don't start a recording — so a stamp left by an ignored or
        // stopping press can never leak into a later tray/UI-triggered start and
        // falsely arm the hotkey→pill latency probe.
        var hotkeyDispatchTimestamp = _hotkeyService.TryConsumeStartDispatchTimestamp();

        if (RecordingState == RecordingState.Recording)
        {
            await StopAndTranscribeAsync();
        }
        else if (RecordingState == RecordingState.Starting)
        {
            // Cancel the model download / startup phase
            await CancelRecordingAsync();
        }
        else if (RecordingState == RecordingState.Transcribing || RecordingState == RecordingState.Enhancing)
        {
            // Hotkey during transcription/enhancement — ignore. Only the MiniRecorder
            // stop button calls CancelPipeline() for explicit cancellation. The press's
            // gesture is invalidated immediately: nothing is recording, so its <300 ms
            // release must not arm the hands-free latch (the →Idle transition sweep is
            // the backstop when the release wins the race to this line).
            Logger.Information("Hotkey pressed during {State} — ignored (use stop button to cancel)", RecordingState);
            _hotkeyService.InvalidateRecordingGesture("hotkey ignored while pipeline busy");
        }
        else if (RecordingState == RecordingState.Idle)
        {
            await StartRecordingAsync(hotkeyDispatchTimestamp);
        }
    }

    /// <summary>
    /// Bail out of the post-stop transcription pipeline cleanly: reset state to Idle,
    /// post a status message, hide the MiniRecorder, and reset the hotkey state machine.
    /// Used by the "no audio recorded / detected / too short / silent WAV" early-return
    /// branches that all need the same teardown.
    /// </summary>
    private void AbortToIdle(string statusText)
    {
        // AUD-6: a pre-transcription abort makes the concurrent preflight pointless — cancel it
        // (a model download must not keep running for a discarded recording) and clear the slot
        // so no later stop can consume a stale task. Idempotent no-op when none is in flight.
        DiscardRecordingPreflight();
        // The stop-side byte checks run BEFORE the App-Mode capture-and-clear since the AUD-6
        // reorder, so their aborts clear it here instead — the clear-on-every-path invariant
        // the capture block used to provide by running first. Harmless on every other caller
        // (they run post-consumption, when it is already null).
        _activeAppModeConfig = null;
        MarkTerminalPresentation(); // IMG-BG epoch fence — terminal presentation
        RecordingState = RecordingState.Idle;
        StatusText = statusText;
        IsMiniRecorderVisible = false;
        ResetHotkeyState?.Invoke();
    }

    /// <summary>Cancel-and-forget the warm preflight (AUD-6). The task's fault, if any, lands in
    /// its forget-continuation; the CTS is this recording's own startup CTS (see
    /// <see cref="AwaitRecordingPreflightAsync"/> for the disposal tolerance).</summary>
    private void DiscardRecordingPreflight()
    {
        if (_recordingPreflight is not { } preflight) return;
        _recordingPreflight = null;
        try { preflight.StartupCts.Cancel(); }
        catch (ObjectDisposedException) { }
        Logger.Information("Recording preflight discarded (recording aborted before transcription)");
    }

    /// <summary>
    /// Gate predicate for the recording entry point (LIC-3). Returns true when the current
    /// license status permits a new recording; false with a user-visible message otherwise.
    /// The message is routed to the PILL ONLY, via <see cref="EmitLicenseRefusalPill"/> — never to
    /// <see cref="StatusText"/>, which nothing recomputes and which therefore kept the refusal on
    /// screen after the user activated a license (LIC-29; that method's remarks carry the why).
    /// <para>Fail-closed on exception: if the license check itself throws (settings IO
    /// fault, fingerprint computation hiccup, etc.) we block the recording rather than
    /// letting the gate fall through open. The user gets the generic
    /// <see cref="LicenseStatusUnknownMessage"/> and the exception is logged so the
    /// underlying issue is observable.</para>
    /// </summary>
    private bool IsLicenseAllowingRecording(out string blockedMessage, out bool trialEnded)
    {
        LicenseStatus status;
        bool storedKeyExpired;
        trialEnded = false;
        try
        {
            status = _license.GetCachedStatus();
            // Expired-key nuance (LIC-4): Invalid + a persisted-past LS expiry means "the
            // key ran out" — the pill should say so instead of generic invalid-key
            // copy. Only computed on the (rare) Invalid path, keeping the extra DPAPI read
            // off the per-press hot path.
            storedKeyExpired = status == LicenseStatus.Invalid && _license.IsStoredKeyExpired();
            // Ended-trial nuance (LIC-21 PR A): Unlicensed + a free trial that ran out on this
            // device is the state every ordinary user reaches without ever owning a key, and
            // "License required" told them nothing about WHY. Read only on the blocked path.
            trialEnded = status == LicenseStatus.Unlicensed && _license.HasFirstRunGraceEnded();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "License gate: GetCachedStatus threw — failing closed and blocking recording");
            blockedMessage = LicenseStatusUnknownMessage;
            return false;
        }

        if (!LicenseService.IsRecordingBlocked(status))
        {
            blockedMessage = string.Empty;
            return true;
        }

        blockedMessage = MessageForBlockedStatus(status, storedKeyExpired, trialEnded);
        return false;
    }

    /// <summary>
    /// Shared copy for "we can't tell what the license is" — the read threw, or a status
    /// reached the gate that this switch doesn't classify. Both are the same situation to a
    /// user (we don't know, go look), so they must not read as two different problems.
    /// </summary>
    private const string LicenseStatusUnknownMessage = "License status unknown — open the License page";

    /// <summary>
    /// Map a blocked <see cref="LicenseStatus"/> to a user-visible message for the gate.
    /// Extracted as <c>internal static</c> so unit tests can pin the classification
    /// without instantiating a full <see cref="MainViewModel"/> (which carries 20
    /// constructor dependencies). The default branch is a loud log — a new
    /// <see cref="LicenseStatus"/> added without updating this switch surfaces the
    /// gap in production logs rather than silently falling through to generic copy.
    /// </summary>
    internal static string MessageForBlockedStatus(LicenseStatus status, bool storedKeyExpired = false, bool trialEnded = false)
    {
        switch (status)
        {
            // The free trial ends as Unlicensed (LIC-21: the local window is the only try path), so
            // the trialEnded flag is what turns the generic line into the reason (PR A).
            case LicenseStatus.Unlicensed:
                return trialEnded
                    ? "Your free trial has ended — open the License page"
                    : "License required — open the License page";
            // GraceExpired covers both "offline too long" and "cache aged out on a freshly-
            // launched session before reconcile finishes" — keep the copy neutral so it's
            // not misleading in the second case.
            case LicenseStatus.GraceExpired:
                return "License needs revalidation — open the License page";
            // Expired keys collapse to Invalid (LIC-4, no extra enum state) — the
            // storedKeyExpired flag restores the honest explanation on the blocked path. It names
            // the KEY, not the free trial: since LIC-21 the free trial is the local window, which
            // ends as Unlicensed above, while an expiring key is a refunded one or an old trial key.
            case LicenseStatus.Invalid:
                return storedKeyExpired
                    ? "Your license key has expired — open the License page"
                    : "License invalid — open the License page";
            case LicenseStatus.DisabledReadOnly:
                return "License disabled — open the License page";
            default:
                Logger.Warning("License gate: unclassified blocked status {Status} — using generic message", status);
                return LicenseStatusUnknownMessage;
        }
    }

    /// <summary>
    /// True while the main pipeline's text enhancement can be skipped via the stop
    /// button — false for the Enhancing sub-states where stop means FULL cancel (image
    /// generation, the image-options picker wait, redo re-enhance). Reaches the pill as
    /// <c>PillContent.Pipeline.CanSkipEnhancement</c>, where its one consumer is the
    /// controller's stop-class handle rotation; the pill has ADVERTISED neither meaning
    /// since PILL-5 removed the 10 s "Stop button to skip" hint. UI-thread access only.
    /// </summary>
    public bool CanSkipCurrentEnhancement => _enhancementSkipCts is not null;

    /// <summary>Sole mutator for <see cref="_enhancementSkipCts"/> — raises PropertyChanged for
    /// <see cref="CanSkipCurrentEnhancement"/> so App can (re)publish the pipeline pill when skip
    /// capability BEGINS (Skip-class stop handle) and ENDS (a new Cancel-class handle, so a press
    /// captured under Skip is rejected — Codex r2 #2). The change fires on the pipeline thread;
    /// App marshals to the UI thread.</summary>
    private void SetEnhancementSkipCts(CancellationTokenSource? cts)
    {
        _enhancementSkipCts = cts;
        OnPropertyChanged(nameof(CanSkipCurrentEnhancement));
    }

    // Public only for test-signature accessibility (xUnit theory rows); the
    // classifier itself stays internal.
    public enum StopTapAction { None, SkipEnhancement, CancelPipeline }

    /// <summary>
    /// Stop-button routing (ENH-3), pure for tests: text enhancement with a live
    /// skip CTS is skipped (pipeline continues with the raw transcript); every other
    /// Transcribing/Enhancing state keeps the full pipeline cancel.
    /// </summary>
    internal static StopTapAction ClassifyStopTap(RecordingState state, bool hasLiveSkipCts)
        => state switch
        {
            RecordingState.Enhancing when hasLiveSkipCts => StopTapAction.SkipEnhancement,
            RecordingState.Enhancing or RecordingState.Transcribing => StopTapAction.CancelPipeline,
            _ => StopTapAction.None
        };

    /// <summary>
    /// Cancel in-progress transcription or enhancement. Called by MiniRecorder stop button.
    /// </summary>
    public void CancelPipeline()
    {
        switch (ClassifyStopTap(RecordingState, _enhancementSkipCts is not null))
        {
            case StopTapAction.SkipEnhancement:
                Logger.Information("Skipping in-progress enhancement via stop button");
                _enhancementSkipCts?.Cancel();
                break;
            case StopTapAction.CancelPipeline:
                Logger.Information("Cancelling in-progress {State} via stop button", RecordingState);
                _transcriptionCts?.Cancel();
                break;
        }
    }

    /// <summary>
    /// Cancel current recording without transcribing.
    /// </summary>
    [RelayCommand]
    public async Task CancelRecordingAsync()
    {
        if (RecordingState == RecordingState.Idle) return;

        Logger.Information("Cancelling recording (state: {State})", RecordingState);

        // Release the UIA-focus capture taken at recording-start. Cancel paths
        // never reach paste, so this is the only release point on this branch.
        ReleaseCapturedFocusedElement(CapturedFocusOwner.Recording);

        // Cancel any in-progress transcription/enhancement HTTP requests
        _transcriptionCts?.Cancel();
        _transcriptionCts?.Dispose();
        _transcriptionCts = null;

        // Cancel any in-progress model download
        _modelDownloadCts?.Cancel();
        _modelDownloadCts?.Dispose();
        _modelDownloadCts = null;

        if (RecordingState == RecordingState.Recording)
        {
            // AUD-11: release our claim on the session BEFORE the stop. This caller owns the live
            // recording and is stopping it deliberately, so the awaited stop leaves a window where
            // the state is still Recording and the recorder is not — exactly what the dead-capture
            // rule looks for. Clearing first keeps an intentional stop silent. The stop itself
            // stays UNSCOPED: an owning caller must stop whatever is live rather than risk
            // stranding a capture on a mismatch.
            _stallMonitor.Detach();
            await _recorder.StopRecordingAsync();
            StopMeterTimer();
            _ = _systemMute.UnmuteAsync();
            _soundFeedback.PlayStopSound();
        }

        // Clean up the recording file
        if (_currentRecordingPath != null && File.Exists(_currentRecordingPath))
        {
            try { File.Delete(_currentRecordingPath); } catch { }
        }
        _currentRecordingPath = null;

        _activeAppModeConfig = null; // Clear App Mode override on cancel
        ClearRedoState();
        var wasStarting = RecordingState == RecordingState.Starting;
        MarkTerminalPresentation(); // IMG-BG epoch fence — terminal presentation
        RecordingState = RecordingState.Idle;
        StatusText = "Cancelled";

        if (!wasStarting)
        {
            // Normal cancel (from Recording state): hide immediately
            IsMiniRecorderVisible = false;
        }
        // If wasStarting: do NOT hide MiniRecorder here.
        // The catch block in StartRecordingAsync will show an error briefly via ShowMiniRecorderError,
        // which auto-hides after the dismiss timer fires. That path sets IsMiniRecorderVisible = false
        // via the ErrorDismissed event.

        ResetHotkeyState?.Invoke();
    }

    /// <summary>
    /// Paste the last transcription text at cursor — triggered by paste-last hotkey.
    /// </summary>
    [RelayCommand]
    public async Task PasteLastTranscriptionAsync()
    {
        if (string.IsNullOrEmpty(LastTranscription))
        {
            Logger.Information("No previous transcription to paste");
            return;
        }

        Logger.Information("Pasting last transcription: {Length} chars", LastTranscription.Length);
        var appendSpace = _settings.GetBool(AppDefaults.AppendTrailingSpace, true);
        var textToPaste = appendSpace ? LastTranscription + " " : LastTranscription;
        using (_hotkeyService.BeginSuppressPromptActions())
        {
            var result = await _clipboard.PasteAtCursorAsync(textToPaste);
            if (!result.Succeeded)
            {
                // Message + tone chosen together (a ClipboardSetFailed must never get
                // "paste from clipboard" copy; declines are amber, not red).
                var (message, tone) = Helpers.PasteResultPresentation.Present(result);
                EmitMiniRecorderError(message, tone);
            }
        }
    }

    /// <summary>
    /// Arm the redo affordance: save context on the coordinator, flip observable
    /// availability flags, start (or PERSIST) the dismiss timer. Lifetime follows the tone via
    /// <see cref="MiniRecorderTimings.DeadlineFor"/>: Success and Warning both 10 s, Error = no timer
    /// (the pill persists with a corner × — ERR-PERSIST 2026-07-20).
    /// ORDER MATTERS: <see cref="RedoTone"/> is set BEFORE <see cref="IsRedoAvailable"/>
    /// so App.xaml.cs's availability-triggered snapshot never reads a stale tone.
    /// </summary>
    private void ArmRedo(RedoContext context, MiniRecorderTone tone)
    {
        // IMG-BG: an arm IS a terminal presentation — a pending image completion older than
        // this arm must never later overwrite it (epoch fence, Codex plan rounds 2–3).
        MarkTerminalPresentation();
        // ENH-6e claim-before-publish: the armed live claims must exist before any
        // observer can reach the context — a history delete racing this arm would
        // otherwise delete a reference copy out from under the just-armed redo.
        _referencePersistence.SetArmedLiveReferences(PathsOf(context.References));
        _redoCoordinator.LastContext = context;
        // Recency + per-slot text FIRST (plain properties, no PropertyChanged), then
        // tone, then availability — every interior apply the two observable writes
        // trigger sees either a not-yet-armed redo (benign fallthrough) or the fully
        // consistent final state (REL-12 / Codex round-3).
        ArmedAffordance = ArmedAffordance.Redo;
        RedoStatusText = StatusText;
        RedoTone = tone;
        IsRedoAvailable = true;
        // AffordanceArmed fires on EVERY arm (order: state/text/tone/availability set FIRST so App reads
        // a consistent snapshot). App (re)publishes the controller affordance from ArmedAffordance —
        // this covers a re-arm while IsRedoAvailable was already true, which no availability transition
        // would signal. The controller owns the pill's lifetime + its pause behind an image job.
        AffordanceArmed?.Invoke();
    }

    /// <summary>ENH-6f: project a reference-selection list to its paths for the claims registry.</summary>
    private static IReadOnlyList<string?>? PathsOf(IReadOnlyList<ReferenceImageSelection>? references)
        => references?.Select(r => r.Path).ToArray();

    /// <summary>
    /// A user-initiated BULK history deletion committed — retire an armed redo whose
    /// source rows just disappeared, and report the reference copies it was pinning so
    /// the caller can delete them in the SAME operation.
    ///
    /// <para>Why this exists: the armed redo holds ENH-6e live claims, and
    /// <c>TryDeleteIfUnreferencedAsync</c> refuses to delete a claimed copy — so
    /// "Delete all" left the most recent run's reference images on disk (owner,
    /// 2026-07-29). Releasing the claim is the right lever; force-deleting past the
    /// gate would also hit an IN-FLIGHT generation's references and break it.</para>
    ///
    /// <para>Scope-matched so "Delete images" cannot retire an armed TEXT redo (and vice
    /// versa), and the independent transcription RETRY affordance is never touched —
    /// <see cref="ClearRedoState"/> is redo-only by design (REL-12). Returns the paths
    /// BEFORE clearing, because after the clear the context is gone. UI-thread only.</para>
    /// </summary>
    /// <summary>The scope-match rule, extracted pure so the matrix is test-pinned:
    /// "Delete images" must never retire an armed TEXT redo (and vice versa); an
    /// unknown/future scope retires nothing rather than something.</summary>
    internal static bool BulkDeleteScopeMatchesArmedRedo(
        Services.Data.TranscriptionHistoryService.BulkDeleteScope scope, bool armedWasImageGeneration)
        => scope switch
        {
            Services.Data.TranscriptionHistoryService.BulkDeleteScope.All => true,
            Services.Data.TranscriptionHistoryService.BulkDeleteScope.Images => armedWasImageGeneration,
            Services.Data.TranscriptionHistoryService.BulkDeleteScope.Text => !armedWasImageGeneration,
            _ => false,
        };

    internal IReadOnlyList<string> RetireRedoForBulkHistoryDelete(
        Services.Data.TranscriptionHistoryService.BulkDeleteScope scope)
    {
        List<string>? retired = null;
        var coversImages = BulkDeleteScopeMatchesArmedRedo(scope, armedWasImageGeneration: true);

        // The BLUNT wipe rule (owner decision 2026-07-29 — see the plan trail's
        // 12-owner-decision file): discard the pending completion UNCONDITIONALLY and
        // retire the armed redo on SCOPE alone. (The stamp bump moved to App's handler
        // via NoteImageHistoryWipeCommitted, diff r9 — it must survive a UI freeze that
        // expires this bounded callback.) No row-id correlation — over-discard is
        // embraced: the cost is a lost announcement/affordance in a delete-races-job
        // window, never a stale arm re-pinning deleted rows' reference copies.
        // Rows/files that survived remain in History, where redo stays available.
        if (coversImages)
        {
            if (_pendingImageCompletion is { } pending)
            {
                CollectReferencePaths(ref retired, pending.RedoContext.References);
                _pendingImageCompletion = null;
                pending.Dispose();
                Logger.Information(
                    "Discarded the pending image completion after a bulk history delete ({Scope})", scope);
            }
        }

        var armed = _redoCoordinator.LastContext;
        if (armed != null && BulkDeleteScopeMatchesArmedRedo(scope, armed.WasImageGeneration))
        {
            var before = retired?.Count ?? 0;
            CollectReferencePaths(ref retired, armed.References);
            ClearRedoState();
            Logger.Information(
                "Retired the armed redo after a bulk history delete ({Scope}); released {Count} reference claim(s)",
                scope, (retired?.Count ?? 0) - before);
        }

        return (IReadOnlyList<string>?)retired ?? Array.Empty<string>();
    }

    private static void CollectReferencePaths(
        ref List<string>? into, IReadOnlyList<ReferenceImageSelection>? references)
    {
        if (references == null) return;
        foreach (var reference in references)
        {
            if (!string.IsNullOrWhiteSpace(reference.Path))
                (into ??= new List<string>()).Add(reference.Path);
        }
    }

    /// <summary>
    /// Clear all REDO state: coordinator context + redo timer, observable flags.
    /// Deliberately redo-only (REL-12): History-page redo flows (picker cancel, redo
    /// start) route here and must never tear down an armed retry — retry teardown is
    /// <see cref="ClearRetryState"/>, called from exactly three places.
    /// </summary>
    private void ClearRedoState()
    {
        _redoCoordinator.Clear();
        // ENH-6e: the armed context is gone — release its live claims (the release
        // cleanup deletes a reference copy only when no row and no other claim —
        // e.g. an in-flight redo's lease — still references it).
        _referencePersistence.SetArmedLiveReferences(null);
        // Write order is load-bearing (zombie pill №2, 2026-07-11): each observable
        // write triggers an INLINE window re-render via App's PropertyChanged handler,
        // so the redo affordance must be retired BEFORE the tone resets — the old
        // order (RedoTone first) exposed a snapshot (IsRedoAvailable=true,
        // RedoTone=Success, stale RedoStatusText) that rendered a green pill still
        // wearing the previous failure's error text. Mirror of ArmRedo's deliberate
        // tone-BEFORE-availability order: arming reveals last, teardown retires first.
        if (ArmedAffordance == ArmedAffordance.Redo)
        {
            // Clearing redo re-exposes a still-armed retry as the recency winner (App's
            // RetireAffordance(Redo, fallback: retry) surfaces it on the controller); else None.
            ArmedAffordance = _redoCoordinator.LastRetryContext != null
                ? ArmedAffordance.Retry
                : ArmedAffordance.None;
        }
        // IsRedoAvailable last: App observes the true→false transition and calls
        // controller.RetireAffordance(Redo, IsRetryAvailable ? retry-fallback : null).
        IsRedoAvailable = false;
        RedoStatusText = null;
        RedoTone = MiniRecorderTone.Success;
    }

    /// <summary>
    /// Arm the retry affordance (REL-12): a transcription-stage failure retained the
    /// WAV; the pill shows the error with a retry button. Caller sets
    /// <see cref="StatusText"/> to the error message FIRST (captured here into
    /// <see cref="RetryStatusText"/> — the pill's own text, immune to later shared
    /// StatusText writes). Lifetime follows the tone (<see cref="MiniRecorderTimings.DeadlineFor"/>):
    /// the default Error retry pill PERSISTS with a corner × (ERR-PERSIST); the PRM-4 echo-block
    /// Warning and a user-cancel Warning keep the timed attention lifetime.
    /// </summary>
    private void ArmRetry(TranscriptionRetryContext context, MiniRecorderTone tone = MiniRecorderTone.Error)
    {
        MarkTerminalPresentation(); // IMG-BG epoch fence — see ArmRedo
        _redoCoordinator.LastRetryContext = context;
        ArmedAffordance = ArmedAffordance.Retry;
        RetryTone = tone;           // BEFORE the availability flip — see RedoTone ordering
        RetryStatusText = StatusText;
        IsRetryAvailable = true;
        // Fires on every arm — App (re)publishes from ArmedAffordance; covers a re-arm while already
        // available (Codex r2 #1). See ArmRedo.
        AffordanceArmed?.Invoke();
    }

    /// <summary>
    /// Explicit retry teardown (REL-12). EXACTLY two callers: recording start
    /// (<paramref name="deleteWav"/> = true — a new recording supersedes the retained
    /// failure) and the retry entry point (false — WAV ownership transfers to the
    /// retry pipeline run). Shutdown does NOT come through here — both exit paths
    /// drain the <see cref="RetainedWavLedger"/> from DI directly. The WAV delete is
    /// fire-and-forget — recording start is the latency-sensitive hotkey path and
    /// must not pay synchronous file IO; the ledger keeps the path tracked until the
    /// delete is CONFIRMED, so an exit racing the async delete still drains it.
    ///
    /// <para><b>Since TRN-17 this is no longer "the" double-invocation guard</b> — that role now
    /// splits three ways and the old single-sentence claim would be false. The
    /// <c>_enhancementPickerActive</c> CAS guards the dialog window, this call guards the
    /// confirm→pipeline leg, and <c>_stopSingleFlight</c> in <c>StopAndTranscribeAsync</c> is the
    /// real backstop for two overlapping stops.</para>
    /// </summary>
    private void ClearRetryState(bool deleteWav)
    {
        var wavPath = _redoCoordinator.LastRetryContext?.WavPath;
        _redoCoordinator.ClearRetry();
        RetryStatusText = null;
        if (ArmedAffordance == ArmedAffordance.Retry)
            ArmedAffordance = _redoCoordinator.LastContext != null
                ? ArmedAffordance.Redo
                : ArmedAffordance.None;
        IsRetryAvailable = false;

        if (deleteWav && wavPath != null)
        {
            var ledger = _wavLedger;
            // REL-17: the superseded retained WAV honors the keep-recordings setting too —
            // it reached the pipeline and failed, exactly the audio worth diagnosing.
            var keepForDebug = _settings.GetBool(AppDefaults.KeepRecordingsForDebug, false);
            _ = Task.Run(() =>
            {
                try
                {
                    // What THIS task did to the bytes — reported only if it also wins the ledger
                    // removal below. "already absent" is the honest answer when the file went
                    // before we looked, which is a real close and not nothing.
                    var outcome = "already absent (superseded)";
                    if (File.Exists(wavPath))
                    {
                        // Write-side junction guard (diff round 5): unverifiable Debug
                        // dir ⇒ delete fallback, same rule as every other move site.
                        var debugDir = keepForDebug ? AppPaths.EnsureRecordingsDebug() : null;
                        // Report what HAPPENED, not what was configured (the GainReason rule):
                        // the junction guard can route a keep-recordings move into the delete
                        // arm, so the branch that RAN chooses the line. Silence here was the
                        // second half of the 2026-08-15 logging gap — a superseded retry WAV
                        // moved to Debug or was deleted with no record either way, so a failed
                        // recording could reach its final resting place having never been named.
                        if (debugDir != null && !VerifiedFileAccess.IsReparsePointOrUnreadable(debugDir))
                        {
                            File.Move(wavPath, Path.Combine(debugDir, Path.GetFileName(wavPath)), overwrite: true);
                            outcome = "kept for debugging (superseded)";
                        }
                        else
                        {
                            File.Delete(wavPath);
                            // REL-23: this delete arm must take the raw sibling too, exactly as the
                            // pipeline's own Delete disposition does. Both self-review lenses found
                            // this independently: keep-recordings ON writes the PRE-GAIN original to
                            // Debug, and if the user turns the setting OFF while a retry is armed,
                            // superseding it deleted the gained WAV while the more sensitive
                            // microphone copy survived — "a toggle-off delete promises to keep
                            // nothing" binds here as strongly as it does there. Fail-soft, and it
                            // refuses a junctioned Debug dir on its own, so the junction fallback
                            // that lands us in this arm is safe.
                            Services.Audio.RecordingAudioPreparation.DeleteRawSiblingFor(wavPath);
                            outcome = "deleted (superseded)";
                        }
                    }
                    // Log through the ledger, never independently (Codex diff round 1): this task
                    // is fire-and-forget and races the shutdown drain. The drain can dispose of
                    // the WAV between the File.Exists check and the delete, after which our
                    // File.Delete succeeds as a missing-file no-op — and an unconditional line
                    // here would then announce a deletion this task did not perform. Only the
                    // winner of the removal reports.
                    ledger.TryCloseRetention(wavPath, outcome);
                }
                catch (Exception ex)
                {
                    // Leave tracked — the shutdown drain / 7-day sweep are the backstops.
                    Logger.Debug(ex, "Superseded retry WAV delete failed {Path}", wavPath);
                }
            });
        }
    }

    /// <summary>
    /// REL-17 executor for <see cref="Helpers.RecordingEndDisposition.RetainForDebug"/>:
    /// move the end-of-life WAV into <c>Recordings\Debug</c> (same volume — a rename, no
    /// copy). A failed move falls back to DELETE — a WAV must never leak ownerless into
    /// the Recordings root (fail-safe). <c>ConfirmDeleted</c> fires either way: the
    /// ledger's job for the ORIGINAL path ends here, and Debug WAVs are never
    /// ledger-tracked (the unconditional 7-day sweep owns them).
    /// </summary>
    private void MoveRecordingToDebugOrDelete(string recordingPath)
    {
        try
        {
            var debugDir = AppPaths.EnsureRecordingsDebug();
            // Write-side junction guard (diff round 5): a junctioned Debug dir would
            // route the WAV outside the app root where the sweeps/erasure refuse to
            // follow — throw into the delete fallback below.
            if (VerifiedFileAccess.IsReparsePointOrUnreadable(debugDir))
                throw new IOException("Recordings\\Debug could not be verified");
            var target = Path.Combine(debugDir, Path.GetFileName(recordingPath));
            File.Move(recordingPath, target, overwrite: true);
            _wavLedger.ConfirmDeleted(recordingPath);
            Logger.Information("Recording retained for debugging: {Name}", Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Debug retention move failed; deleting {Path}", recordingPath);
            try
            {
                File.Delete(recordingPath);
                // Information, and UNCONDITIONAL — matching the success branch above, which also
                // names every recording it moves whether or not the ledger tracked it (an ordinary
                // dictation under keep-recordings reaches here untracked and has always been
                // named). Silence was a BLOCKER (Codex diff round 1): this branch promised Debug
                // retention, failed to keep it, and deleted the file — so a retained recording
                // whose move failed left "Recording retained for retry" as the last word about it,
                // and support would conclude the WAV was still available. It is the one path where
                // the success branch's own wording would have been a lie.
                Logger.Information(
                    "Recording could not be retained for debugging and was deleted: {Name}",
                    Path.GetFileName(recordingPath));
                _wavLedger.ConfirmDeleted(recordingPath);
            }
            catch (Exception ex2) { Logger.Debug(ex2, "Failed to delete recording {Path}", recordingPath); }
        }
    }

    // Shutdown cleanup of retained retry audio deliberately does NOT go through this
    // ViewModel: App.Cleanup() runs after ShutdownMiniRecorder() has nulled its VM
    // reference (internal review), so both exit paths (Cleanup + the forced-exit
    // flush) resolve RetainedWavLedger from DI and call DrainBestEffort() directly —
    // the armed slot's WAV is tracked there from retain time, and the drain is pure
    // file IO (no observables, no DispatcherTimer).

    // ─────────────────────────────────────────────────────────────────────────
    // IMG-BG (2026-07-16): background image generation. The multi-minute
    // generation no longer holds RecordingState.Enhancing — it runs as a
    // detached, UI-thread-affine job coordinated by ImageGenerationJobService,
    // and the recording pipeline returns to Idle at dispatch. Everything the
    // job touches is captured BY VALUE in ImageGenJobRequest (the live VM
    // fields belong to the NEXT recording), and its completion presents through
    // a versioned pending object applied only when the pipeline is idle.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Immutable dispatch snapshot for one background image generation. Target-window data and
    /// the focus element are captured at dispatch because a new recording overwrites
    /// <c>_targetWindowHandle</c>/<c>_targetWindowCapturedAtUtc</c>/<c>_pasteTargetSnapshot</c>
    /// and steals the focus slot the moment the pipeline is idle again. The job OWNS
    /// <paramref name="FocusedElement"/> (detached via <see cref="CapturedFocusSlot.TakeForHandoffAsync"/>)
    /// and releases it itself; <paramref name="ReferenceLease"/> is the ENH-6e in-flight claim
    /// over the SOURCE reference files (generation read + failed-attempt re-read), job-body
    /// scoped — the fresh-copy claims made at persist time transfer to the pending
    /// presentation instead.
    /// </summary>
    // internal (not private) so BuildImageRow can be internal and directly test-covered —
    // the row builder is the thing that keeps the success and failure write paths from
    // drifting, so it must be exercisable without hand-building a row.
    internal sealed record ImageGenJobRequest(
        string Text,
        CustomPrompt? Prompt,           // CLONED at dispatch — never the live cached instance
        // The ONE nonblank (provider, model) route, resolved AT DISPATCH and used for BOTH the
        // generation call and the History/redo metadata — a settings change mid-generation, or
        // a blank persisted model that generation would silently default, can never make the
        // record disagree with the actual request (Codex diff review rounds 1–2).
        AIProvider ResolvedProvider,
        string ResolvedModel,
        IReadOnlyList<ReferenceImageSelection>? References,
        string TranscriptionModelName,
        // The language the ORIGINAL transcription recognised with — persisted by BuildImageRow.
        // NULLABLE since TRN-1 step 3: an IMG-1 text-first chain has no transcription behind it, and
        // the redo dispatch used to paper over that by re-reading the live global setting, stamping
        // a weeks-old record with today's selection. No transcription means no language to report.
        string? Language,
        bool WasPushToTalk,
        bool IsNewGeneration,
        // IMG-3: how many versions this job generates (pre-clamped at dispatch via
        // ImageBatchPolicy.ClampCount). Count > 1 ships FocusedElement = null — a batch
        // never pastes, so no cross-process RCW is held through it.
        int Count,
        IntPtr TargetWindow,
        DateTime TargetCapturedAtUtc,
        PasteTargetSnapshot? TargetSnapshot,
        Helpers.UiaFocusBridge.IUIAutomationElement? FocusedElement,
        IDisposable? ReferenceLease);

    /// <summary>
    /// A finished job's presentation, waiting for the pipeline to be idle. OWNS the fresh-copy
    /// reference claims until presented (ArmRedo re-establishes armed claims first) or
    /// discarded — otherwise a History delete could reap a reference copy the deferred redo
    /// context still needs (Codex plan round 2). <see cref="Epoch"/> is the presentation epoch
    /// at completion time: any terminal pipeline presentation after it invalidates this
    /// completion entirely (nothing applies — the history row and image survive regardless).
    /// </summary>
    private sealed class PendingImageCompletion : IDisposable
    {
        public required long Epoch { get; init; }
        public required string StatusText { get; init; }
        public required MiniRecorderTone Tone { get; init; }
        public required RedoContext RedoContext { get; init; }
        /// <summary>Null = leave <see cref="MainViewModel.LastTranscription"/> untouched.</summary>
        public required string? LastTranscriptionText { get; init; }
        public required int? HistoryId { get; init; }
        /// <summary>True (success) assigns HistoryId even when null — the success paths
        /// deliberately clear a stale id when history is disabled; failure keeps the
        /// only-when-present semantics of the failure paths.</summary>
        public required bool AssignHistoryIdUnconditionally { get; init; }
        /// <summary>HIS-4 blunt wipe rule (owner decision 2026-07-29):
        /// <see cref="_imageHistoryWipeStamp"/> captured ONCE when this completion's job
        /// STARTED. The apply gate discards the completion if any image-covering bulk
        /// History delete ran since — no row-id correlation, no exemptions; a lost
        /// announcement in a delete-races-job window is the accepted cost, a stale arm
        /// re-pinning deleted rows' reference copies is the bug it prevents.</summary>
        public required long WipeStampAtJobStart { get; init; }
        public required List<IDisposable> Claims { get; init; }

        public void Dispose()
        {
            foreach (var claim in Claims)
                claim.Dispose();
            Claims.Clear();
        }
    }

    private PendingImageCompletion? _pendingImageCompletion;

    /// <summary>
    /// HIS-4: bumped by every bulk History delete whose scope covers images. Each image
    /// job captures it ONCE at start; the apply gate discards a completion whose job
    /// started before the last wipe — the BLUNT rule (owner decision 2026-07-29), so a
    /// wiped row's id can never be published and its reference claims never re-pinned.
    /// THREAD-SAFETY (diff r9): the bump happens in App's event handler on the RAISING
    /// pool thread via <see cref="NoteImageHistoryWipeCommitted"/> — inside the delete's
    /// awaited settlement, so a UI freeze that expires the bounded retirement callback
    /// can no longer skip it — hence Interlocked/Volatile access; reads stay on the UI
    /// thread. Deliberately synchronous state, never an await-based re-validation — see
    /// the recorded declination in <see cref="ApplyPendingImageCompletionIfCurrent"/>.
    /// </summary>
    private long _imageHistoryWipeStamp;

    /// <summary>HIS-4 diff r9: advance the image-wipe stamp — called by App's
    /// <c>BulkDeleteCommitted</c> handler (pool thread) for image-covering scopes,
    /// BEFORE the bounded UI retirement callback, so the stamp is guaranteed to move
    /// once the delete commits regardless of dispatcher liveness.</summary>
    internal void NoteImageHistoryWipeCommitted()
        => global::System.Threading.Interlocked.Increment(ref _imageHistoryWipeStamp);

    /// <summary>
    /// Monotonic presentation epoch (UI thread only). Advanced by EVERY terminal pipeline
    /// presentation — plain success/decline, cancel, abort-to-idle, and both arms — so a
    /// pending image completion older than the latest presentation is discarded instead of
    /// overwriting it (Codex plan rounds 2–3: LastTranscription/redo recency). Post ERR-PERSIST
    /// controller refactor its ONLY remaining role is that image-completion recency gate — the
    /// pill's own staleness rejection is now the controller's opaque PillHandle.
    /// </summary>
    private long _presentationEpoch;

    private void MarkTerminalPresentation() => _presentationEpoch++;

    /// <summary>
    /// Emit a MiniRecorder message pill (a terminal presentation, so it advances the epoch for the
    /// image-completion recency gate). The controller owns the pill's lifetime — an Error tone
    /// renders a PERSISTENT pill (corner ×), a Warning auto-dismisses after
    /// <see cref="MiniRecorderTimings.AttentionSeconds"/>.
    /// </summary>
    private void EmitMiniRecorderError(string message, MiniRecorderTone tone)
    {
        MarkTerminalPresentation();
        StatusText = message;
        ShowMiniRecorderError?.Invoke(message, tone, PillMessageAction.None);
    }

    /// <summary>
    /// LIC-29: the license gate's refusal, pill ONLY — deliberately without the
    /// <see cref="StatusText"/> write <see cref="EmitMiniRecorderError"/> performs.
    /// <para>What makes this one message different is NOT that the pipeline never started —
    /// several other callers are pre-pipeline refusals too (update-in-progress, no transcription
    /// model, the image-model gate) and they keep the status line. It is that this sentence
    /// becomes FALSE after a later action the user actually performs and which is not a
    /// recording: activating a license. Nothing recomputes <see cref="StatusText"/>, so the
    /// refusal outlived the activation and a paying customer kept reading "your free trial has
    /// ended" on Home — through page navigation and a full wizard re-run (tester log,
    /// 2026-09-09: blocked at 20:40:11, activated 20:49:35, dictating normally from 20:59).
    /// "Update in progress" dies with the process and "no model available" stays roughly true;
    /// this one does not. Leave every other caller on <see cref="EmitMiniRecorderError"/>.</para>
    /// <para><see cref="MarkTerminalPresentation"/> is still advanced: its remaining role is the
    /// image-completion recency gate, and a refused start is a terminal presentation of that
    /// attempt — without it a pending image completion could overwrite the very pill the user is
    /// meant to read. <see cref="MiniRecorderTone.Error"/> renders an UntilDismissed pill, so the
    /// reason survives without a status line to back it up.</para>
    /// </summary>
    private void EmitLicenseRefusalPill(string message)
    {
        MarkTerminalPresentation();
        // LNC-11: every refusal names the License page, so its pill OPENS it on click — the one
        // message pill that carries an action.
        ShowMiniRecorderError?.Invoke(message, MiniRecorderTone.Error, PillMessageAction.OpenLicensePage);
    }

    /// <summary>
    /// LNC-11: the recording gate's once-per-session automatic navigation. Only the hotkey/record
    /// path calls this (the two image-generation gates emit their pill and return — a History redo
    /// is not the moment the trial ends under the user's hands); only a TRIAL-ENDED refusal claims.
    /// </summary>
    private void RequestLicensePageOnFirstTrialEndBlock(bool trialEnded)
    {
        if (!trialEnded) return;
        // A relaunched setup wizard (Settings → Relaunch setup wizard) owns the window and the
        // main window refuses page navigation while it does; claiming here would spend the session's
        // one jump on a navigation that cannot land (self-review, lens A). Pill only, the claim stays.
        if (App.MainWindowInstance?.IsOnboarding == true)
        {
            Logger.Information("Recording blocked after the free trial ended while the setup wizard is open — pill only");
            return;
        }
        if (!_trialEndRedirect.TryClaim(trialEnded)) return;
        Logger.Information("Recording blocked after the free trial ended — opening the License page once this session");
        OpenLicensePageRequested?.Invoke();
    }


    /// <summary>
    /// Raised when a job-scoped notice should render INSIDE the GeneratingImage pill (e.g.
    /// "An image is already generating") — never through the ActiveError surface, which would
    /// hide the job's stop button for the whole attention interval (Codex round 4 / owner
    /// "working stop button whenever idle"). Arg: the notice text.
    /// </summary>
    public event Action<string>? ImageJobNoticeRequested;

    /// <summary>
    /// AUD-1: raised when the mic-fallback notice should render INSIDE the pipeline pill
    /// ("Selected mic unavailable — using default") — never through the message surface,
    /// which both COVERS the pipeline pill's stop button (message outranks pipeline in the
    /// controller's precedence) and gets WIPED by the next PublishPipeline. Arg: the notice text.
    /// </summary>
    public event Action<string>? PipelineNoticeRequested;

    /// <summary>AUD-1: one-shot amber notice when a recording proceeded on the system default
    /// although a device is pinned (unplugged/disabled/timeout/activation-retry). Latch lives
    /// in the selection service; claims are per device id per app run.</summary>
    private void MaybeNotifyDeviceFallback(ResolvedRecordingDevice resolution)
    {
        if (resolution.Kind != RecordingDeviceResolutionKind.FallbackToDefault) return;
        if (resolution.SelectedId is not { } id || !_deviceSelection.TryClaimFallbackNotice(id)) return;
        Logger.Information("Recording-device fallback notice shown (pinned mic unavailable this recording)");
        PipelineNoticeRequested?.Invoke("Selected mic unavailable — using default");
    }

    /// <summary>Thread-safe view for guards + the maintenance source.</summary>
    public bool IsImageJobRunning => _imageJob.IsRunning;

    /// <summary>Cancel the background image job (pill stop button / tray item). Idempotent.</summary>
    public void CancelImageJob()
    {
        Logger.Information("Image generation job cancel requested");
        _imageJob.Cancel();
    }

    /// <summary>
    /// Gate a resolved image model before ANY resource transfer. Returns true to proceed; on refusal
    /// it disposes the reservation (<c>TryReserve</c> demands Start-or-Dispose), presents a terminal
    /// Warning, returns the pipeline to Idle, and returns false.
    /// <para>Callers MUST treat false as HANDLED — for the voice path that means returning
    /// <c>true</c> from <c>TryRunImageGenerationAsync</c>, or the image prompt would fall through and
    /// paste as plain dictation.</para>
    /// <para>The notice channel used by <see cref="RequestImageJobRefusalNotice"/> is deliberately NOT
    /// used here: it renders inside a running job's pill and does not advance the presentation epoch,
    /// which a standalone terminal refusal must do (Codex plan review round 2).</para>
    /// </summary>
    private bool GateImageModelOrRefuse(
        Helpers.ImageModelResolution resolution,
        AIProvider provider,
        Services.AIEnhancement.ImageGenerationJobService.JobReservation reservation)
    {
        var action = Helpers.ImageModelGate.Decide(resolution, ModelDisplayPolicy.IsImageModel);
        if (action == Helpers.ImageModelGateAction.Proceed)
            return true;

        // A bad PROVIDER DEFAULT is our bug, not a user setting — log it loudly and distinctly so it
        // cannot hide among ordinary misconfiguration.
        if (resolution.Source == Helpers.ImageModelSource.ProviderDefault)
            Logger.Error("Image generation refused: the built-in default model '{Model}' for {Provider} is not an image model",
                resolution.Model, provider);
        else
            Logger.Warning("Image generation refused: model '{Model}' ({Source}) for {Provider} is a text model{Healed}",
                resolution.Model, resolution.Source, provider,
                action == Helpers.ImageModelGateAction.RefuseAndHeal ? " — clearing the persisted selection" : "");

        if (action == Helpers.ImageModelGateAction.RefuseAndHeal)
            _enhancement.ClearPersistedImageModel(provider);

        reservation.Dispose();
        EmitMiniRecorderError(Helpers.ImageModelGate.RefusalMessage(provider.ToString()), MiniRecorderTone.Warning);
        RecordingState = RecordingState.Idle;
        IsMiniRecorderVisible = true;
        return false;
    }

    private void RequestImageJobRefusalNotice()
    {
        Logger.Information("Image request refused — a background image generation is already running");
        ImageJobNoticeRequested?.Invoke("An image is already generating");
    }

    /// <summary>Run <paramref name="action"/> on a FRESH dispatcher turn (inline fallback for
    /// tests/no-window). A fresh turn is load-bearing for the pending apply — see
    /// <see cref="OnRecordingStateChanged"/>.</summary>
    private static void EnqueueUiTurn(Action action)
    {
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue == null || !queue.TryEnqueue(() => action()))
            action();
    }

    /// <summary>Job body → stash the completion (last-wins) and, when already idle, schedule
    /// the apply on a fresh turn. UI thread only.</summary>
    private void QueueImageCompletionPresentation(PendingImageCompletion completion)
    {
        _pendingImageCompletion?.Dispose();
        _pendingImageCompletion = completion;
        if (RecordingState == RecordingState.Idle)
            EnqueueUiTurn(ApplyPendingImageCompletionIfCurrent);
    }

    private void ApplyPendingImageCompletionIfCurrent()
    {
        var pending = _pendingImageCompletion;
        if (pending == null)
            return;

        switch (Helpers.ImageJobPresentationGate.Decide(
            RecordingState == RecordingState.Idle, pending.Epoch, _presentationEpoch))
        {
            case Helpers.ImageJobPresentationGate.Decision.Defer:
                return; // the next Idle transition re-enqueues
            case Helpers.ImageJobPresentationGate.Decision.Discard:
                Logger.Information("Image completion presentation discarded — a newer presentation landed first");
                _pendingImageCompletion = null;
                pending.Dispose();
                return;
        }

        // HIS-4: a completion whose job ran through a bulk History delete must not
        // publish a possibly-dead id or arm a redo that re-pins deleted rows' reference
        // claims — the BLUNT rule discards it outright (owner decision 2026-07-29).
        // Judged by the SYNCHRONOUS wipe stamp — never an awaited History query (the
        // recorded declination below). Disposing releases the fresh-copy claims; the
        // release-time cleanup + sweep then remove the row-less copies.
        if (Helpers.ImageJobPresentationGate.IsStaleAfterBulkDelete(
            pending.WipeStampAtJobStart, global::System.Threading.Volatile.Read(ref _imageHistoryWipeStamp)))
        {
            Logger.Information("Image completion presentation discarded — a bulk history delete removed its rows");
            _pendingImageCompletion = null;
            pending.Dispose();
            return;
        }

        _pendingImageCompletion = null;
        try
        {
            if (pending.LastTranscriptionText != null)
                LastTranscription = pending.LastTranscriptionText;
            // RECORDED DECLINATION (owner, 2026-07-28): the id landing here is NOT
            // re-validated against History, even though IMG-4b's incremental commits let
            // the user delete a just-appeared row before the job ends (so this id can
            // already be gone). Re-validating it was implemented and dropped — the
            // validation query is an await, and every fence for it (id-only, then id +
            // text epoch) still let a stale continuation erase a NEWER completion's
            // state, because both text paths publish their text before their id and
            // equal-value publications don't advance a change-hook epoch. The residual
            // is cosmetic and self-healing: Home may point at a deleted row until the
            // next dictation or delete (whose own sync clears it). Do not re-add without
            // solving the publication-versioning problem at its source.
            if (pending.AssignHistoryIdUnconditionally)
                _lastTranscriptionHistoryId = pending.HistoryId;
            else if (pending.HistoryId.HasValue)
                _lastTranscriptionHistoryId = pending.HistoryId;

            StatusText = pending.StatusText;
            IsMiniRecorderVisible = true;
            // ArmRedo advances the epoch and re-establishes the armed live claims — only
            // after that may the pending's fresh-copy claims release (the finally).
            ArmRedo(pending.RedoContext, pending.Tone);
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>
    /// The background job body (invoked via <see cref="Services.AIEnhancement.ImageGenerationJobService.JobReservation.Start"/>,
    /// fire-and-forget on the UI thread — continuations stay UI-affine so every existing
    /// threading invariant holds: <c>DispatcherTimer</c> in ArmRedo, UI-thread-only focus slot,
    /// inline pill re-renders). Commit boundary (Codex rounds 3–5): once image bytes exist and
    /// <see cref="Services.AIEnhancement.ImageGenerationJobService.TryBeginCommit"/> wins, the
    /// persistence tail runs with <see cref="CancellationToken.None"/> — a cancel/quit can
    /// never strand a saved image without its History row.
    /// </summary>
    private async Task RunImageGenerationJobAsync(ImageGenJobRequest req, CancellationToken ct)
    {
        // IMG-3: the body is delegate wiring around the production ImageBatchExecutor —
        // it owns the runner loop, the terminal decision, the SINGLE compose-and-queue
        // site, and the ONE claims list (seeded with the reference lease at entry; the
        // executor is the lease's only owner — this method's finally releases only the
        // focus element). N=1 runs the same composition and stays user-observably
        // identical: no progress text, the same paste tail, the same presentations.
        Services.AIEnhancement.ImageGenerationResult? lastGeneration = null;
        LastCommittedItem? lastCommitted = null;
        // HIS-4, the BLUNT wipe rule (owner decision 2026-07-29): the stamp is captured
        // ONCE at job start; the apply gate discards the completion if any image-covering
        // bulk delete ran since. No per-row correlation — over-discard by design.
        // Volatile: the writer is App's handler on a pool thread (diff r9).
        var wipeStampAtJobStart = global::System.Threading.Volatile.Read(ref _imageHistoryWipeStamp);
        // IMG-4: the parallel batch's shared call state (memoized reference read +
        // response-materialization gate) — created only for N>1 and disposed at method
        // exit, which is safely after the executor returned (the runner joins every
        // wrapper task before returning, so no call can touch the context afterwards).
        // N=1 passes no context and stays byte-identical.
        using var batchContext = req.Count > 1
            ? new Services.AIEnhancement.ImageBatchCallContext(readCt =>
                Services.AIEnhancement.AIEnhancementService.ReadReferenceImagesForBatchAsync(
                    req.References, req.Count, readCt))
            : null;
        try
        {
            Logger.Information("Image generation job started ({Length} chars, {Count} version(s))",
                req.Text.Length, req.Count);
            await Helpers.ImageBatchExecutor.RunAsync(
                count: req.Count,
                referenceLease: req.ReferenceLease,
                generateItemAsync: async (item, jobCt) =>
                {
                    // The job's OWN token first (Codex diff round 3): tray Cancel / quit
                    // shutdown during Preparing cancel THIS token, not the pipeline's —
                    // and generation's provider/model/API-key validation runs before its
                    // first cancellation-aware await, so entering it pre-cancelled could
                    // surface a configuration error instead of a clean cancellation.
                    jobCt.ThrowIfCancellationRequested();
                    if (req.Count > 1)
                        Logger.Information("Image generation item {Item}/{Count} launched", item, req.Count);
                    // Always the dispatch-resolved route — never a live re-resolution.
                    // IMG-4: a batch goes through the internal overload carrying the
                    // shared context (all N calls run CONCURRENTLY on the runner's batch
                    // token); N=1 keeps today's public call byte-identical.
                    // Batches route through the IMG-4b logging guard; N=1 keeps the
                    // direct await BYTE-IDENTICAL (no extra async frame in its traces).
                    var generation = req.Count > 1
                        ? await GenerateBatchItemLoggedAsync(
                            () => _enhancement.GenerateImageWithModelForBatchAsync(
                                req.Text, req.Prompt, req.ResolvedModel, req.ResolvedProvider, req.References, batchContext!, jobCt),
                            item, req.Count, jobCt, Logger)
                        : await _enhancement.GenerateImageWithModelAsync(
                            req.Text, req.Prompt, req.ResolvedModel, req.ResolvedProvider, req.References, jobCt);
                    lastGeneration = generation;
                    // IMG-4: batch metrics count at GENERATION SETTLEMENT — a paid,
                    // delivered image counts even when a later persistence stop (or the
                    // quit latch) drops its commit; the per-item persist path would
                    // undercount every success after the first persist-side stop (diff
                    // review r1). N=1 keeps its persist-time metric verbatim. Fail-soft:
                    // a metrics hiccup must never fail a paid generation.
                    if (req.Count > 1)
                    {
                        try
                        {
                            await RecordLifetimeMetricsAsync(
                                Models.Entities.TranscriptionRecord.ImageGeneratedMarker,
                                DateTime.UtcNow, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning(ex, "Batch generation metric failed");
                        }
                    }
                    return generation;
                },
                isHistoryEnabled: () => _settings.GetBool(AppDefaults.IsHistoryEnabled, true),
                tryBeginCommit: CommitImageAdmission,
                persistItemAsync: async (item, generation) =>
                {
                    var persist = await PersistImageItemAsync(req, generation);
                    if (persist.Outcome == Helpers.ImageItemPersistOutcome.Committed)
                        lastCommitted = new LastCommittedItem(
                            persist.HistoryId,
                            generation.Effective.Aspect,
                            generation.Effective.SizeTier,
                            generation.Effective.Quality,
                            persist.RearmSelections);
                    return persist;
                },
                resumeAfterCommit: _imageJob.EndCommitResumeGenerating,
                reportProgress: item => ReportImageJobProgress(item, req.Count),
                isShutdownRequested: () => _imageJob.IsShutdownRequested,
                // IMG-4c: each failed version's OWN row, written inline by the runner
                // (admission + phase-close are the runner's commit cycle). Sanitation is
                // per item — this item's own exception decides which references survive.
                persistFailedItemAsync: (_, ex) =>
                    WriteFailedImageRowAsync(req, SanitizeReferencesAfterFailure(req.References, ex)),
                persistFailedMarkerAsync: ex => PersistFailedImageMarkerAsync(req, ex),
                pasteAsync: async () =>
                {
                    // Clipboard/paste LAST, immediately before the presentation is queued
                    // (Codex diff review round 1): nothing can overwrite the clipboard
                    // between this write and the "Image on clipboard" announcement except
                    // a genuinely-later user action. N=1 only — a batch never pastes.
                    var imagePaste = await PasteImageWithFocusRestoreAsync(
                        lastGeneration!.ImageBytes, req.TargetWindow, req.FocusedElement,
                        req.TargetCapturedAtUtc, req.TargetSnapshot);
                    return ResolveImagePasteRedoPresentation(imagePaste);
                },
                composeAndQueueAsync: (result, marker, pasteDerived, terminalRearm, claims) =>
                {
                    var composition = ComposeBatchCompletion(
                        result.Reason, result.Completed, req.Count,
                        req.Text, req.Prompt, req.ResolvedProvider, req.ResolvedModel,
                        req.References, req.TranscriptionModelName, req.Language,
                        req.TargetWindow, req.WasPushToTalk, req.IsNewGeneration,
                        lastCommitted, result.Failure,
                        marker?.MarkerRowId, marker?.RearmSelections, terminalRearm, pasteDerived);
                    QueueImageCompletionPresentation(new PendingImageCompletion
                    {
                        Epoch = _presentationEpoch,
                        StatusText = composition.StatusText,
                        Tone = composition.Tone,
                        RedoContext = composition.RedoContext,
                        LastTranscriptionText = composition.LastTranscriptionText,
                        HistoryId = composition.HistoryId,
                        AssignHistoryIdUnconditionally = composition.AssignHistoryIdUnconditionally,
                        WipeStampAtJobStart = wipeStampAtJobStart,
                        Claims = claims,
                    });
                    // IMG-4c: state both halves — an all-failed batch that wrote four
                    // failure rows previously logged "0/4 committed", hiding the exact
                    // behaviour this change added. Labels stay deliberately precise:
                    // "committed" keeps its pre-IMG-4c meaning (an N=1 legacy fail-soft
                    // success can report 1 with no row on disk), and the new count is
                    // explicitly the runner's INLINE failure rows — an N=1 terminal
                    // marker row is not one of them and stays 0 here.
                    Logger.Information(
                        "Image generation job ended: {Reason} ({Completed}/{Count} committed, {FailureRows} inline failure rows)",
                        result.Reason, result.Completed, req.Count, result.FailureRowsCommitted);
                    return Task.CompletedTask;
                },
                presentBareCancel: (completedCount, total) =>
                {
                    Logger.Information("Image generation job cancelled ({Completed}/{Count} committed)",
                        completedCount, total);
                    PresentImageJobCancelled();
                },
                ct: ct);
        }
        finally
        {
            // The job owns the handed-off RCW (TakeForHandoffAsync detached it from the slot;
            // null for a batch — see the dispatch sites); the paste path only borrows. Release
            // through the bridge worker, never inline. The reference lease is deliberately NOT
            // touched here: it joined the executor's claims list at entry and lives or dies
            // with that single owner (Codex round 3 — a second alias double-disposes).
            if (req.FocusedElement != null)
                Helpers.UiaFocusBridge.EnqueueRelease(req.FocusedElement);
        }
    }

    /// <summary>
    /// IMG-4b failure-independence guard around one batch item's provider call: a
    /// cancellation (the runner's linked batch token — user/quit or structured cleanup)
    /// passes through unlogged, while every genuine per-item failure at N&gt;1 gets ONE
    /// local Warning — siblings keep running and only the FIRST-processed failure
    /// presents, so this line is the only diagnostic trail for the others (incl.
    /// parse-stage throws that never reach a provider-client log line). ex.ToString()
    /// rides the redacted {LocalException} property (Sentry-bound events see
    /// &lt;REDACTED&gt;) — for an EXPECTED provider verdict the exception OBJECT is
    /// deliberately NOT attached: its message can carry provider prose past the
    /// enricher, and its stack adds nothing the client did not already log (so the
    /// property carries "Type: message", never `ex.ToString()`). An UNEXPECTED type is
    /// a defect and logs at Error WITH the exception, so it reaches Sentry. The
    /// production wiring calls this for BATCHES ONLY (N=1 keeps its direct provider
    /// await byte-identical — no extra async frame in its traces); the count&gt;1 filter
    /// is defense-in-depth, test-pinned. Static + logger-injected so
    /// <c>MainViewModelGateTests</c> executes the boundary against a capturing sink.
    /// </summary>
    internal static async Task<T> GenerateBatchItemLoggedAsync<T>(
        Func<Task<T>> generateAsync, int item, int count, CancellationToken batchToken, ILogger logger)
    {
        try
        {
            return await generateAsync();
        }
        catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
        {
            throw; // a cancel is not a failure — never logged as one
        }
        // An EXPECTED provider/network verdict (safety block, 4xx/5xx, timeout, DNS):
        // message only, no stack. The provider client already logged the reason and body
        // one line above, so `ex.ToString()` was dumping ~6 identical frames PER failed
        // version — 24 for an all-failed 4-version run (owner, 2026-07-28: "unnecessary
        // stack traces"). The predicate matches the image path's existing one (see
        // PersistFailedImageMarkerAsync) — ProviderApiException derives from
        // HttpRequestException, so typed provider errors are covered — plus
        // OperationCanceledException, which reaches here only as a NON-caller cancel
        // (a caller cancel returned above): that is an HTTP timeout, exactly the
        // provider noise the Warning policy exists to keep out of Sentry events.
        catch (Exception ex) when (count > 1 &&
            ex is HttpRequestException or InvalidOperationException or TimeoutException
                or OperationCanceledException or Helpers.InvalidApiKeyFormatException)
        {
            logger.Warning("Image generation item {Item}/{Count} failed: {LocalException}",
                item, count, $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        catch (Exception ex) when (count > 1)
        {
            // An UNEXPECTED type is a real defect, not provider noise: Error + the
            // attached exception, so the stack survives locally AND the event reaches
            // Sentry (whose BeforeSend redacts message, exception values, and stack
            // frames). Mirrors the else-branch precedent in PersistFailedImageMarkerAsync.
            logger.Error(ex, "Image generation item {Item}/{Count} failed", item, count);
            throw;
        }
    }

    /// <summary>
    /// The ONE commit-admission adapter (IMG-3, Codex rounds 5–6), shared by the success
    /// runner and the failed-marker path: false means the shutdown latch won (the caller
    /// goes silent — the quit drain owns the surface, today's commit-refused rule); a
    /// refusal WITHOUT the latch is a phase bug and throws — it must never masquerade as
    /// a quiet quit and silently discard paid output.
    /// </summary>
    private bool CommitImageAdmission()
    {
        if (_imageJob.TryBeginCommit())
            return true;
        if (_imageJob.IsShutdownRequested)
        {
            Logger.Information("Image commit refused — shutdown in progress");
            return false;
        }
        throw new InvalidOperationException("Image commit admission refused outside shutdown — phase bug");
    }

    /// <summary>Batch progress (IMG-4 parallel): value 0 = launch ("Generating 4
    /// images…"), k&gt;0 = SETTLED PROVIDER SUCCESSES ("k of n images generated…" —
    /// since IMG-4b each settled version's History row commits within the same pump
    /// turn, but a persist-side stop can leave later settlements uncommitted, so
    /// settled generations remain the honest live signal; the persist-side terminal
    /// copy says "saved" so the two
    /// vocabularies can't contradict). The pill via <see cref="ImageJobProgressChanged"/>,
    /// the Home status only when the pipeline is Idle (an active dictation owns
    /// StatusText; the job body is UI-thread-affine, so the check is not racy). N=1
    /// renders nothing — the runner still calls <c>reportProgress(1)</c>, but count ≤ 1
    /// maps to no text, so today's rendering is untouched.</summary>
    private void ReportImageJobProgress(int value, int count)
    {
        if (count <= 1)
            return;
        var text = value <= 0
            ? Helpers.ImageBatchPolicy.BatchStartText(count)
            : Helpers.ImageBatchPolicy.BatchProgressText(value, count);
        if (text == null)
            return;
        if (RecordingState == RecordingState.Idle)
            StatusText = text;
        RaiseImageJobProgress(text);
    }

    /// <summary>Raised at batch launch and per settled generation ("Generating n
    /// images…" / "k of n images generated…"). Never raised for N=1. Raised
    /// per-subscriber under try/catch — a throwing subscriber must never abort a paid
    /// batch (the RaiseHistoryChanged pattern).</summary>
    public event Action<string?>? ImageJobProgressChanged;

    private void RaiseImageJobProgress(string? text)
    {
        var handlers = ImageJobProgressChanged;
        if (handlers == null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<string?>)handler)(text); }
            catch (Exception ex) { Logger.Error(ex, "Image job progress subscriber threw"); }
        }
    }

    /// <summary>
    /// One batch item's persistence: metrics FIRST at N=1 (the provider call is paid and
    /// committed — usage counts regardless of History, today's ordering; batches count
    /// at generation settlement instead — IMG-4, see the generate delegate), PNG save, row
    /// write, ENH-6b/6f/6g reference copies. The row DISPOSITION is captured by closure
    /// from <c>TryWriteWithDispositionAsync</c> inside the
    /// <c>ReferencePersistence.PersistWithRowAsync</c> callback — that service's
    /// signature and its own query-only reconciliation stay untouched (the callback runs
    /// on every branch and the writer never throws non-cancellation, so the default only
    /// covers a hypothetical skipped-callback path, mapping to the AMBIGUOUS arm).
    /// </summary>
    /// <summary>
    /// The ONE builder behind every image History row — success and failure alike. It
    /// exists so a field can never be recorded on one path and forgotten on the other
    /// (the version count is exactly that kind of field), and so tests exercise the
    /// production shape instead of a hand-built row.
    ///
    /// <para>The image OPTIONS are explicit parameters rather than inferred, because the
    /// two paths legitimately differ: a success records
    /// <c>generation.Effective.*</c> — the per-model NORMALIZED options that actually
    /// rendered (IMG-2) — while a failure records the REQUESTED prompt options, since
    /// nothing rendered. Collapsing that into "read them off the prompt" would silently
    /// rewrite what successful rows claim.</para>
    /// </summary>
    internal static Models.Entities.TranscriptionRecord BuildImageRow(
        ImageGenJobRequest req,
        string enhancedTextMarker,
        string? imageFilePath,
        DateTime timestampUtc,
        string? aspect,
        string? sizeTier,
        string? quality,
        string? referencePath)
        => new()
        {
            // The effective prompt that actually generated the image (dialog-edited),
            // not the raw transcription — History renders Text as the image prompt.
            Text = req.Text,
            EnhancedText = enhancedTextMarker,
            WasEnhanced = true,
            PromptUsed = req.Prompt?.Id,
            Timestamp = timestampUtc,
            AudioFilePath = null,
            ModelName = req.TranscriptionModelName,
            EnhancementModelName = ImageJobModelLabel(req),
            Language = req.Language,
            ImageFilePath = imageFilePath,
            ImageAspect = aspect,
            ImageSizeTier = sizeTier,
            ImageQuality = quality,
            // The run's version count, recorded on EVERY image row (N=1 included, as an
            // explicit 1) so History's Redo can restore it — see the property's docs.
            ImageVersionCount = Helpers.ImageBatchPolicy.ClampCount(req.Count),
            ReferenceImagePath = referencePath,
        };

    private async Task<Helpers.ImageItemPersistResult> PersistImageItemAsync(
        ImageGenJobRequest req, Services.AIEnhancement.ImageGenerationResult generation)
    {
        var completedAtUtc = DateTime.UtcNow;
        return await Helpers.ImageItemPersistence.PersistAsync(
            applyBatchContract: req.Count > 1,
            readHistoryEnabled: () => _settings.GetBool(AppDefaults.IsHistoryEnabled, true),
            // IMG-4: batch metrics moved to generation settlement (see the generate
            // delegate) — the persist-time metric would miss every settled success
            // dropped after a persist-side stop. N=1 keeps the legacy persist-time
            // metric verbatim.
            recordMetricsAsync: () => req.Count > 1
                ? Task.CompletedTask
                : RecordLifetimeMetricsAsync(
                    Models.Entities.TranscriptionRecord.ImageGeneratedMarker, completedAtUtc, CancellationToken.None),
            saveImageAsync: () => SaveGeneratedImageAsync(generation.ImageBytes, generation.Kind),
            writeRowAsync: async (historyEnabled, imagePath) =>
            {
                var row = new Services.Data.RowWriteResult(Services.Data.RowWriteDisposition.AttemptedAmbiguous, null);
                var persist = await _referencePersistence.PersistWithRowAsync(
                    PairUsedReferences(generation.UsedReferences, req.References), historyEnabled,
                    async referencePath =>
                    {
                        row = await _historyWriter.TryWriteWithDispositionAsync(
                            BuildImageRow(
                                req,
                                Models.Entities.TranscriptionRecord.ImageGeneratedMarker,
                                imagePath,
                                completedAtUtc,
                                // IMG-2: what actually rendered (per-model normalized) —
                                // the SUCCESS side's options, deliberately different from
                                // the failure side's requested ones.
                                generation.Effective.Aspect,
                                generation.Effective.SizeTier,
                                generation.Effective.Quality,
                                referencePath), CancellationToken.None);
                        return row.Id;
                    }, CancellationToken.None);
                return new Helpers.ItemRowWrite(row, persist.FreshCopyClaim, persist.RearmSelections);
            },
            probeRowByImagePathAsync: path => _historyService.TryFindIdByImagePathAsync(path),
            tryDeleteImageAsync: TryDeleteUncommittedImageAsync);
    }

    /// <summary>Best-effort removal of a just-written PNG whose row provably did not
    /// commit (IMG-3 batch contract). The path was created by THIS run under Images —
    /// no foreign-path canonicalization applies.</summary>
    private static Task TryDeleteUncommittedImageAsync(string path)
        => Task.Run(() =>
        {
            try
            {
                File.Delete(path);
                Logger.Information("Removed uncommitted generated image: {Path}", path);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to remove uncommitted generated image");
            }
        });

    // Public (like StopTapAction) so the pure ResolveImageJobCancelPresentation rows can
    // name members in InlineData; the resolver itself stays internal.
    public enum ImageJobCancelPresentation { None, WarningPill, HidePill }

    /// <summary>
    /// Pure classification of the cancelled-job presentation (2026-07-18 incident). A busy
    /// pipeline keeps the surface (None — DecideRehydration's ActiveError branch ignores
    /// RecordingState, so gating at the source IS the protection). A quit-drain cancel ends
    /// HIDDEN: the drain runs before App sets <c>_isQuitting</c>, so the Drop gate can't
    /// suppress it, and suppressing only the event would still rehydrate the visible pill
    /// as the bare Idle "Ready". A user cancel gets the amber confirmation.
    /// </summary>
    internal static ImageJobCancelPresentation ResolveImageJobCancelPresentation(
        RecordingState state, bool shutdownRequested)
        => state != RecordingState.Idle ? ImageJobCancelPresentation.None
            : shutdownRequested ? ImageJobCancelPresentation.HidePill
            : ImageJobCancelPresentation.WarningPill;

    /// <summary>
    /// Terminal presentation for a cancelled job: the epoch advances (a pending older
    /// completion loses to the cancel, like every terminal presentation), the Home-page
    /// status stops saying "Generating image..." and the PILL gets told too — the job-end
    /// state change re-renders a visible idle pill as the bare "Ready" state, never Hidden,
    /// so without a presentation the user got no cancel feedback and (pre
    /// MiniRecorderStopRouting) the still-visible stop button turned a follow-up tap into a
    /// phantom recording (live incident 2026-07-18 17:04). The amber confirmation rides
    /// ShowError, which collapses the stop button. Accepted: a latent redo/retry dismiss
    /// timer resumed at job end can hide the confirmation early (rare, cosmetic —
    /// cross-timer coordination deliberately avoided); a commit-won quit still presents its
    /// pending success normally (out of scope, see the 2026-07-18 decision record).
    /// </summary>
    private void PresentImageJobCancelled()
    {
        MarkTerminalPresentation();
        switch (ResolveImageJobCancelPresentation(RecordingState, _imageJob.IsShutdownRequested))
        {
            case ImageJobCancelPresentation.WarningPill:
                EmitMiniRecorderError("Image generation cancelled", MiniRecorderTone.Warning);
                break;
            case ImageJobCancelPresentation.HidePill:
                StatusText = "Image generation cancelled";
                IsMiniRecorderVisible = false;
                break;
        }
    }

    /// <summary>"Provider/Model" label for the job's history rows — resolved AT DISPATCH
    /// (never re-read from live settings, which the user may change mid-generation).</summary>
    private static string ImageJobModelLabel(ImageGenJobRequest req)
        => $"{req.ResolvedProvider}/{req.ResolvedModel}";

    /// <summary>
    /// Failure tail of the job body: ENH-6f re-arm sanitation, failed-attempt retention
    /// (inside the Committing phase — reference copies are personal data too, Codex round 4),
    /// and the error-toned pending presentation. All writes uncancellable. Returns true when
    /// the pending presentation was queued and OWNS the claims + the request's reference
    /// lease; false = the caller's finally releases the lease.
    /// </summary>
    /// <summary>
    /// Failed-marker persistence (IMG-3 refactor of the old <c>PersistFailedImageJobAsync</c>):
    /// the ImageFailedMarker row + ENH-6c failed-attempt reference retention, returning the
    /// typed outcome for <c>ImageBatchExecutor</c> — which owns presentation, the claims
    /// list, and the reference lease (this method no longer queues or holds anything;
    /// same writes, same sanitation, pinned by the k=0 identity rows). A commit refusal
    /// under the shutdown latch returns <see cref="Helpers.MarkerPersistOutcome.ShutdownRefused"/>
    /// (executor goes silent — today's behavior); a wrong-phase refusal throws in
    /// <see cref="CommitImageAdmission"/>.
    /// </summary>
    private async Task<Helpers.MarkerPersist> PersistFailedImageMarkerAsync(ImageGenJobRequest req, Exception ex)
    {
        if (ex is HttpRequestException or InvalidOperationException or TimeoutException
            or Helpers.InvalidApiKeyFormatException)
            Logger.Warning("Image generation job failed: {ErrorType}: {ErrorMessage}", ex.GetType().Name, ex.Message);
        else
            Logger.Error(ex, "Image generation job failed");

        var sanitizedReferences = SanitizeReferencesAfterFailure(req.References, ex);

        if (!CommitImageAdmission())
            return new Helpers.MarkerPersist(
                Helpers.MarkerPersistOutcome.ShutdownRefused, null, sanitizedReferences, null);

        var row = await WriteFailedImageRowAsync(req, sanitizedReferences);

        // N=1 keeps its pre-IMG-4c terminal contract VERBATIM (recorded decision, plan
        // r2 finding 1): a row that doesn't commit degrades to "no retry target" while
        // the provider failure presents — MarkerPersistOutcome carries no disposition,
        // and a single image never promised a per-version row to break. Only a BATCH
        // supersedes its reason on a failed row write.
        return new Helpers.MarkerPersist(
            Helpers.MarkerPersistOutcome.Committed,
            row.RowId,
            row.RearmSelections,
            row.Claim);
    }

    /// <summary>
    /// IMG-4c: writes ONE <c>ImageFailedMarker</c> row for a failed generation — the
    /// shared body behind both the N=1 terminal marker and each batch version's own
    /// inline row, so the row shape, reference sanitation, and retention can never
    /// drift between them. Deliberately does NOT log and does NOT take commit
    /// admission: the N=1 wrapper already logs through <c>{ErrorMessage}</c> and each
    /// batch item is logged exactly once through the Sentry-redacted
    /// <c>{LocalException}</c> boundary in <see cref="GenerateBatchItemLoggedAsync"/>,
    /// so logging here would both duplicate and widen what reaches breadcrumbs (plan
    /// r1 finding 7); admission belongs to the runner's commit cycle for a batch.
    /// </summary>
    private async Task<Helpers.ItemFailureRowPersist> WriteFailedImageRowAsync(
        ImageGenJobRequest req, IReadOnlyList<ReferenceImageSelection>? sanitizedReferences)
    {
        var disposition = Services.Data.RowWriteDisposition.AttemptedAmbiguous;
        var failedPersist = await _referencePersistence.PersistFailedAttemptWithRowAsync(
            sanitizedReferences,
            _settings.GetBool(AppDefaults.IsHistoryEnabled, true),
            async referencePath =>
            {
                var write = await _historyWriter.TryWriteWithDispositionAsync(
                    BuildImageRow(
                        req,
                        Models.Entities.TranscriptionRecord.ImageFailedMarker,
                        imageFilePath: null,
                        DateTime.UtcNow,
                        // The REQUESTED options, deliberately — nothing rendered, so
                        // there are no normalized effective ones to record.
                        req.Prompt?.ImageAspect,
                        req.Prompt?.ImageSizeTier,
                        req.Prompt?.ImageQuality,
                        referencePath), CancellationToken.None);
                disposition = write.Disposition;
                return write.Id;
            }, CancellationToken.None);

        // Re-arm with the persisted copies on confirmed ownership, else the sanitized
        // in-memory selections (dead items dropped per index).
        return new Helpers.ItemFailureRowPersist(
            disposition switch
            {
                Services.Data.RowWriteDisposition.Committed => Helpers.ItemFailureRowOutcome.Committed,
                Services.Data.RowWriteDisposition.Disabled => Helpers.ItemFailureRowOutcome.HistoryDisabled,
                Services.Data.RowWriteDisposition.AttemptedAmbiguous => Helpers.ItemFailureRowOutcome.RowAmbiguous,
                _ => Helpers.ItemFailureRowOutcome.RowFailed,
            },
            failedPersist.HistoryId,
            failedPersist.RearmSelections,
            failedPersist.FreshCopyClaim);
    }

    /// <summary>
    /// Public wrapper for ClearRedoState. Called from App.xaml.cs when the model picker
    /// dialog is cancelled or cannot show models.
    /// </summary>
    public void ClearRedoStatePublic()
    {
        ClearRedoState();
        // If the redo picker was cancelled after capturing UIA focus, there will be no
        // RedoEnhancementAsync finally block to release the borrowed element.
        ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
        // PST-11: same reasoning for the retained recording element — the redo was abandoned, so
        // its finally will never run. Note this is the PUBLIC wrapper (picker cancel / no models),
        // NOT ClearRedoState itself: that one runs at redo START, before the paste that needs the
        // element, and releasing there would ship the whole feature as a silent no-op.
        _redoFocusRetention.Release();
        // Zombie-pill guard (2026-07-11 incident): opening the picker STOPPED the redo
        // dismiss timer, so after a cancel nothing else will ever hide the pill — and
        // the rehydration decision maps visible+idle+unarmed to VisibleState, not
        // Hidden. The cancel itself must dismiss the pill when nothing owns it.
        if (ShouldHideOrphanedPill(RecordingState, ArmedAffordance, IsMiniRecorderVisible))
            IsMiniRecorderVisible = false;
    }

    /// <summary>
    /// Pure rule for the picker-cancel teardown: hide the pill iff it is visible with
    /// nothing left to own it — pipeline idle AND no armed affordance (a retry that
    /// took the surface over stays; an active pipeline's state always wins). The
    /// App-side transient error pill is unaffected by the resulting visibility flip:
    /// <c>MiniRecorderLifecycleState.DecideRehydration</c> checks ActiveError BEFORE
    /// visibility, so an unexpired error keeps rendering.
    /// (2026-07-11 zombie-pill incident №1: error redo pill → picker opened → timer
    /// stopped → cancel cleared state but nothing hid the window → a permanently
    /// visible, success-toned redo button with a null context.)
    /// The dead-redo-click sweep in <see cref="RequestModelPicker"/> deliberately does
    /// NOT use this predicate since zombie №2 (2026-07-11): the visibility conjunct
    /// reads the ViewModel flag, and that flag being desynced (false while the window
    /// shows) is exactly the state the click-time sweep must recover from — the click
    /// itself is the visibility evidence there.
    /// </summary>
    internal static bool ShouldHideOrphanedPill(
        RecordingState recordingState,
        ArmedAffordance armedAffordance,
        bool isMiniRecorderVisible)
        => recordingState == RecordingState.Idle
           && armedAffordance == ArmedAffordance.None
           && isMiniRecorderVisible;

    /// <summary>
    /// Re-run transcription from the failure-retained WAV (REL-12). Entered from the
    /// retry pill's button and from the redo-last hotkey (via
    /// <see cref="RedoOrRetryLastAsync"/>). Re-reads CURRENT settings (keys, global
    /// model, vocabulary) so fix-key / switch-provider works; the failed attempt's
    /// App-Mode overrides ride the context (matching what a fresh recording in the
    /// same target app would resolve). Guard order matters: the update-apply refusal
    /// leaves the retry ARMED (the user can retry after the update restart is
    /// declined... the retained WAV is drained at update-quit either way), while the
    /// missing-file case tears it down.
    ///
    /// <para><b>TRN-17: this now opens a picker first</b> — model + recognition language, for this
    /// re-run only. Three consequences worth stating here rather than only at the sites: the paste
    /// target and focus element are captured BEFORE the dialog (showing it foregrounds VoiceWink,
    /// and the capture is <c>GetForegroundWindow</c>); the volatile guards run TWICE, because the
    /// dialog is open for an unbounded time and a recording started under it deletes this WAV; and
    /// a CANCEL deliberately changes nothing at all.</para>
    /// </summary>
    public async Task RetryTranscriptionAsync()
    {
        var ctx = _redoCoordinator.LastRetryContext;
        if (ctx == null)
        {
            Logger.Information("Retry requested but no retry context is armed");
            return;
        }

        if (App.IsExclusiveMaintenanceActive())
        {
            const string updateMsg = "Update in progress — please wait";
            Logger.Information("Retry blocked: an update is being applied.");
            // A transient maintenance refusal, not a failure — a Warning auto-hides so it can't
            // linger indefinitely if the apply ends without another pill (Codex review 2026-07-20).
            EmitMiniRecorderError(updateMsg, MiniRecorderTone.Warning);
            return;
        }

        if (RecordingState != RecordingState.Idle)
        {
            Logger.Information("Retry blocked — pipeline is busy (state={State})", RecordingState);
            return;
        }

        // Moved AHEAD of ClearRetryState by TRN-17 (it used to sit just after). Same observable
        // outcome — torn down, Error pill — only earlier, because there is now a dialog in
        // between and opening one over a recording that no longer exists is a dead end.
        if (!File.Exists(ctx.WavPath))
        {
            Logger.Warning("Retry requested but the retained recording is gone");
            ClearRetryState(deleteWav: false);
            EmitMiniRecorderError("Recording no longer available", MiniRecorderTone.Error);
            return;
        }

        // TRN-17: what this retry COULD run with, decided before the dialog slot is taken so a
        // refusal never holds it.
        //
        // In its own try because it is the one leg here that touches the filesystem
        // (GetDownloadedModels enumerates the Models directory) and the one that can throw from a
        // catalogue fault (TryResolve throws when two runtimes claim a name). This whole method is
        // fire-and-forget from the pill, so an escaping exception would be swallowed with nothing
        // logged and no pill (both diff reviewers).
        IReadOnlyList<TranscriptionModelInfo> runnable;
        try
        {
            runnable = Helpers.RunnableTranscriptionModels.Resolve(
                _modelDownloader.GetDownloadedModels(),
                _transcriptionRegistry.HasKeyFor,
                // Health, not ownership: a downloaded Parakeet on a build with the lever off (or a
                // CPU under its floor) is installed and cannot run, and PrepareOutcome.Unavailable
                // only arrives after the user has picked it.
                m => _localModels.TryResolve(m.Name)?.Runtime.IsAvailable ?? false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not determine which transcription models can run — retry stays armed");
            EmitMiniRecorderError("Couldn't list transcription models", MiniRecorderTone.Warning);
            return;
        }

        if (runnable.Count == 0)
        {
            // A TRANSIENT refusal, so Warning — the house rule the maintenance refusal above
            // follows for the same reason. An Error pill PERSISTS until dismissed
            // (MiniRecorderTimings.DeadlineFor), and it would sit over the still-armed Retry
            // button, which is the user's route back once they add a key or download a model.
            // (An earlier revision used Error and claimed parity with the maintenance refusal —
            // which is Warning. Kimi caught the comment asserting the opposite of the code.)
            Logger.Warning("Retry blocked — no transcription model can run (nothing runnable, no API key)");
            EmitMiniRecorderError("No transcription model available", MiniRecorderTone.Warning);
            return;
        }

        // One ContentDialog per XamlRoot, so this shares the enhancement picker's gate. Taken
        // BEFORE any state mutation, for the reason recorded at the field: by the time a
        // second entrant could be refused downstream it has already overwritten the focus and
        // target state the OPEN dialog still owns.
        if (Interlocked.CompareExchange(ref _enhancementPickerActive, 1, 0) != 0)
        {
            Logger.Information("Retry picker request ignored — another dialog is open");
            return;
        }

        TranscriptionRetrySelection? selection;
        try
        {
            // Recency (REL-12): opening the retry picker IS the user's most recent intent, so the
            // redo-last hotkey routes here and then no-ops on the CAS above rather than launching
            // the OTHER affordance behind the open dialog. IsRetryAvailable stays true — unlike
            // the redo path, nothing here is hiding a pill.
            ArmedAffordance = ArmedAffordance.Retry;

            // Capture BEFORE the dialog, and this ordering is the whole reason TRN-17 is a
            // capture-and-paste change: showing it calls RestoreMainWindow(), and
            // CaptureFreshPasteTarget is GetForegroundWindow() — captured after, every retry
            // would paste into VoiceWink. The redo picker already captures ahead of its dialog
            // for exactly this reason.
            //
            // REDO ownership, not Recording: a cancel then releases with Release(Redo), which
            // no-ops if a real recording has since taken the slot. Recording ownership has no
            // such release — a cancel would destroy the live recording's capture. Confirm
            // retags it via AdoptForRecording so the pipeline's finally frees it.
            if (ctx.TargetWindow != IntPtr.Zero)
            {
                // A FRESH capture is only meaningful while the user is somewhere else. After a
                // CANCELLED picker VoiceWink is the foreground window — the dialog restored it and
                // nothing put the editor back — so a second Retry tap would capture OURSELVES and
                // paste the transcript into VoiceWink (Codex diff review; the cancel path had made
                // "cancel changes nothing" false for the tap after it).
                //
                // The pill itself never causes this: it is WS_EX_NOACTIVATE, so tapping it cannot
                // take the foreground and an ordinary first tap still captures the real editor.
                // But the dialog is NOT the only route (Kimi verification) — the redo-last HOTKEY
                // fires whatever is in front, so a user who browsed History after the failure and
                // then pressed it lands here on a FIRST tap. The clipboard answer is right there
                // too: we have no evidence about where they want the text, and guessing is what
                // the branch below refuses to do.
                if (IsForegroundOurOwnProcess())
                {
                    // Degrade to CLIPBOARD, and specifically do NOT re-adopt ctx.TargetWindow.
                    // Two things are wrong with a stale handle and only one is obvious: the
                    // window may be gone, but worse, Windows RECYCLES HWNDs — rebuilding an
                    // identity snapshot around an old handle would hand PasteTargetValidation a
                    // fresh, self-consistent identity for whatever now owns it, and it would
                    // validate. That is a private dictation pasted into another application
                    // (Codex verification round). A zero handle routes to the honest clipboard
                    // fallback: the user gets "Text copied" and pastes it themselves.
                    Logger.Information("Retry target: VoiceWink is foreground — no external target, using the clipboard");
                    _targetWindowHandle = IntPtr.Zero;
                    _pasteTargetSnapshot = null;
                    ResetPasteTargetAppName();
                }
                else
                {
                    CaptureFocusedElementForRedo();
                }
            }
            else
            {
                // Targetless capture (edge): zero handle → paste gates degrade to the
                // clipboard fallback honestly.
                _targetWindowHandle = IntPtr.Zero;
                _pasteTargetSnapshot = null;
                ResetPasteTargetAppName();
            }

            var attemptModel = Helpers.RetryAttemptResolution.Model(
                userPick: ctx.UserModelPick,
                appModeOverride: ctx.ModelOverride,
                globalSelectedModel: _settings.GetString(
                    AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel));
            var attemptLanguage = Helpers.RetryAttemptResolution.Language(
                userPick: ctx.UserLanguagePick,
                capturedLanguage: ctx.LanguageOverride,
                globalLanguage: _settings.GetString(AppDefaults.SelectedLanguage, "auto"));

            var handler = ShowTranscriptionRetryPickerRequested;
            if (handler == null)
            {
                // Unreachable in production (App subscribes at startup); reached by tests and by a
                // teardown race. Retrying with today's semantics beats a dead pill — the owner's
                // "always open the dialog" rule is about the shipped path, not about refusing to
                // work when the UI layer is gone.
                Logger.Warning("No retry picker handler — retrying with the current settings");
                selection = null;
            }
            else
            {
                selection = await handler.Invoke(new TranscriptionRetryPickerRequest(
                    Models: runnable,
                    PreselectedModelName: Helpers.RunnableTranscriptionModels.Preselect(runnable, attemptModel),
                    RequestedModelDisplayName: Helpers.ModelDisplayName.Resolve(attemptModel),
                    PreselectedLanguage: attemptLanguage));

                if (selection == null)
                {
                    // CANCEL DOES NOTHING, and that asymmetry with the redo picker's
                    // ClearRedoStatePublic() is deliberate — do not "fix" it by adding a teardown,
                    // which would destroy the user's only copy of the recording. The redo cancel
                    // exists because opening THAT picker stops the redo dismiss timer, so nothing
                    // else would ever hide its pill (the 2026-07-11 zombie-pill incident). This
                    // picker stops no timer: an Error-tone retry pill is UntilDismissed.
                    //
                    // Residual, same as the redo picker's: a WARNING-tone retry pill (echo block,
                    // no-speech block) is TimedVisible 10 s, so a dialog left open longer lets it
                    // auto-dismiss; the retry stays armed and reachable via the redo-last hotkey.
                    Logger.Information("Retry picker cancelled — retry stays armed");
                    ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // The pill path is fire-and-forget (App discards the task), so an escaping exception
            // is a real surface. Treated as a cancel: armed, WAV intact, capture released.
            Logger.Error(ex, "Retry picker failed — retry stays armed");
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _enhancementPickerActive, 0);
        }

        // OWNERSHIP FIRST, ahead of every other post-dialog guard. The dialog was open for an
        // unbounded time; a recording started under it clears this retry and, if it fails, arms a
        // DIFFERENT one. Everything below acts on shared state — the retry slot, the paste target,
        // the focus slot — so a stale completion must touch none of it.
        //
        // File.Exists is NOT a freshness check and cannot stand in for this (Codex diff review):
        // ClearRetryState's delete is fire-and-forget on a thread pool task, so our WAV can still
        // be on disk while a newer retry owns the slot. Clearing then would retire the user's most
        // recent failed dictation, and proceeding would run our audio against the newer retry's
        // paste target. Identity, not value equality — a re-armed value-equal context is a
        // different arming.
        if (!ReferenceEquals(_redoCoordinator.LastRetryContext, ctx))
        {
            Logger.Information("Retry picker completed for a superseded retry — abandoning");
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
            return;
        }

        // Re-run the VOLATILE guards: all three can have changed while the dialog was open (the
        // RedoEnhancementAsync precedent). The File.Exists re-check is not redundant with the one
        // before the dialog — a recording started under it deletes this very WAV.
        if (App.IsExclusiveMaintenanceActive())
        {
            Logger.Information("Retry blocked after the picker: an update is being applied.");
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
            EmitMiniRecorderError("Update in progress — please wait", MiniRecorderTone.Warning);
            return;
        }

        if (RecordingState != RecordingState.Idle)
        {
            Logger.Information("Retry blocked after the picker — pipeline is busy (state={State})", RecordingState);
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
            return;
        }

        if (!File.Exists(ctx.WavPath))
        {
            Logger.Warning("The retained recording went away while the retry picker was open");
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
            ClearRetryState(deleteWav: false);
            EmitMiniRecorderError("Recording no longer available", MiniRecorderTone.Error);
            return;
        }

        if (selection != null)
            ctx = ctx with { UserModelPick = selection.ModelName, UserLanguagePick = selection.Language };

        // Hand the Redo-owned capture to the run that consumes it: the pipeline's finally does an
        // owner-scoped Release(Recording), which would not free a Redo-owned slot.
        _focusSlot.AdoptForRecording();

        // Double-invocation guard for the confirm→pipeline leg; the WAV's ownership transfers to
        // this pipeline run (deleteWav: false — the run's finally / catch decides its fate). The
        // dialog window itself is guarded by the CAS above, and _stopSingleFlight in
        // StopAndTranscribeAsync remains the backstop for overlapping stops.
        ClearRetryState(deleteWav: false);

        // UI-7: a retry never runs StartRecordingAsync, so the latch would still carry the PREVIOUS
        // recording's answer. The retry decides its own target above (TRN-17 zeroes it when
        // VoiceWink is in front), and the outcome would be right either way — but the pill would
        // read "Dialog was open" about a dialog that closed minutes ago (Kimi round 3). The latch
        // describes one capture; a capture that did not happen owns no value.
        _pasteSuppressedByPicker = false;

        Logger.Information("Retrying transcription from retained recording");
        await StopAndTranscribeAsync(ctx);
    }

    /// <summary>
    /// Redo-last hotkey router (REL-12): recency-first with existence fallback.
    /// Whichever affordance armed LAST wins (a user who ignored a failure and then
    /// did History redos gets the hotkey back on their redo — an armed-forever retry
    /// must not capture the hotkey unboundedly); the other remains the fallback while
    /// it still exists.
    /// </summary>
    public async Task RedoOrRetryLastAsync()
    {
        var retryArmed = _redoCoordinator.LastRetryContext != null;
        var redoArmed = _redoCoordinator.LastContext != null;

        if (ArmedAffordance == ArmedAffordance.Retry && retryArmed)
            await RetryTranscriptionAsync();
        else if (ArmedAffordance == ArmedAffordance.Redo && redoArmed)
            RequestModelPicker();
        else if (retryArmed)
            await RetryTranscriptionAsync();
        else
            RequestModelPicker(); // logs "no recent enhancement" when nothing is armed
    }

    /// <summary>
    /// Request the model picker dialog. Called from the pill's redo action (via App →
    /// <see cref="RedoOrRetryLastAsync"/>) and from the redo-last hotkey. The dialog is shown on the
    /// main window (which has XamlRoot).
    ///
    /// <para>The old "orphaned/zombie pill" recovery is gone with the ERR-PERSIST controller refactor:
    /// the redo button is visible only while the controller holds the affordance slot, which is
    /// armed iff <c>LastContext</c> is set — a dead redo click with nothing armed can no longer
    /// happen (button-visible ⟺ armed), so a null context is simply a hotkey no-op.</para>
    /// </summary>
    public void RequestModelPicker()
    {
        var ctx = _redoCoordinator.LastContext;
        if (ctx == null)
        {
            Logger.Information("Redo requested but no recent enhancement available");
            return;
        }

        RequestModelPickerWithContext(ctx);
    }

    /// <summary>
    /// IMG-1: open the image-generation dialog with an EMPTY prompt — the text-first
    /// entry point (tray menu, AI Enhancement page button, generate-image hotkey).
    /// Reuses the regenerate dialog + pipeline via a synthetic context: no lineage,
    /// zero TargetWindow (result goes to clipboard + Images + History, no paste
    /// attempt — same semantics as a History-page redo). Provider/model/options
    /// default to the current image-generation selections. All picker guards apply
    /// (pipeline-busy, one-dialog reentrancy, cancel teardown incl. the orphan hide).
    /// </summary>
    public void RequestNewImageGeneration()
    {
        // Same LIC-3 gate as the recording entry (Codex IMG-1 review): image generation
        // consumes provider APIs, so a blocked license blocks this entry too — the
        // startup redirect alone doesn't cover mid-session tray/button/hotkey use.
        if (!IsLicenseAllowingRecording(out var blockedMessage, out _))
        {
            Logger.Information("New image generation blocked by license gate: {Message}", blockedMessage);
            EmitLicenseRefusalPill(blockedMessage);
            return;
        }

        RequestModelPickerWithContext(new RedoContext(
            RawText: "",
            EnhancedText: null,
            Prompt: null,
            WasImageGeneration: true,
            TargetWindow: IntPtr.Zero,
            WasPushToTalk: false,
            TranscriptionModelName: "",
            // IMG-1 text-first entry: no transcription behind this chain, so there is no
            // language to report. Stated explicitly rather than defaulted.
            TranscriptionLanguage: null,
            IsNewGeneration: true));
    }

    /// <summary>
    /// ENH-6: open the image-generation dialog pre-seeded with a reference image from
    /// the app's own Images folder (History page "Iterate" on a successful image row).
    /// Same synthetic-context semantics as <see cref="RequestNewImageGeneration"/> —
    /// empty prompt, no lineage, clipboard-only result. The path is validated HERE as
    /// an AppImages-origin selection (MediaPathPolicy containment + existence) so a
    /// stale or corrupt history row degrades to the plain no-reference dialog instead
    /// of carrying a dead path; the service re-validates at generation time either way.
    /// ENH-6d (2026-07-12): the source row's generation settings ride the synthetic
    /// context as Previous* so the dialog takes over the EXACT provider, model,
    /// aspect, size, and quality that produced the source image — "another one like
    /// this" must start from this one's settings, not the current defaults.
    /// </summary>
    public void RequestNewImageGenerationWithReference(
        string imagePath,
        AIProvider? previousProvider = null,
        string? previousModel = null,
        string? previousImageAspect = null,
        string? previousImageSizeTier = null,
        string? previousImageQuality = null)
    {
        if (!IsLicenseAllowingRecording(out var blockedMessage, out _))
        {
            Logger.Information("New image generation blocked by license gate: {Message}", blockedMessage);
            EmitLicenseRefusalPill(blockedMessage);
            return;
        }

        IReadOnlyList<ReferenceImageSelection>? references = null;
        if (Helpers.ReferenceImagePolicy.IsUsableAppImagePath(imagePath, AppPaths.ImagesDir, out var fullPath))
        {
            references = new[] { new ReferenceImageSelection(fullPath, ReferenceImageOrigin.AppImages) };
        }
        else
        {
            Logger.Information("Iterate requested but the source image is unusable — opening without a reference");
        }

        RequestModelPickerWithContext(BuildNewGenerationContext(
            previousProvider, previousModel,
            previousImageAspect, previousImageSizeTier, previousImageQuality,
            references));
    }

    /// <summary>
    /// ENH-6d: the pure synthetic-context mapping for the reference-seeded new-image
    /// entry — internal static so a test can pin that EVERY source-row setting
    /// (provider, model, aspect, size, quality) and the references actually land on
    /// the context the dialog reads its Previous* seeds from. The reference list is
    /// SNAPSHOTTED here (ENH-6f boundary rule, Codex diff review): a caller-retained
    /// mutable list mutated later would desynchronize the armed live claims from what
    /// the generation reads and break index pairing.
    /// </summary>
    internal static RedoContext BuildNewGenerationContext(
        AIProvider? previousProvider,
        string? previousModel,
        string? previousImageAspect,
        string? previousImageSizeTier,
        string? previousImageQuality,
        IReadOnlyList<ReferenceImageSelection>? references)
        => new(
            RawText: "",
            EnhancedText: null,
            Prompt: null,
            WasImageGeneration: true,
            TargetWindow: IntPtr.Zero,
            WasPushToTalk: false,
            TranscriptionModelName: "",
            // IMG-1 text-first entry: no transcription behind this chain, so there is no
            // language to report. Stated explicitly rather than defaulted.
            TranscriptionLanguage: null,
            PreviousProvider: previousProvider,
            PreviousModel: previousModel,
            PreviousImageAspect: previousImageAspect,
            PreviousImageSizeTier: previousImageSizeTier,
            PreviousImageQuality: previousImageQuality,
            IsNewGeneration: true,
            References: references?.ToArray());

    /// <summary>
    /// Open the model picker dialog with an explicit context.
    /// Used by History page redo to share the same dialog as MiniRecorder/hotkey redo.
    /// </summary>
    public void RequestModelPickerWithContext(RedoContext ctx)
    {
        // F19: redo / new-image generation writes SQLite rows, Images, and References —
        // refuse to START it while an exclusive maintenance op (update apply or data erasure)
        // is in flight, same as the recording/transcribe/model-download start paths.
        if (App.IsExclusiveMaintenanceActive())
        {
            Logger.Information("Redo/new-image blocked — exclusive maintenance in progress");
            return;
        }

        // Don't open the picker while a pipeline is active — redo must not interrupt work
        if (RecordingState != RecordingState.Idle)
        {
            Logger.Information("Redo blocked — pipeline is busy (state={State})", RecordingState);
            return;
        }

        // IMG-BG: one image job at a time (owner decision 2026-07-16). Refuse the IMAGE-
        // flavored picker while a job runs — text redo is never job-gated. Check-then-recheck:
        // DispatchImageRedoAsync re-reserves at dispatch, so a job starting while the dialog
        // sits open is still refused there.
        if (ctx.WasImageGeneration && _imageJob.IsRunning)
        {
            RequestImageJobRefusalNotice();
            return;
        }

        // Reentrancy gate BEFORE any state mutation (Codex review 2026-07-10): WinUI
        // allows one ContentDialog per XamlRoot, so a duplicate request while the picker
        // is open/opening must be a true no-op. A later (App-side) guard is not enough —
        // by then this method has already overwritten the redo context, target label,
        // and captured focus slot the OPEN dialog still owns, so a second retry click
        // could redirect the first dialog's paste. Released when the handler's dialog
        // task completes (any outcome), below.
        if (Interlocked.CompareExchange(ref _enhancementPickerActive, 1, 0) != 0)
        {
            Logger.Information("Model picker request ignored — another enhancement dialog is open");
            return;
        }

        // Set context so RedoEnhancementAsync can use it. (The pill's own dismiss timing is the
        // controller's now — if the picker sits open past the affordance's TimedVisible window the
        // pill simply auto-dismisses; the captured context here still drives the redo either way.)
        // ENH-6e claim-before-publish, placed AFTER the reentrancy CAS above so a
        // rejected duplicate request stays a true no-op (it must not displace the
        // open dialog's armed claims).
        _referencePersistence.SetArmedLiveReferences(PathsOf(ctx.References));
        _redoCoordinator.LastContext = ctx;
        // Opening a redo picker IS the user's most recent intent (REL-12 recency):
        // without this, a stale armed retry would capture the redo-last hotkey while
        // the picker is open and launch an unrelated retry pipeline that then blocks
        // the picker's own redo. IsRedoAvailable stays false, so rendering is
        // unaffected (the affordance branch requires both); a picker cancel falls the
        // affordance back via ClearRedoState (→ Retry if one is still armed).
        ArmedAffordance = ArmedAffordance.Redo;

        // PILL-1 invariant: clear the target label BEFORE the conditional capture
        // below. A History-page redo passes a zero target, skips the capture, and
        // still shows the pill (Enhancing) — it must not wear the PREVIOUS
        // recording's app name. Redo-with-target re-resolves inside the capture;
        // the generation bump fences out any still-running resolver.
        ResetPasteTargetAppName();

        // Capture fresh UIA focus at redo invocation time. Redo pastes wherever the
        // user is now; this avoids threading the original recording focus through
        // RedoContext and mirrors the normal recording-start capture.
        if (ctx.TargetWindow != IntPtr.Zero)
            CaptureFocusedElementForRedo();

        // Fire-and-forget: handler has its own try/catch.
        var handler = ShowModelPickerRequested;
        if (handler == null)
        {
            Interlocked.Exchange(ref _enhancementPickerActive, 0);
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
            return;
        }
        try
        {
            var task = handler.Invoke(ctx.WasImageGeneration, ctx);
            if (task == null)
            {
                Interlocked.Exchange(ref _enhancementPickerActive, 0);
            }
            else
            {
                // Release the picker gate when the dialog task completes — success,
                // cancel, or fault alike (fault keeps the pre-gate Error log).
                _ = task.ContinueWith(t =>
                {
                    Interlocked.Exchange(ref _enhancementPickerActive, 0);
                    if (t.Exception != null)
                        Logger.Error(t.Exception, "Unhandled exception in ShowModelPickerRequested handler");
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ShowModelPickerRequested handler threw synchronously");
            Interlocked.Exchange(ref _enhancementPickerActive, 0);
            // Same teardown as a picker cancel — clear + release + orphan-hide (the
            // dismiss timer was already stopped above, so this path would otherwise
            // leave the same zombie pill; Codex diff review 2026-07-11).
            ClearRedoStatePublic();
        }
    }

    /// <summary>
    /// Re-run enhancement with a different model. Called from the model picker dialog.
    /// Accepts an optional captured context to avoid a timer race with the coordinator.
    /// </summary>
    public async Task RedoEnhancementAsync(string modelId, AIProvider? providerOverride = null, RedoContext? capturedContext = null)
    {
        var ctx = capturedContext ?? _redoCoordinator.LastContext;
        if (ctx == null)
        {
            Logger.Warning("Redo requested but no RedoContext available");
            return;
        }

        // Don't start redo if a pipeline is already active
        if (RecordingState != RecordingState.Idle)
        {
            Logger.Information("Redo blocked — pipeline is busy (state={State})", RecordingState);
            return;
        }

        // F19: re-check exclusivity HERE, not only at the picker (RequestModelPickerWithContext).
        // The picker's check-then-await-dialog leaves a window where an erasure / update apply can
        // start before this executes — and this is where the DB/Images/References writes begin. The
        // redo context stays armed (no ClearRedoState yet) so the affordance survives the refusal.
        if (App.IsExclusiveMaintenanceActive())
        {
            Logger.Information("Redo/new-image blocked — exclusive maintenance in progress");
            return;
        }

        // IMG-BG: the image arm DISPATCHES to the background job instead of running inline —
        // a multi-minute regenerate must not hold RecordingState.Enhancing. A refusal inside
        // the dispatch (lost the reservation race) keeps the armed redo intact, mirroring the
        // F19 refusal above. Text redo continues below, unchanged and never job-gated.
        if (ctx.WasImageGeneration)
        {
            await DispatchImageRedoAsync(ctx, modelId, providerOverride);
            return;
        }

        // ENH-6e: hold ONE composite in-flight claim on the context's references for
        // the WHOLE run — taken BEFORE ClearRedoState releases the armed claims
        // (gapless handoff), spanning the CTS work and both catch handlers (the
        // ENH-6c failed-retention re-reads must still find the files after a mid-run
        // row delete), released at method exit. Fresh copies persisted during the run
        // arrive with their own claims (collected below, disposed in the finally).
        // (Text-redo contexts normally carry no references — the lease is a no-op then.)
        using var referenceLease = _referencePersistence.BeginInFlightLiveReferences(PathsOf(ctx.References));
        var freshCopyClaims = new List<IDisposable>(2);

        // Clear redo state immediately to prevent double-invocation
        ClearRedoState();

        // The token is hoisted ABOVE the try: the failure catch below needs it for
        // failed-attempt retention and for routing a retention OCE through the same
        // cancellation cleanup as the sibling catch (Codex final check R2/R3).
        _transcriptionCts?.Cancel();
        _transcriptionCts?.Dispose();
        _transcriptionCts = new CancellationTokenSource();
        var ct = _transcriptionCts.Token;

        // Text-redo paste presentation. Hoisted ABOVE the try (PST-6, Codex round-4 C2)
        // so the cancellation catch can preserve a declined paste's warning instead of
        // replacing it with "Redo cancelled" — the post-paste bookkeeping is what got
        // cancelled, and the delivery warning is exactly what the user must still see.
        var redoTextPasteLanded = true;
        string? redoClipboardFallback = null;
        // Tone rides WITH the fallback message (Codex final check): computed once at the
        // paste/copy site from the actual result, never re-derived downstream.
        var redoTextTone = MiniRecorderTone.Success;

        try
        {
            RecordingState = RecordingState.Enhancing;
            StatusText = "Re-enhancing...";
            IsMiniRecorderVisible = true;

            // Resolve actual provider for logging/history
            var effectiveProvider = providerOverride ?? _enhancement.SelectedProvider;

            var redoTargetWindow = ResolveRedoTargetWindow(ctx.TargetWindow, _targetWindowHandle);
            var focusedElement = await _focusSlot.ResolveAsync();
            // Back to "Done" (owner, 2026-08-10), reversing the 2026-07-25 artifact-naming
            // rename to "Text pasted": the success path is frequent and expected, so this is
            // a confirmation, not a report of what happened.
            //
            // THE RULE: "Done" means delivery is CERTAIN — the paste landed. Every outcome
            // that only reached the clipboard keeps naming the destination ("Text copied" in
            // the no-target branch below, "Image on clipboard"), because there the wording is
            // the user's only hint that a manual Ctrl+V is still needed. So exactly the two
            // Pasted arms say "Done" — this one and ResolveImagePasteRedoPresentation's.
            //
            // Keep this, the plain-success branch, and the ResolvePostPasteRedoPresentation
            // default in step.
            var redoTextSuccessStatus = "Done";

            var vocabularyTerms = (await _customVocabulary.GetVocabularyContextAsync(ct)).Terms;
            var enhanced = await _enhancement.EnhanceWithModelAsync(
                ctx.RawText, ctx.Prompt, modelId, vocabularyTerms, providerOverride, ct);

            // Whitespace check (redo path) — same reasoning as the main pipeline.
            if (string.IsNullOrWhiteSpace(enhanced))
                enhanced = ctx.RawText;

            LastTranscription = enhanced;

            if (redoTargetWindow != IntPtr.Zero)
            {
                // Paste at cursor (normal redo flow with valid target window)
                var appendSpace = _settings.GetBool(AppDefaults.AppendTrailingSpace, true);
                var textToPaste = appendSpace ? enhanced + " " : enhanced;

                IsMiniRecorderVisible = false;
                await Task.Delay(100);

                // PST-11 (owner decision, 2026-08-16): a redo NEVER sends Enter.
                //
                // The hazard is only reachable BECAUSE this card fixes the paste. Dictate into a
                // chat with push-to-talk + send-Enter, the message goes out with a flaw, redo — and
                // a rescued paste would now land in the composer and SEND A SECOND MESSAGE to the
                // recipient. Today's clipboard decline is what has been shielding the user from
                // that, so restoring the paste without this line would ship a new way to send
                // something nobody re-read.
                //
                // Chosen over "suppress only when the retained element was used": that would make
                // redo behave differently depending on an internal path the user cannot see. A
                // correction gets reviewed before it goes out — the one keystroke is cheap, an
                // unintended send is not.
                var redoPaste = await PasteWithOptionalEnterAsync(
                    textToPaste, willSendEnter: false, redoTargetWindow, focusedElement,
                    fallbackFocusedElement: _redoFocusRetention.ElementFor(redoTargetWindow));
                if (!redoPaste.Succeeded)
                {
                    // PILL-3: don't early-return a short pill. Capture message + tone and
                    // fall through to arm an attention redo (which is also the retry
                    // affordance) — mirroring the main pipeline. Declines are amber
                    // warnings; a clipboard-set failure stays a red error.
                    redoTextPasteLanded = false;
                    (redoClipboardFallback, redoTextTone) = Helpers.PasteResultPresentation.Present(redoPaste);
                }
            }
            else
            {
                // No target window (e.g. history redo) — copy to clipboard only. Keeps naming
                // the destination: "Done" is reserved for a paste that actually LANDED (owner,
                // 2026-08-10), and nothing was pasted here. Same rule as "Image on clipboard".
                redoTextSuccessStatus = "Text copied";
                if (!await _clipboard.SetClipboardAsync(enhanced))
                {
                    // Copy-only failure: the text is NOT on the clipboard — a real error.
                    // Shares the paste path's constant so the same failure never reads two ways.
                    redoTextPasteLanded = false;
                    redoClipboardFallback = Helpers.PasteResultPresentation.ClipboardFailedMessage;
                    redoTextTone = MiniRecorderTone.Error;
                }
            }

            var completedAtUtc = DateTime.UtcNow;
            await RecordLifetimeMetricsAsync(enhanced, completedAtUtc, ct);

            _lastTranscriptionHistoryId = await _historyWriter.TryWriteAsync(
                new Models.Entities.TranscriptionRecord
                {
                    Text = ctx.RawText,
                    EnhancedText = enhanced,
                    WasEnhanced = true,
                    PromptUsed = ctx.Prompt?.Id,
                    Timestamp = completedAtUtc,
                    ModelName = ctx.TranscriptionModelName,
                    EnhancementModelName = $"{effectiveProvider}/{modelId}",
                    // The ORIGINAL attempt's language. A redo re-runs enhancement on existing text
                    // and never re-transcribes, so re-reading the LIVE setting recorded a different
                    // value the moment any override applied — or the moment the user changed the
                    // setting between recording and redo.
                    Language = ctx.TranscriptionLanguage
                }, ct);

            // Ensure MiniRecorder is visible for the redo state. (Inert bookkeeping since the ERR-PERSIST
            // controller refactor — the redo pill actually appears when ArmRedo below fires
            // AffordanceArmed → the controller publishes the affordance; the pipeline was hidden pre-paste.)
            IsMiniRecorderVisible = true;

            // Set up redo context so the user can redo again with a different model.
            // Present via the shared PILL-3 helper so a declined text paste is a
            // warning-toned attention redo, not a success-looking one (and a clipboard
            // failure stays a red error).
            var (redoStatus, redoToneResolved) =
                ResolvePostPasteRedoPresentation(redoTextPasteLanded, redoClipboardFallback, redoTextTone, redoTextSuccessStatus);
            StatusText = redoStatus;
            RecordingState = RecordingState.Idle;

            ArmRedo(new RedoContext(
                RawText: ctx.RawText,
                EnhancedText: enhanced,
                Prompt: ctx.Prompt,
                WasImageGeneration: false,
                TargetWindow: redoTargetWindow,
                WasPushToTalk: ctx.WasPushToTalk,
                TranscriptionModelName: ctx.TranscriptionModelName,
                TranscriptionLanguage: ctx.TranscriptionLanguage,
                PreviousProvider: providerOverride,
                PreviousModel: modelId,
                PreviousImageAspect: ctx.Prompt?.ImageAspect,
                PreviousImageSizeTier: ctx.Prompt?.ImageSizeTier,
                PreviousImageQuality: ctx.Prompt?.ImageQuality,
                References: ctx.References
            ), redoToneResolved);
            Logger.Information("Redo enhancement completed with model {Model}", modelId);
        }
        catch (OperationCanceledException)
        {
            Logger.Information("Redo enhancement cancelled");
            MarkTerminalPresentation(); // IMG-BG epoch fence
            RecordingState = RecordingState.Idle;

            // PST-6 (Codex round-4 C2): same shield as the main pipeline — a paste that
            // already ran and was declined must keep its warning; only the post-paste
            // bookkeeping was cancelled.
            if (redoClipboardFallback != null)
            {
                StatusText = redoClipboardFallback;
                EmitMiniRecorderError(redoClipboardFallback, redoTextTone);
                IsMiniRecorderVisible = true;
            }
            else
            {
                StatusText = "Redo cancelled";
                IsMiniRecorderVisible = false;
            }
        }
        catch (Exception ex)
        {
            try
            {
                // Cloud-API failures already logged the response body on the line above
                // (see HttpResponseExtensions / client error paths) — drop the redundant
                // stack trace for those, keep it for unexpected exceptions.
                // TimeoutException carries a self-explanatory message from the client and
                // would otherwise be lumped into the generic stack-trace branch below.
                // Warning for the provider-shaped branch: these are environmental (outages,
                // safety blocks, timeouts), fully handled with user-facing feedback, and the
                // Sentry sub-logger forwards Error+ — VOICEWINK-2 was 169 events of one 520
                // storm. Unexpected exceptions stay Error so real bugs still reach Sentry.
                if (ex is HttpRequestException or InvalidOperationException or TimeoutException
                    or Helpers.InvalidApiKeyFormatException)
                    Logger.Warning("Redo enhancement failed: {ErrorType}: {ErrorMessage}", ex.GetType().Name, ex.Message);
                else
                    Logger.Error(ex, "Redo enhancement failed");

                // Save the failed attempt to history so the user can see it and retry.
                // (IMG-BG: this catch serves the TEXT arm only — the image arm dispatches to
                // the background job, whose PersistFailedImageJobAsync twin carries the
                // ENH-6/6f sanitation + ENH-6c reference retention. Text-redo contexts carry
                // no references, so retention routes through with null.)
                var failedPersist = await _referencePersistence.PersistFailedAttemptWithRowAsync(
                    null,
                    _settings.GetBool(AppDefaults.IsHistoryEnabled, true),
                    referencePath => _historyWriter.TryWriteAsync(
                        new Models.Entities.TranscriptionRecord
                        {
                            Text = ctx.RawText,
                            EnhancedText = "[Enhancement failed]",
                            WasEnhanced = true,
                            PromptUsed = ctx.Prompt?.Id,
                            Timestamp = DateTime.UtcNow,
                            ModelName = ctx.TranscriptionModelName,
                            EnhancementModelName = $"{providerOverride ?? _enhancement.SelectedProvider}/{modelId}",
                            // Same rule as the success path above: the original attempt's language.
                            Language = ctx.TranscriptionLanguage,
                            ReferenceImagePath = referencePath
                        }, ct), ct);
                // Preserve pre-extraction semantics: only update _lastTranscriptionHistoryId
                // when the save actually succeeded; leave it unchanged on history-disabled
                // or save-failure (unlike the success paths which explicitly null it).
                if (failedPersist.HistoryId.HasValue)
                    _lastTranscriptionHistoryId = failedPersist.HistoryId.Value;
                // ENH-6e: independent ownership per claim — a success-persist followed
                // by a presentation throw lands BOTH claims in the list (never
                // overwritten), and the finally releases both.
                if (failedPersist.FreshCopyClaim != null)
                    freshCopyClaims.Add(failedPersist.FreshCopyClaim);

                RecordingState = RecordingState.Idle;

                // PILL-3: one attention surface, not two. The error-styled redo pill IS the
                // prominent error (Error-toned via ArmRedo → PERSISTS with a corner ×, ERR-PERSIST)
                // AND the retry affordance — no separate ShowMiniRecorderError that would stack two
                // surfaces. ArmRedo sets the coordinator context, so the redo hotkey works immediately.
                // ENH-1: prefer the provider's structured error text over the status-only message — but
                // never a typed 401's body, which is credential prose (see ProviderPillSafeText).
                var redoMsg = ProviderPillTextForRedo(ex);
                StatusText = redoMsg;
                IsMiniRecorderVisible = true;
                var rearmCtx = ctx with
                {
                    // Carry the JUST-ATTEMPTED provider/model (not ctx's prior selection) so the next
                    // redo dialog pre-selects what was tried — mirrors the success arm (F17 review).
                    PreviousProvider = providerOverride,
                    PreviousModel = modelId,
                };
                ArmRedo(rearmCtx, MiniRecorderTone.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Retention hit the (already-cancelled) token mid-failure-handling. An OCE
                // thrown from inside a catch block BYPASSES sibling catches — without this
                // route it would escape and strand RecordingState (Codex final check R2/R3).
                // Mirror the sibling cancellation catch's cleanup exactly.
                Logger.Information("Redo failure handling cancelled");
                MarkTerminalPresentation(); // IMG-BG epoch fence
                RecordingState = RecordingState.Idle;
                StatusText = "Redo cancelled";
                IsMiniRecorderVisible = false;
            }
        }
        finally
        {
            // ENH-6e: release the fresh-copy claims AFTER any re-arm above — the
            // release cleanup then finds the re-armed copy in the armed slot (or its
            // committed row) and retains it, while a copy whose row was deleted
            // mid-run is removed here, at its last pointer's release. The method-scoped
            // referenceLease disposes after this block, same rule.
            foreach (var claim in freshCopyClaims)
                claim.Dispose();
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);

            // PST-11: the retained element has had its one use. Releasing here rather than
            // carrying it into the re-armed redo above is deliberate — a redo CHAIN would
            // otherwise keep re-offering an ever-older element, compounding staleness, while the
            // next redo's own fresh capture is often perfectly good. Pinned by a test so the
            // choice reads as a decision rather than an omission.
            _redoFocusRetention.Release();
        }
    }

    /// <summary>
    /// IMG-BG: dispatch an image redo/regenerate/new-image to the background job. Order is
    /// load-bearing (Codex plan round 2): RESERVE first — a refusal leaves the armed redo and
    /// the focus slot untouched — then transfer resources (in-flight lease BEFORE
    /// ClearRedoState releases the armed claims, gapless ENH-6e handoff; focus element via
    /// slot handoff), then Start. The job's own CTS replaces the old `_transcriptionCts`
    /// reset — a subsequent recording can no longer cancel/dispose the token under a running
    /// generation.
    /// </summary>
    private async Task DispatchImageRedoAsync(RedoContext ctx, string modelId, AIProvider? providerOverride)
    {
        var reservation = _imageJob.TryReserve();
        if (reservation == null)
        {
            RequestImageJobRefusalNotice();
            return;
        }

        // Gate the picker's model BEFORE any resource transfer, same as the voice twin. The model is
        // an explicit user choice here (PickerSelection), so a refusal never rewrites settings — it
        // just declines to spend a call on a model that cannot return an image.
        var redoProvider = providerOverride
            ?? (ctx.Prompt != null ? ResolvePromptProvider(ctx.Prompt) : _enhancement.SelectedImageProvider);
        if (!GateImageModelOrRefuse(
                new Helpers.ImageModelResolution(modelId, Helpers.ImageModelSource.PickerSelection),
                redoProvider,
                reservation))
            return;

        Helpers.UiaFocusBridge.IUIAutomationElement? focusedElement = null;
        IDisposable? referenceLease = null;
        try
        {
            referenceLease = _referencePersistence.BeginInFlightLiveReferences(PathsOf(ctx.References));
            ClearRedoState();

            var redoTargetWindow = ResolveRedoTargetWindow(ctx.TargetWindow, _targetWindowHandle);
            // IMG-3: the dialog wrote its confirmed count onto the context
            // (PreviousImageCount — dialog-authoritative, like References).
            var count = Helpers.ImageBatchPolicy.ClampCount(ctx.PreviousImageCount ?? 1);
            if (count == 1)
            {
                focusedElement = await _focusSlot.TakeForHandoffAsync();
            }
            else
            {
                // A batch never pastes — release the redo capture owner-scoped instead
                // of carrying a cross-process RCW through a multi-minute batch.
                ReleaseCapturedFocusedElement(CapturedFocusOwner.Redo);
            }

            // Prompt snapshot + the dispatch-time route — see the voice dispatch twin. The
            // picker's modelId is nonblank by construction; ResolvedProvider mirrors the old
            // effectiveProvider rule (null prompt = image redo without a prompt record —
            // ResolvePromptProvider's null path would fall back to the TEXT provider).
            var promptSnapshot = ctx.Prompt?.Clone();
            var request = new ImageGenJobRequest(
                Text: ctx.RawText,
                Prompt: promptSnapshot,
                ResolvedProvider: providerOverride
                    ?? (promptSnapshot != null ? ResolvePromptProvider(promptSnapshot) : _enhancement.SelectedImageProvider),
                ResolvedModel: modelId,
                References: ctx.References,
                TranscriptionModelName: ctx.TranscriptionModelName,
                // The ORIGINAL transcription's language, not the live setting. This is the image
                // sibling of the two text redo history writes: BuildImageRow persists req.Language,
                // and re-reading Settings here stamped a weeks-old record with whatever the user
                // has selected today. Null stays null for a text-first chain — no transcription
                // happened, so there is no language to report.
                Language: ctx.TranscriptionLanguage,
                WasPushToTalk: ctx.WasPushToTalk,
                IsNewGeneration: ctx.IsNewGeneration,
                Count: count,
                TargetWindow: redoTargetWindow,
                // Freshness stamps NOW (mirrors the old pre-generation refresh) so a FAST
                // regenerate can still auto-paste; slow generations degrade to clipboard-only
                // through the same 10 s window as always.
                TargetCapturedAtUtc: redoTargetWindow != IntPtr.Zero ? DateTime.UtcNow : DateTime.MinValue,
                TargetSnapshot: global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot),
                FocusedElement: focusedElement,
                ReferenceLease: referenceLease);

            reservation.Start(jobCt => RunImageGenerationJobAsync(request, jobCt));
            Logger.Information("Image redo dispatched to background job (model {Model})", modelId);
        }
        catch (Exception ex)
        {
            // Preparation failed — free the slot + every transferred resource (plan rule).
            reservation.Dispose();
            referenceLease?.Dispose();
            if (focusedElement != null)
                Helpers.UiaFocusBridge.EnqueueRelease(focusedElement);
            Logger.Error(ex, "Image redo dispatch failed");
            EmitMiniRecorderError("Image redo failed", MiniRecorderTone.Error);
        }
    }

    /// <summary>
    /// Release the UIA-element RCW captured for recording or redo. Single owner pattern:
    /// only this method releases the COM ref. <c>ClipboardService</c> consumes a borrowed
    /// reference and never releases. When <paramref name="expectedOwner"/> is supplied,
    /// cleanup is skipped if another flow has since captured its own focus element.
    /// Idempotent — safe to call when the field is already null. Swallows exceptions so
    /// cleanup never blocks state transitions.
    ///
    /// <para>The release is routed through <see cref="Helpers.UiaFocusBridge.EnqueueRelease"/>
    /// rather than calling <see cref="global::System.Runtime.InteropServices.Marshal.ReleaseComObject"/>
    /// here on the UI thread. The underlying cross-process <c>Release()</c> would otherwise
    /// block the UI thread when the target app (e.g. Excel during a heavy recalc) is wedged,
    /// because the RCW belongs to that target's apartment and COM serializes Release through
    /// it. Routing through the bridge's MTA worker keeps the UI thread responsive.</para>
    /// </summary>
    private void ReleaseCapturedFocusedElement(CapturedFocusOwner? expectedOwner = null)
    {
        _focusSlot.Release(expectedOwner);
    }

    /// <summary>
    /// Capture the UIA-focused element for a redo paste. Redo capture is fresh by
    /// design: it reflects the focus at redo invocation time, not the original
    /// recording's focus.
    /// </summary>
    private void CaptureFocusedElementForRedo()
    {
        if (_focusSlot.Owner == CapturedFocusOwner.Recording)
        {
            Logger.Warning("Redo focus capture skipped because a recording focus capture is active");
            return;
        }

        CaptureFreshPasteTarget();
        _focusSlot.CaptureForRedo();
    }

    /// <summary>
    /// Capture the CURRENT foreground window + identity snapshot as the paste target
    /// and kick off the label/class enrichment. Shared by the redo capture above and
    /// the retry entry (REL-12) — both paste "wherever the user is now".
    ///
    /// <para><b>Since TRN-17 both callers reach it through <see cref="CaptureFocusedElementForRedo"/></b>,
    /// so the UIA focus capture is the synchronous <c>CaptureForRedo</c> on both paths. This doc
    /// used to say the retry used the non-blocking <c>BeginForRecording</c> "whose Recording
    /// ownership pairs with the pipeline finally's <c>Release(Recording)</c>", which is now false in
    /// both halves: the retry captures BEFORE its picker dialog (showing it foregrounds VoiceWink,
    /// and this method is <c>GetForegroundWindow</c>), needs Redo ownership so a CANCEL can release
    /// its own capture without touching a recording that superseded it, and hands the capture over
    /// with <c>CapturedFocusSlot.AdoptForRecording</c> only once the user confirms.</para>
    /// </summary>
    private void CaptureFreshPasteTarget() => AdoptPasteTarget(NativeInterop.GetForegroundWindow());

    /// <summary>
    /// True when the foreground window belongs to THIS process — VoiceWink's main window, a dialog,
    /// or the pill (TRN-17).
    ///
    /// <para>Exists because the retry picker foregrounds the main window to show itself, so after a
    /// CANCEL the foreground is ours and a fresh "paste wherever the user is now" capture would
    /// name VoiceWink. A zero/unreadable pid answers FALSE — the fallback it guards discards a
    /// fresh capture, and doing that on an unreadable answer would silently pin every retry to a
    /// possibly-stale handle.</para>
    ///
    /// <para>The caller reads the foreground a SECOND time when this answers false (through
    /// <see cref="CaptureFreshPasteTarget"/>), and that race is deliberately not closed: the two
    /// reads are microseconds apart, and if the user did switch windows between them the second
    /// read is the more correct answer to "where is the user now" — which is the contract. Passing
    /// the handle through would freeze a staler one to remove a race whose only outcome is being
    /// right.</para>
    ///
    /// <para>The implementation moved to <see cref="ForegroundOwnership"/> for UI-7, which needs
    /// the identical test in <c>App</c>'s picker handlers. Kept as a named member here because the
    /// paragraphs above are about THIS call site, not about the shared helper.</para>
    /// </summary>
    private static bool IsForegroundOurOwnProcess() => ForegroundOwnership.IsForegroundOurs();

    /// <summary>
    /// UI-7: mark a picker dialog as ON SCREEN for the lifetime of the returned scope. <c>App</c>
    /// wraps each <c>ShowAsync</c> — and ONLY the <c>ShowAsync</c>, never the work that follows a
    /// confirm — so <see cref="_pickerDialogVisible"/> answers "is a dialog up right now".
    ///
    /// <para>A scope rather than a pair of methods because the failure mode of a missed decrement is
    /// invisible and permanent: every later dictation would be silently copied instead of pasted,
    /// with nothing on screen to explain it. <c>using</c> makes the exception path free.</para>
    /// </summary>
    internal IDisposable BeginPickerDialogVisible()
    {
        Interlocked.Increment(ref _pickerDialogVisible);
        return new PickerDialogVisibleScope(this);
    }

    /// <summary>TRN-64: is a picker or offer dialog up right now? The App's retry-picker host
    /// reads it before opening a second <c>ContentDialog</c> on the same XamlRoot, which WinUI
    /// refuses with a throw.</summary>
    internal bool IsPickerDialogVisible => Volatile.Read(ref _pickerDialogVisible) != 0;

    private sealed class PickerDialogVisibleScope(MainViewModel owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Idempotent: a double Dispose must not drive the count negative and leave the gate
            // permanently off.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._pickerDialogVisible);
        }
    }

    /// <summary>
    /// Make <paramref name="hwnd"/> the paste target and rebuild the identity snapshot + label
    /// around it.
    ///
    /// <para><b>The handle must be freshly observed, never a stored one.</b> This builds an
    /// identity snapshot AROUND whatever the handle names right now, so feeding it a historical
    /// HWND launders a stale reference into a valid-looking target: Windows recycles handles, and
    /// <c>PasteTargetValidation</c> would then pass a self-consistent identity belonging to a
    /// DIFFERENT application. TRN-17's cancel path tried exactly that and was rejected in
    /// verification; it degrades to the clipboard instead.</para>
    /// </summary>
    private void AdoptPasteTarget(IntPtr hwnd)
    {
        _targetWindowHandle = hwnd;
        _targetWindowCapturedAtUtc = DateTime.UtcNow;
        var threadId = NativeInterop.GetWindowThreadProcessId(_targetWindowHandle, out var pid);
        _pasteTargetSnapshot = _targetWindowHandle != IntPtr.Zero && pid != 0
            ? new PasteTargetSnapshot(_targetWindowHandle, pid, threadId, null, _targetWindowCapturedAtUtc)
            : null;
        EnrichPasteTargetSnapshotClass();
        ResetPasteTargetAppName();
        ResolvePasteTargetAppName();
    }

    /// <summary>
    /// Fire-and-forget class-name enrichment of the current paste-target snapshot.
    /// Runs on a worker (GetClassName can block on a wedged target); revalidates the
    /// hwnd's pid/tid ownership before reading, and publishes with a reference CAS so
    /// a newer recording's snapshot is never clobbered by a slow older enrichment.
    /// Validation self-disarms its class layer while ClassName is still null.
    /// </summary>
    private void EnrichPasteTargetSnapshotClass()
    {
        var original = global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot);
        if (original == null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                if (!NativeInterop.IsWindow(original.Hwnd))
                    return;
                var tid = NativeInterop.GetWindowThreadProcessId(original.Hwnd, out var pid);
                if (pid != original.Pid || tid != original.ThreadId)
                    return; // hwnd already recycled — leave the class layer disarmed

                var sb = new global::System.Text.StringBuilder(256);
                if (NativeInterop.GetClassName(original.Hwnd, sb, sb.Capacity) <= 0)
                    return;

                global::System.Threading.Interlocked.CompareExchange(
                    ref _pasteTargetSnapshot, original with { ClassName = sb.ToString() }, original);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Paste-target class enrichment failed");
            }
        });
    }

    /// <summary>
    /// Fire-and-forget resolution of the paste target's friendly app name (PILL-1).
    /// Fully off the hot path (the pill never waits on it); the metadata read is
    /// BOUNDED (500 ms — Process.GetProcessById/MainModule are slow paths in this
    /// repo, see AppModeManager) and the read itself is fail-soft by construction
    /// (every failure returns null), so an abandoned late task can never fault
    /// unobserved. Publication requires the capture to still be LIVE (IsWindow +
    /// pid/tid ownership, mirroring class enrichment) AND still be the CURRENT
    /// logical capture (IsSameCapture — deliberately not reference equality: class
    /// enrichment replaces the snapshot reference via `with`). Timeout/stale/dead
    /// ⇒ no label — a late or wrong label is worse than none.
    /// </summary>
    private void ResolvePasteTargetAppName()
    {
        var generation = global::System.Threading.Volatile.Read(ref _targetLabelGeneration);
        var captured = global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot);
        if (captured == null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var work = Task.Run(() => ReadProcessDisplayName(captured.Pid));
                var winner = await Task.WhenAny(work, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
                if (winner != work)
                    return; // timed out — prefer no label (work is fail-soft; no late fault possible)

                var name = work.Status == TaskStatus.RanToCompletion ? work.Result : null;
                if (string.IsNullOrEmpty(name))
                    return;

                // Live re-validation: the hwnd may be destroyed/recycled by now.
                if (!NativeInterop.IsWindow(captured.Hwnd))
                    return;
                var tid = NativeInterop.GetWindowThreadProcessId(captured.Hwnd, out var pid);
                if (pid != captured.Pid || tid != captured.ThreadId)
                    return;

                // Stale-publish decision + assignment as an ATOMIC pair under the
                // reset lock — a check-then-assign without it lets a reset slip in
                // between and the stale name republish (Codex diff round 2).
                lock (_targetLabelLock)
                {
                    if (!Helpers.MiniRecorderTargetLabel.ShouldPublish(
                            generation, _targetLabelGeneration,
                            captured, global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot)))
                        return;
                    PasteTargetAppName = name;
                }

                // The paste diagnostics record window class and pid but never the
                // process NAME, so a log-only investigation cannot say WHICH app a
                // failed paste was aimed at — the one question the 2026-08-19
                // cold-tree investigation could not answer from the logs. The name is
                // already resolved and already published to the pill; this just writes
                // it down. No extra call.
                Logger.Information(
                    "Paste target app: {AppName} (hwnd=0x{Hwnd:X} pid={Pid})",
                    name, captured.Hwnd.ToInt64(), captured.Pid);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Paste-target app-name resolution failed");
            }
        });
    }

    /// <summary>
    /// Fire-and-forget editability probe of the element captured at recording
    /// start — feeds <see cref="PasteTargetEditableConfirmed"/>. OBSERVABILITY
    /// ONLY since 2026-07-09: the PILL-2 label tint it used to drive was retired
    /// (owner: colors over-promised — even a confirmed-editable capture can't
    /// promise the paste, cf. the PST-4 same-window tab switch), but the probe's
    /// "Paste target editability" log line stays — it proved its diagnostic value
    /// in the 2026-07-09 PowerPoint/Copilot investigation.
    /// Mirrors <see cref="ResolvePasteTargetAppName"/>'s
    /// discipline exactly: generation + snapshot + pending-task reference read on
    /// the UI thread; the bounded await + probe run in Task.Run; publication from
    /// the worker under <see cref="_targetLabelLock"/> behind the ShouldPublish
    /// fence. The pending task is OBSERVED only (ownership stays with the slot);
    /// staleness is re-checked before the probe AND before publication. Residual
    /// race (a release routed the RCW between the re-check and the probe) is
    /// harmless: the probe throws inside the UIA worker, is caught, yields null →
    /// the indicator stays unknown. Worker-contention note: the probe is enqueued
    /// once at recording start and self-bounds at 500 ms — it completes well
    /// before the earliest paste-time UIA work (transcription alone takes
    /// longer); the theoretical straggler degrades PST-3's rescue to its block
    /// (clipboard + honest message), never a wrong paste.
    /// </summary>
    private void ResolvePasteTargetEditability()
    {
        var generation = global::System.Threading.Volatile.Read(ref _targetLabelGeneration);
        var captured = global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot);
        var pending = _focusSlot.PendingRecordingCapture;
        if (captured == null || pending == null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var winner = await Task.WhenAny(
                    pending, Task.Delay(CapturedFocusSlot.PendingCaptureResolveTimeout)).ConfigureAwait(false);
                if (winner != pending)
                    return; // capture still pending after the bound — no claim

                var element = pending.Status == TaskStatus.RanToCompletion ? pending.Result : null;
                if (element == null)
                    return; // capture failed — unknown, no claim

                // Staleness re-check BEFORE paying for the probe (a cancel/reset
                // may have raced the capture) — and again before publication.
                if (global::System.Threading.Volatile.Read(ref _targetLabelGeneration) != generation)
                    return;

                var shape = Helpers.UiaFocusBridge.TryGetCapturedElementShape(element);
                if (shape is not { } s)
                    return; // probe failed/timed out/released RCW — unknown

                var editable = Helpers.NoEditableFocusGate.IsEditableShapedForCapturedRescue(s);
                lock (_targetLabelLock)
                {
                    if (!Helpers.MiniRecorderTargetLabel.ShouldPublish(
                            generation, _targetLabelGeneration,
                            captured, global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot)))
                        return;
                    PasteTargetEditableConfirmed = editable;
                }
                Logger.Information(
                    "Paste target editability: {State} (controlType={ControlType})",
                    editable ? "editable" : "non-editable", s.ControlTypeId);

                // PST-13: the capture hit a Chromium accessibility tree that had not been
                // built yet, so it holds the top-level Pane rather than the composer. The
                // probe above was itself the touch that starts Chromium building it — so
                // re-take the capture now, while the recording is still running.
                if (Helpers.ColdCapturePolicy.ShouldReCapture(s))
                    await UpgradeColdCaptureAsync(generation, captured, s.ControlTypeId)
                        .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Paste-target editability resolution failed");
            }
        });
    }

    /// <summary>
    /// PST-13 delays before each re-capture attempt, measured from the editability
    /// probe's verdict. Two attempts: Chromium's tree hydrates within tens of ms of
    /// the first UIA touch, and the second is slack for a busy target. The shortest
    /// recording that produced this defect in the field ran 6.7 s, so a ~0.6 s budget
    /// always fits inside the recording session (<c>Starting</c> or <c>Recording</c> — see
    /// <see cref="IsColdCaptureGateOpen"/>); a recording shorter than the budget simply
    /// fails the pipeline-state gate and keeps today's behaviour.
    /// </summary>
    private static readonly int[] ColdCaptureRetryDelaysMs = { 120, 500 };

    /// <summary>
    /// PST-13: bounded attempt loop to replace a cold-tree capture with a real one.
    /// Runs on the editability probe's worker, never the UI thread.
    ///
    /// <para>Each attempt checks the staleness gate on the UI thread, does the capture
    /// and shape probe OFF it (both are cross-process UIA calls), then commits through
    /// <see cref="CommitColdCaptureUpgrade"/>, which re-checks the gate and swaps in a
    /// single uninterrupted UI-thread turn.</para>
    ///
    /// <para><b>PST-4 note.</b> A same-window focus move between the probe and the
    /// upgrade re-arms identity against the moved-to element, so a paste can now land
    /// where the caret was ~120–620 ms into the recording instead of blocking. That is
    /// a deliberate, narrow relaxation: the cold Pane carried no user intent (it is not
    /// keyboard-focusable, so the user could not have focused it), and
    /// <see cref="Helpers.ColdCapturePolicy.ShouldReCapture"/> excludes every capture
    /// that could have been deliberate.</para>
    /// </summary>
    private async Task UpgradeColdCaptureAsync(
        int generation, PasteTargetSnapshot captured, int capturedControlType)
    {
        for (var attempt = 0; attempt < ColdCaptureRetryDelaysMs.Length; attempt++)
        {
            await Task.Delay(ColdCaptureRetryDelaysMs[attempt]).ConfigureAwait(false);

            // Cheap pre-checks so a moved-on or too-late recording pays no UIA call at all.
            // The authoritative evaluation is the one inside CommitColdCaptureUpgrade.
            if (!Helpers.ColdCapturePolicy.IsWithinUpgradeDeadline(captured.CapturedAtUtc, DateTime.UtcNow))
            {
                LogColdCaptureUpgrade(Helpers.ColdCaptureUpgradeOutcome.DeadlineExpired,
                    attempt, capturedControlType, null);
                return;
            }

            if (!await RunOnUiThreadAsync(
                        () => IsColdCaptureGateOpen(generation, captured), whenNoDispatcher: false)
                    .ConfigureAwait(false))
            {
                LogColdCaptureUpgrade(Helpers.ColdCaptureUpgradeOutcome.GateClosed,
                    attempt, capturedControlType, null);
                return;
            }

            var candidate = Helpers.UiaFocusBridge.CaptureFocusedElement();
            if (candidate == null)
            {
                LogColdCaptureUpgrade(Helpers.ColdCaptureUpgradeOutcome.CaptureFailed,
                    attempt, capturedControlType, null);
                continue;
            }

            var shape = Helpers.UiaFocusBridge.TryGetCapturedElementShape(candidate);
            if (shape is null)
            {
                // A null shape is NOT evidence the tree stayed cold — TryGetCapturedElementShape
                // returns null for a busy worker, an unready probe, a timeout or a dead element
                // just as readily. Logging it as CandidateStillCold would corrupt the one signal
                // this change exists to produce, and the UAT fragment tells the owner to read
                // that outcome as grounds to abandon this approach (Codex diff round, blocker).
                Helpers.UiaFocusBridge.EnqueueRelease(candidate);
                LogColdCaptureUpgrade(Helpers.ColdCaptureUpgradeOutcome.CaptureFailed,
                    attempt, capturedControlType, null);
                continue;
            }

            var ns = shape.Value;
            if (!Helpers.NoEditableFocusGate.IsEditableShapedForCapturedRescue(ns))
            {
                // The hypothesis this change rests on — that a re-capture returns a
                // hydrated Edit — did NOT hold on this attempt, and here we KNOW that
                // rather than merely failing to find out. That distinction is the whole
                // point of the separate outcome: this is the signal that says to fall
                // back to relaxing the paste-time guard instead.
                Helpers.UiaFocusBridge.EnqueueRelease(candidate);
                LogColdCaptureUpgrade(Helpers.ColdCaptureUpgradeOutcome.CandidateStillCold,
                    attempt, capturedControlType, ns.ControlTypeId);
                continue;
            }

            var outcome = await RunOnUiThreadAsync(
                () => CommitColdCaptureUpgrade(generation, captured, candidate),
                whenNoDispatcher: Helpers.ColdCaptureUpgradeOutcome.GateClosed)
                .ConfigureAwait(false);

            if (outcome != Helpers.ColdCaptureUpgradeOutcome.Upgraded)
                Helpers.UiaFocusBridge.EnqueueRelease(candidate);

            LogColdCaptureUpgrade(outcome, attempt, capturedControlType, ns.ControlTypeId);

            // Upgraded — done. GateClosed/SlotRefused/DeadlineExpired will not change on a retry.
            if (outcome != Helpers.ColdCaptureUpgradeOutcome.SlotInFlight)
                return;
        }
    }

    /// <summary>
    /// PST-13 commit step. <b>Deliberately NOT async</b>: the staleness gate and the
    /// swap must run in ONE uninterrupted UI-thread turn, and making the method
    /// non-async is what makes an interleaved <c>await</c> a compile error rather than
    /// a comment a later edit can quietly violate (Kimi plan round, A2).
    ///
    /// <para>That single turn is the whole safety argument. The paste path borrows the
    /// slot's element on the UI thread, and by then the pipeline has left the recording
    /// session for <c>Transcribing</c>/<c>Enhancing</c>. A swap that can only commit while
    /// the state still reads <c>Starting</c> or <c>Recording</c> (see
    /// <see cref="IsColdCaptureGateOpen"/> for why <c>Starting</c> counts), with no yield
    /// between the check and the swap, therefore can never release an element the paste
    /// already took.</para>
    /// </summary>
    private Helpers.ColdCaptureUpgradeOutcome CommitColdCaptureUpgrade(
        int generation, PasteTargetSnapshot captured,
        Helpers.UiaFocusBridge.IUIAutomationElement candidate)
    {
        // Deadline first, so a late arrival is reported as late rather than as the target
        // having moved on — the two point at different follow-ups.
        if (!Helpers.ColdCapturePolicy.IsWithinUpgradeDeadline(captured.CapturedAtUtc, DateTime.UtcNow))
            return Helpers.ColdCaptureUpgradeOutcome.DeadlineExpired;

        if (!IsColdCaptureGateOpen(generation, captured))
            return Helpers.ColdCaptureUpgradeOutcome.GateClosed;

        if (_focusSlot.TryUpgradeRecordingCapture(candidate))
            return Helpers.ColdCaptureUpgradeOutcome.Upgraded;

        // The slot refuses an in-flight capture (retryable) and every ownership reason
        // (not). PendingRecordingCapture is non-null and incomplete in exactly the
        // first case.
        var pending = _focusSlot.PendingRecordingCapture;
        return pending is { IsCompleted: false }
            ? Helpers.ColdCaptureUpgradeOutcome.SlotInFlight
            : Helpers.ColdCaptureUpgradeOutcome.SlotRefused;
    }

    /// <summary>
    /// PST-13 staleness gate. UI thread only.
    ///
    /// <para>Capture identity is compared with
    /// <see cref="Helpers.MiniRecorderTargetLabel.IsSameCapture"/>, NOT reference
    /// equality: <see cref="EnrichPasteTargetSnapshotClass"/> replaces the snapshot
    /// instance via <c>original with { ClassName = … }</c> within milliseconds of
    /// recording start, so a reference check would fail on essentially every recording
    /// and abort the upgrade in exactly the case it exists for (Kimi plan round,
    /// blocker B2). This is the same fence
    /// <see cref="Helpers.MiniRecorderTargetLabel.ShouldPublish"/> applies to the probe
    /// that triggers this — the upgrade must not be gated more weakly, and reference
    /// equality made it spuriously stronger.</para>
    ///
    /// <para><b>The state term admits <c>Starting</c>, and it MUST.</b> The editability
    /// probe that triggers the upgrade is launched before the warm/cold path split, and on
    /// the COLD audio path the pipeline sits in <c>Starting</c> from there through the
    /// whole pre-flight — App Mode detect, a local model ensure-load that can be a full
    /// DOWNLOAD, device resolution, and the capture start — reaching <c>Recording</c> only
    /// afterwards. A <c>Recording</c>-only term therefore closed the gate on the first
    /// attempt for every cold-path recording and terminated the loop, leaving the fix a
    /// path-dependent no-op on exactly the recordings it exists for (Kimi diff round,
    /// blocker). The warm path flips <c>Starting</c>→<c>Recording</c> microseconds apart
    /// and was winning that race, which is what made it look like it worked.</para>
    ///
    /// <para><b>Admitting <c>Starting</c> costs no safety.</b> The invariant the swap rests
    /// on is that the paste path has not yet BORROWED the element, and its only
    /// <c>ResolveAsync</c> sites run at <c>Transcribing</c>/<c>Enhancing</c> — both strictly
    /// later. A cancel during <c>Starting</c> lands on <c>Idle</c> and a supersede bumps the
    /// generation, so both still close this gate through the other two terms.</para>
    /// </summary>
    private bool IsColdCaptureGateOpen(int generation, PasteTargetSnapshot captured)
        => global::System.Threading.Volatile.Read(ref _targetLabelGeneration) == generation
           && Helpers.MiniRecorderTargetLabel.IsSameCapture(
               captured, global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot))
           && Helpers.ColdCapturePolicy.IsUpgradableSessionState(RecordingState)
           && NativeInterop.GetForegroundWindow() == captured.Hwnd;

    private static void LogColdCaptureUpgrade(
        Helpers.ColdCaptureUpgradeOutcome outcome, int attempt,
        int capturedControlType, int? candidateControlType)
    {
        Logger.Information(
            "Cold-tree capture upgrade: {Outcome} attempt={Attempt} capturedControlType={Captured} candidateControlType={Candidate}",
            outcome, attempt + 1, capturedControlType,
            candidateControlType?.ToString() ?? "none");
    }

    /// <summary>
    /// Run <paramref name="func"/> on the UI thread and await its result.
    ///
    /// <para><b>Deliberately NOT <see cref="EnqueueUiTurn"/>'s inline fallback.</b> That
    /// helper runs its action on the calling thread when there is no dispatcher, which is
    /// right for a fire-and-forget UI update and wrong here: PST-13's callers read
    /// pipeline state and MUTATE <see cref="CapturedFocusSlot"/>, whose every member is
    /// UI-thread-only, so an inline run on the probe's worker thread would violate that
    /// discipline exactly when the dispatcher is gone or shutting down. Both no-dispatcher
    /// paths therefore answer <paramref name="whenNoDispatcher"/> instead — a fail-closed
    /// value the caller picks (abort the gate, refuse the swap), never the real work off
    /// the wrong thread.</para>
    /// </summary>
    private static Task<T> RunOnUiThreadAsync<T>(Func<T> func, T whenNoDispatcher)
    {
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue == null)
            return Task.FromResult(whenNoDispatcher);

        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
        {
            return Task.FromResult(whenNoDispatcher);
        }

        return tcs.Task;
    }

    /// <summary>
    /// Fail-soft process metadata read: FileDescription ("Windows Terminal")
    /// preferred, ProcessName fallback (MainModule access throws for elevated
    /// targets), null on any failure. Never throws — the bounded wrapper above
    /// relies on that to keep abandoned late tasks fault-free.
    /// </summary>
    private static string? ReadProcessDisplayName(uint pid)
    {
        try
        {
            using var p = global::System.Diagnostics.Process.GetProcessById((int)pid);
            string? description = null;
            try { description = p.MainModule?.FileVersionInfo.FileDescription; }
            catch { /* elevated/protected target — fall back to ProcessName */ }
            return Helpers.MiniRecorderTargetLabel.PickName(description, p.ProcessName);
        }
        catch
        {
            return null; // process exited / access denied
        }
    }

    /// <summary>
    /// Which window a redo should paste into — the FRESHLY captured foreground, or nothing.
    ///
    /// <para><b>There is no historical fallback, and its removal is the point (UI-7, Codex round
    /// 4).</b> This used to substitute the redo context's stored <c>TargetWindow</c> when the fresh
    /// capture came back zero. But a zero capture also means <c>_pasteTargetSnapshot</c> is null,
    /// and <c>PasteTargetValidation</c> returns <c>NotApplicable</c> on a null snapshot — so the
    /// stored handle reached the paste path with the identity gate DISARMED. Shipped scenario: an
    /// enhanced dictation targets window A; A closes and Windows recycles its HWND to unrelated
    /// window B; the fresh capture fails; the fallback selects A's stale numeric handle; VoiceWink
    /// activates B and pastes private text into it.</para>
    ///
    /// <para>A test pinned that fallback, which is why an earlier revision of this card preserved
    /// it as "deliberate". It was pinning the fail-open, not making it safe — a distinction worth
    /// remembering the next time a test looks like permission.</para>
    ///
    /// <para>The picker-suppression case needs no special handling: suppression zeroes
    /// <c>_targetWindowHandle</c>, which IS <paramref name="capturedRedoTargetWindow"/>, so it
    /// takes the same clipboard-only answer as any other failed capture. The explicit flag this
    /// method carried for one round is gone with the branch that needed it.</para>
    /// </summary>
    internal static IntPtr ResolveRedoTargetWindow(IntPtr contextTargetWindow, IntPtr capturedRedoTargetWindow)
    {
        // History-page redo deliberately passes a zero context target and must remain
        // clipboard-only even though VoiceWink is foreground while the picker is open.
        if (contextTargetWindow == IntPtr.Zero)
            return IntPtr.Zero;

        // MiniRecorder/hotkey redo captures the current foreground before the picker activates the
        // main window. A zero here means we do not know where the user is; the honest answer is the
        // clipboard, never a handle remembered from before the dialog opened.
        return capturedRedoTargetWindow;
    }

    /// <summary>
    /// Pill status line for the fallback-failure redo state (ENH-1). Two rules:
    /// (a) honest about the paste outcome — the branch runs whether or not Ctrl+V
    /// landed, and "pasted" on a failed paste would misreport; (b) the REASON gets
    /// the character budget — it is the actionable part, and a boilerplate-first
    /// string would lose it entirely to the pill truncation (Codex diff round 4).
    /// Returns the FINAL, already-truncated string; the error styling + redo
    /// affordance carry the "failed" signal, History/logs carry the full text.
    /// The 55-char default derives from the 500-DIP pill's redo layout — see the
    /// budget math at MiniRecorderWindow.PillWidthDips.
    /// </summary>
    internal static string ComposeFallbackFailureStatus(
        bool wasImageAttempt, bool pasteLanded, string? reason, int maxLength = 55)
    {
        var what = wasImageAttempt ? "Image generation failed" : "Enhancement failed";
        // Name WHAT survived, not just that something did (owner, 2026-07-25): a bare "pasted"
        // left the user guessing whether the enhanced text or the raw transcript landed. On both
        // paths — enhancement failed, or an image attempt failed — what reaches the user is the
        // plain transcription. Costs the reason budget ~14 chars; the reason is already
        // truncated for length, the outcome never is, so the outcome wins.
        var outcome = pasteLanded ? "pasted transcription" : "transcription on clipboard";
        if (string.IsNullOrWhiteSpace(reason))
            return TruncateForMiniRecorder($"{what} — {outcome}", what, maxLength);

        // Normalize BEFORE budgeting: provider text can arrive with leading whitespace, and
        // slicing it raw let a space-prefixed reason collapse into a bare "… — on clipboard"
        // (Codex diff review round 3, 2026-07-25).
        reason = reason.Trim();

        var suffix = $" — {outcome}";
        var room = maxLength - suffix.Length;
        // A reason that fits whole is never touched. Only the TRUNCATING branch pays for the
        // ellipsis (−1), so the composed result honors maxLength without shortening messages
        // that already fit exactly (Codex diff review round 2, 2026-07-25).
        if (reason.Length <= room)
            return reason + suffix;

        var budget = Math.Max(8, room - 1);
        return reason[..budget].TrimEnd() + "…" + suffix;
    }

    /// <summary>
    /// Diagnostic only — log the target window's native focus state at recording-start
    /// so log analysis can compare it against ClipboardService's pre-paste / post-paste
    /// log lines and detect mid-recording focus drift. Native focus only: for Chromium-
    /// based targets (Electron apps) this CANNOT see DOM-element focus inside the
    /// renderer, so a "focus unchanged" reading does not prove paste will land at the
    /// expected DOM editable — only that OS-level focus didn't drift.
    /// </summary>
    private static void LogRecordingStartFocus(IntPtr targetWindow)
    {
        try
        {
            if (targetWindow == IntPtr.Zero)
            {
                Logger.Information("Recording start native focus: target=0x0 (foreground capture failed)");
                return;
            }
            var info = ClipboardService.CaptureFocusInfo(targetWindow);
            Logger.Information(
                "Recording start native focus: target=0x{Target:X} targetClass=\"{TargetClass}\" tid={Tid} pid={Pid} " +
                "hwndActive=0x{Active:X} hwndFocus=0x{Focus:X} focusClass=\"{FocusClass}\" " +
                "caretHwnd=0x{Caret:X} caretRect=({CL},{CT},{CR},{CB}) flags=0x{Flags:X}",
                targetWindow, info.TargetClass, info.ThreadId, info.ProcessId,
                info.ActiveHwnd, info.FocusHwnd, info.FocusClass,
                info.CaretHwnd, info.CaretRect.Left, info.CaretRect.Top, info.CaretRect.Right, info.CaretRect.Bottom,
                info.Flags);
        }
        catch (Exception ex)
        {
            Logger.Information("Recording start native focus: target=0x{Target:X} (capture failed: {Reason})",
                targetWindow, ex.Message);
        }
    }

    private async Task StartRecordingAsync(long? hotkeyDispatchTimestamp = null)
    {
        // TRN-49: recording admission cancels any in-flight GPU warm-up, permanently for the
        // session — the warm-up must never sit ahead of real work (codex plan-round Blocker 1).
        // First statement deliberately: even a gate-refused start signals the user is HERE.
        // TRN-57: this cancel is also what makes the coordinator's spawn GRACE safe for the
        // recording path — by the time StartRecordingAsync's prepare reaches the quiesce, nothing
        // is left to wait for.
        GpuWarmup.Instance.Cancel(GpuWarmupCancelReason.RecordingAdmission);

        // License gate (LIC-3): block new recordings when the license is in a non-usable
        // state. Uses the synchronous cache (no network) so the hotkey path never stalls
        // on a server round-trip. The startup reconcile runs fire-and-forget from
        // ApplyLicenseStartupRoute — if a hotkey fires before it finishes, the gate falls
        // back to whatever GetCachedStatus can prove locally; that's safe because the
        // four blocking statuses (Unlicensed, GraceExpired, Invalid, DisabledReadOnly)
        // all represent durable no-key / fingerprint-mismatch / persisted-flag / aged-
        // cache conditions — none of which are fixed by a reconcile that's still in flight.
        if (!IsLicenseAllowingRecording(out var blockedMessage, out var trialEnded))
        {
            Logger.Information("Recording blocked by license gate: {Message}", blockedMessage);
            EmitLicenseRefusalPill(blockedMessage);
            // LNC-11: the first trial-ended refusal of the session also OPENS the page the pill
            // names (once); every later one is the pill alone, whose click reopens it.
            RequestLicensePageOnFirstTrialEndBlock(trialEnded);
            ResetHotkeyState?.Invoke();
            return;
        }

        // Update-apply gate (UPD-1 Phase 3): refuse new recordings while an update is being applied,
        // so the updater doesn't yank the binary out mid-recording. Mirrors the license gate's
        // refusal shape (pill + hotkey reset) — but keeps the status write the license gate gave
        // up in LIC-29: "an update is being applied" dies with the process, so it cannot go stale
        // the way "your free trial has ended" did once the user activated a key.
        if (App.IsExclusiveMaintenanceActive())
        {
            const string updateMsg = "Update in progress — please wait";
            Logger.Information("Recording blocked: an update is being applied.");
            // Transient maintenance refusal → Warning (auto-hides), not a persistent red pill
            // (Codex review 2026-07-20).
            EmitMiniRecorderError(updateMsg, MiniRecorderTone.Warning);
            ResetHotkeyState?.Invoke();
            return;
        }

        // Latency probe: arm only AFTER the gates above pass (a blocked start
        // must never arm) and only for a fresh hotkey-dispatched start — tray/UI
        // starts carry no timestamp because ToggleRecordAsync consumed it.
        // The pill probe measures hotkey → pill window shown; the start probe measures hotkey →
        // audio actually flowing. Both arm from the same gate-passed, fresh-hotkey condition, and
        // the start probe's token is what makes its later marks attempt-scoped.
        long startProbeToken = 0;
        if (hotkeyDispatchTimestamp is { } hotkeyTs
            && MiniRecorderShowLatencyProbe.Instance.IsFresh(hotkeyTs))
        {
            MiniRecorderShowLatencyProbe.Instance.Arm(hotkeyTs);
            startProbeToken = RecordingStartLatencyProbe.Instance.Arm(hotkeyTs);
        }

        // Only the success path leaves the start probe armed — it is disarmed in the finally
        // below on every failure/cancel/supersede exit, so an abandoned attempt can never keep
        // the probe armed into the next recording. On success the terminal mark comes later,
        // from the capture thread's first buffer.
        var startedOk = false;
        CancellationTokenSource? startupCts = null;
        // Codex 2026-05-19 review-loop round-6: declared at method scope (not
        // inside try) so the catch blocks can pass it to
        // HandleStartRecordingFailureCleanup and delete a half-written WAV that
        // AudioRecorderService.StartRecordingAsync created via WaveFileWriter
        // BEFORE its pre-StartRecording cancellation/failure observation point.
        string? startupRecordingPath = null;
        try
        {
            ClearRedoState();
            // A new recording supersedes any failure-retained retry (REL-12) — the
            // retained WAV is deleted fire-and-forget (never sync IO on this hot path).
            ClearRetryState(deleteWav: true);

            // Capture the target app's window BEFORE any UI changes that could steal focus.
            // GetForegroundWindow is a fast kernel call (non-blocking even under contention).
            //
            _targetWindowHandle = NativeInterop.GetForegroundWindow();

            // UI-7: showing any picker calls RestoreMainWindow(), so with a dialog open the line
            // above names VOICEWINK and the dictation would paste into our own window. There is no
            // honest target here — the pre-dialog window is known, but targeting a handle stored
            // across a dialog is exactly what AdoptPasteTarget forbids (Windows recycles handles;
            // an identity snapshot built around an old one validates whatever now owns it), and a
            // revision that tried it behind an identity re-check was killed in review because the
            // check was discarded when the snapshot below re-probed the bare handle. So: no target,
            // and the transcript goes to the clipboard with a pill saying so. Same answer TRN-17
            // already gives when it finds VoiceWink in front, and an owner decision (2026-08-16).
            //
            // Gated on a picker being OPEN, not merely on the foreground being ours: dictating into
            // VoiceWink's own text boxes (the AI Enhancement prompt editor) is a working behaviour
            // and must keep pasting there. That distinction is the whole reason this is not the
            // blanket "never target our own process" rule, which the owner rejected.
            // Classify THE HANDLE WE JUST CAPTURED, never a second GetForegroundWindow(). The two
            // reads can disagree — the user activating their editor between them makes the target
            // VoiceWink and the decision "not ours", so suppression is skipped and the transcript
            // pastes into VoiceWink, which is the exact defect this gate exists to prevent (Codex
            // round 3). ForegroundOwnership.IsOurOwnProcess exists for this and its own doc says
            // so; an earlier revision of this line called the other overload anyway.
            _pasteSuppressedByPicker = Helpers.PickerPasteSuppression.ShouldSuppress(
                pickerDialogVisible: _pickerDialogVisible != 0,
                foregroundIsOurOwnProcess: Helpers.ForegroundOwnership.IsOurOwnProcess(_targetWindowHandle));
            if (_pasteSuppressedByPicker)
            {
                Logger.Information("Recording started under an open picker — no external target, copying to the clipboard");
                _targetWindowHandle = IntPtr.Zero;
            }

            _targetWindowCapturedAtUtc = DateTime.UtcNow;

            // Capture target PID synchronously from the just-captured HWND, BEFORE
            // the pill is shown (`IsMiniRecorderVisible = true` below) and BEFORE
            // any await yields the UI thread. Doing this here means the PID always
            // refers to the user's
            // actual target — not VoiceWink itself (which the pill could give
            // focus to) and not whatever app has focus when a later worker runs.
            // GetWindowThreadProcessId is a fast kernel call, safe to keep sync.
            // Fed into DetectActiveAppModeAsync below so the slow
            // Process.GetProcessById lookup runs off the UI thread.
            int? targetProcessId = null;
            var targetThreadId = NativeInterop.GetWindowThreadProcessId(_targetWindowHandle, out var rawPid);
            if (rawPid != 0) targetProcessId = (int)rawPid;

            // Identity snapshot for the paste-time liveness/UIPI gates. Hwnd/pid/tid only —
            // all from the two kernel calls above; the class name is enriched async after
            // the pill is visible (see EnrichPasteTargetSnapshotClass).
            _pasteTargetSnapshot = _targetWindowHandle != IntPtr.Zero && rawPid != 0
                ? new PasteTargetSnapshot(_targetWindowHandle, rawPid, targetThreadId, null, _targetWindowCapturedAtUtc)
                : null;

            // PILL-1: the pill must never wear a previous recording's target label.
            // Reset synchronously at capture (also fences stale resolvers via the
            // generation bump); the async resolution kicks off after the pill is
            // visible (next to class enrichment below).
            ResetPasteTargetAppName();

            // LogRecordingStartFocus calls ClipboardService.CaptureFocusInfo which uses
            // GetGUIThreadInfo + GetClassName. These have been observed taking up to 20s
            // under heavy CPU contention on the target's thread. Diagnostic only — fire-
            // and-forget so it can't delay the pill visibility on the UI thread.
            var targetForLog = _targetWindowHandle;
            _ = Task.Run(() => LogRecordingStartFocus(targetForLog));

            // Capture the UIA-focused DOM element so we can re-focus it before paste.
            // Win32-only restoration (SetForegroundWindow) can't reach DOM focus inside
            // Chromium-based targets; UIA can. Failure is non-fatal — paste falls
            // through to current Ctrl+V behavior. NON-BLOCKING: the capture is
            // enqueued on the UIA bridge's MTA worker immediately (so it still
            // reflects recording-start focus) but the 10–500 ms cross-process wait
            // no longer runs on the UI thread ahead of the pill — the paste path
            // materializes the element via CapturedFocusSlot.ResolveAsync.
            // UI-7: skip it entirely when the picker gate fired. The only element to capture would
            // be a control inside VoiceWink's own dialog — for the redo and image pickers that is
            // an editable TextBox (Kimi verification round) — and handing that to anything
            // downstream is how the transcript ends up in our own prompt editor. Nothing consumes
            // an empty slot: the PST-11 retention below is already gated on a non-zero target, and
            // the copy-only branch never reaches the paste path at all.
            if (!_pasteSuppressedByPicker)
                _focusSlot.BeginForRecording();

            // PST-11: a new recording supersedes the previous one's retained element. Its own
            // explicit call, because BeginForRecording only reaches _focusSlot — a separate field
            // needs a separate release, and the reader who assumes otherwise is the one who leaks.
            _redoFocusRetention.Release();

            // PILL-2: probe the captured element's editability off the hot path and
            // tint the pill's "→ AppName" label (green = text box confirmed).
            ResolvePasteTargetEditability();

            // Install the cancellation token source BEFORE flipping the pill visible.
            // Otherwise a hotkey-during-Starting cancel between the pill flip and the
            // CTS install can't reach the in-flight startup (the previous _modelDownloadCts
            // is still pointing at a stale CTS).
            // The CTS hand-over + preflight-slot clear run under the publish gate (Codex diff
            // r2): replacing the CTS is what invalidates an in-flight preflight's ownership, and
            // it must not interleave with that preflight's locked publish.
            lock (_preflightPublishGate)
            {
                _modelDownloadCts?.Cancel();
                _modelDownloadCts?.Dispose();
                startupCts = new CancellationTokenSource();
                _modelDownloadCts = startupCts;
                // AUD-6: a stale preflight task from a CANCELLED warm recording must never be
                // awaited by a later stop — cleared on EVERY start (the warm branch re-sets it
                // after its kick).
                _recordingPreflight = null;
            }
            _warmStartGate = null;
            var startupToken = startupCts.Token;

            // AUD-6 warm path: claim the standing capture. Success ⇒ audio has been flowing into
            // the RAM ring since before this line — the state flips to Recording immediately, the
            // WAV drain starts at the HOTKEY instant out of the ring, and the whole preflight
            // (App-Mode detect, language, model ensure-load) runs DURING the recording instead of
            // in front of it. Any refusal (setting off / not running / rebuilding / stale stream)
            // ⇒ the cold path below, byte-for-byte unchanged. Failures inside the warm body THROW
            // into this method's existing catches — a warm writer-create failure presents exactly
            // like a cold start failure.
            var warmClaim = _standingCapture.TryClaim(
                hotkeyDispatchTimestamp ?? global::System.Diagnostics.Stopwatch.GetTimestamp());
            if (warmClaim != null)
            {
                // The gate is assigned SYNCHRONOUSLY when the warm body first yields (the method
                // runs inline to that point, so the flip has already happened) — a stop landing
                // in any later yield awaits it before touching recording state (Codex diff r3).
                var warmStart = StartRecordingWarmAsync(
                    warmClaim, startupCts, startupToken, targetProcessId, startProbeToken,
                    pathCreated: p => startupRecordingPath = p);
                _warmStartGate = warmStart;
                await warmStart;
                startedOk = true;
                return;
            }

            // AUD/latency: start resolving the capture device NOW so it overlaps the pre-flight
            // below (App Mode detect, language/model resolution) instead of running after it —
            // those legs are independent, because App Mode can override the transcription MODEL
            // but never the MICROPHONE. If a device-override feature is ever added, this overlap
            // becomes invalid and must be revisited.
            //
            // The declaration point is load-bearing (Kimi plan review): it must precede the first
            // await after this line, or the supersession returns further down would leave the
            // resolution undisposed. `using` covers every one of them, including the two
            // superseded catch paths.
            using var pendingDevice = new Services.Audio.PendingDeviceResolution(
                _deviceSelection.ResolveForRecordingAsync, startupToken);

            // MarkPreShow BEFORE the Starting flip: since the controller refactor the pill becomes
            // visible when RecordingState → Starting drives PublishPipeline → Show() (no longer on the
            // IsMiniRecorderVisible flag). Stamping the pre-show boundary first keeps the latency log's
            // preShow positive instead of -1 (Codex r4 #2, non-blocking diagnostic).
            MiniRecorderShowLatencyProbe.Instance.MarkPreShow();
            RecordingState = RecordingState.Starting;
            StatusText = "Starting...";
            IsMiniRecorderVisible = true;  // Show pill early so download progress is visible

            // Class enrichment AFTER the pill is visible — GetClassName has been observed
            // blocking under target contention, so it must never delay the pill.
            EnrichPasteTargetSnapshotClass();
            ResolvePasteTargetAppName();

            // Detect App Mode config AFTER the pill is visible. The slow part is
            // Process.GetProcessById, which blocked the UI thread for 2.25 s under
            // heavy contention on 2026-05-21 (Excel + OBS + Teams active). Running
            // it post-pill makes the pill visible immediately, but a queued cross-DPI
            // MiniRecorder recreate (from the `IsMiniRecorderVisible = true` setter
            // immediately above) also needed the dispatcher to pump — a sync block
            // here starved that queue. DetectActiveAppModeAsync runs the slow
            // lookup on a thread-pool
            // worker bounded to 250 ms; the await yields the UI thread so the
            // dispatcher can pump the queued recreate, and abandonment under extreme
            // contention falls back to "no App Mode override" (benign degradation,
            // equivalent to the existing no-match branch).
            // Pre-flight extracted VERBATIM to RunRecordingPreflightAsync (AUD-6) so the warm
            // path can run it concurrently with the live recording; the cold path awaits it
            // HERE, at the exact position the inline block occupied. Superseded carries the old
            // inline supersession `return`s out of the extracted method — the cold session must
            // still exit before it opens a microphone it no longer owns.
            if (await RunRecordingPreflightAsync(
                    startupCts, startupToken, targetProcessId, () => _pendingPromptOverride)
                == PreflightOutcome.Superseded)
            {
                return;
            }

            // Yield to the UI thread so the MiniRecorder pill renders before
            // GetCurrentDevice() blocks the thread.
            await Task.Delay(1);
            if (_modelDownloadCts != null && !ReferenceEquals(_modelDownloadCts, startupCts))
                return;
            startupToken.ThrowIfCancellationRequested();

            // Create recording file path. Recordings dir is pre-created at startup
            // (App.xaml.cs initializer); EnsureRecordings re-runs here as cheap self-heal
            // against mid-session deletion (manual cleanup, AV quarantine, drive remount)
            // — Directory.CreateDirectory is idempotent and ~50µs when the dir exists.
            // Codex 2026-05-19 review-loop round-8: filename includes ticks
            // suffix so a cancel-then-immediate-retry within the same wall-
            // clock second produces distinct paths. Without this, two attempts
            // at HH:MM:SS would share a path; the older one's
            // TryDeleteStartupRecording could then delete the newer session's
            // WAV mid-record, leaving capture running with no file on disk.
            // DateTime.Now.Ticks is 100-ns precision and de-collides the
            // common cases observed in practice (rapid cancel-then-retry).
            // It is NOT a strict uniqueness primitive — DateTime.Now is wall-
            // clock and could go backwards across DST/NTP adjustments — so it
            // is a defense-in-depth layer for the delete-the-wrong-WAV case
            // only, dropping the collision probability from "every second-
            // aligned retry" to "essentially never".
            //
            // AUD-11: this comment used to name the superseded branch's
            // CurrentFilePath-vs-startupRecordingPath comparison as "the actual
            // correctness guard". It was not one. That comparison read state
            // OUTSIDE the recorder's lock and then called an unscoped stop, so
            // it could pass and still stop a DIFFERENT session by the time the
            // stop ran — which is what lost a recording on 2026-08-06. The
            // guard is now the recorder's session id, compared inside the lock
            // that stops (StopRecordingIfCurrentAsync).
            var now = DateTime.Now;
            _currentRecordingPath = Path.Combine(AppPaths.EnsureRecordings(),
                $"recording_{now:yyyyMMdd_HHmmss}_{now.Ticks}.wav");
            // Codex 2026-05-19 review-loop round-5 P1: capture the path into the
            // method-scope local BEFORE awaiting the recorder. CancelRecordingAsync
            // (which may run on the UI thread between our awaits) can null
            // `_currentRecordingPath` after best-effort-deleting a still-open
            // WAV file. Without this local, the post-await cancel branch AND
            // the catch blocks below would lose the path and leave canceled mic
            // audio under %LOCALAPPDATA%\VoiceWink\Recordings\.
            startupRecordingPath = _currentRecordingPath;

            RecordingStartLatencyProbe.Instance.MarkPreflightDone(startProbeToken);

            // AUD-1: selection-aware bounded resolution — RecordingDeviceSelectionService owns
            // the policy (pinned device, fallback classification, 500 ms bounds; BoundedComCall's
            // continuation disposes a late-arriving MMDevice). Null device is acceptable —
            // _recorder.StartRecordingAsync passes it through to WasapiCapture's internal
            // default-device resolution, unchanged from the pre-AUD-1 path.
            //
            // Prefer the hoisted resolution started before the pre-flight, but only while it is
            // young enough to trust (HoistedResolutionPolicy). A slow pre-flight — typically a
            // local Whisper model load — means the device choice may be stale in a way NO
            // exception would reveal (the default moved, yet the old endpoint still activates
            // fine), so past the bound we discard it and resolve fresh: exactly the sequential
            // behaviour and cost this path always had.
            var resolution = await pendingDevice.TryTakeAsync(startupToken)
                             ?? await _deviceSelection.ResolveForRecordingAsync(startupToken);

            RecordingStartLatencyProbe.Instance.MarkDeviceResolved(startProbeToken);
            // AUD-11: the session id is this attempt's handle on the capture it started — the only
            // thing that lets a later stop say "mine" rather than "whatever is recording now".
            long startedSessionId;
            try
            {
                startedSessionId = await _recorder.StartRecordingAsync(
                    _currentRecordingPath, resolution.Device, startupToken, resolution.Kind, startProbeToken,
                    resolution.DeviceTag, resolution.EndpointId);
            }
            catch (Exception ex) when (RecordingDeviceSelectionService.ShouldRetryWithDefault(resolution.Kind, ex))
            {
                // The resolve→activation race: the SELECTED endpoint vanished between
                // GetDeviceById and WASAPI init. One retry with the system default — a stale
                // selection must never fail the recording; a second failure propagates as today.
                Logger.Warning("Selected device failed to activate ({ErrorType}) — retrying once with system default",
                    ex.GetType().Name);
                // AUD-16: the tag comes back WITH the device, computed on the worker that produced
                // it. Carrying the resolution's tag here would attribute this recording to the
                // selected device it provably failed to open — the one way this feature could be
                // worse than no feature — and tagging it afterwards would race the recorder's
                // ownership of that device.
                var (fallbackDevice, fallbackTag, fallbackEndpointId) = await _deviceSelection.ResolveDefaultWithTagAsync(startupToken);
                // Reassign: the first attempt's session died with its throw, so carrying that id
                // forward would arm a stop against a session that never went live (Kimi r3).
                startedSessionId = await _recorder.StartRecordingAsync(
                    _currentRecordingPath, fallbackDevice, startupToken,
                    RecordingDeviceResolutionKind.FallbackToDefault, startProbeToken, fallbackTag,
                    fallbackEndpointId);
                resolution = resolution with
                {
                    Kind = RecordingDeviceResolutionKind.FallbackToDefault,
                    DeviceTag = fallbackTag,
                    EndpointId = fallbackEndpointId,
                };
            }
            catch (Exception ex) when (resolution.Kind == RecordingDeviceResolutionKind.SelectedDevice
                                       && ex is not OperationCanceledException)
            {
                // A selected-device start failure OUTSIDE the typed retry allowlist. Behavior
                // unchanged (propagates like today) — this line exists so UAT/support bundles can
                // spot a real device-removal surfacing as an unexpected exception type that
                // should widen ShouldRetryWithDefault's allowlist (Kimi diff review r2).
                Logger.Warning("Selected-device start failed with non-retried {ErrorType} — propagating unchanged",
                    ex.GetType().Name);
                throw;
            }
            // Notice AFTER a successful start (the pipeline pill is live, so the notice renders
            // inside it), one-shot per device id per run — inform, never nag.
            MaybeNotifyDeviceFallback(resolution);

            // Codex 2026-05-19 review-loop round-4 finding: there's a residual
            // race window between `_capture.StartRecording()` (inside the call
            // above) and this line. If the user cancelled during that window —
            // or if a newer session has taken over by replacing
            // `_modelDownloadCts` — the recorder is now capturing against the
            // user's cancel intent. The AudioRecorderService.StartRecordingAsync
            // side has its own pre-StartRecording ThrowIfCancellationRequested
            // check; this covers the post-StartRecording window the inner check
            // can't see.
            //
            // Codex 2026-05-19 review-loop round-8 finding: the two reasons we
            // get here need DIFFERENT cleanup. Same-session cancel owns the
            // recorder + UI + paths and should do the full cleanup. Superseded
            // means a NEWER session has taken over those resources and we MUST
            // NOT touch them — stopping the recorder would stop the newer
            // session's recording, nulling _currentRecordingPath would lose
            // the newer session's file, releasing UIA focus would drop the
            // newer session's paste target. Only the disk file for OUR
            // superseded attempt is ours to clean up.
            var supersededByNewerSession =
                !ReferenceEquals(_modelDownloadCts, startupCts) && _modelDownloadCts != null;

            if (supersededByNewerSession)
            {
                // Codex 2026-05-19 review-loop round-9 finding: a newer
                // _modelDownloadCts existing doesn't prove the newer session
                // OWNS the recorder. The newer session installs its CTS
                // BEFORE calling _recorder.StartRecordingAsync (model load /
                // download happens between). So when we get here, the
                // recorder may STILL belong to our (older) attempt — and
                // walking away with path-only cleanup would leave it
                // silently capturing under Idle UI until the newer session
                // finally calls StartRecordingAsync (which would see
                // IsRecording true and route through "Already recording,
                // stopping previous session"). To close that window:
                //   - If the recorder still has OUR startupRecordingPath,
                //     stop it ourselves before deleting the file.
                //   - If CurrentFilePath has already advanced past us, the
                //     newer session owns the recorder; we only do path-only
                //     cleanup to avoid stopping their capture.
                // AUD-11: ask the recorder to stop OUR session, decided under its own lock. The
                // previous shape — read IsRecording/CurrentFilePath here, then call the unscoped
                // StopRecordingAsync — is the bug: both reads happen before a lock wait that on
                // 2026-08-06 lasted 3.1 s, and by the time the stop ran the successor's capture
                // was the live one. It was stopped 5 ms after starting and 17 s of dictation went
                // into a dead recorder. Never reintroduce a check here followed by a plain stop.
                try
                {
                    if (await _recorder.StopRecordingIfCurrentAsync(startedSessionId))
                        Logger.Information("Recording startup superseded but recorder still ours — stopped our capture before path-only cleanup");
                    else
                        Logger.Information("Recording startup superseded and recorder already moved on — path-only cleanup, newer session owns the rest");
                }
                catch (Exception ex) { Logger.Information("Stop-after-superseded threw: {ExType}", ex.GetType().Name); }
                TryDeleteStartupRecording(startupRecordingPath);
                return;
            }

            if (startupToken.IsCancellationRequested)
            {
                Logger.Information("Recording cancelled after recorder started — stopping capture and cleaning up");
                // Scoped for the same reason as the superseded branch above: this continuation can
                // resume after a newer attempt has taken the recorder. A false return just means
                // someone else owns the capture — the WAV below is ours to delete either way.
                try { await _recorder.StopRecordingIfCurrentAsync(startedSessionId); }
                catch (Exception ex) { Logger.Information("Stop-after-cancel-during-startup threw: {ExType}", ex.GetType().Name); }

                // Path-only delete AFTER the recorder has stopped (which
                // closes the WaveFileWriter). CancelRecordingAsync may have
                // already nulled _currentRecordingPath and best-effort-failed
                // to delete the still-open file; this is the second-chance
                // deletion that actually succeeds.
                TryDeleteStartupRecording(startupRecordingPath);

                // TOCTOU defense (2026-05-20 fix-loop): `await
                // _recorder.StopRecordingAsync()` above yields the UI thread.
                // A FAST cancel-then-retry can run StartRecordingAsync_B on
                // the UI thread during that yield, installing
                // `_modelDownloadCts = ctsB`, setting `_currentRecordingPath
                // = path_B`, and flipping `RecordingState` back to Starting
                // before our continuation resumes. Without this re-check we
                // would then clobber B's path / focus / pill / state. Only
                // touch shared mutable state when we're still the current
                // session (or no session has taken over).
                if (ReferenceEquals(_modelDownloadCts, startupCts) || _modelDownloadCts == null)
                {
                    _currentRecordingPath = null;
                    _activeAppModeConfig = null;
                    ReleaseCapturedFocusedElement(CapturedFocusOwner.Recording);

                    // Codex 2026-05-19 review-loop round-5 P2: CancelRecording-
                    // Async leaves IsMiniRecorderVisible=true on Starting
                    // cancels, on the assumption that the StartRecordingAsync
                    // catch path will show an auto-hiding error. This branch
                    // returns OUTSIDE the catch path, so without this we'd
                    // strand the pill onscreen (visible + Idle =
                    // MiniRecorderLifecycleState.VisibleState). Explicit-user-
                    // cancel UX is a quiet dismiss, not an error pill.
                    MarkTerminalPresentation(); // IMG-BG epoch fence
                    IsMiniRecorderVisible = false;
                    RecordingState = RecordingState.Idle;
                }
                else
                {
                    Logger.Information("Same-session cancel raced with a newer session — recorder is stopped + file deleted, but shared VM state belongs to the newer session and is left untouched.");
                }
                return;
            }

            RecordingStartedAtUtc = DateTime.UtcNow;
            RecordingState = RecordingState.Recording;
            // Set before any further UI mutation: capture is already live, so the start probe
            // must stay armed for its terminal mark (the capture thread's first buffer, which
            // may already have fired). Anything that throws below this point is a post-start
            // failure, not an unstarted attempt — and must not disarm a live measurement.
            startedOk = true;
            StatusText = "Recording...";
            Logger.Information("Recording started: {Path}", _currentRecordingPath);

            // AUD-11: the capture is provably live by now (the start throws otherwise), so the
            // watchdog arms WITH the timer — its prime tick runs synchronously, and that tick
            // catching an already-dead recorder is exactly the incident's shape: the successor's
            // capture had been stopped three milliseconds before this line ran. The warm path is
            // the mirror image and must pass 0 here; see the site there.
            StartMeterTimer(startedSessionId);

            PlayStartFeedbackThenMute();
        }
        catch (Services.Audio.AudioCaptureUnavailableException ex)
        {
            // Newer-session guard — same as the generic handler below.
            if (startupCts != null && _modelDownloadCts != null && !ReferenceEquals(_modelDownloadCts, startupCts))
            {
                Logger.Information("Recording startup abandoned because a newer session took over");
                // Codex 2026-05-19 review-loop round-7: path-only WAV delete
                // before the early return. The newer session owns
                // _currentRecordingPath / RecordingState / UIA focus / Power-
                // Mode override now, so we MUST NOT call the full
                // HandleStartRecordingFailureCleanup helper here — it would
                // clobber their state. Only the disk file for OUR superseded
                // attempt is ours to clean up.
                TryDeleteStartupRecording(startupRecordingPath);
                return;
            }

            Logger.Warning("Recording start failed: audio device contention. {Message}", ex.Message);
            HandleStartRecordingFailureCleanup(startupRecordingPath);
            StatusText = "Microphone unavailable";
            // ShowMiniRecorderError invokes MiniRecorderWindow.ShowError which calls Show(); an
            // Error-tone pill PERSISTS (no auto-hide) until the corner × or a newer presentation
            // (ERR-PERSIST). Do NOT manually set IsMiniRecorderVisible = false here — doing so would
            // immediately hide the actionable error before the user can read it.
            EmitMiniRecorderError("Microphone unavailable — try again", MiniRecorderTone.Error);
            ResetHotkeyState?.Invoke();
        }
        catch (Exception ex)
        {
            if (startupCts != null && _modelDownloadCts != null && !ReferenceEquals(_modelDownloadCts, startupCts))
            {
                Logger.Information("Recording startup abandoned because a newer session took over");
                // Codex 2026-05-19 review-loop round-7: path-only WAV delete
                // before the early return. See parallel comment in the
                // AudioCaptureUnavailableException catch above.
                TryDeleteStartupRecording(startupRecordingPath);
                return;
            }

            Logger.Error(ex, "Failed to start recording");
            HandleStartRecordingFailureCleanup(startupRecordingPath);

            // Deliberately cause-NEUTRAL: this catch also covers model download/load failures
            // (EnsureModelLoadedAsync runs inside the same try), so naming the microphone here
            // would be wrong half the time. Mic-specific copy belongs to the
            // AudioCaptureUnavailableException catch above, which knows the cause.
            var errorMsg = ex is OperationCanceledException
                ? "Recording cancelled"
                : "Recording failed";
            // One string for both surfaces — a longer window-status variant would only restate
            // the pill in a second style (copy review 2026-07-25).
            StatusText = errorMsg;

            // Show error in MiniRecorder. ShowError() calls Show() internally, so it works
            // even if CancelRecording() did not hide the window (Starting state path). Mark terminal so
            // this error's epoch supersedes any prior affordance timer (ERR-PERSIST).
            // A user CANCEL is a normal transient outcome, not a failure — amber Warning (auto-hides),
            // never a persistent red pill (Codex review 2026-07-20); a genuine start failure stays Error.
            MarkTerminalPresentation();
            var startFailureTone = ex is OperationCanceledException
                ? MiniRecorderTone.Warning
                : MiniRecorderTone.Error;
            ShowMiniRecorderError?.Invoke(errorMsg, startFailureTone, PillMessageAction.None);

            ResetHotkeyState?.Invoke();  // Idempotent if CancelRecording already called it
        }
        finally
        {
            // Every non-success exit — the five supersession returns, the cancel-after-start
            // path, and both catches. Token-scoped, so a SUPERSEDED attempt disarming here
            // cannot touch the measurement of the attempt that replaced it.
            if (!startedOk)
                RecordingStartLatencyProbe.Instance.Disarm(startProbeToken);
        }
    }

    /// <summary>
    /// AUD-6 warm start body — the instant path. The claim proves audio is already flowing into
    /// the standing ring, so the state flips to Recording BEFORE any disk touch and the WAV drain
    /// starts at the hotkey instant out of the ring. Failures THROW into
    /// <see cref="StartRecordingAsync"/>'s existing catches (a warm writer-create failure
    /// presents exactly like a cold start failure); <paramref name="pathCreated"/> assigns the
    /// caller's <c>startupRecordingPath</c> local the moment the path exists, so those catches'
    /// half-written-WAV cleanup covers the warm path too.
    /// </summary>
    private async Task StartRecordingWarmAsync(
        Services.Audio.StandingCaptureClaim claim,
        CancellationTokenSource startupCts,
        CancellationToken startupToken,
        int? targetProcessId,
        long startProbeToken,
        Action<string> pathCreated)
    {
        // Staleness guard (plan review round 3): a superseded/faulted preflight must never leave
        // a PREVIOUS recording's values visible to THIS recording's stop path — nulled before the
        // concurrent preflight below can race anything, under the publish gate so a stale
        // publish can never interleave with the reset (Codex diff r2).
        lock (_preflightPublishGate)
        {
            _activeAppModeConfig = null;
            _recordingModelSnapshot = null;
            _recordingLanguageOverride = null;
        }

        // PRM-5 (Kimi diff r1 #2): snapshot the prompt-hotkey override AT THE CLAIM INSTANT.
        // The preflight reads it after a 250 ms await — on the warm path that window overlaps
        // LIVE audio, so a prompt hotkey pressed mid-recording could steer this recording's
        // language/model against the documented rule. The cold path keeps its live read
        // (today's Starting-phase semantics, byte-for-byte).
        var promptOverrideAtClaim = _pendingPromptOverride;

        // Preflight kicked (and its slot ASSIGNED) here, in the synchronous UI-thread prefix
        // before the flip (Kimi diff r3 #2): assigning after the recorder await ran on a pool
        // continuation while every reader/clearer assumes UI-thread state — and the field is a
        // nullable ValueTuple, so even the write wasn't atomic. Bonus: the model load starts a
        // few ms earlier. StopAndTranscribeAsync awaits this task before it consumes the
        // snapshot fields; the forget-continuation observes faults on the paths nobody awaits.
        var preflight = RunRecordingPreflightAsync(
            startupCts, startupToken, targetProcessId, () => promptOverrideAtClaim);
        _recordingPreflight = (preflight, startupCts);
        _ = preflight.ContinueWith(
            t => Logger.Warning("Recording preflight faulted in background: {ExType}",
                t.Exception?.InnerException?.GetType().Name ?? "Unknown"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        // Everything from the flip to the ownership hand-off can throw (disk, UIA); until the
        // recorder call is REACHED the claim is ours, and losing it un-detached would refuse
        // every later recording as AlreadyClaimed and strand latched standing teardowns
        // (Codex diff r1 blocker).
        var claimOwnedHere = true;
        string? warmRecordingPath = null;
        try
        {
            // Instant flip — the same Starting→Recording sequence the presentation machinery
            // always sees, microseconds apart, and deliberately BEFORE the recording path is
            // created: the pill must not wait on a disk touch (EnsureRecordings can stall
            // under AV).
            MiniRecorderShowLatencyProbe.Instance.MarkPreShow();
            RecordingState = RecordingState.Starting;
            StatusText = "Starting...";
            IsMiniRecorderVisible = true;
            RecordingStartedAtUtc = DateTime.UtcNow;
            RecordingState = RecordingState.Recording;
            StatusText = "Recording...";
            RecordingStartLatencyProbe.Instance.MarkRecordingLive(startProbeToken);

            // Post-flip enrichment, parity with the cold path (final check R5): the pill's
            // "→ AppName" label and class enrichment must not wait on — or delay — the flip.
            EnrichPasteTargetSnapshotClass();
            ResolvePasteTargetAppName();

            // AUD-11: 0 — no capture is attached yet on this path, and this call primes a tick
            // SYNCHRONOUSLY. Arming a real session here would make every warm start report a dead
            // recorder: nothing is capturing, and LastDataReceivedAt still holds the PREVIOUS
            // recording's stamp. Instant recording ships on, so that is the default path. The id
            // is assigned after the attach below.
            StartMeterTimer(0);
            // Beep at the flip (the instant cue); the delayed MUTE is deliberately deferred
            // until after the recorder attach below — see PlayStartFeedbackThenMute's doc.
            _soundFeedback.PlayStartSound();

            // The one disk-touching stretch, safely behind the flip: path creation + writer
            // attach. The ring covers the gap — audio since the hotkey instant replays into the
            // writer. pathCreated assigns the caller's startupRecordingPath local, so the outer
            // catches' half-written-WAV cleanup covers everything past this line.
            var now = DateTime.Now;
            _currentRecordingPath = Path.Combine(AppPaths.EnsureRecordings(),
                $"recording_{now:yyyyMMdd_HHmmss}_{now.Ticks}.wav");
            warmRecordingPath = _currentRecordingPath;
            pathCreated(warmRecordingPath);

            // The recorder owns the claim from ENTRY, both outcomes (its documented contract) —
            // flip the local BEFORE the call so a throw from inside it is not double-detached.
            claimOwnedHere = false;
            var warmSessionId = await _recorder.StartFromStandingAsync(
                warmRecordingPath, claim, startupToken, startProbeToken);

            // AUD-11: arm the dead-capture watchdog only NOW — the mirror of the `StartMeterTimer(0)`
            // above, and the ordering the whole warm design turns on.
            //
            // Ownership-guarded like every other post-await write in this file: the await yielded
            // the UI thread, and a stale continuation must not resurrect its session id over the
            // newer attempt's (Codex r3).
            if (ReferenceEquals(_modelDownloadCts, startupCts))
                _stallMonitor.AttachCapture(warmSessionId);
        }
        catch
        {
            if (claimOwnedHere)
            {
                try { claim.Detach(); }
                catch (Exception dex) { Logger.Debug(dex, "Detaching unclaimed warm claim failed"); }
            }
            // The warm path starts the meter timer BEFORE the recorder attach (the cold path
            // only after a successful start), so the shared failure cleanup — which never needed
            // to stop a timer — would leave it ticking and RecordingStartedAtUtc set.
            StopMeterTimer();
            // The preflight was kicked before the flip — a failed start makes it pointless
            // (and its model download must not keep running for a recording that never was).
            DiscardRecordingPreflight();
            throw; // the caller's catches own presentation + WAV cleanup
        }

        Logger.Information("Recording started: {Path}", warmRecordingPath);

        // Only now — the session provably exists — may the delayed mute arm (Codex diff r3).
        ScheduleDelayedMute();

        // AUD-1 notice parity (Kimi diff r1 #1): a pinned-but-missing mic resolved
        // FallbackToDefault at the STANDING capture's start — surface it through the same
        // one-shot latch the cold path uses, now that the pipeline pill is live to render it.
        MaybeNotifyDeviceFallback(claim.ResolutionInfo);
    }

    /// <summary>
    /// The recording pre-flight, extracted VERBATIM from <see cref="StartRecordingAsync"/>
    /// (AUD-6): App-Mode detect → PRM-5 language resolution → model snapshot → local model
    /// ensure-load. The cold path awaits it inline at the exact position the block occupied; the
    /// warm path runs it concurrently with the live recording. Every original supersession guard
    /// is preserved — as <see cref="PreflightOutcome.Superseded"/> returns, because an extracted
    /// <c>return</c> no longer exits the caller (final check R1).
    /// </summary>
    /// <param name="resolvePendingPromptOverride">How this run reads the prompt-hotkey override:
    /// the COLD path passes a live read (<c>() => _pendingPromptOverride</c> — today's
    /// Starting-phase semantics, byte-for-byte), the WARM path a snapshot captured at the claim
    /// instant so a prompt hotkey pressed over LIVE audio cannot steer this recording's
    /// language/model (PRM-5; Kimi diff r1 #2).</param>
    private async Task<PreflightOutcome> RunRecordingPreflightAsync(
        CancellationTokenSource startupCts, CancellationToken startupToken, int? targetProcessId,
        Func<CustomPrompt?> resolvePendingPromptOverride)
    {
        var detectedAppMode = await _appModeManager.DetectActiveAppModeAsync(
            targetProcessId,
            TimeSpan.FromMilliseconds(250),
            startupToken);

        // Superseded-session guard: the await above yielded the UI thread; a
        // newer hotkey press during that yield could have replaced
        // _modelDownloadCts. Writing _activeAppModeConfig from this stale
        // invocation would clobber the newer session's value (read later in
        // StopAndTranscribeAsync's transcribe path). Same pattern applied at
        // every post-await ownership check in this method (model preload,
        // EnsureModelLoadedAsync) and in StartRecordingAsync —
        // search for `!ReferenceEquals(_modelDownloadCts, startupCts)`.
        if (_modelDownloadCts != null && !ReferenceEquals(_modelDownloadCts, startupCts))
            return PreflightOutcome.Superseded;
        startupToken.ThrowIfCancellationRequested();

        // Everything below computes into LOCALS; the shared snapshot fields are written ONCE, at
        // the locked publish at the end (Codex diff r2 — no SynchronizationContext means these
        // continuations run on the pool, and a mid-run field write can race the UI thread's
        // next-recording reset).
        var appMode = detectedAppMode;

        // PRM-5: resolve the recording's speech-recognition language HERE, before the local
        // preload below — that preload builds the Whisper processor for a specific language, and
        // resolving later would let it build for one and transcribe with another (Codex plan
        // review round 2: a 2-8 s rebuild, or worse, recognition in the wrong language).
        // The pre-recognition prompt priority is hotkey → App-Mode-linked → globally active;
        // trigger words are matched in the TRANSCRIPT, so they cannot participate. Reusing
        // PromptRunSnapshot.Resolve keeps that priority table in one already-pinned place.
        var startPrompts = Helpers.PromptRunSnapshot.Capture(_enhancement.GetPrompts(), resolvePendingPromptOverride());
        var (startPrompt, startPromptSource) = startPrompts.Resolve(
            triggerFired: false,
            triggerPrompt: null,
            appModePrompt: startPrompts.FindById(appMode?.LinkedEnhancementId));
        // The GLOBAL language is read HERE too, so the resolved value is concrete. Leaving
        // "global" as null let a mid-recording Settings edit change this recording's language,
        // and made a retry re-read Settings later — recognising the same WAV differently than
        // the attempt it replays (Codex diff review, 2026-07-25).
        var (startLanguage, startLanguageSource) = Helpers.TranscriptionLanguageResolver.Resolve(
            retryLanguage: null,
            effectivePrePrompt: startPrompt,
            appModeLanguage: appMode?.LanguageOverride,
            globalLanguage: _settings.GetString(AppDefaults.SelectedLanguage, "auto"));
        Logger.Information(
            "Recognition language {Language} from {Source} (via {PromptSource}) userTerm={Prompt}",
            startLanguage, startLanguageSource, startPromptSource,
            Helpers.LogValueSanitizer.SingleLine(startPrompt?.Title ?? "(none)"));
        if (appMode != null)
        {
            // SEC-3: the App Mode label is user-authored and must ride {AppModeName}, never the
            // generic {Name} (dialog/page titles use that and stay un-redacted). Kept LAST so
            // the rendered-line scrub can anchor to end-of-line. See AppModeManager's
            // DetectActiveAppMode for the full privacy contract.
            Logger.Information("App Mode active: model={Model} lang={Lang} appMode={AppModeName}",
                appMode.ModelOverride ?? "default",
                appMode.LanguageOverride ?? "default",
                Helpers.LogValueSanitizer.SingleLine(appMode.Name));

            if (!string.IsNullOrEmpty(appMode.LinkedEnhancementId))
            {
                Logger.Information("App Mode config has linked enhancement: {Id} appMode={AppModeName}",
                    appMode.LinkedEnhancementId,
                    Helpers.LogValueSanitizer.SingleLine(appMode.Name));
            }
        }

        // Determine which model to use: App Mode override takes priority over settings.
        // The stop path reads the published SNAPSHOT rather than asking the registry to re-read
        // Settings, so preparation and transcription cannot diverge (see _recordingModelSnapshot).
        var selectedModel = (string.IsNullOrEmpty(appMode?.ModelOverride)
            ? null : appMode.ModelOverride)
            ?? _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel);
        var isCloudModel = Models.CloudModels.IsCloudModel(selectedModel);

        // For local models: always call EnsureModelLoadedAsync so the correct model is loaded.
        // LoadModelAsync already short-circuits when the same path is already loaded (idempotent),
        // but this ensures that after an App Mode recording loaded an override model, subsequent
        // non-App-Mode recordings restore the default model correctly.
        if (!isCloudModel)
        {
            // If a background preload is in progress, await it first to avoid a
            // concurrent second LoadModelAsync call on the same model file.
            if (_preloadTask is { IsCompleted: false })
            {
                SetPreflightStatus("Loading model...");
                await _preloadTask;
                if (_modelDownloadCts != null && !ReferenceEquals(_modelDownloadCts, startupCts))
                    return PreflightOutcome.Superseded;
                startupToken.ThrowIfCancellationRequested();
            }

            SetPreflightStatus("Loading model...");
            if (_modelDownloadCts != null && !ReferenceEquals(_modelDownloadCts, startupCts))
                return PreflightOutcome.Superseded;
            startupToken.ThrowIfCancellationRequested();
            // PRM-5: load WITH the resolved language so TranscribeAsync asks for the same one.
            await EnsureModelLoadedAsync(startupToken, selectedModel, startLanguage);
        }

        // ONE publish, atomic against the start-side reset. Cancellation keeps its OCE contract
        // (the cold catch presents "Recording cancelled" — a silent Superseded here would strand
        // the Starting pill; the warm side lands in AwaitRecordingPreflightAsync's degrade
        // branch); supersession returns Superseded so the caller exits without touching UI a
        // newer session owns.
        lock (_preflightPublishGate)
        {
            startupToken.ThrowIfCancellationRequested();
            if (_modelDownloadCts != null && !ReferenceEquals(_modelDownloadCts, startupCts))
                return PreflightOutcome.Superseded;
            _activeAppModeConfig = appMode;
            _recordingLanguageOverride = startLanguage;
            _recordingModelSnapshot = selectedModel;
        }
        // REL-26 (owner UAT §28.3): the same resolution into the PROMPT TRACE — the per-request
        // entries carry only the wire-level language, so App-Mode context and which precedence
        // source won were invisible in the one file the owner reads for a dictation's context.
        // AFTER the locked publish, deliberately: a superseded or cancelled preflight returns
        // above and writes nothing, so an entry only ever describes a PUBLISHED snapshot — not
        // necessarily a transcription: a take aborted after publish (too short, cancelled, or
        // superseded before the stop) leaves its entry with no request entry following it, which
        // is honest context, not an error. The main-log lines above keep logging the pre-publish
        // attempt — they are diagnostics for THIS invocation.
        // The App Mode name is user-authored: verbatim in the raw file, masked in the sidecar.
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.LanguageResolution,
            new Helpers.TraceMeta(),
            "language resolution",
            ("language", startLanguage),
            ("language source", startLanguageSource.ToString()),
            ("app mode", appMode?.Name));
        return PreflightOutcome.Completed;
    }

    /// <summary>
    /// AUD-6 (plan review B1): preflight/model StatusText writes are STATE-conditional, not
    /// flag-routed. While the state is Recording (warm path, dictation in progress) the pill
    /// shows "Recording..." + a live waveform, and overwriting it with "Loading model..." /
    /// "Downloading X..." would be worse than silence; in every other state — cold Starting,
    /// stop-side Transcribing, retries — the writes pass through exactly as they always have.
    /// </summary>
    private void SetPreflightStatus(string status)
    {
        if (RecordingState == RecordingState.Recording) return;
        StatusText = status;
    }

    /// <summary>
    /// AUD-6 stop-side barrier: the warm path's preflight runs concurrently with the recording,
    /// so the stop must not read <c>_activeAppModeConfig</c> / <c>_recordingModelSnapshot</c> /
    /// <c>_recordingLanguageOverride</c> until it settles. No-op on the cold path (task is null —
    /// the cold preflight completed inline before recording ever started).
    ///
    /// <para>Cancellable via <paramref name="ct"/> (the pipeline token — the pill stop button's
    /// CancelPipeline cancels it): a WaitAsync cancellation propagates to the caller's existing
    /// user-cancel handling. A preflight that FAULTED or was superseded degrades honestly:
    /// App-Mode overrides stay null (the bounded detect already degrades to null under contention
    /// today), the language resolves NOW from the global setting (logged — the one case that
    /// reads settings at stop; today's answer to the same fault is refusing the whole recording),
    /// and the model falls through the existing snapshot ?? override ?? settings chain, with one
    /// inline prep attempt at the attemptModel site.</para>
    /// </summary>
    private async Task AwaitRecordingPreflightAsync(CancellationToken ct)
    {
        if (_recordingPreflight is not { } preflight) return;
        _recordingPreflight = null;

        try
        {
            var outcome = await preflight.Task.WaitAsync(ct);
            if (outcome == PreflightOutcome.Completed) return;
            Logger.Information("Recording preflight was superseded — stop-side defaults apply");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The preflight's own startup token was cancelled (a cancel raced the stop) —
            // degrade rather than kill a stop the user explicitly requested.
            Logger.Warning("Recording preflight was cancelled — stop-side defaults apply");
        }
        catch (OperationCanceledException)
        {
            // Pipeline token — the user cancelled the STOP. Cancel the preflight ITSELF too:
            // WaitAsync only abandons the wait, and a multi-GB model download would otherwise
            // keep running headless after the pipeline returned to Idle (Codex diff r1). The
            // startup CTS is THIS recording's own; a newer session has already cancelled and
            // disposed it, hence the tolerance. The forget-continuation observes the fault.
            try { preflight.StartupCts.Cancel(); }
            catch (ObjectDisposedException) { }
            throw; // the caller's catch owns the user-cancel presentation
        }
        catch (Exception ex)
        {
            Logger.Warning("Recording preflight failed ({ExType}) — stop-side defaults apply",
                ex.GetType().Name);
        }

        if (_recordingLanguageOverride == null)
        {
            var (stopLanguage, stopLanguageSource) = Helpers.TranscriptionLanguageResolver.Resolve(
                retryLanguage: null,
                effectivePrePrompt: null,
                appModeLanguage: null,
                globalLanguage: _settings.GetString(AppDefaults.SelectedLanguage, "auto"));
            _recordingLanguageOverride = stopLanguage;
            Logger.Warning(
                "Recognition language resolved at STOP ({Language} from {Source}) — preflight did not complete",
                stopLanguage, stopLanguageSource);
            // REL-26: this degraded path resolves with no prompt and no App Mode AVAILABLE — a
            // faulted preflight may have detected an App Mode and dropped it (the documented
            // degrade above), so "app mode: (none)" records what this attempt will USE, not
            // what was on screen; the "(stop-side fallback)" kind is the reader's cue.
            Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.LanguageResolution,
                new Helpers.TraceMeta(),
                "language resolution (stop-side fallback)",
                ("language", stopLanguage),
                ("language source", stopLanguageSource.ToString()),
                ("app mode", null));
        }
    }

    /// <summary>Start beep + delayed system-audio mute — the COLD start's combined call (pure
    /// code motion of its former local function; it runs only after a successful recorder
    /// start, so the mute can never outlive a failed one there). The WARM path calls the two
    /// halves separately: the beep at the flip (the instant "you can talk" cue), the mute only
    /// AFTER the recorder attach — scheduling it earlier let a >300 ms writer stall mute the
    /// system for a recording that then failed, with nothing ever unmuting (Codex diff r3).</summary>
    private void PlayStartFeedbackThenMute()
    {
        _soundFeedback.PlayStartSound();
        ScheduleDelayedMute();
    }

    /// <summary>Mute other apps 300 ms after the call (once the start beep has played out),
    /// only if the recording is still live. Fire-and-forget so the pipeline doesn't wait.</summary>
    private void ScheduleDelayedMute()
    {
        _ = DelayedMuteAsync();
        async Task DelayedMuteAsync()
        {
            try
            {
                await Task.Delay(300);
                if (RecordingState == RecordingState.Recording)
                    _systemMute.Mute();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Delayed mute failed");
            }
        }
    }

    /// <summary>
    /// Shared cleanup for failed recording-start paths. Both the generic exception
    /// handler and the audio-specific <see cref="Services.Audio.AudioCaptureUnavailableException"/>
    /// handler call this so the post-failure state stays in sync (Codex's round-4 ask:
    /// the two failure paths must not drift over time). Clears the App-Mode
    /// override, the recording-path RCW capture, the recording file path, and
    /// resets the state machine.
    ///
    /// <para>Codex 2026-05-19 review-loop round-6 finding: deletes the locally-captured
    /// <paramref name="startupRecordingPath"/> from disk. <see cref="AudioRecorderService.StartRecordingAsync"/>
    /// creates the <see cref="WaveFileWriter"/> (and thus the WAV file on disk) BEFORE
    /// its pre-StartRecording cancellation check, and disposes the writer in its catch.
    /// But the file remains on disk after the writer closes. Without this delete a
    /// canceled-or-failed start leaves an empty/header-only WAV under
    /// <c>%LOCALAPPDATA%\VoiceWink\Recordings\</c> — small but cumulative privacy /
    /// storage waste. Caller passes the local captured BEFORE
    /// <c>await _recorder.StartRecordingAsync</c> so a concurrent
    /// <see cref="CancelRecordingAsync"/> that nulled <c>_currentRecordingPath</c>
    /// cannot strand the file.</para>
    /// </summary>
    private void HandleStartRecordingFailureCleanup(string? startupRecordingPath)
    {
        _activeAppModeConfig = null;
        _currentRecordingPath = null;
        MarkTerminalPresentation(); // IMG-BG epoch fence — terminal presentation
        RecordingState = RecordingState.Idle;
        // Release the UIA capture taken in StartRecordingAsync; startup failure means
        // no paste will run.
        ReleaseCapturedFocusedElement(CapturedFocusOwner.Recording);

        TryDeleteStartupRecording(startupRecordingPath);
    }

    /// <summary>
    /// Path-only deletion of a half-written WAV from a failed/canceled/superseded
    /// recording-start attempt. Pulled out of <see cref="HandleStartRecordingFailureCleanup"/>
    /// (Codex 2026-05-19 review-loop round-7) because the "newer-session took over"
    /// early returns in <see cref="StartRecordingAsync"/> must clean up the disk
    /// file but MUST NOT touch <c>_currentRecordingPath</c>, <c>RecordingState</c>,
    /// the UIA focus capture, or the App-Mode override — all of which now belong
    /// to the newer session.
    ///
    /// <para>By the time any caller reaches this method, both
    /// <see cref="Services.Audio.AudioRecorderService.Cleanup"/> and any outer
    /// disposal have already closed the <see cref="WaveFileWriter"/>, so the file
    /// is no longer locked. Failures are logged at Information and swallowed —
    /// the worst case is one stranded ~44-byte WAV header, never a crash.</para>
    /// </summary>
    private void TryDeleteStartupRecording(string? startupRecordingPath)
    {
        if (startupRecordingPath != null && File.Exists(startupRecordingPath))
        {
            try { File.Delete(startupRecordingPath); }
            catch (Exception ex)
            {
                Logger.Information("Cleanup of half-written WAV at {Path} failed: {ExType}",
                    startupRecordingPath, ex.GetType().Name);
            }
        }
    }

    /// <param name="retry">Non-null when this run is a RETRY of a failure-retained
    /// recording (REL-12): the recorder-shutdown prefix is skipped (nothing is
    /// recording), the recording-scoped inputs (path, App-Mode overrides, hotkey
    /// prompt, push-to-talk mode) come from the context instead of live state, and a
    /// user cancel re-arms retry instead of discarding the WAV.</param>
    private async Task StopAndTranscribeAsync(TranscriptionRetryContext? retry = null)
    {
        // C3: single-flight gate FIRST — before any mutation. A second concurrent stop
        // (rapid double hotkey, hotkey racing a pill/tray click) sees the lease held and
        // no-ops instead of entering the body and disposing the first stop's live CTS /
        // deleting its retained WAV. Covers normal stop AND retry entry. `using var` scopes
        // the release to the WHOLE method body (every normal / early-return / throw path,
        // including a throw before the inner try or from the inner finally); disposing a
        // null lease is a harmless no-op, so the null-check return below is safe.
        using var stopLease = _stopSingleFlight.TryAcquire();
        if (stopLease is null)
        {
            Logger.Information("Stop already in progress — ignored");
            return;
        }

        // Capture and clear prompt override immediately so it never leaks to the next
        // recording; a retry re-uses the override its failed attempt consumed. CLONE at
        // consumption (PRM-4 Option B): the retry context (built below) stores this
        // reference and the whole pipeline reads it — an in-flight settings edit must
        // not mutate the prompt's text/provider/image-intent mid-transcription, nor arm
        // Retry with a live cached prompt (Codex diff review).
        var hotkeyOverride = (retry != null ? retry.HotkeyPromptOverride : _pendingPromptOverride)?.Clone();
        if (retry == null)
            _pendingPromptOverride = null;
        // AUD-6 (Codex diff r3): a WARM start may still be mid-flight — its Recording flip
        // precedes the recorder hand-off by design, so a stop that lands in a yield inside that
        // stretch would otherwise stop "nothing", read a null recording path, abort, and leave
        // the drain recording under Idle UI. Settle the start first: after this await the
        // recorder either owns the session (normal stop proceeds) or the start failed (its own
        // catch owns cleanup/presentation; the byte checks below then abort honestly).
        // Completed-gate awaits are no-ops — the common fully-synchronous warm start pays nothing.
        if (_warmStartGate is { } warmStartGate)
        {
            try { await warmStartGate; }
            catch { /* the start's own catches present the failure */ }
        }

        var recordingPath = retry?.WavPath ?? _currentRecordingPath;

        // Set when transcription+enhancement succeeded but the paste was declined and the
        // text is on the clipboard. Declared before the try so the generic catch can prefer
        // it over a misleading "Transcription failed" if a later (fail-soft) write throws.
        // The tone rides WITH the message (computed once from the actual PasteResult) so no
        // later consumer — redo presentation, no-redo pill, catch net — re-derives it.
        string? clipboardFallbackMessage = null;
        var clipboardFallbackTone = MiniRecorderTone.Success;

        // REL-12/REL-13 retry-eligibility marker, non-null from the pre-transcription
        // guards until the pipeline produces USABLE PROCESSED TEXT — so the catches
        // AND the empty-transcript branches can distinguish "no usable text yet with
        // a recoverable recording" (retain + arm retry) from post-text failures
        // (clipboard-fallback / plain-error semantics). Enhancement-stage failures
        // are past the boundary and stay non-retryable.
        TranscriptionRetryContext? retryCandidate = null;
        // Set by the retain branches in the catches AND HandleEmptyTranscript
        // (REL-13); the finally skips the WAV delete exactly when this is true.
        var keepWavForRetry = false;
        // REL-17: set ONLY by the pure user-cancel catch — an explicitly discarded take
        // is deleted even when the keep-recordings debug setting is on (disposition
        // precedence in RecordingDisposition.Decide).
        var userCancelledPipeline = false;
        // The pipeline token, hoisted so the OCE catch FILTER can tell a user cancel
        // (token cancelled) from a client-side HttpClient.Timeout expiry (surfaces as
        // TaskCanceledException with the caller token NOT cancelled — Codex diff review).
        var pipelineCt = CancellationToken.None;

        try
        {
            if (retry == null)
            {
                // Stop recording. Skipped entirely on retry: nothing is recording (the
                // entry point guards Idle), and the stop sound must not replay.
                // AUD-11: release the session first — same reason as CancelRecordingAsync's stop.
                _stallMonitor.Detach();
                // AUD-14: THE one graced stop — the mic stays hot ≤ 1 s so a final word still
                // crossing the endpoint lands in the WAV, then the tail trims back to the last
                // speech (never below today's cut point). Cancel/supersede/dispose stops keep
                // the default no-grace byte-identical path.
                await _recorder.StopRecordingAsync(grace: true);
                StopMeterTimer();

                await _systemMute.UnmuteAsync();
                _soundFeedback.PlayStopSound();
            }

            _transcriptionCts?.Cancel();
            _transcriptionCts?.Dispose();
            _transcriptionCts = new CancellationTokenSource();
            var ct = _transcriptionCts.Token;
            pipelineCt = ct;

            RecordingState = RecordingState.Transcribing;
            StatusText = "Transcribing...";
            // The pill may be hidden when a retry is hotkey-triggered after its pill
            // auto-dismissed; state is set first so rehydration shows Transcribing.
            if (retry != null)
                IsMiniRecorderVisible = true;

            // The three byte checks consume NOTHING from the preflight, so they run BEFORE the
            // AUD-6 barrier below — a fast double-press with an un-downloaded local model must
            // reach "Recording too short" immediately, not after the whole download (Kimi diff
            // r1 #3). Their aborts clear the App-Mode config + discard the preflight inside
            // AbortToIdle, preserving the clear-on-every-path invariant the capture block below
            // used to guarantee by running first.
            if (recordingPath == null || !File.Exists(recordingPath))
            {
                Logger.Warning("No recording file found");
                AbortToIdle("No audio recorded");
                return;
            }

            // Check file has actual content (more than just WAV header)
            var fileInfo = new FileInfo(recordingPath);
            if (fileInfo.Length <= 44)
            {
                Logger.Warning("Recording file is empty (only header)");
                AbortToIdle("No audio detected");
                return;
            }

            // Guard against very short recordings (<0.5s) that cause Whisper hallucinations.
            // 16kHz mono 16-bit = 32,000 bytes/sec → 0.5s = 16,000 bytes + 44-byte header.
            const long MinRecordingBytes = 16_044;
            if (fileInfo.Length < MinRecordingBytes)
            {
                Logger.Information("Recording too short ({Length} bytes, <0.5s) — treating as cancelled", fileInfo.Length);
                AbortToIdle("Recording too short");
                return;
            }

            // AUD-6: a warm recording's preflight ran concurrently with the recording — settle it
            // before ANY snapshot field below is read. Pinned AFTER the Transcribing flip (the
            // pill shows "Transcribing..."/"Downloading X..." during a long model wait, never a
            // frozen "Recording...") and cancellable via the pipeline token (the pill stop
            // button reaches a mid-download wait). No-op for cold recordings and retries.
            await AwaitRecordingPreflightAsync(ct);

            // Capture and clear App Mode config early — all paths (including early returns) must
            // clear it (the byte-check aborts above do so via AbortToIdle)
            var appModeConfig = retry == null ? _activeAppModeConfig : null;
            _activeAppModeConfig = null;

            // Extract overrides as local variables — do NOT mutate the cached AppModeConfig
            // object (it's a shared reference from AppModeManager._cache). A retry replays
            // the overrides its failed attempt captured ([R3] — identical to what a fresh
            // recording in the same target app would resolve).
            var modelOverride = retry != null ? retry.ModelOverride
                : string.IsNullOrEmpty(appModeConfig?.ModelOverride) ? null : appModeConfig.ModelOverride;
            // PRM-5: the language was fully resolved at RECORDING START (see StartRecordingAsync) and
            // stashed as a CONCRETE value — prompt override, else App-Mode, else the global setting
            // as it was when recording began. Two reasons it must not be re-derived here: the
            // local-model preload at start already built Whisper for that language, and a
            // mid-recording Settings edit must not change the language THIS audio is recognised with
            // (Codex plan review round 2 + diff review). The `??` is a defensive floor only — a
            // retry context persisted by an older build can carry null.
            var languageOverride = retry != null ? retry.LanguageOverride : _recordingLanguageOverride;
            var linkedEnhancementId = retry != null ? retry.LinkedEnhancementId
                : string.IsNullOrEmpty(appModeConfig?.LinkedEnhancementId) ? null : appModeConfig.LinkedEnhancementId;

            // Push-to-talk mode drives send-enter-after-paste (and redo contexts). A
            // retry must not read LIVE hotkey state — the mode rides the context.
            var wasPushToTalk = retry?.WasPushToTalk ?? _hotkeyService.WasPushToTalk;

            // No-speech gate — Silero VAD (primary) with the legacy whole-file RMS check
            // as fallback when VAD is unavailable; skipped when THIS run is the user's
            // explicit "I really spoke" retry from a previous block. Avoids Whisper
            // hallucinations ("Thank you", "and many more", etc.) on non-speech audio:
            // the 2026-07-23 incident showed whole-file RMS passes "3 s of silence + one
            // click". A block RETAINS the WAV and arms an amber Retry (the PRM-4
            // echo-block idiom) — the old silent discard would lose real quiet speech.
            // AUD-2: PrepareAsync evaluates this gate on the RAW file, then applies the
            // bounded soft-speech gain (fresh recordings only — a retry replays audio its
            // original attempt already normalized), then returns the verdict. The order is
            // the load-bearing part: gates are never fed AUD-2's disk gain (the Silero gate
            // conditions its OWN in-memory input copy since AUD-24; the RMS fallback judges
            // the raw file), while a blocked WAV is retained ALREADY normalized for its retry.
            // TRN-18: capture the VAD's own verdict as it goes past. Only `SpeechDetected` is
            // POSITIVE evidence that this recording contains speech — `Unavailable` means the RMS
            // fallback decided, and that gate is documented three lines above as passing "3 s of
            // silence + one click". The empty-transcript pill needs the distinction: telling a user
            // to switch engines is safe advice only when something actually heard speech.
            Helpers.NoSpeechVerdict? gateVerdict = null;
            var prepared = await _audioPreparation.PrepareAsync(
                    recordingPath,
                    isRetry: retry != null,
                    () => Helpers.NoSpeechGateRouting.ShouldBlockAsync(
                        retry?.SkipNoSpeechGate == true,
                        async () =>
                        {
                            var verdict = await _voiceActivityDetection.EvaluateAsync(recordingPath, ct);
                            gateVerdict = verdict;
                            return verdict;
                        },
                        () => TranscriptionOutputFilter.IsSilentWav(recordingPath)),
                    ct);
            if (prepared.Blocked)
            {
                HandleBlockedNoSpeech(prepared.DigitallySilent);
                return;
            }

            void HandleBlockedNoSpeech(bool digitallySilent)
            {
                // AUD-21 (self-review L1): an all-zero recording is the one evidence source that
                // catches a stream which latched silent AFTER once carrying audio — the standing
                // service's own from-birth flag is monotonic and cannot see that flavor. Since
                // AUD-34 the fact is ESTABLISHED only once the stream rule's evidence bound is
                // reached — by one recording, or by consecutive short all-zero recordings adding
                // up to it — so a gated microphone's short press no longer trips it. Re-arms
                // the flag and posts a rebuild so the NEXT dictation gets a fresh client either
                // way. No-op when the standing capture is parked, stopped, or disposed.
                if (digitallySilent)
                    _standingCapture.NotifyRecordingWasDigitallySilent();

                var blockedContext = BuildRetryContext(
                    recordingPath, modelOverride, languageOverride, linkedEnhancementId,
                    hotkeyOverride, _targetWindowHandle, wasPushToTalk,
                    consumedRetry: retry, armFromNoSpeechBlock: true);
                // Publish the candidate BEFORE any observable callback (Codex diff
                // round 4): if the choreography throws mid-way (Idle/status/arm), the
                // generic catch finds retryCandidate + the retained WAV and arms Retry
                // through the normal REL-12 failure surface — retained audio is never
                // left without an affordance.
                retryCandidate = blockedContext;
                // The WHOLE choreography — retention FIRST, then the epoch fence +
                // Idle transition, status BEFORE arm, amber arm, pill, hotkey reset —
                // is the test-pinned NoSpeechBlockAction: retention completes before
                // ANY observable callback (incl. RecordingState's PropertyChanged
                // subscribers, Codex diff round 3) can throw, so the pipeline's
                // finally can never delete the recording an armed Retry points at.
                Helpers.NoSpeechBlockAction.Execute(
                    retainWav: () =>
                    {
                        keepWavForRetry = true;
                        _wavLedger.Track(recordingPath);
                    },
                    transitionToIdle: () =>
                    {
                        MarkTerminalPresentation(); // IMG-BG epoch fence — see HandleBlockedLikelyEcho
                        RecordingState = RecordingState.Idle;
                    },
                    retryContext: blockedContext,
                    setStatus: s => StatusText = s,
                    armRetry: (c, tone) => ArmRetry(c, tone),
                    showPill: () => IsMiniRecorderVisible = true,
                    resetHotkey: () => ResetHotkeyState?.Invoke(),
                    // AUD-21: name the capture as the cause when every sample was digital zero,
                    // instead of telling the user their voice was not heard.
                    statusMessage: Helpers.NoSpeechBlockAction.MessageFor(digitallySilent));
            }

            // REL-12: from here to TranscribeAsync returning, a throw means "failed
            // at-or-before transcription with a good recording on disk" — the catches
            // retain the WAV and arm retry. Armed BEFORE model prep and the vocab
            // fetch deliberately: a model download/load failure or a vocab DB error
            // would otherwise lose the recording (Codex round-3 [V4]).
            // Both gate-bypass flags CARRY from the consumed retry (PRM-4 round 4): if a
            // bypassing retry fails again and re-arms from THIS candidate, the next
            // Retry must still bypass — the carry rules live in BuildRetryContext.
            retryCandidate = BuildRetryContext(
                recordingPath, modelOverride, languageOverride, linkedEnhancementId,
                hotkeyOverride, _targetWindowHandle, wasPushToTalk,
                consumedRetry: retry);

            // Retry-only model preparation [R1]: recording start ensured the model for
            // the ORIGINAL attempt; by retry time the effective model may have changed
            // (e.g. cloud failed → user switched to local) or been swapped out —
            // WhisperTranscriptionService.TranscribeAsync would throw ("no model
            // loaded") or silently use the stale loaded model.
            // The model this attempt prepares AND transcribes with — one value, resolved once.
            // A retry recomputes it (the effective model can have changed since the original
            // attempt); a normal run uses the snapshot taken at recording start.
            //
            // TRN-17: the retry arm's precedence lives in RetryAttemptResolution, because the retry
            // PICKER pre-selects with the same rule and a dialog showing one model while the
            // pipeline runs another is a wrong-output failure no gate would catch. With no user
            // pick the expression is identical to the pre-TRN-17 one.
            var attemptModel = retry != null
                ? Helpers.RetryAttemptResolution.Model(
                    userPick: retry.UserModelPick,
                    appModeOverride: retry.ModelOverride,
                    globalSelectedModel: _settings.GetString(
                        AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel))
                : _recordingModelSnapshot ?? modelOverride
                    ?? _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel);

            // The language this attempt REQUESTS — resolved ONCE here so the preload below and the
            // TranscribeAsync call further down cannot ask for different things. They read different
            // sources before TRN-17 (`retry.LanguageOverride` vs `languageOverride`), which was
            // harmless only because the two were always equal; a per-run pick makes them diverge,
            // costing a 2-8 s Whisper rebuild and preloading a language the wire never asks for.
            var requestedLanguageForAttempt = retry != null
                ? Helpers.RetryAttemptResolution.Language(
                    userPick: retry.UserLanguagePick,
                    capturedLanguage: retry.LanguageOverride,
                    globalLanguage: _settings.GetString(AppDefaults.SelectedLanguage, "auto"))
                : languageOverride;

            if (retry != null)
            {
                if (!Models.CloudModels.IsCloudModel(attemptModel))
                {
                    StatusText = "Loading model...";
                    // The captured language override rides along so the preload uses the
                    // language TranscribeAsync will ask for (else: redundant 2-8s rebuild).
                    await EnsureModelLoadedAsync(ct, attemptModel, requestedLanguageForAttempt);
                    StatusText = "Transcribing...";
                }
            }
            else if (_recordingModelSnapshot == null && !Models.CloudModels.IsCloudModel(attemptModel))
            {
                // AUD-6 degraded warm path (plan review item 13): the concurrent preflight never
                // set the snapshot (faulted/superseded/cancelled), so the model was never
                // prepared for this recording. One inline attempt — mirroring the retry branch —
                // converts what would be a REL-12 manual Retry into an automatic delayed
                // transcription; if THIS also fails, the catches below retain the WAV and arm
                // Retry exactly as any model-prep failure at transcription does. Cold recordings
                // never enter (their inline preflight always sets the snapshot before recording).
                StatusText = "Loading model...";
                await EnsureModelLoadedAsync(ct, attemptModel, languageOverride);
                StatusText = "Transcribing...";
            }

            // Transcribe (local or cloud via registry). The EXACT model this attempt prepared is
            // passed in — GetService would otherwise re-read Settings and could route to a
            // different model than the one just loaded.
            var transcriber = _transcriptionRegistry.GetService(attemptModel);
            var requestedLanguage = requestedLanguageForAttempt
                ?? _settings.GetString(AppDefaults.SelectedLanguage, "auto");

            // What the ATTEMPT MODEL will actually recognise with. Derived here because this is the
            // one place that knows both the requested language and the model finally chosen, and
            // because `language` from here feeds BOTH TranscribeAsync and the history row.
            //
            // Deriving it only inside EnsureModelLoadedAsync (as the first version of this change
            // did) fixed nothing: preparation loaded ggml-small.en as English and this line then
            // handed TranscribeAsync "nl", forcing the 2-8 s rebuild the constraint exists to avoid
            // and recording a language the weights cannot produce. Same pure helper, same model, so
            // the two agree by construction.
            var effectiveLanguage = Helpers.EffectiveTranscriptionLanguage
                .ForModelName(attemptModel, requestedLanguage);
            if (effectiveLanguage.Diverges)
            {
                Logger.Information(
                    "Model constrains recognition: requested {Requested}, effective {Effective} ({Constraint})",
                    requestedLanguage, effectiveLanguage.Language, effectiveLanguage.Constraint);
            }
            var language = effectiveLanguage.Language;

            // Fetch vocabulary for the transcription stage. Hints merge every prompt's
            // trigger phrases (FIRST — action phrases must survive provider-budget
            // truncation) with Dictionary words (PRM-4): the recognizer must know the
            // exact phrases that gate prompt switching. Composed at request time,
            // never stored into Dictionary data. The context is master-gated
            // (Dictionary-page toggle) and read PER STAGE — the AI enhancement step
            // below does its own fresh fetch at ITS moment, so a mid-run toggle flip
            // can't ride a stale snapshot into the enhancement request.
            var vocabContext = await _customVocabulary.GetVocabularyContextAsync(ct);
            // ONE deep-cloned prompt snapshot for the whole run (PRM-4 Option B):
            // composition, trigger detection, App-Mode lookup, active resolution, AND
            // the hotkey/retry override all read it, so a settings edit mid-transcription
            // (which rebuilds the cache and can mutate cached prompt objects) can't make
            // them disagree. EnhancementViewModel mutates cached objects → deep clone.
            var promptRun = Helpers.PromptRunSnapshot.Capture(_enhancement.GetPrompts(), hotkeyOverride);
            hotkeyOverride = promptRun.HotkeyOverride; // all downstream uses the frozen clone
            var hintTerms = Helpers.HintTermComposition.Compose(vocabContext.Terms, promptRun.Candidates);
            // The tokenizer vocab stamp is now inert on every shipped path — no transport
            // budgets a Whisper prompt since TRN-11 — but FromTerms still requires one and
            // Deepgram/ElevenLabs read Terms directly, so the default stamp is passed
            // rather than the signature changed. Removing the parameter would reach into
            // WhisperPromptTokenizer and its golden-pinned rank files, which is a
            // deletion, not this fix.
            var transcriptionHints = Models.TranscriptionHints.FromTerms(hintTerms);
            // The exact terms a GENERATIVE recognizer could echo back (PRM-4). Captured
            // from the RESOLVED transcriber's transport, never a later registry lookup.
            // Empty for every transport except StructuredKeywords: since TRN-11 nothing
            // declares GenerativePrompt, and Keyterms never echoes by construction.
            var hintTransport = transcriber.HintTransport;
            IReadOnlyList<string> echoableTerms = global::System.Array.Empty<string>();
            if (hintTransport == Services.Transcription.HintTransportKind.StructuredKeywords)
            {
                // OpenAI gpt-transcribe's keywords[]. Bound HERE — before the retry wrapper
                // below — so both attempts send the identical payload and the echo gate matches
                // exactly it. Reads GetBoolDefaulted, the SAME call the Models-page toggle
                // renders from: a literal here could disagree with the UI's literal on a
                // malformed stored value, showing the switch ON while sending nothing.
                var binding = Helpers.KeywordHintBinding.Bind(
                    transcriptionHints,
                    hintTransport,
                    _settings.GetBoolDefaulted(AppDefaults.OpenAIKeywordsEnabled));
                transcriptionHints = binding.Hints;
                echoableTerms = binding.EchoableTerms;
            }

            // Diarization is only used for file transcription (AudioTranscribePage), not live recording.
            // NET-1: one automatic retry if the attempt died establishing the connection — the only
            // transcription failure class where nothing was uploaded, so a replay is provably safe.
            // The whole call is re-invoked (not the request), because each client opens its own
            // FileStream inside TranscribeAsync; a handler-level retry could not replay that stream.
            string rawText = await Services.Transcription.TranscriptionConnectRetry.ExecuteAsync(
                token => transcriber.TranscribeAsync(recordingPath, language, transcriptionHints, diarize: false, token),
                onRetrying: () =>
                {
                    PipelineNoticeRequested?.Invoke("Poor connection — retrying");
                    return Task.CompletedTask;
                },
                ct);

            // RAW recognizer output, before the text pipeline — the request-side trace
            // entries (prompt/keyterms) get their return leg here. Carries the
            // routed model itself because the whisper request entry fires per processor
            // (re)build, not per recording, so it may not be adjacent in the file.
            //
            // DEFERRED until image intent is known (REL-21, both diff reviewers). Writing it
            // here unconditionally put a dictated image description — "create an image of my
            // daughter at the beach" — verbatim into prompts-*.log as a TRANSCRIPTION output,
            // which the image sub-option does not gate. Voice is the primary way this app
            // creates an image prompt, so the option would have promised to withhold exactly
            // the content it still wrote. Intent is only resolvable after promptRun.Resolve, so
            // the entry is held by this scope instead: Resolve() records the verdict at the
            // resolution point below, and disposal emits — so an early return, a throw, or a
            // fall-through each make exactly one emit CALL, with unresolved-but-non-empty text
            // failing CLOSED. One emit call is NOT one RAW entry: the emit still passes the raw
            // gate, so a fail-closed exit writes no raw entry while the sub-option is off — the
            // always-written sidecar still carries the masked line (2026-09-13). That rule lives
            // in DeferredTranscriptionTrace, which is also why it isn't a pile of locals here
            // (AGENTS.md: don't grow the hub).
            using var transcriptionTrace = new Helpers.DeferredTranscriptionTrace(rawText, attemptModel);

            // CONTENT-empty, not just whitespace-empty (2026-08-03 incident). A near-silent
            // recording is transcribed as a lone "," often enough to matter, and a comma is
            // neither null nor whitespace — it passed this guard, reached a paid enhancement
            // provider as "clean this up", and came back as 100 characters of commentary about
            // the input. See TranscriptContent for why the rule is whitespace-or-punctuation
            // rather than the obvious no-letter-or-digit form.
            if (Helpers.TranscriptContent.IsEmpty(rawText))
            {
                // No explicit trace call on any early return — disposal emits. Contentless text
                // classifies as an ordinary transcription output, so the entry that makes this path
                // diagnosable survives. It carries the text VERBATIM, not the literal `(empty)` —
                // PromptTraceLog.WriteOutput reserves that for null/whitespace, and the lone comma
                // this branch exists for is neither.
                Logger.Warning("Transcription returned no content ({Length} chars)", rawText?.Length ?? 0);
                // The ENGINE produced nothing — the one caller entitled to name it (TRN-18).
                HandleEmptyTranscript(engineReturnedNothingUsable: true);
                return;
            }

            // Text processing pipeline: OutputFilter → FillerWords → TextFormatter → WordReplacements.
            // `requestedLanguage` — what the user ASKED for (resolved above: prompt/App-Mode override,
            // else the global setting), never `language`, the EFFECTIVE one the engine was asked
            // for: Parakeet auto-detects only, so its effective language is `auto` for every
            // attempt whatever was requested, and gating on it would apply the English filler list
            // to a German recording on the default engine (self-review, correctness lens). The
            // history row keeps recording the effective value; the filler gate wants the intent.
            Logger.Information("Transcription received ({Length} chars)", rawText.Length);
            var text = _textPipeline.Run(rawText, requestedLanguage);

            // DELIBERATELY the plain whitespace check, NOT the content rule used above. The
            // difference is provenance, and it is the whole reason the two guards differ.
            //
            // Above, the text is TRANSCRIBER output: punctuation-only there is definitionally an
            // artifact, because nobody dictates a bare comma. Here it has been through
            // WordReplacementService, which applies USER-authored replacements with no charset
            // restriction — so a mapping like `comma -> ,` or `dash -> —` is supported, and its
            // output is punctuation-only ON PURPOSE. Applying the content rule here discarded that
            // silently, and retry could not recover it: the replacement re-runs and produces the
            // same result, so the dictation became permanently un-actionable (Codex diff review;
            // Kimi flagged the same path independently).
            //
            // The irony worth recording: the emoji carve-out in TranscriptContent exists BECAUSE
            // WordReplacementService can emit arbitrary characters. That argument applies just as
            // forcefully to punctuation, and this guard originally missed it.
            if (string.IsNullOrWhiteSpace(text))
            {
                // rawText is NON-empty here (the pipeline emptied it), so intent is unresolved
                // AND there is real dictated text — disposal fails closed, withholding the entry
                // unless the user opted in. Accepted cost: this path loses its trace line while
                // the sub-option is off.
                Logger.Warning("Text empty after processing pipeline (raw was {Length} chars)", rawText.Length);
                // OUR pipeline emptied text the engine did produce — naming the engine here would
                // be a false accusation in front of the user (TRN-18).
                HandleEmptyTranscript(engineReturnedNothingUsable: false);
                return;
            }

            // TRN-29 auto-cleanup proof — the criterion is ENGINE HEALTH, not user receipt
            // (OWNER DESIGN DECISION, 2026-08-24, closing four review rounds that each found
            // a later user-intent gate rejecting after an earlier notify point: raw-empty →
            // pipeline-emptied → echo-gated → image-picker-cancelled). The property being
            // protected is "parakeet.cpp decodes well enough to delete the sherpa fallback",
            // and a good decode whose image picker was cancelled proves the engine exactly as
            // well as one that got pasted — so everything BELOW this line (echo gates, image
            // dispatch/refusal/cancel, enhancement, paste) judges the USER'S INTENT and is
            // irrelevant to the cleanup decision by construction. What IS required, and is
            // satisfied at this exact point: the engine returned non-empty raw text (the guard
            // above) AND the machine-owned pipeline did not empty it (the whitespace gate
            // above) — round 2's point that pipeline-emptied artifacts are not evidence of a
            // healthy decode stands. A wrong delete costs a 940 MB re-download, never data.
            // The coordinator no-ops unless the service armed a pending proof for THIS call,
            // so a Whisper/cloud transcription (or a sherpa fall-through) can never commit it;
            // the model guard just avoids a pointless call on the common non-Parakeet path.
            //
            // Cancellation check DELIBERATELY outside the fail-soft try below (Codex r5): the
            // service's own final ct check covers cancel-during-decode, but a cancel landing in
            // the window between the service returning and this line would otherwise consume the
            // arm and clean on a dictation the user just discarded. One check, outside the
            // catch, so the OCE routes to the pipeline's normal cancellation teardown instead of
            // being logged-and-ignored as a notify failure. The health evidence would have been
            // real — the next dictation re-proves it for free, so refusing costs nothing.
            ct.ThrowIfCancellationRequested();
            if (_pcppBackend is not null && Models.ParakeetCatalog.IsParakeetName(attemptModel))
            {
                try { _pcppBackend.NotifyUsableTranscription(); }
                catch (Exception ex) { Logger.Warning(ex, "pcpp usable-transcription notify failed"); }
            }

            // The pipeline produced usable text — later failures are NOT retry-eligible.
            // (REL-13 moved this boundary BELOW the empty checks: an empty outcome with
            // the WAV on disk is retryable — the live 2026-07-17 23:09 incident was a
            // real 9 s dictation Deepgram answered 200-with-empty, and the recording
            // was gone before anyone could retry or diagnose.)
            // PRM-4 (Codex round 4): the echo gates run AFTER this boundary but their
            // block must still retain + arm Retry — a false positive is REAL SPEECH
            // (e.g. a dictation of one hinted name) and deleting the WAV would lose it
            // irrecoverably. Capture the candidate here; the REL-12 catch contract
            // (retain only for at-or-before-transcription throws) is unchanged.
            var echoRetryCandidate = retryCandidate;
            retryCandidate = null;

            // REL-13: empty transcript (provider returned no words, or the local
            // pipeline emptied the text) — retain the recording and arm the retry
            // pill when possible, so the user can retry as-is or switch the
            // transcription model first. Mirrors the catch-site retain branch.
            void HandleEmptyTranscript(bool engineReturnedNothingUsable)
            {
                MarkTerminalPresentation(); // IMG-BG epoch fence
                RecordingState = RecordingState.Idle;
                // Deliberately NOT "No speech detected" — that is the VAD gate's amber
                // suspicion copy, and reusing the same words in a red persistent pill for a
                // different cause made two situations look like one (Codex diff review round 2,
                // 2026-07-25). "No usable transcription" rather than "…returned" because BOTH
                // callers land here: the provider returning nothing, AND the processing pipeline
                // emptying non-empty provider text (round 3) — "returned" is false in the second.
                // Since the tails were dropped (below) this cause phrase is the ONLY thing
                // telling this pill apart from the VAD gate's — don't converge the two.
                //
                // CAUSE ONLY, and the SAME text on both branches (owner, 2026-08-01): the retain
                // branch used to append "— Retry to transcribe anyway", which restated the Retry
                // button already sitting on the pill. Dropping the tail also dissolves the reason
                // the copy was branch-scoped (a tail promising Retry would have been a lie on the
                // else path, which keeps NOTHING — owner, 2026-07-25), so it is set once here,
                // still BEFORE ArmRetry, which captures StatusText into RetryStatusText.
                //
                // TRN-18: the copy SPECIALIZES for the one engine whose empty-decode collapse is
                // measured (Parakeet). Retry replays the same WAV at the same gain, and the same
                // MODEL unless the user changes it — an unpinned retry re-reads SelectedModelName
                // live (see attemptModel above), so switch-then-Retry genuinely works and is
                // exactly what the added clause is for. (An earlier revision of this comment said
                // Retry "replays the identical decision" full stop; that is true only under an
                // App Mode override, and it overstated in the direction that flattered the change
                // — self-review caught it.) Everything above still holds: the specialization is a
                // more precise CAUSE plus the action the Retry button cannot perform, not the
                // button-restating tail the owner removed, and it is refused outright when our own
                // pipeline (not the engine) emptied the text.
                StatusText = Helpers.EmptyTranscriptMessage.For(
                    engineReturnedNothingUsable,
                    attemptModel,
                    // POSITIVE evidence only. `Unavailable` means the RMS fallback decided, and
                    // that gate passes "3 s of silence + one click" — advising an engine switch
                    // there would point at the engine that hallucinates on exactly that input.
                    speechConfirmed: gateVerdict == Helpers.NoSpeechVerdict.SpeechDetected,
                    // A pinned retry replays the override instead of re-reading Settings, so the
                    // Models page the advice implies would not change the next attempt.
                    modelIsPinnedByOverride: retry != null
                        ? retry.ModelOverride != null
                        : modelOverride != null);
                // Snapshot: makes the closure invariant compiler-visible (this runs
                // strictly BEFORE the success-boundary nulling; the local removes any
                // reliance on that ordering and every null-forgiveness below).
                var candidate = retryCandidate;
                if (candidate != null &&
                    Helpers.TranscribeFailureSurface.ShouldRetainEmptyTranscript(
                        retryCandidateArmed: true,
                        wavStillExists: File.Exists(candidate.WavPath)))
                {
                    keepWavForRetry = true;
                    _wavLedger.Track(candidate.WavPath);
                    ArmRetry(candidate);
                    IsMiniRecorderVisible = true;
                }
                else
                {
                    IsMiniRecorderVisible = false;
                }
            }

            // PRM-4 Option B: a GENERATIVE recognizer can echo the hint terms it was sent
            // on near-silence. Since TRN-11 the ONLY transport that still puts hint text in
            // front of one is OpenAI gpt-transcribe's keywords[] — the Whisper paths send
            // nothing, so echoableTerms is empty for them and this gate is inert there.
            // If the RAW transcript is a contiguous run of the exact terms
            // sent, it is (almost certainly) an echo, textually identical to real
            // speech. BLOCK it — no override, paste, enhancement, or Enter — but retain
            // the WAV and arm Retry (see HandleBlockedLikelyEcho). Discriminative
            // providers (Deepgram/ElevenLabs) can't echo, so echoableTerms is empty for
            // them and this never fires. OpenAI is NO LONGER in that exempt group:
            // gpt-transcribe sends keywords[] to a generative model (StructuredKeywords),
            // so echoableTerms IS populated for it and the gate does run — see that
            // enum member for why the shape-match is defence rather than proof. Only
            // the legacy OpenAI models, which send no hints at all, stay exempt.
            // A retry armed FROM an echo block
            // bypasses the gate (SkipEchoGate) — the user's explicit "I really said
            // that" must not loop into the same drop.
            var skipEchoGates = retry?.SkipEchoGate == true;
            if (!skipEchoGates && Helpers.TriggerEchoGate.IsLikelyGenerativeEcho(rawText, echoableTerms))
            {
                // Intent is UNRESOLVABLE here — this return precedes trigger detection, so a
                // trigger-selected image prompt ("draw Alice", image trigger + Dictionary term)
                // looks identical to ordinary dictation. Disposal fails closed, which is what
                // makes that case safe without reordering the pipeline (Codex diff round 2).
                Logger.Information("Transcript is a likely bias-prompt echo — blocked (no override, paste, or enhancement); Retry armed when possible");
                HandleBlockedLikelyEcho();
                return;
            }

            void HandleBlockedLikelyEcho()
            {
                MarkTerminalPresentation(); // IMG-BG epoch fence
                RecordingState = RecordingState.Idle;
                // Codex round 4 (High): a false positive here is REAL SPEECH — a
                // dictation of exactly one hinted name is textually identical to an
                // echo. Terminal deletion would lose it irrecoverably, so mirror the
                // REL-13 retain branch: keep the WAV and arm Retry carrying
                // SkipEchoGate. No AI, clipboard, picker, or Enter runs either way.
                if (echoRetryCandidate != null && File.Exists(echoRetryCandidate.WavPath))
                {
                    StatusText = "Possible prompt echo — Retry to transcribe anyway";
                    keepWavForRetry = true;
                    _wavLedger.Track(echoRetryCandidate.WavPath);
                    // The retry pill owns the message (ArmRetry captures StatusText
                    // into RetryStatusText). AMBER, not the retry pill's red default —
                    // a suspicion, not a failure (owner UAT 2026-07-18).
                    ArmRetry(echoRetryCandidate with { SkipEchoGate = true }, MiniRecorderTone.Warning);
                    IsMiniRecorderVisible = true;
                }
                else
                {
                    EmitMiniRecorderError("Possible prompt echo — nothing pasted", MiniRecorderTone.Warning);
                }
                ResetHotkeyState?.Invoke();
            }

            // Trigger word detection — may override the active prompt for this transcription.
            // Uses the frozen snapshot candidates (not a fresh GetPrompts) so it can't
            // disagree with the composition above.
            var detection = _promptDetection.Analyze(text, promptRun.Candidates);
            if (detection.ShouldOverridePrompt && !string.IsNullOrWhiteSpace(detection.ProcessedText))
            {
                text = detection.ProcessedText;
                Logger.Information("Prompt trigger detected; processedLength={Length} userTerm={Prompt}",
                    text.Length, Helpers.LogValueSanitizer.SingleLine(detection.OverridePrompt?.Title ?? "(unknown)"));
            }

            // Resolve linked enhancement from app mode config (if any), against the snapshot.
            CustomPrompt? appModePrompt = promptRun.FindById(linkedEnhancementId);
            if (linkedEnhancementId != null)
            {
                if (appModePrompt != null)
                    Logger.Information("App Mode linked enhancement: userTerm={Prompt}", Helpers.LogValueSanitizer.SingleLine(appModePrompt.Title));
                else
                    Logger.Warning("App Mode linked enhancement ID '{Id}' not found (prompt may have been deleted)",
                        linkedEnhancementId);
            }

            // Resolve the effective prompt + its SOURCE (hotkey > trigger > app-mode >
            // active), all from the frozen snapshot.
            var (prompt, promptSource) = promptRun.Resolve(
                detection.ShouldOverridePrompt, detection.OverridePrompt, appModePrompt);
            if (prompt != null)
                Logger.Information("Prompt resolved: source={Source} isImage={IsImage} userTerm={Prompt}",
                    promptSource, prompt.IsImageGeneration, Helpers.LogValueSanitizer.SingleLine(prompt.Title));

            // PRM-4 Option B: a TRIGGER-SELECTED image generation is FAIL-CLOSED — it
            // fires only when the detected trigger boundary-matches the RAW transcript
            // AND the raw remainder carries a token outside the hint-word union (a
            // word the recognizer wasn't biased toward = a real description). Blocks a
            // word-replacement-created trigger and hint-fragment echo residue from
            // starting a costly, irreversible generation. Gated on the SOURCE (not
            // reference identity) so hotkey / App-Mode / globally-active image
            // prompts — deliberate, no spoken trigger — correctly skip it. Cover set =
            // the FULL composed hint list on every transport (Codex round 4: keyterm
            // bias can also produce false recognitions; over-blocking is cheap now
            // that the blocked path arms a gate-bypassing Retry).
            if (prompt?.IsImageGeneration == true && promptSource == Helpers.PromptSource.Trigger &&
                !skipEchoGates &&
                !Helpers.TriggerEchoGate.ImageActivationAllowed(rawText, detection.DetectedTriggerWord, hintTerms))
            {
                // Intent IS resolved here and it IS an image generation — the refusal means the
                // transcript looked like echo residue, but a real description is exactly what
                // the gate could not rule out, so this entry rides the image gate.
                transcriptionTrace.Resolve(isImagePrompt: true);
                Logger.Information("Trigger-selected image generation refused — no boundary-valid raw trigger with a real description (likely echo); Retry armed when possible");
                HandleBlockedLikelyEcho();
                return;
            }

            // Capture the image-generation intent + forced-prompt sources BEFORE the image branch,
            // which nulls `prompt` when image generation fails. An image-generation prompt must never
            // fall through to a TEXT enhancement: after a failure the prompt is nulled but a forced
            // source (hotkey/trigger/app-mode) would otherwise keep enhancement on and silently
            // clean up + overwrite the transcript. Decision is made by the pure ImagePromptGate below.
            var promptWasImageGeneration = prompt?.IsImageGeneration == true;
            var hasForcedPrompt = hotkeyOverride != null || detection.ShouldOverridePrompt || appModePrompt != null;

            // The normal emit point for the deferred transcription trace (REL-21): the first
            // place the effective prompt — and therefore image intent — is fully known, and
            // still ahead of every downstream trace entry, so file order is unchanged. Emitted
            // explicitly rather than left to disposal so the entry keeps its position (and
            // timestamp) relative to the request-side entry it is the return leg of.
            transcriptionTrace.Resolve(promptWasImageGeneration);
            transcriptionTrace.Emit();

            // Image generation flow — dispatches a BACKGROUND job (IMG-BG) and returns; the
            // pipeline goes Idle while the image generates, so recording stays available.
            if (ImageGenerationFeature.IsEnabled && prompt?.IsImageGeneration == true &&
                (_enhancement.IsEnabled || detection.ShouldOverridePrompt || hotkeyOverride != null || appModePrompt != null))
            {
                var transcriptionModelNameForImage = attemptModel;
                var handledByImageBranch = await TryRunImageGenerationAsync(
                    text, rawText, prompt, transcriptionModelNameForImage, language, wasPushToTalk, ct);
                if (handledByImageBranch) return;
                // IMG-BG: the ONLY fall-through is a REFUSAL (a background image job is
                // already running, or exclusive maintenance). The words are handled as a
                // PLAIN dictation below — ImagePromptGate suppresses enhancement for image
                // intents, the history row is a normal text row (no ImageFailedMarker), and
                // the refusal notice renders inside the GeneratingImage pill. Generation
                // FAILURES never fall through — they surface minutes later on the job's own
                // error-toned pill.
                prompt = null;
            }

            // Capture pre-enhancement text for redo context (text may be overwritten by enhancement).
            var preEnhancementText = text;

            // AI Enhancement step:
            // - Hotkey or trigger word always forces enhancement (even if IsEnabled is off)
            // - IsEnabled alone only enhances if a prompt is actively selected
            //   (allows "enhancement on but only via hotkey/trigger" when no prompt is active)
            string? enhancedText = null;
            bool enhancementFailed = false;
            // Text + its pill budget travel as ONE value from catch to compose (Codex plan round 3,
            // 2026-07-26): a split string/budget pair is exactly how a budget drifts from its text —
            // and a default-initialized struct would carry MaxCodeUnits = 0 into the composer, so the
            // no-failure state is null, never `default`.
            ProviderPillText? enhancementFailure = null;

            // An image-generation intent must never be run as a text enhancement — the pure gate
            // reproduces MainViewModel's shouldEnhance and suppresses it for image intents (covers
            // the image-failure fall-through, where the image branch nulled the prompt but a forced
            // source would otherwise keep enhancement on and text-clean the transcript).
            var shouldEnhance = Helpers.ImagePromptGate.ShouldEnhanceText(
                promptWasImageGeneration,
                _enhancement.IsEnabled,
                hasForcedPrompt,
                prompt != null);

            if (shouldEnhance)
            {
                // Skip CTS (ENH-3): the stop button cancels THIS, not the pipeline ct, so a skip keeps
                // the raw transcript flowing to paste/history/redo. Assign the field BEFORE the
                // RecordingState flip so CanSkipCurrentEnhancement is already true at the transition App
                // observes — the pill's stop button then carries Skip semantics (and the matching
                // stop-class handle) from the first frame of Enhancing. Redo / image-options Enhancing
                // installs no CTS and stays non-skippable, so its stop is a full cancel (Codex diff
                // review finding 4). The ORDERING still matters after PILL-5 removed the hint that
                // used to depend on it: a late flip would rotate the handle mid-Enhancing and reject
                // a stop press the user had already begun.
                using var enhancementSkipCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                SetEnhancementSkipCts(enhancementSkipCts);
                RecordingState = RecordingState.Enhancing;
                StatusText = "Enhancing...";
                try
                {
                    if (hotkeyOverride != null)
                        Logger.Information("Using hotkey-overridden prompt: userTerm={Prompt}", Helpers.LogValueSanitizer.SingleLine(hotkeyOverride.Title));
                    // Fresh master-gated fetch at the enhancement stage's own moment
                    // (stage-by-stage semantics; Codex diff r1) — reusing the
                    // pre-transcription vocabContext would send stale vocabulary after
                    // a mid-run Dictionary-off flip. Rides the skip CTS: a stop-button
                    // skip aborts the fetch along with the enhancement it feeds.
                    var enhancementVocab = await _customVocabulary.GetVocabularyContextAsync(enhancementSkipCts.Token);
                    enhancedText = await _enhancement.EnhanceAsync(text, prompt, enhancementVocab.Terms, ct: enhancementSkipCts.Token);
                    // Whitespace check, matching the service-side fallback — a custom prompt may
                    // legitimately return punctuation only. See AIEnhancementService for why.
                    if (!string.IsNullOrWhiteSpace(enhancedText))
                    {
                        text = enhancedText;
                        Logger.Information("Transcription enhanced ({Length} chars)", enhancedText.Length);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Real pipeline cancel (supersede) — abort via the outer OCE catch.
                    // Pre-ENH-3 this fell into the generic catch and pasted raw text
                    // into a pipeline that was being torn down.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Stop-button skip: only the linked skip CTS fired — continue with
                    // the raw transcript; paste, history, and redo proceed normally.
                    Logger.Information("Enhancement skipped via stop button — using unenhanced text");
                    enhancementFailed = true;
                    // App copy on the app budget — this path bypasses ProviderPillSafeText, so it
                    // states its own budget explicitly rather than inheriting a struct default of 0.
                    enhancementFailure = new ProviderPillText("Enhancement skipped", DefaultPillMaxCodeUnits);
                }
                catch (Exception ex)
                {
                    // ENH-15 adds InvalidApiKeyFormatException, and this site is why it is not
                    // optional: BEFORE the pre-send guard existed, a malformed stored key failed at
                    // SEND as HttpRequestException, which this list already covered. Introducing the
                    // typed exception without adding it here would have REGRESSED the very incident
                    // the change fixes, into one Sentry event per utterance (Kimi diff review).
                    if (ex is HttpRequestException or InvalidOperationException or TimeoutException
                        or Helpers.InvalidApiKeyFormatException)
                        Logger.Warning("AI enhancement failed, using unenhanced text: {ErrorType}: {ErrorMessage}",
                            ex.GetType().Name, ex.Message);
                    else
                        // Unexpected exceptions keep Error + stack so real defects in the
                        // hottest enhancement path DO reach Sentry (F16 — this was the sole
                        // outlier among the five sibling catch sites, all of which already
                        // split Warning/provider-shaped vs Error/unexpected per the
                        // documented provider-noise policy).
                        Logger.Error(ex, "AI enhancement failed, using unenhanced text");
                    enhancementFailed = true;
                    // UI-only text (never logged): the provider's structured error when
                    // available (ProviderApiException.UserMessage), else the status-only
                    // exception message — but never a typed 401's body, which is credential prose.
                    // UNTRUNCATED on purpose: ComposeFallbackFailureStatus trims then budgets it,
                    // and the budget rides the same value (110 only for structured provider prose).
                    enhancementFailure = ProviderPillReasonForEnhancementFallback(ex);
                }
                finally
                {
                    SetEnhancementSkipCts(null);
                }
            }

            LastTranscription = text;

            // Append trailing space if configured — but skip when Enter will be sent after paste
            var appendSpace = _settings.GetBool(AppDefaults.AppendTrailingSpace, true);
            var willSendEnter = _settings.GetBoolDefaulted(AppDefaults.SendEnterAfterPaste)
                && wasPushToTalk;
            var textToPaste = (appendSpace && !willSendEnter) ? text + " " : text;

            // Hide mini recorder BEFORE pasting so it doesn't interfere with focus
            IsMiniRecorderVisible = false;
            await Task.Delay(100);

            // UI-7: COPY ONLY — never touch the paste path when a picker dialog was on screen at
            // recording start. A zero target does NOT mean "clipboard" to ClipboardService: every
            // gate there treats zero as "no target to check" and passes (TargetStillValid,
            // TargetIsForeground, checkForeground, ForceForegroundWindow all return true/null on
            // zero), so the text would be Ctrl+V'd into whatever holds the foreground — VoiceWink,
            // by construction — and the Enter gate at PostPasteEnterSequence treats zero the same
            // way, so a push-to-talk dictation would also press Enter in our own dialog. Both
            // reviewers found this independently; it is worse than the defect UI-7 set out to fix.
            //
            // Branching here rather than deeper is deliberate: the paste path's contract is "put
            // this text in that window", and there is no window. Nothing to weaken, nothing to
            // gate — just don't call it.
            //
            // Gated on the HANDLE, not on the picker flag, so it also covers the two paths that
            // already zeroed the target and already CLAIMED a clipboard fallback in their comments
            // without anything implementing one — TRN-17's retry cancel (`MainViewModel.cs:2317`)
            // and the targetless-capture edge. Both reviewers asked for the unification rather than
            // a UI-7-only special case; the flag now only chooses which sentence the user reads.
            PasteResult pasteResult;
            if (_targetWindowHandle == IntPtr.Zero)
            {
                Logger.Information(
                    "No paste target ({Reason}) — copying the transcript to the clipboard",
                    _pasteSuppressedByPicker ? "picker open" : "none captured");
                pasteResult = await _clipboard.SetClipboardAsync(textToPaste)
                    ? PasteResult.Fail(
                        _pasteSuppressedByPicker ? PasteAttemptOutcome.NoTargetPickerOpen : PasteAttemptOutcome.NoTarget,
                        _pasteSuppressedByPicker
                            ? Helpers.PasteResultPresentation.PickerOpenMessage
                            : Helpers.PasteResultPresentation.NoTargetMessage)
                    : PasteResult.Fail(PasteAttemptOutcome.ClipboardSetFailed);
            }
            else
            {
                var pastedElement = await _focusSlot.ResolveAsync();
                pasteResult = await PasteWithOptionalEnterAsync(
                    textToPaste, willSendEnter, _targetWindowHandle, pastedElement);
            }
            var pasteLanded = pasteResult.Succeeded;

            // PST-11: hold the element this dictation actually pasted into, for the redo that may
            // follow. Taken HERE rather than in the finally for one reason worth stating: the
            // resolve above has already materialized it, so retention costs nothing, whereas the
            // finally would have to TakeForHandoffAsync on paths that never resolved (image, empty
            // transcript) and pay a bounded cross-process wait for an element no redo can use.
            //
            // Gated on the paste LANDING, which is the evidence that matters: a paste that landed
            // proves the element was a real editable that accepted text. A declined one proves the
            // opposite, and retaining it would re-offer the rescue the very element that just
            // failed. Ownership transfers out of the slot, so the finally's owner-scoped release
            // finds it empty and cannot double-release.
            //
            // KNOWN LIMIT, measured rather than assumed (PST-12; Codex verification round): this
            // retains the SLOT's element, which on the PST-4 recapture path is not the element that
            // received the text. If the recording-start element went stale (a Chromium re-render)
            // and the paste succeeded by recapturing whatever held focus, the slot still holds the
            // dead one, so the redo's fallback probes dead and the rescue declines. Fail-closed —
            // it degrades to exactly the pre-PST-11 answer, never a wrong paste — and rare: in five
            // days of the owner's logs, 265 of 266 identity-verified restores were direct/Success
            // and the recapture path fired ONCE (~0.4%). Propagating the recaptured element would
            // mean changing UiaFocusBridge's ownership contract (RefocusCurrentElement releases it
            // internally) and threading it back up through the paste result — a cross-layer change
            // to RCW lifetime in the repo's most-scarred subsystem, for a path that already lands
            // on today's behaviour. Carded, not done here.
            if (pasteLanded && _targetWindowHandle != IntPtr.Zero)
                _redoFocusRetention.Retain(await _focusSlot.TakeForHandoffAsync(), _targetWindowHandle);
            if (!pasteLanded)
            {
                // Capture message + tone together; the surface is chosen once below (after
                // the fail-soft writes) so a redo-owning branch can present it as an
                // attention redo instead of a flash-then-replaced pill. Declines are amber
                // warnings; a clipboard-set failure stays a red error.
                (clipboardFallbackMessage, clipboardFallbackTone) =
                    Helpers.PasteResultPresentation.Present(pasteResult);
                StatusText = clipboardFallbackMessage;
                // Don't return — still save to history below
            }

            var transcriptionCompletedAtUtc = DateTime.UtcNow;
            await RecordLifetimeMetricsAsync(text, transcriptionCompletedAtUtc, ct);

            // Save to history (if enabled). Plain row write — since IMG-BG, failed-image
            // reference retention lives in the background job (PersistFailedImageJobAsync),
            // so this path always routes through with null references (no reference IO).
            var modelName = attemptModel;
            var fallthroughPersist = await _referencePersistence.PersistFailedAttemptWithRowAsync(
                null,
                _settings.GetBool(AppDefaults.IsHistoryEnabled, true),
                referencePath => _historyWriter.TryWriteAsync(
                    new Models.Entities.TranscriptionRecord
                    {
                        Text = rawText,
                        EnhancedText = enhancementFailed ? "[Enhancement failed]" : enhancedText,
                        WasEnhanced = enhancedText != null || enhancementFailed,
                        PromptUsed = prompt?.Id,
                        Timestamp = transcriptionCompletedAtUtc,
                        AudioFilePath = null, // WAV is deleted after transcription
                        ModelName = modelName,
                        EnhancementModelName = enhancedText != null || enhancementFailed
                            ? GetEnhancementModelLabel(prompt) : null,
                        Language = language,
                        ReferenceImagePath = referencePath
                    }, ct), ct);
            _lastTranscriptionHistoryId = fallthroughPersist.HistoryId;

            var transcriptionModelName = attemptModel;

            // IMG-BG epoch fence: a PLAIN completion (no arm below) is a terminal presentation
            // too — an older pending image completion must not overwrite this dictation's
            // "Done"/LastTranscription (Codex plan round 3).
            MarkTerminalPresentation();
            RecordingState = RecordingState.Idle;
            // Only claim success when the paste actually landed; on a declined paste
            // StatusText stays the clipboard-fallback message set above. Same copy as the redo
            // path — the d763bfc rename missed this branch (workflow review 2026-07-25) and the
            // two drifted apart for a while; keep them in step.
            if (pasteLanded)
                StatusText = "Done";

            // Show enhancement error AFTER state is Idle and text is pasted,
            // so UpdateState(Idle) doesn't overwrite the error display
            if (enhancementFailed)
            {
                // Text enhancement failed — offer redo to retry with a different model.
                // ENH-1: a silent raw-text fallback looked identical to a successful
                // enhancement (the user only noticed by READING the pasted text). The
                // redo pill owns the message — the affordance renders StatusText (captured into
                // RedoStatusText) in error styling — deliberately NOT a separate message pill, which
                // would supersede the just-armed redo. Already truncated inside the composer (reason-first).
                // Unwrap the ONE captured value — text and budget together, so a structured provider
                // reason composes at 110 (two lines) while app copy and internal text stay at 55.
                StatusText = ComposeFallbackFailureStatus(
                    wasImageAttempt: false, pasteLanded,
                    enhancementFailure?.Text,
                    enhancementFailure?.MaxCodeUnits ?? DefaultPillMaxCodeUnits);
                ArmRedo(new RedoContext(
                    RawText: preEnhancementText,
                    EnhancedText: null,
                    Prompt: prompt,
                    WasImageGeneration: false,
                    TargetWindow: _targetWindowHandle,
                    WasPushToTalk: wasPushToTalk,
                    TranscriptionModelName: transcriptionModelName,
                    TranscriptionLanguage: language,
                    // Carry the prompt's effective provider/model so a failed-attempt redo
                    // pre-selects them, matching the success arm.
                    PreviousProvider: ResolvePromptProvider(prompt),
                    // Effective model (override, else the effective provider's model) so a "Default"
                    // model doesn't fall through to the global selection in the redo dialog (F17).
                    PreviousModel: _enhancement.ResolveModelForPrompt(prompt)
                ), MiniRecorderTone.Error);
                IsMiniRecorderVisible = true;
            }
            else if (enhancedText != null)
            {
                // Enhancement succeeded — offer redo with a different model. If the paste
                // was declined, the redo pill IS the attention surface (warning-toned,
                // attention-timed) and owns the clipboard-fallback message — a green
                // "Done" redo would otherwise clear the pill and hide the decline (PILL-3).
                var (redoStatus, redoToneResolved) = ResolvePostPasteRedoPresentation(
                    pasteLanded, clipboardFallbackMessage, clipboardFallbackTone);
                StatusText = redoStatus;
                ArmRedo(new RedoContext(
                    RawText: preEnhancementText,
                    EnhancedText: enhancedText,
                    Prompt: prompt,
                    WasImageGeneration: false,
                    TargetWindow: _targetWindowHandle,
                    WasPushToTalk: wasPushToTalk,
                    TranscriptionModelName: transcriptionModelName,
                    TranscriptionLanguage: language,
                    // Carry the EFFECTIVE provider/model so the redo dialog pre-selects the prompt's
                    // override (not the global selection) — matching the failed arm (F17).
                    PreviousProvider: ResolvePromptProvider(prompt),
                    PreviousModel: _enhancement.ResolveModelForPrompt(prompt)
                ), redoToneResolved);
                // Re-show MiniRecorder for the redo state (it was hidden before paste).
                // Set AFTER IsRedoAvailable so _showingRedoState is already true and
                // UpdateState(Idle) — which is enqueued but not yet processed — gets skipped.
                IsMiniRecorderVisible = true;
            }
            else if (!pasteLanded)
            {
                // Plain transcription (no enhancement → no redo affordance): the message pill
                // is the only surface. Shown here, after the fail-soft writes, so it is not a
                // flash-then-replaced pill. Tone was chosen with the message at the paste site.
                EmitMiniRecorderError(
                    clipboardFallbackMessage ?? Helpers.PasteResultPresentation.GenericDeclineMessage,
                    clipboardFallbackTone);
            }

            if (pasteLanded)
                Logger.Information("Transcription pasted ({Length} chars)", text.Length);
            else
                // Outcome-aware (PST-6): every decline used to be logged as "focus restore
                // failed", which is wrong for the delivery outcomes (the restore succeeded)
                // and for the clipboard-set failure, where the text is NOT on the clipboard
                // at all.
                //
                // PST-16: this must describe OUR ACTION, never the target's contents. It used
                // to read "Transcription NOT pasted", and PasteDeliveryUncertain now reaches
                // it precisely when absence CANNOT be proved — so on a lagging Chromium tree
                // that line asserted the text was missing while it was sitting in the
                // composer. That is the same false-absence claim the pill copy had, and this
                // log is what a diagnosis starts from (it is the closing line of the
                // 2026-08-30 10:27:13 double-paste trace).
                Logger.Warning("Transcription auto-paste did not complete ({Length} chars) — outcome={PasteOutcome}; {Disposition}",
                    text.Length, pasteResult.Outcome,
                    pasteResult.Outcome == Helpers.PasteAttemptOutcome.ClipboardSetFailed
                        ? "clipboard write failed — text not recoverable from the clipboard"
                        : "left on clipboard for manual Ctrl+V");
        }
        catch (OperationCanceledException oce)
            when (!Helpers.TranscribeFailureSurface.IsUserCancel(pipelineCt.IsCancellationRequested))
        {
            // A client-side timeout, NOT a user cancel: HttpClient.Timeout expiry
            // surfaces as TaskCanceledException with the caller token untouched (the
            // transcription named client sends with the raw pipeline token and a 5-min
            // budget). Route through the shared failure surface exactly like a
            // TimeoutException so a transcription-stage upload timeout retains the WAV
            // and arms retry instead of silently discarding the recording (Codex diff
            // review — the exact SLOW_UPLOAD class REL-12 exists for).
            HandleTranscribePipelineFailure(new TimeoutException("Transcription timed out", oce));
        }
        catch (OperationCanceledException)
        {
            Logger.Information("Transcription/enhancement cancelled by user");
            userCancelledPipeline = true; // REL-17: user discard — never debug-retained
            MarkTerminalPresentation(); // IMG-BG epoch fence
            RecordingState = RecordingState.Idle;

            // PST-6 (Codex round-4 C2): the paste already happened and was declined —
            // the post-paste tail (metrics/history) is what got cancelled. Presenting
            // "cancelled" here would ERASE the delivery warning the user must see, and
            // an unconfirmed delivery is exactly the case where they need it. The
            // captured presentation wins; the WAV/hotkey cleanup below still runs.
            if (clipboardFallbackMessage != null)
            {
                EmitMiniRecorderError(clipboardFallbackMessage, clipboardFallbackTone);
                IsMiniRecorderVisible = true;
                ResetHotkeyState?.Invoke();
                return;
            }

            // REL-12 [R6]: cancelling a RETRY attempt must not destroy the recording
            // the feature exists to protect (the stop button is tappable throughout
            // Transcribing) — return to the retry-armed state. A NORMAL recording's
            // cancel keeps today's discard semantics (pinned by ShouldReArmOnCancel).
            if (retry != null
                && Helpers.TranscribeFailureSurface.ShouldReArmOnCancel(
                    wasRetryRun: true, wavStillExists: File.Exists(retry.WavPath)))
            {
                keepWavForRetry = true;
                _wavLedger.Track(retry.WavPath);
                StatusText = "Retry cancelled";
                // A user cancel is transient, not a failure — Warning (auto-hides), never a
                // persistent red pill, consistent with startup cancellation (Codex review 2026-07-20).
                // The retry stays armed for the hotkey either way.
                ArmRetry(retry, MiniRecorderTone.Warning);
                IsMiniRecorderVisible = true;
            }
            else
            {
                IsMiniRecorderVisible = false;
            }
            ResetHotkeyState?.Invoke();
        }
        catch (Exception ex)
        {
            HandleTranscribePipelineFailure(ex);
        }
        finally
        {
            // End-of-life disposition for the WAV (REL-17): an armed retry keeps
            // ownership (REL-12); an explicit user cancel discards; otherwise the
            // keep-recordings debug setting MOVES it to Recordings\Debug (location =
            // retention marker; the unconditional 7-day sweep owns lifetime) instead of
            // deleting. Pure decision in RecordingDisposition.Decide.
            if (recordingPath != null)
            {
                var disposition = Helpers.RecordingDisposition.Decide(
                    keepWavForRetry,
                    userCancelledPipeline,
                    _settings.GetBool(AppDefaults.KeepRecordingsForDebug, false));
                if (disposition == Helpers.RecordingEndDisposition.RetainForDebug)
                {
                    MoveRecordingToDebugOrDelete(recordingPath);
                }
                else if (disposition == Helpers.RecordingEndDisposition.Delete)
                {
                    try
                    {
                        File.Delete(recordingPath);
                        // Confirm out of the ledger: this may have been a retry run's
                        // retained WAV reaching its natural end (success/abort/cancel).
                        // Closes ONLY an announced retention — TryCloseRetention logs iff it
                        // removed a tracked path, so a "Recording retained for retry" line naming
                        // this file always gets its closing counterpart, while an ordinary
                        // dictation's WAV (never tracked) stays unlogged and this cannot become a
                        // per-recording line. Without it the retry-SUCCEEDS path — the most common
                        // ending a retained WAV has — deleted the file in silence while every
                        // other disposition named theirs, and that asymmetry reads as "still on
                        // disk in Recordings\".
                        _wavLedger.TryCloseRetention(recordingPath, "deleted after its retry completed");
                    }
                    catch (Exception ex) { Logger.Debug(ex, "Failed to delete recording {Path}", recordingPath); }

                    // REL-23: the Delete disposition must also take the raw sibling the gain
                    // layer may have retained — "an explicit user cancel discards" binds the
                    // PRE-GAIN microphone copy at least as strongly as the gained WAV (it is
                    // the more sensitive artifact), and a toggle-off delete promises to keep
                    // nothing. Fail-soft; the 7-day Debug sweep remains the backstop.
                    Services.Audio.RecordingAudioPreparation.DeleteRawSiblingFor(recordingPath);
                }
            }
            _currentRecordingPath = null;
            // Release UIA capture: covers every early-return path in this method
            // (no file, silent audio, empty text, image-fully-handled, paste-failed)
            // because they all flow through this finally.
            ReleaseCapturedFocusedElement(CapturedFocusOwner.Recording);
        }
        // C3: the single-flight lease releases via `using var` at method-end (outer scope) —
        // NOT here, so a throw from this cleanup finally can never strand it.

        // Shared failure surface for the generic catch AND the non-caller-OCE (client
        // timeout) catch above — a local function so it can mutate keepWavForRetry and
        // read retryCandidate/clipboardFallback state via closure. Runs synchronously
        // on the UI thread; its early return exits before the finally runs (identical
        // control flow to the former in-catch body).
        void HandleTranscribePipelineFailure(Exception ex)
        {
            // Cloud/network failures → Warning: environmental, already user-surfaced via
            // the message below, and the client sites logged status+body. Everything else
            // (local Whisper, pipeline code) stays Error + stack so it reaches Sentry.
            // InvalidOperationException deliberately stays in the Error branch here —
            // on the transcribe path it can come from the local Whisper stack, which IS
            // an app defect, unlike the enhancement paths where clients throw it for
            // provider-shape outcomes.
            // ENH-15 adds InvalidApiKeyFormatException: a legacy malformed key would otherwise log
            // Error on EVERY dictation, and Error+ ships to Sentry — one event per utterance until
            // the user re-enters the key. Its message is app-authored and carries no key material,
            // so it is safe in a log template.
            // TRN-64 adds GpuSelfTestRefusedException: an EXPECTED refusal (the verdict itself
            // was reported at the level REL-30 chose when it landed), so a retried Whisper pick
            // must not put an Error per attempt in front of Sentry.
            if (ex is HttpRequestException or TimeoutException or Helpers.InvalidApiKeyFormatException
                or Helpers.GpuSelfTestRefusedException)
                Logger.Warning("Failed to transcribe: {ErrorType}: {ErrorMessage}", ex.GetType().Name, ex.Message);
            else
                Logger.Error(ex, "Failed to transcribe");
            MarkTerminalPresentation(); // IMG-BG epoch fence
            RecordingState = RecordingState.Idle;

            // PILL-3 safety net: transcription+enhancement already succeeded and the text is
            // on the clipboard (only the paste, then a later fail-soft bookkeeping write, failed).
            // Surface the honest clipboard-fallback message with the tone captured AT the paste
            // site, never a misleading "Transcription failed". The logging above already ran,
            // so the unexpected write still reaches Sentry.
            if (clipboardFallbackMessage != null)
            {
                EmitMiniRecorderError(clipboardFallbackMessage, clipboardFallbackTone);
                ResetHotkeyState?.Invoke();
                return;
            }

            var errorMsg = DescribeTranscriptionFailure(ex);

            // REL-12: a transcription-stage failure with the WAV still on disk retains
            // the recording and arms the retry pill instead of the plain error pill.
            // Routing is the pure TranscribeFailureSurface (clipboard-fallback already
            // returned above, so only the two lower-precedence kinds reach here).
            var surface = Helpers.TranscribeFailureSurface.Decide(
                clipboardFallbackAvailable: false,
                retryCandidateArmed: retryCandidate != null,
                wavStillExists: retryCandidate != null && File.Exists(retryCandidate.WavPath));
            if (surface == Helpers.TranscribeFailureSurface.Kind.RetryPill)
            {
                keepWavForRetry = true;
                _wavLedger.Track(retryCandidate!.WavPath);
                // The retry pill owns the message (ArmRetry captures StatusText into
                // RetryStatusText) — deliberately NOT ShowMiniRecorderError, whose
                // dismiss would hide the just-armed retry pill (mirrors the redo arms).
                StatusText = errorMsg;
                ArmRetry(retryCandidate);
                IsMiniRecorderVisible = true;
            }
            else
            {
                // Same single-string rule as the start-failure branch — the pill copy is the
                // message (copy review 2026-07-25).
                StatusText = errorMsg;

                // Show the error pill (persistent — ERR-PERSIST): the controller keeps it until the
                // user dismisses it (corner ×) or a newer presentation replaces it. Mark terminal
                // for the image-completion recency gate.
                MarkTerminalPresentation();
                ShowMiniRecorderError?.Invoke(errorMsg, MiniRecorderTone.Error, PillMessageAction.None);
            }

            // TRN-64: after the surface (the retry pill is armed by now), offer the restart that
            // puts Whisper on the processor — once per session.
            if (ex is Helpers.GpuSelfTestRefusedException { OffersRestart: true } refusal)
            {
                OfferGpuSelfTestRestart(refusal);
            }

            ResetHotkeyState?.Invoke();
        }
    }

    /// <summary>TRN-64: the one-shot restart offer after a refused Whisper decode. Fire-and-forget
    /// by design (the pipeline has already ended); the claim is released when the handler could
    /// not show the dialog, so a later refusal offers again (Codex r2 F4).</summary>
    private void OfferGpuSelfTestRestart(Helpers.GpuSelfTestRefusedException refusal)
    {
        if (!_gpuSelfTestRestartOffer.TryClaim())
        {
            return;
        }
        var handler = ShowGpuSelfTestRestartRequested;
        if (handler is null)
        {
            _gpuSelfTestRestartOffer.Release();
            return;
        }
        _ = OfferAsync();

        async Task OfferAsync()
        {
            var shown = false;
            try
            {
                shown = await handler.Invoke(refusal).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not offer the restart after a refused Whisper decode");
            }
            if (!shown)
            {
                _gpuSelfTestRestartOffer.Release();
            }
        }
    }

    /// <summary>
    /// Ensure the selected LOCAL model is downloaded and loaded — Whisper or Parakeet; the runtime
    /// seam decides which engine, this method never does. Supports cancellation
    /// and reports download progress via events.
    /// When <paramref name="modelNameOverride"/> is provided, loads that specific model
    /// instead of reading from settings (used by App Mode).
    /// </summary>
    private async Task EnsureModelLoadedAsync(
        CancellationToken ct, string? modelNameOverride = null, string? languageOverride = null)
    {
        // Canonicalized ONCE at the top: settings and App Mode overrides carry whichever Parakeet
        // bundle spelling was current when written (TRN-29 flip), and everything below — the
        // registry route, the prepare, and especially the NotDownloaded repair's exact-name
        // catalog `.First()` — must see the active row's name. Without this, the repair path
        // threw "Sequence contains no matching element" on an upgrader's un-rewritten selection
        // and the user saw "Recording failed" with no in-app route back (self-review, config lens).
        var selectedName = Models.ParakeetCatalog.CanonicalName(
            modelNameOverride
            ?? _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel))!;

        // The language this attempt will recognise with — resolved before preparing so the model
        // loads with it and TranscribeAsync doesn't trigger a second 2-8s rebuild (PRM-5).
        var language = _settings.GetString(AppDefaults.SelectedLanguage, "auto");
        var effectiveLanguageOverride = languageOverride ?? _activeAppModeConfig?.LanguageOverride;
        if (!string.IsNullOrEmpty(effectiveLanguageOverride))
            language = effectiveLanguageOverride;

        // What the MODEL will actually do with that — a different question from what was asked for,
        // and the one the load must use. Previously this site applied no model constraint while the
        // Models page's preload coerced `.en` to English, so the two prepared the same model with
        // two different languages and paid a rebuild for the disagreement.
        var effective = Helpers.EffectiveTranscriptionLanguage.ForModelName(selectedName, language);
        if (effective.Diverges)
        {
            Logger.Information(
                "Model constrains the recognition language: requested {Requested}, effective {Effective} ({Constraint})",
                language, effective.Language, effective.Constraint);
        }
        language = effective.Language;

        SetPreflightStatus("Loading model...");
        var outcome = await _localModels.PrepareAsync(selectedName, language, ct);

        if (outcome == Services.Transcription.PrepareOutcome.Loaded)
        {
            IsModelLoaded = true;
            return;
        }

        if (outcome == Services.Transcription.PrepareOutcome.UnknownModel)
        {
            // Owner decision 2026-08-02: resolve the SELECTED model or fail honestly. This used to
            // fall back to ggml-small, which silently transcribed with a model the user never
            // chose — and once a second local runtime exists it would also mean the wrong ENGINE.
            // The message deliberately does not echo the stored name: settings import validates
            // that field only as a string, so it can carry control or bidi characters.
            Logger.Error("Selected local model is not served by any runtime");
            throw new InvalidOperationException(
                "That transcription model isn't available. Pick one on the Models page.");
        }

        if (outcome == Services.Transcription.PrepareOutcome.Unavailable)
        {
            // MUST be handled before the download path below. Every arm after this point treats
            // "not Loaded" as "fetch it", so without this an engine that is compiled out or
            // unsupported on this CPU would offer to download a multi-gigabyte model it could never
            // run — the download would succeed and the next recording would fail identically.
            Logger.Error("Selected local model's runtime is unavailable on this build or machine");
            throw new InvalidOperationException(
                "That transcription model can't run on this version of VoiceWink. Pick one on the Models page.");
        }

        // NotDownloaded — a known catalog model whose file isn't on disk. Download THE SELECTED
        // ONE. This is where the second substitution lived: the old code took whatever else
        // happened to be downloaded (downloaded[0]) and transcribed with that instead.
        {
            var modelInfo = Models.PredefinedModels.Models
                .First(m => string.Equals(m.Name, selectedName, StringComparison.OrdinalIgnoreCase));

            IsDownloadingModel = true;
            DownloadModelName = modelInfo.DisplayName;
            SetPreflightStatus($"Downloading {modelInfo.DisplayName}...");

            // WinUI 3 has no SynchronizationContext, so Progress<T> posts callbacks
            // to the thread pool — adding an extra hop that delays/batches updates.
            // Use a synchronous IProgress implementation so the event fires directly
            // on the download thread; ShowDownloadProgress marshals to UI via DispatcherQueue.
            var displayName = modelInfo.DisplayName;
            var progress = new SynchronousProgress<double>(p =>
            {
                DownloadProgress = p;
                DownloadProgressUpdated?.Invoke(p, displayName);
            });

            try
            {
                await _modelDownloader.DownloadModelAsync(modelInfo, progress, ct);
                // Only persist model name if this is a user-initiated download, not an App Mode override
                if (modelNameOverride == null)
                {
                    _settings.SetString(AppDefaults.SelectedModelName, modelInfo.Name);
                    App.Services.GetRequiredService<ModelManagementViewModel>().SelectedModelName = modelInfo.Name;
                }
            }
            finally
            {
                IsDownloadingModel = false;
                DownloadModelName = "";
            }
        }

        // Freshly downloaded — prepare again, now that the files exist. Same seam, same language.
        SetPreflightStatus("Loading model...");
        var afterDownload = await _localModels.PrepareAsync(selectedName, language, ct);

        // Unavailable is split out because the stock advice is actively HARMFUL here: "try
        // downloading it again" cannot repair an engine, and it invites the user to delete a bundle
        // that is perfectly good and just cost them 670 MB.
        //
        // The CAUSE is narrower than the general Unavailable case, which is why the message differs
        // from the pre-download one. The lever and the CPU floor are decided by IsAvailable, a
        // cached static property consulted BEFORE the download — if either had said no, we would
        // never have got here. So the only cause that survives to this point is the engine failing
        // to START on this machine: a native library that would not load, an antivirus quarantine
        // of onnxruntime.dll, a broken payload. The copy says "on this computer" rather than "on
        // this version of VoiceWink" because that is a machine fact, and an earlier draft blaming
        // the app version would have sent users hunting for an update that changes nothing (Codex).
        if (afterDownload == Services.Transcription.PrepareOutcome.Unavailable)
        {
            Logger.Error("Model downloaded but its engine failed to start on this machine");
            throw new InvalidOperationException(
                "That model downloaded, but its engine couldn't start on this computer. " +
                "Pick a different model on the Models page — the download is kept.");
        }

        if (afterDownload != Services.Transcription.PrepareOutcome.Loaded)
        {
            Logger.Error("Model still unavailable after a completed download");
            throw new InvalidOperationException(
                "That transcription model couldn't be loaded. Try downloading it again from the Models page.");
        }
        IsModelLoaded = true;
    }

    /// <param name="captureSessionId">The recorder session this recording is watching, or 0 when
    /// no capture is attached YET. The warm path must pass 0 — it starts the timer before the
    /// attach — and assign the real id afterwards; the cold path has a live capture already and
    /// passes it here, so even this method's synchronous prime tick can catch a dead recorder
    /// (AUD-11). Passing it rather than setting a field around the call keeps that ordering
    /// visible at both sites instead of implied by statement order.</param>
    private void StartMeterTimer(long captureSessionId)
    {
        // AUD-11: the one call every start path makes, so it is where the watchdog resets. See
        // AudioStallMonitor.BeginRecording for why both halves of that reset are load-bearing.
        _stallMonitor.BeginRecording(captureSessionId);
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // 30fps — fast onset feedback
        _meterTimer.Tick += OnMeterTimerTick;
        _meterTimer.Start();
        // Prime the visualizer so an already-arrived first chunk doesn't wait
        // up to 33ms for the next tick. Caller is on the dispatcher thread.
        OnMeterTimerTick(this, EventArgs.Empty);
    }

    private void StopMeterTimer()
    {
        _meterTimer?.Stop();
        if (_meterTimer != null)
        {
            _meterTimer.Tick -= OnMeterTimerTick;
        }
        _meterTimer = null;
        AudioLevel = -160f;
        RecordingStartedAtUtc = null;
        // AUD-11: every teardown passes through here, so this is the backstop that stops the tick
        // from evaluating a session nobody is recording any more.
        _stallMonitor.Detach();
    }

    /// <summary>
    /// AUD-11 dead-capture / stall watchdog state. A type rather than two fields so its lifecycle
    /// \u2014 the per-recording reset and the warm path's attach-after-prime ordering \u2014 is executable
    /// in a test without constructing this ViewModel.
    /// </summary>
    private readonly Helpers.AudioStallMonitor _stallMonitor = new();

    private void OnMeterTimerTick(object? sender, object e)
    {
        AudioLevel = _recorder.AveragePower;

        // Snapshot both recorder reads ONCE. NAudio's capture thread can clear the live session
        // between two reads, and the log line below is forensic \u2014 reporting a session the verdict
        // never compared against would misdirect the next incident reconstruction. Making the
        // identity atomic and then reading it twice would have given that back (Kimi diff review).
        var recorderSessionId = _recorder.CurrentSessionId;
        var silence = DateTime.UtcNow - _recorder.LastDataReceivedAt;

        switch (_stallMonitor.Evaluate(RecordingState == RecordingState.Recording, recorderSessionId, silence))
        {
            case Helpers.AudioStallVerdict.WarnRecorderDead:
                StatusText = "Microphone stopped \u2014 recording may be incomplete";
                Logger.Warning(
                    "Capture lost while recording: expected session {ExpectedSession}, recorder reports {RecorderSession}",
                    _stallMonitor.ExpectedSessionId, recorderSessionId);
                break;

            case Helpers.AudioStallVerdict.WarnStall:
                StatusText = "No audio \u2014 check microphone";
                Logger.Warning("Audio stall detected: no data for {Seconds:F1}s", silence.TotalSeconds);
                break;

            case Helpers.AudioStallVerdict.Recovered:
                StatusText = "Recording...";
                break;
        }
    }

    /// <summary>
    /// Paste text at the cursor, suppress the prompt-hotkey hook during the Ctrl+V, and send
    /// Enter afterwards when requested. Returns false if the clipboard paste failed. Enter is
    /// sent ONLY when the paste landed (<c>pasted == true</c>) and the target is still the
    /// foreground window after the post-paste delay — otherwise a declined/failed paste would
    /// inject a stray Enter into whatever app now has focus.
    /// </summary>
    /// <param name="fallbackFocusedElement">
    /// PST-11: a SECOND element for the captured-element rescue, consulted only when
    /// <paramref name="focusedElement"/> is not alive-and-editable and only on the already-blocked
    /// path. The CALLER owns the window match — <c>RedoFocusRetention.ElementFor</c> returns null
    /// unless the retained element belongs to the target this paste actually resolved to — because
    /// nothing downstream can tell which window an opaque element came from. Null on every path
    /// but the redo, which leaves the rescue exactly as it was.
    /// </param>
    private async Task<PasteResult> PasteWithOptionalEnterAsync(string textToPaste, bool willSendEnter, IntPtr targetWindow, object? focusedElement = null, object? fallbackFocusedElement = null)
    {
        // REL-16 diagnostic: one attempt-scoped probe collector spans the paste AND the
        // delayed Enter window, so the pre-enter endpoint observes the 200 ms interval and
        // lands in the same verdict. Emission is idempotent and off-path; the outer finally
        // backstops a throw from the paste itself.
        var probeAttempt = new ClipboardProbeAttempt();
        using (_hotkeyService.BeginSuppressPromptActions())
        {
            try
            {
                var result = await _clipboard.PasteAtCursorAsync(
                    textToPaste, targetWindow, focusedElement, _targetWindowCapturedAtUtc,
                    global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot),
                    probeSink: probeAttempt,
                    fallbackFocusedElement: fallbackFocusedElement);

                // Only press Enter if the paste actually landed AND the target is still foreground.
                // Otherwise a declined/failed paste (e.g. the post-UIA foreground bail in
                // PasteAtCursorAsync) would still inject a stray Enter into whatever app now has
                // focus — submitting a form or sending a message in the wrong window. Also recheck
                // foreground after the post-paste delay so a drift during that gap can't redirect
                // Enter. PostPasteEnterSequence pins the ordering: delay → capture the pre-enter
                // probe → foreground recheck (stays the LAST gate) → Enter → emit.
                var sendEnter = result.Succeeded && willSendEnter;
                if (sendEnter)
                    probeAttempt.ExpectPreEnter();
                var enterOutcome = await PostPasteEnterSequence.RunAsync(
                    sendEnter,
                    () => Task.Delay(200),
                    () => probeAttempt.Add(ClipboardService.CaptureClipboardProbe(ClipboardProbePhase.PreEnter)),
                    () => targetWindow == IntPtr.Zero || NativeInterop.GetForegroundWindow() == targetWindow,
                    () => _clipboard.PressEnterAsync(),
                    () => ClipboardService.EmitClipboardProbes(probeAttempt));
                if (enterOutcome == PostPasteEnterOutcome.SkippedForeground)
                {
                    Logger.Warning(
                        "Skipped Enter-after-paste: target 0x{Target:X} lost foreground during the post-paste delay",
                        targetWindow);
                }
                return result;
            }
            finally
            {
                // Idempotent backstop — a paste throw must still emit the collected endpoints.
                ClipboardService.EmitClipboardProbes(probeAttempt);
            }
        }
    }

    // CapturedFocusOwner moved to Helpers/CapturedFocusSlot.cs with the slot extraction.

    /// <summary>
    /// Image paste/copy outcome. Benign clipboard-only fallback (target not fresh —
    /// the DESIGNED initial-recording path) is a SUCCESS because the generated image is
    /// still available to the user. <see cref="Declined"/> is different: a paste was
    /// ATTEMPTED at a fresh target and refused (e.g. no editable focus), so the image is
    /// on the clipboard but "something happened" — an attention-timed error redo.
    /// </summary>
    // Public (like StopTapAction) so the pure ResolveImagePasteRedoPresentation rows can
    // pass it as a [Theory] parameter — an internal type can't parameterize a public test.
    public enum ImagePasteResult { Failed, CopiedToClipboard, Pasted, Declined }

    /// <summary>
    /// The result of an image paste attempt plus, for <see cref="ImagePasteResult.Declined"/>,
    /// the actionable message to surface on the redo pill. The message is carried here
    /// (captured before the fail-soft metrics/history writes and consumed after) so it
    /// survives those writes — the image counterpart of the text
    /// <c>clipboardFallbackMessage</c> guarantee.
    /// </summary>
    public readonly record struct ImagePasteOutcome(ImagePasteResult Result, string? DeclineMessage = null);

    internal enum ImagePasteFreshnessDecision { Fresh, NoTarget, NoCapture, Stale, ForegroundMismatch }

    /// <summary>
    /// PILL-3: the pill status + tone for a text paste outcome that ends in a redo pill.
    /// A landed paste is a Success (<paramref name="successStatus"/>); a failed paste
    /// surfaces the clipboard-fallback message with the tone CAPTURED AT the paste/copy
    /// site (amber Warning for a decline, red Error for a clipboard failure) — this helper
    /// never re-derives it. Pure — shared by the main pipeline and redo (redo passes
    /// "Text copied" for the no-target clipboard case — nothing was pasted, so it does
    /// not get the "Done" of a landed paste).
    /// </summary>
    internal static (string StatusText, MiniRecorderTone Tone) ResolvePostPasteRedoPresentation(
        bool pasteLanded, string? clipboardFallbackMessage, MiniRecorderTone fallbackTone,
        string successStatus = "Done")
        => pasteLanded
            ? (successStatus, MiniRecorderTone.Success)
            : (clipboardFallbackMessage ?? Helpers.PasteResultPresentation.GenericDeclineMessage,
               fallbackTone);

    /// <summary>
    /// PILL-3: the pill status + tone for an image paste outcome that ends in a redo pill.
    /// Pasted and the benign clipboard-only fallback are Successes; a
    /// <see cref="ImagePasteResult.Declined"/> paste surfaces its actionable message as an
    /// amber Warning (the image IS on the clipboard); <see cref="ImagePasteResult.Failed"/>
    /// (clipboard set failed — the image is NOT available) is a red Error. All four values
    /// have explicit arms on purpose — Failed must never ride a default (Codex final check);
    /// the discard arm only covers out-of-range enum values. Pure — shared by initial
    /// generation and redo/regenerate.
    /// </summary>
    internal static (string StatusText, MiniRecorderTone Tone) ResolveImagePasteRedoPresentation(ImagePasteOutcome outcome)
        => outcome.Result switch
        {
            ImagePasteResult.Pasted => ("Done", MiniRecorderTone.Success),
            ImagePasteResult.CopiedToClipboard => ("Image on clipboard", MiniRecorderTone.Success),
            ImagePasteResult.Declined => (
                outcome.DeclineMessage ?? "Image ready — paste from clipboard with Ctrl+V",
                MiniRecorderTone.Warning),
            ImagePasteResult.Failed => ("Couldn't copy the image to the clipboard", MiniRecorderTone.Error),
            _ => ("Image on clipboard", MiniRecorderTone.Success),
        };

    /// <summary>
    /// Paste a generated image only when the captured target is still fresh and foreground;
    /// otherwise copy it to the clipboard. Image clipboard contents are intentionally left in place.
    /// <para>IMG-BG: runs inside the background job body, so every target datum arrives AS A
    /// PARAMETER captured at dispatch — never read from the live VM fields, which the next
    /// recording overwrites (<c>_targetWindowHandle</c>/<c>_targetWindowCapturedAtUtc</c>/
    /// <c>_pasteTargetSnapshot</c>, wrong-window paste). A pipeline that is no longer Idle at
    /// paste time degrades to clipboard-only: the user is mid-recording, and stealing
    /// foreground for an auto-paste (plus fighting the recording pill for
    /// <c>IsMiniRecorderVisible</c>) would be worse than the designed clipboard posture.</para>
    /// </summary>
    private async Task<ImagePasteOutcome> PasteImageWithFocusRestoreAsync(
        byte[] imageBytes, IntPtr targetWindow, object? focusedElement,
        DateTime targetCapturedAtUtc, PasteTargetSnapshot? targetSnapshot)
    {
        if (targetWindow == IntPtr.Zero || !IsImagePasteTargetFresh(targetWindow, targetCapturedAtUtc))
            return new ImagePasteOutcome(await CopyImageToClipboardOnlyAsync(imageBytes));

        if (RecordingState != RecordingState.Idle)
        {
            Logger.Information("Image auto-paste skipped: recording pipeline is {State}", RecordingState);
            return new ImagePasteOutcome(await CopyImageToClipboardOnlyAsync(imageBytes));
        }

        Logger.Information("Image auto-paste: target fresh, attempting paste");
        IsMiniRecorderVisible = false;
        await Task.Delay(100);

        // Re-check after the delay AND hand the paste a mid-sequence fence: a recording can
        // start during the ~1.5 s of foreground/UIA gates, and its target may be the SAME
        // window — foreground checks can't see that (Codex diff review round 1).
        if (RecordingState != RecordingState.Idle)
        {
            Logger.Information("Image auto-paste skipped: recording started during the pre-paste delay");
            return new ImagePasteOutcome(await CopyImageToClipboardOnlyAsync(imageBytes));
        }

        PasteResult imageResult;
        using (_hotkeyService.BeginSuppressPromptActions())
        {
            // The gate runs inside ClipboardService's ConfigureAwait(false) continuations —
            // off the UI thread — so it reads the VOLATILE busy mirror, never RecordingState
            // (non-volatile field, UI-thread writer).
            imageResult = await _clipboard.PasteImageAtCursorAsync(imageBytes, targetWindow, focusedElement,
                targetSnapshot, proceedGate: () => !IsRecordingPipelineBusy);
        }

        if (imageResult.Succeeded)
            return new ImagePasteOutcome(ImagePasteResult.Pasted);

        if (imageResult.Outcome != PasteAttemptOutcome.ClipboardSetFailed)
        {
            // Every post-clipboard failure (NoEditableFocused, target gone/elevated,
            // foreground loss, SendInput) leaves the image ALREADY on the clipboard —
            // don't set it a second time. Carry the actionable message on the outcome
            // (PILL-3: the redo pill owns it as an error-styled attention surface — no
            // immediate ShowMiniRecorderError that would flash then be replaced).
            Logger.Warning("Image paste declined ({Outcome}); generated image remains on the clipboard for manual retry",
                imageResult.Outcome);
            return new ImagePasteOutcome(ImagePasteResult.Declined, imageResult.UserMessage);
        }

        Logger.Warning("Image clipboard set failed; retrying clipboard-only copy");
        var fallbackResult = await CopyImageToClipboardOnlyAsync(imageBytes);
        if (fallbackResult == ImagePasteResult.Failed)
            IsMiniRecorderVisible = true;
        return new ImagePasteOutcome(fallbackResult);
    }

    private async Task<ImagePasteResult> CopyImageToClipboardOnlyAsync(byte[] imageBytes)
    {
        return await _clipboard.SetClipboardImageAsync(imageBytes)
            ? ImagePasteResult.CopiedToClipboard
            : ImagePasteResult.Failed;
    }

    private static bool IsImagePasteTargetFresh(IntPtr targetWindow, DateTime targetCapturedAtUtc)
    {
        var nowUtc = DateTime.UtcNow;
        var foreground = targetWindow == IntPtr.Zero ? IntPtr.Zero : NativeInterop.GetForegroundWindow();
        var decision = EvaluateImagePasteFreshness(
            targetCapturedAtUtc,
            nowUtc,
            targetWindow,
            foreground,
            ImagePasteFreshnessWindow);

        switch (decision)
        {
            case ImagePasteFreshnessDecision.Fresh:
                return true;
            case ImagePasteFreshnessDecision.Stale:
                Logger.Information(
                    "Image auto-paste skipped: target capture is stale ({Elapsed:F1}s, limit {Limit:F1}s)",
                    (nowUtc - targetCapturedAtUtc).TotalSeconds, ImagePasteFreshnessWindow.TotalSeconds);
                return false;
            case ImagePasteFreshnessDecision.ForegroundMismatch:
                Logger.Information(
                    "Image auto-paste skipped: foreground moved from target 0x{Target:X} to 0x{Foreground:X}",
                    targetWindow, foreground);
                return false;
            default:
                return false;
        }
    }

    internal static ImagePasteFreshnessDecision EvaluateImagePasteFreshness(
        DateTime capturedAtUtc,
        DateTime nowUtc,
        IntPtr targetWindow,
        IntPtr foregroundWindow,
        TimeSpan freshnessWindow)
    {
        if (targetWindow == IntPtr.Zero)
            return ImagePasteFreshnessDecision.NoTarget;
        if (capturedAtUtc == DateTime.MinValue)
            return ImagePasteFreshnessDecision.NoCapture;
        if (nowUtc - capturedAtUtc > freshnessWindow)
            return ImagePasteFreshnessDecision.Stale;
        if (foregroundWindow != targetWindow)
            return ImagePasteFreshnessDecision.ForegroundMismatch;

        return ImagePasteFreshnessDecision.Fresh;
    }

    /// <summary>
    /// Handle the image-generation branch of StopAndTranscribeAsync (IMG-BG, 2026-07-16).
    /// Shows the AskImageSize picker in-pipeline (the user's own interactive moment), then
    /// RESERVES the single background job slot, captures the dispatch snapshot (focus handoff,
    /// reference lease, target triple by value), and starts the detached job — the pipeline
    /// returns to Idle immediately, so the hotkey records again while the image generates.
    /// Returns true when handled (dispatched, or picker-cancelled); false ONLY on a REFUSAL
    /// (a job already running / exclusive maintenance / lost the reservation race) — the
    /// caller then treats the words as a PLAIN dictation. Generation failures no longer fall
    /// through to a text paste: they surface minutes later on the job's own error pill (a
    /// deliberate behavior change — a failure-time paste would land wherever focus is THEN).
    /// </summary>
    private async Task<bool> TryRunImageGenerationAsync(
        string text,
        string rawText,
        CustomPrompt prompt,
        string transcriptionModelName,
        string language,
        bool wasPushToTalk, // resolved by the caller — a RETRY run must not read live hotkey state (REL-12)
        CancellationToken ct)
    {
        // Refusal BEFORE the options dialog and before ANY resource transfer
        // (reservation-before-resources, Codex plan round 2). The exclusivity re-check
        // matches the redo path's F19 pattern — the job writes DB/Images/References.
        if (_imageJob.IsRunning || App.IsExclusiveMaintenanceActive())
        {
            RequestImageJobRefusalNotice();
            return false;
        }

        // ENH-6: reference images only enter the voice flow via the options dialog
        // below (v1 has no other voice-side picker surface).
        IReadOnlyList<ReferenceImageSelection>? references = null;
        // IMG-3: the version count enters the same way — no dialog (AskImageSize off)
        // means 1 by construction, count is a per-run spend decision (owner decision 4).
        var count = 1;

        // Ask for image options if the prompt has AskImageSize enabled. Reuses the rich redo
        // dialog (editable transcribed text + provider/model + image options) so the user can
        // review/edit the prompt text and pick the model up front. Clone the prompt to avoid
        // mutating the shared cached instance.
        if (prompt.AskImageSize && ShowImageOptionsPickerRequested != null)
        {
            RecordingState = RecordingState.Enhancing;
            StatusText = "Waiting for image options...";
            var provider = ResolvePromptProvider(prompt);
            var model = _enhancement.ResolveModelForPrompt(prompt);

            // Pre-seed the dialog from a synthetic context describing this first generation.
            var pickerContext = new RedoContext(
                RawText: text,
                EnhancedText: null,
                Prompt: prompt,
                WasImageGeneration: true,
                TargetWindow: _targetWindowHandle,
                WasPushToTalk: wasPushToTalk,
                TranscriptionModelName: transcriptionModelName,
                TranscriptionLanguage: language,
                PreviousProvider: provider,
                PreviousModel: model,
                PreviousImageAspect: prompt.ImageAspect,
                PreviousImageSizeTier: prompt.ImageSizeTier,
                PreviousImageQuality: prompt.ImageQuality);

            // Same picker gate as RequestModelPickerWithContext: if a redo picker is
            // already open, showing this dialog would throw (one ContentDialog per
            // XamlRoot) — fall into the existing cancel path instead.
            EnhancementDialogSelection? picked;
            if (Interlocked.CompareExchange(ref _enhancementPickerActive, 1, 0) != 0)
            {
                Logger.Information("Image options picker unavailable — another enhancement dialog is open; cancelling image generation");
                picked = null;
            }
            else
            {
                try { picked = await ShowImageOptionsPickerRequested.Invoke(pickerContext, ct); }
                finally { Interlocked.Exchange(ref _enhancementPickerActive, 0); }
            }
            // A dialog resolving with a selection just as Stop fires must still route through
            // the cancel path — the user asked to stop, not to generate (F20).
            if (ct.IsCancellationRequested)
                picked = null;
            if (picked is null)
            {
                Logger.Information("Image generation cancelled by user (options picker)");
                MarkTerminalPresentation(); // IMG-BG epoch fence — terminal presentation
                IsMiniRecorderVisible = false;
                RecordingState = RecordingState.Idle;
                StatusText = "Cancelled";
                ResetHotkeyState?.Invoke();
                // Restore focus to the previously active window so the global hotkey
                // isn't consumed by the main window's Alt-menu handling
                if (_targetWindowHandle != IntPtr.Zero)
                    NativeInterop.SetForegroundWindow(_targetWindowHandle);
                return true;
            }

            // The edited prompt text drives generation; the cloned prompt carries the chosen
            // model/provider + image options so resolution, history, and redo all agree.
            if (!string.IsNullOrWhiteSpace(picked.EditedText))
                text = picked.EditedText;
            prompt = prompt.Clone();
            prompt.ImageAspect = picked.Aspect;
            prompt.ImageSizeTier = picked.SizeTier;
            prompt.ImageQuality = picked.Quality;
            if (!string.IsNullOrWhiteSpace(picked.Model))
                prompt.ModelOverride = picked.Model;
            if (picked.Provider != null)
                prompt.ProviderOverride = picked.Provider.Value.ToString();
            references = picked.References;
            count = Helpers.ImageBatchPolicy.ClampCount(picked.Count);
        }

        // Reserve the slot — the options dialog may have sat open while another entry point
        // (tray / History) started a job, so this can still lose after the entry check above.
        var reservation = _imageJob.TryReserve();
        if (reservation == null)
        {
            RequestImageJobRefusalNotice();
            return false;
        }

        // Dispatch snapshot — BY VALUE. The live VM fields (_targetWindowHandle /
        // _targetWindowCapturedAtUtc / _pasteTargetSnapshot) belong to the NEXT recording
        // the moment the pipeline is Idle again; the focus handoff detaches the slot so
        // that recording's BeginForRecording finds it empty (no steal, no double-release).
        // The in-flight lease keeps the dialog-picked source references alive for the
        // generation read + a possible failed-attempt re-read (ENH-6c/6e).
        Helpers.UiaFocusBridge.IUIAutomationElement? focusedElement = null;
        IDisposable? referenceLease = null;
        try
        {
            // Cancellation FIRST, before resolve/gate (Codex diff review): the token can already be
            // cancelled when we get here — the options dialog may have sat open while the user hit
            // Stop — and a pre-cancelled dispatch must take the normal cancellation teardown, not be
            // reported as a model refusal that also clears a persisted setting. The OCE is handled by
            // the pipeline's outer catch; the catch below releases the reservation.
            ct.ThrowIfCancellationRequested();

            // Resolve the ONE (provider, model) route and GATE it — still before ANY resource
            // transfer, so a refusal costs nothing to unwind: no focus handoff, no reference lease,
            // no provider call, no charge. Resolving here also means the request below consumes this
            // value instead of re-resolving (one source of truth).
            var promptSnapshot = prompt.Clone();
            var resolvedProvider = ResolvePromptProvider(promptSnapshot);
            var imageModel = _enhancement.ResolveEffectiveImageModelWithSource(promptSnapshot, resolvedProvider);
            if (!GateImageModelOrRefuse(imageModel, resolvedProvider, reservation))
                return true; // handled: an image prompt must NOT fall through and paste as plain dictation

            if (count == 1)
            {
                focusedElement = await _focusSlot.TakeForHandoffAsync();
            }
            else
            {
                // IMG-3: a batch never pastes, so no focus element rides the job — the
                // recording's capture is released owner-scoped (handles a still-pending
                // capture without materializing it; a newer owner's capture is untouched).
                ReleaseCapturedFocusedElement(CapturedFocusOwner.Recording);
            }
            // A Stop click during the handoff await cancelled the pipeline — a paid cloud
            // request must not start after the user said stop (Codex diff review round 2).
            // The catch below releases the reservation + transferred resources, and the
            // outer OCE handler presents the normal "Cancelled" teardown.
            ct.ThrowIfCancellationRequested();
            referenceLease = _referencePersistence.BeginInFlightLiveReferences(PathsOf(references));
            // promptSnapshot / resolvedProvider / imageModel were resolved and gated ABOVE, before
            // any resource transfer. The snapshot matters for the same reason as before: the direct
            // path would otherwise carry the LIVE cached instance a settings edit can mutate
            // mid-generation. Generation and metadata both use this one route.
            var request = new ImageGenJobRequest(
                Text: text,
                Prompt: promptSnapshot,
                ResolvedProvider: resolvedProvider,
                ResolvedModel: imageModel.Model,
                References: references,
                TranscriptionModelName: transcriptionModelName,
                Language: language,
                WasPushToTalk: wasPushToTalk,
                IsNewGeneration: false,
                Count: count,
                TargetWindow: _targetWindowHandle,
                TargetCapturedAtUtc: _targetWindowCapturedAtUtc,
                TargetSnapshot: global::System.Threading.Volatile.Read(ref _pasteTargetSnapshot),
                FocusedElement: focusedElement,
                ReferenceLease: referenceLease);

            // Pipeline exit state BEFORE Start (Codex diff review round 2): a PRE-CANCELLED
            // job token (tray Cancel during preparation) makes the body's synchronous prefix
            // present its terminal cancel INSIDE Start — writes after Start would clobber it.
            // Same-turn ordering: nothing can interleave between these statements on the UI
            // thread, so the Idle write can't admit a recording before Start runs.
            Logger.Information("Image generation dispatched to background job ({Length} chars)", text.Length);
            RecordingState = RecordingState.Idle;
            StatusText = "Generating image...";
            IsMiniRecorderVisible = false;
            ResetHotkeyState?.Invoke();

            reservation.Start(jobCt => RunImageGenerationJobAsync(request, jobCt));
        }
        catch
        {
            // Preparation failed — the reservation AND every already-transferred resource
            // release here (plan rule: reserve → prepare → start; a failed prepare leaves
            // nothing stranded). After a successful Start the job body owns them instead.
            reservation.Dispose();
            referenceLease?.Dispose();
            if (focusedElement != null)
                Helpers.UiaFocusBridge.EnqueueRelease(focusedElement);
            throw;
        }

        // The job owns the rest: the GeneratingImage pill takes over via the job-state
        // snapshot (that rehydration kind deliberately ignores IsMiniRecorderVisible), and a
        // dispatch is NOT a terminal presentation — an unapplied pending completion from an
        // earlier job must still present when the pipeline idles.
        return true;
    }

    /// <summary>
    /// ENH-6f: pair the run's materialized reference bytes with the caller's original
    /// selections for <c>PersistWithRowAsync</c> — index pairing made EXPLICIT (the
    /// read path guarantees both lists share selection order and length).
    /// </summary>
    private static IReadOnlyList<(Services.AIEnhancement.ReferenceImage Bytes, ReferenceImageSelection Original)>? PairUsedReferences(
        IReadOnlyList<Services.AIEnhancement.ReferenceImage> usedReferences,
        IReadOnlyList<ReferenceImageSelection>? selections)
    {
        if (usedReferences.Count == 0 || selections == null)
            return null;
        var pairs = new (Services.AIEnhancement.ReferenceImage, ReferenceImageSelection)[usedReferences.Count];
        for (var i = 0; i < usedReferences.Count; i++)
            pairs[i] = (usedReferences[i], selections[i]);
        return pairs;
    }

    /// <summary>
    /// ENH-6f re-arm sanitation, per item: an index-carrying multi-read failure drops
    /// exactly the dead items (they would fail identically on every redo); a BASE
    /// <see cref="Services.AIEnhancement.ReferenceImageUnavailableException"/>
    /// (count/aggregate cap — whole-selection verdicts) drops everything; any other
    /// exception (provider/network) keeps the full list. Internal static so tests can
    /// pin the mapping without constructing the ViewModel.
    /// </summary>
    internal static IReadOnlyList<ReferenceImageSelection>? SanitizeReferencesAfterFailure(
        IReadOnlyList<ReferenceImageSelection>? references, Exception ex)
    {
        if (references == null || references.Count == 0)
            return references;
        if (ex is Services.AIEnhancement.ReferenceImagesUnavailableException multi)
        {
            var failed = new HashSet<int>(multi.FailedIndices);
            var kept = references.Where((_, i) => !failed.Contains(i)).ToArray();
            return kept.Length == 0 ? null : kept;
        }
        return ex is Services.AIEnhancement.ReferenceImageUnavailableException ? null : references;
    }

    private async Task RecordLifetimeMetricsAsync(string metricsText, DateTime timestampUtc, CancellationToken ct)
    {
        try
        {
            await _lifetimeMetrics.RecordTranscriptionAsync(metricsText, timestampUtc, ct);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to update lifetime metrics");
        }
    }

    /// <summary>
    /// Truncate an exception message to fit the MiniRecorder pill. The 55-char
    /// default derives from the 500-DIP pill's redo layout — see the budget math
    /// at MiniRecorderWindow.PillWidthDips. Returns <paramref name="fallback"/>
    /// if the message is empty.
    /// <para>The ellipsis counts against the budget (Codex diff review round 2,
    /// 2026-07-25): this used to return <c>maxLength</c> characters PLUS "…", i.e.
    /// 56 for the documented 55-char cap, so the cap the code enforced was never
    /// the cap the comments and tests claimed.</para>
    /// </summary>
    private static string TruncateForMiniRecorder(string? message, string fallback, int maxLength = 55)
    {
        if (string.IsNullOrWhiteSpace(message)) return fallback;
        return message.Length <= maxLength ? message : message[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// SELECTION of provider-path text that is safe to show on a pill — no formatting, no truncation.
    /// <para>A <b>TYPED</b> <see cref="Helpers.ProviderApiException"/> 401 carries the provider's own
    /// credential prose, and providers echo key fragments into it (OpenAI's "Incorrect API key
    /// provided: sk-…" shape) — so it is replaced with app copy naming where to fix the key. The
    /// transcription mapper has said exactly this since PR #244; the image, redo and enhancement paths
    /// simply never got the same treatment and were surfacing those bodies verbatim.</para>
    /// <para>A <b>PLAIN</b> 401 gets NEITHER treatment. It is not a key verdict —
    /// <c>ImageGenerationClient</c>'s image-URL download calls <c>EnsureSuccessStatusCode()</c>, so an
    /// expired/authorised URL raises a bare 401 while the user's key is perfectly fine, and claiming
    /// "Invalid API key" there is the misdiagnosis PR #244 removed from the 403 arm. But its message is
    /// not trustworthy either: <c>EnsureSuccessStatusCode()</c> embeds the <b>server-supplied</b>
    /// HTTP reason phrase, so it is response-controlled text, not app copy (Codex diff review,
    /// 2026-07-26 — an earlier revision of this comment wrongly called it safe). It therefore gets
    /// neutral app copy that diagnoses nothing.</para>
    /// <para><b>No status-bearing exception message reaches a pill through here at all</b> — not just
    /// 401s. Image downloads can return any status, and a framework <c>EnsureSuccessStatusCode()</c>
    /// message embeds the reason phrase. So the rule is: structured provider text yes (that is ENH-1's
    /// accepted surface), exception MESSAGES no. <b>SEC-2 (2026-08-18) narrowed one clause of this:</b>
    /// <c>HttpResponseExtensions</c> no longer raises a PLAIN exception when the body read fails — it
    /// throws the typed one — and the typed message no longer embeds the phrase. The RULE is unchanged
    /// and still load-bearing for the framework-message sites (SEC-5).</para>
    /// <para><b>SEC-2 SHIPPED 2026-08-18</b> and this paragraph used to describe it as pending. The
    /// phrase is off <c>ProviderApiException.Message</c> and rides a <c>{ReasonPhrase}</c> property that
    /// the redaction allowlist DOES cover, so it reaches the local log and not Sentry breadcrumbs.
    /// Remaining: the FRAMEWORK-built <c>EnsureSuccessStatusCode()</c> message, which we do not compose
    /// and cannot sanitise — tracked as SEC-5.</para>
    /// <para><b>Returns UNTRUNCATED text on purpose.</b> Callers that render directly truncate;
    /// <see cref="ComposeFallbackFailureStatus"/> callers must NOT, because that method trims
    /// whitespace BEFORE budgeting and pre-truncating reintroduces the space-prefixed collapse its own
    /// comment documents (a 50-space + "Rate limited" reason would become "Rate…").</para>
    /// <para>NOTE the transcription surface never reaches the plain-401 branch in production:
    /// <see cref="DescribeTranscriptionFailure"/>'s own <c>Unauthorized</c> arm precedes everything
    /// (transcription has no image-URL download, and that path's 401s are auth). <b>Since REL-22 that
    /// arm is no longer unconditional</b> — a 401 the provider labelled as credit exhaustion in a
    /// structured field takes the out-of-credits arm ahead of it, in BOTH mappers, so the two cannot
    /// disagree about the same exception.</para>
    /// <para><b>PRM-6 two-line budget rides the return value.</b> Only branch 2 — genuine structured
    /// provider prose, ENH-1's accepted surface — carries <see cref="ProviderMessageMaxCodeUnits"/>;
    /// every other branch (app copy, internal/SDK messages) stays at the 55-unit app cap. Text and
    /// budget travel as ONE struct so no downstream site can pair a text with the wrong budget — the
    /// abandoned static-plan's round 4 died on exactly that (a bare string loses provenance, and the
    /// composer then widened internal exception text to ~78 composed units).</para>
    /// </summary>
    internal static ProviderPillText ProviderPillSafeText(
        Exception ex, string unauthorizedMessage, string? quotaMessage = null)
    {
        // 0. REL-22: a 401 the provider labelled as credit exhaustion in a STRUCTURED field. Must
        //    precede branch 1, which would otherwise call a valid key invalid (ElevenLabs answers an
        //    empty balance with 401). App-authored copy only — the classifier hands over a bool, so
        //    nothing from the body crosses the boundary and SEC-1's guarantee is unchanged. The
        //    ordering here is the one load-bearing sequence in this method and is pinned by test.
        if (ex is Helpers.ProviderApiException
            { StatusCode: global::System.Net.HttpStatusCode.Unauthorized, IsOutOfCredits: true })
            return new ProviderPillText(quotaMessage ?? OutOfCreditsMessage, DefaultPillMaxCodeUnits);

        // 1. Any other typed provider 401 — credential prose. Replaced with the surface's key copy.
        if (ex is Helpers.ProviderApiException
            { StatusCode: global::System.Net.HttpStatusCode.Unauthorized })
            return new ProviderPillText(unauthorizedMessage, DefaultPillMaxCodeUnits);

        // 2. The provider's own STRUCTURED error text (from the response envelope) is the accepted
        //    surface — ENH-1's recorded declination: do not sniff or normalise it. The ONE branch
        //    with the two-line budget.
        if (ex is Helpers.ProviderApiException pex && !string.IsNullOrWhiteSpace(pex.UserMessage))
            return new ProviderPillText(pex.UserMessage!, ProviderMessageMaxCodeUnits);

        // 3. ANY other status-bearing failure gets app-authored copy, never `ex.Message`. The
        //    FRAMEWORK's `EnsureSuccessStatusCode()` message embeds the SERVER-SUPPLIED HTTP
        //    ReasonPhrase, so a custom provider could put arbitrary prose on the pill through it
        //    (Codex diff review round 2, 2026-07-26; round 1 closed only the 401 case). SEC-2
        //    (2026-08-18) removed the phrase from ProviderApiException's own message, so that half of
        //    the original claim no longer holds — but this branch still refuses `ex.Message` for the
        //    framework sites (SEC-5) and because app copy is better copy. Split by transience so
        //    "try again" is promised only where retrying can help, as DescribeTranscriptionFailure does.
        if (ex is HttpRequestException { StatusCode: { } status })
            return new ProviderPillText(
                Services.Http.RetryingHandler.IsTransientStatus(status)
                    ? "Provider error — try again"
                    : "Provider rejected the request",
                DefaultPillMaxCodeUnits);

        // 4. Statusless failures keep today's message. NOT because "no status means no response" —
        //    that would be false, e.g. GeminiImageClient derives a statusless throw from a SUCCESSFUL
        //    response's finish reason (Codex diff review round 3, 2026-07-26). It is simply that these
        //    messages are app- or SDK-authored rather than a server-supplied status line, so this
        //    branch preserves existing behaviour instead of neutralising it — at the app cap.
        return new ProviderPillText(
            Helpers.ProviderApiException.UserFacingMessage(ex), DefaultPillMaxCodeUnits);
    }

    /// <summary>
    /// Pill text plus the budget it was selected under, as ONE value. The budget is decided at
    /// selection time (see <see cref="ProviderPillSafeText"/>) and consumed verbatim at formatting —
    /// never reconstructed downstream. The no-value state is <c>null</c>, never <c>default</c>
    /// (a defaulted struct would carry MaxCodeUnits = 0 into a truncator).
    /// </summary>
    internal readonly record struct ProviderPillText(string Text, int MaxCodeUnits);

    /// <summary>
    /// REL-22 out-of-credits copy — GENERIC across every surface (owner decision, 2026-08-11), unlike
    /// the 401 copy which names the screen holding the key. Deliberate: under BYOK, credits are bought
    /// at the provider's own site, so naming any in-app screen would misdirect.
    /// <para>44 UTF-16 code units, inside the 55-unit app budget. <see cref="OutOfCreditsMessageShort"/>
    /// exists for the two COMPOSED surfaces for the same measured reason the short 401 copy does:
    /// <see cref="ComposeBatchImageFailureStatus"/> prepends "{k} of {n} generated — " (~19 units) and
    /// the enhancement fallback appends " — pasted transcription" (23), both truncating at the same
    /// 55, so the long form would lose its actionable half.</para>
    /// </summary>
    internal const string OutOfCreditsMessage = "Out of credits — check your provider account";
    internal const string OutOfCreditsMessageShort = "Out of credits";

    /// <summary>The app-copy cap — one pill line at the default text scale (house style ≤55).</summary>
    private const int DefaultPillMaxCodeUnits = 55;

    /// <summary>
    /// PRM-6: the budget for STRUCTURED provider prose — roughly two pill lines at the default text
    /// scale. UTF-16 CODE UNITS, deliberately not "characters": the slicers use string.Length + range
    /// indexing, so a cut can split a surrogate pair (pre-existing, recorded as a backlog candidate;
    /// this budget neither causes nor cures it). MaxLines=2 + trimming is the real rendering bound —
    /// this number only decides how much text is OFFERED to the layout.
    /// </summary>
    internal const int ProviderMessageMaxCodeUnits = 110;

    // The four pill surfaces. Each owns its own 401 copy (which screen holds THAT key) and its own
    // formatting, because only three of them render the string directly.

    /// <summary>Transcription failure text — truncated at its selection's budget, rendered directly.</summary>
    internal static string ProviderPillTextForTranscription(Exception ex)
        => Format(ProviderPillSafeText(ex, "Invalid API key — check the Models page"), "Transcription failed");

    /// <summary>Image-generation failure text — truncated at its selection's budget, rendered directly.</summary>
    internal static string ProviderPillTextForImage(Exception ex)
        => Format(ProviderPillSafeText(ex, "Invalid API key — check AI Enhancement"), "Image generation failed");

    /// <summary>Redo failure text — truncated at its selection's budget, rendered directly.</summary>
    internal static string ProviderPillTextForRedo(Exception ex)
        => Format(ProviderPillSafeText(ex, "Invalid API key — check AI Enhancement"), "Redo failed");

    /// <summary>The one direct-render formatting rule: the selection's own budget, nothing else's.</summary>
    private static string Format(ProviderPillText sel, string fallback)
        => TruncateForMiniRecorder(sel.Text, fallback, sel.MaxCodeUnits);

    /// <summary>
    /// Batch-aware image failure text (IMG-3). A SINGLE image keeps
    /// <see cref="ProviderPillTextForImage"/> verbatim (today's presentation — "0 of 1
    /// generated" is noise); a BATCH always prefixes "{k} of {n} generated — " onto the
    /// UNTRUNCATED safe selection, INCLUDING k=0 (owner, 2026-07-28 UAT: an all-failed
    /// batch that omits the count reads as though the run never happened), and
    /// truncates at that selection's OWN budget — the app prefix spends from the same
    /// budget, so structured provider prose keeps its two-line 110 and app copy its 55
    /// (the ComposeFallbackFailureStatus trim-then-budget rule).
    /// </summary>
    internal static string ComposeBatchImageFailureStatus(int completed, int total, Exception ex)
    {
        if (total <= 1)
            return ProviderPillTextForImage(ex);
        // The 401 copy is the SHORT form — the ProviderPillReasonForEnhancementFallback
        // precedent: this composer prepends "{k} of {n} generated — " (~19 units), so the
        // long "…— check AI Enhancement" form would have its actionable half truncated
        // away at the 55-unit app budget.
        var sel = ProviderPillSafeText(ex, "Invalid API key", OutOfCreditsMessageShort);
        return TruncateForMiniRecorder(
            $"{completed} of {total} generated — {sel.Text}", "Image generation failed", sel.MaxCodeUnits);
    }

    /// <summary>
    /// The metadata of the LAST contract-completed batch item, tracked by the persist
    /// delegate's closure. <see cref="HistoryId"/> is NULLABLE on purpose — an N=1
    /// history-off/fail-soft success has no row id yet still carries effective options
    /// and re-arm selections (today's rule; a sentinel id would be a lie).
    /// </summary>
    internal sealed record LastCommittedItem(
        int? HistoryId, string? Aspect, string? SizeTier, string? Quality,
        IReadOnlyList<ReferenceImageSelection>? RearmSelections);

    /// <summary>A composed terminal presentation, ready to materialize into
    /// <see cref="PendingImageCompletion"/> — status, tone, History assignment,
    /// LastTranscription carry, and the COMPLETE redo context.</summary>
    internal sealed record BatchCompletionComposition(
        string StatusText, MiniRecorderTone Tone,
        int? HistoryId, bool AssignHistoryIdUnconditionally,
        string? LastTranscriptionText, RedoContext RedoContext);

    /// <summary>
    /// The post-side-effect composer (IMG-3): runs AFTER the terminal action's side
    /// effects (paste / failed-marker write), so the marker row's id can feed the
    /// composition without circularity. Pure — the full output matrix is gate-test
    /// pinned. Rules: status/tone per stop reason (paste-derived at N=1;
    /// <c>ImageBatchPolicy</c> app copy otherwise; provider text only through
    /// <see cref="ComposeBatchImageFailureStatus"/>); HistoryId = marker id else last
    /// committed (Completed assigns UNCONDITIONALLY — success clears a stale id even
    /// when null, today's rule); LastTranscription only on Completed; the redo context
    /// always carries the batch count. References: PROVIDER failures take the marker
    /// step's re-arm VERBATIM including null (authoritative fail-closed sanitation, no
    /// fallback — see the inline rule); non-provider stops fall back
    /// last-committed → terminal-persist → request references. Previous* options come
    /// from the last committed item's Effective else the prompt's requested values
    /// (failure keeps the ask).
    /// </summary>
    internal static BatchCompletionComposition ComposeBatchCompletion(
        Helpers.ImageBatchRunner.BatchStop reason, int completed, int total,
        string text, CustomPrompt? prompt, AIProvider resolvedProvider, string resolvedModel,
        IReadOnlyList<ReferenceImageSelection>? requestReferences, string transcriptionModelName,
        // Rides alongside the model name for the same reason: the redo context this composes must
        // carry the ORIGINAL transcription's language, and a static method cannot read it from
        // instance state. Null for a text-first chain with no transcription behind it.
        string? transcriptionLanguage,
        IntPtr targetWindow, bool wasPushToTalk, bool isNewGeneration,
        LastCommittedItem? lastCommitted, Exception? failure,
        int? failedMarkerRowId, IReadOnlyList<ReferenceImageSelection>? failedMarkerRearm,
        IReadOnlyList<ReferenceImageSelection>? terminalPersistRearm,
        (string Status, MiniRecorderTone Tone)? pasteDerived)
    {
        var (status, tone) = reason switch
        {
            Helpers.ImageBatchRunner.BatchStop.Completed when total == 1 => pasteDerived
                ?? throw new ArgumentNullException(nameof(pasteDerived), "N=1 success composes from the paste outcome"),
            Helpers.ImageBatchRunner.BatchStop.Completed
                => (Helpers.ImageBatchPolicy.SuccessStatus(total)!, MiniRecorderTone.Success),
            Helpers.ImageBatchRunner.BatchStop.ProviderFailure
                => (ComposeBatchImageFailureStatus(completed, total,
                        failure ?? new InvalidOperationException("provider failure without exception")),
                    MiniRecorderTone.Error),
            Helpers.ImageBatchRunner.BatchStop.PersistFailure
                => (Helpers.ImageBatchPolicy.SaveFailedStatus(completed, total), MiniRecorderTone.Error),
            Helpers.ImageBatchRunner.BatchStop.RowAmbiguous
                => (Helpers.ImageBatchPolicy.AmbiguousSaveStatus(completed, total), MiniRecorderTone.Warning),
            Helpers.ImageBatchRunner.BatchStop.HistoryDisabled
                => (Helpers.ImageBatchPolicy.HistoryOffStatus(completed, total), MiniRecorderTone.Warning),
            Helpers.ImageBatchRunner.BatchStop.Cancelled
                => (Helpers.ImageBatchPolicy.CancelStatus(completed, total), MiniRecorderTone.Warning),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason,
                "ShutdownRefused never composes — the executor goes Silent"),
        };

        var isCompleted = reason == Helpers.ImageBatchRunner.BatchStop.Completed;
        var isProviderFailure = reason == Helpers.ImageBatchRunner.BatchStop.ProviderFailure;
        // Provider failures take the marker step's re-arm AS-IS — fully authoritative,
        // INCLUDING null (Codex diff r2+r3; today's single-image rule verbatim): null can
        // mean "every reference was dead/dropped by sanitation", and NO fallback is safe —
        // the raw request references are the dead originals, and even lastCommitted's
        // re-arm may carry a non-durable ORIGINAL path (ReferencePersistence keeps the
        // original selection when a private copy fails to land). Non-provider stops
        // (persist/history/cancel) had no reference failure — their references were just
        // used successfully, so the fallback chain stays.
        var references = isProviderFailure
            ? failedMarkerRearm
            : lastCommitted?.RearmSelections ?? terminalPersistRearm ?? requestReferences;

        return new BatchCompletionComposition(
            StatusText: status,
            Tone: tone,
            HistoryId: isProviderFailure
                ? failedMarkerRowId ?? lastCommitted?.HistoryId
                : lastCommitted?.HistoryId,
            AssignHistoryIdUnconditionally: isCompleted,
            LastTranscriptionText: isCompleted ? text : null,
            RedoContext: new RedoContext(
                RawText: text,
                EnhancedText: isCompleted ? Models.Entities.TranscriptionRecord.ImageGeneratedMarker : null,
                Prompt: prompt,
                WasImageGeneration: true,
                TargetWindow: targetWindow,
                WasPushToTalk: wasPushToTalk,
                TranscriptionModelName: transcriptionModelName,
                TranscriptionLanguage: transcriptionLanguage,
                PreviousProvider: resolvedProvider,
                PreviousModel: resolvedModel,
                PreviousImageAspect: lastCommitted != null ? lastCommitted.Aspect : prompt?.ImageAspect,
                PreviousImageSizeTier: lastCommitted != null ? lastCommitted.SizeTier : prompt?.ImageSizeTier,
                PreviousImageQuality: lastCommitted != null ? lastCommitted.Quality : prompt?.ImageQuality,
                PreviousImageCount: total,
                IsNewGeneration: isNewGeneration,
                References: references));
    }

    /// <summary>
    /// Enhancement-fallback reason — **untruncated text plus its budget**, because it is fed to
    /// <see cref="ComposeFallbackFailureStatus"/>, which trims then budgets it (the budget rides the
    /// same value so the composer applies 110 only to structured provider prose).
    /// <para>The 401 copy is deliberately the SHORT "Invalid API key": the composer appends
    /// " — pasted transcription" (23 units) and truncates the reason to fit the budget, so the long
    /// "…— check AI Enhancement" form would have its own actionable half cut off at 55
    /// ("Invalid API key — check AI Enha… — pasted transcription").</para>
    /// </summary>
    internal static ProviderPillText ProviderPillReasonForEnhancementFallback(Exception ex)
        => ProviderPillSafeText(ex, "Invalid API key", OutOfCreditsMessageShort);

    /// <summary>
    /// User-facing copy for a transcription-pipeline failure. Pure + internal so the routing
    /// is test-pinned: <see cref="Helpers.ProviderApiException"/> SUBCLASSES
    /// <see cref="HttpRequestException"/>, so a provider that answered with an error status but
    /// no extractable message fell through to the connectionless arm and told the user to check
    /// their network — wrong, and actively misleading once that arm gained "check your
    /// connection" (Codex diff review round 2, 2026-07-25). The rule: connection advice ONLY
    /// when nothing answered (no HTTP status at all).
    /// <para>Provider-authored text is truncated to the pill budget here; it arrives
    /// unbounded (a 161-char provider message has been observed) and the enhancement path's
    /// equivalent already truncated, so the two now agree.</para>
    /// </summary>
    internal static string DescribeTranscriptionFailure(Exception ex) => ex switch
    {
        // TRN-64: a local Whisper decode refused because the GPU failed (or never finished) its
        // self-test in this process. App-authored; nothing device-derived but the reason.
        Helpers.GpuSelfTestRefusedException refused => refused.UserMessage,
        // ENH-15: the stored key cannot form an HTTP header, so no request was ever built. This is
        // NOT the 401 case below — the provider never saw anything — and the distinction is what
        // the user needs: "regenerate your key" is wrong advice for a key that is merely mistyped.
        // UserMessage is app-authored, so nothing provider- or key-derived crosses the boundary.
        Helpers.InvalidApiKeyFormatException invalid => invalid.UserMessage,
        // REL-22: a 401 the PROVIDER labelled as credit exhaustion in a structured field is not a
        // credential failure. ElevenLabs answers an empty balance with 401, so the arm below sent a
        // user with a perfectly good key off to regenerate it. Must precede that arm; app-authored
        // copy only, so no part of the 401 body crosses the boundary and SEC-1 is untouched.
        Helpers.ProviderApiException
            { StatusCode: global::System.Net.HttpStatusCode.Unauthorized, IsOutOfCredits: true }
            => OutOfCreditsMessage,
        // Any OTHER 401 is credentials, and its provider text is the one we do NOT want to
        // surface (OpenAI echoes a masked key fragment into it) — fixed copy wins here.
        // Names WHERE to fix it (owner, 2026-07-25). Safe to be specific: this mapper serves the
        // TRANSCRIPTION path only, and transcription provider keys are entered on the Models page
        // (ModelsPage's provider cards) — enhancement keys, which live elsewhere, never reach here.
        HttpRequestException { StatusCode: global::System.Net.HttpStatusCode.Unauthorized }
            => "Invalid API key — check the Models page",
        // ENH-1: prefer the provider's own error text (e.g. Deepgram 408 SLOW_UPLOAD →
        // "Request upload timeout." rather than generic copy). Deliberately ahead of the 403
        // arm: a 403 is often a region block, model-permission, or policy denial whose own
        // sentence is far more useful than "Invalid API key", which would send the user off to
        // regenerate a perfectly good key (Codex diff review round 3, 2026-07-25). ENH-1's
        // "auth failures stay actionable" intent survives via the 403 fallback below.
        // Whitespace-aware, not just `not null`: an empty UserMessage used to match and put an
        // EMPTY string on the pill. Blank text is "no usable provider message" — fall through.
        // Routed through the shared surface helper so the 401 mask no longer depends on the arm
        // ORDER above alone (belt and braces — this arm is unreachable for a 401). Behaviour here is
        // unchanged: the guard and this arm's position are load-bearing for the message-less non-401
        // mappings below, so neither moved (Codex plan review round 3, 2026-07-26).
        Helpers.ProviderApiException pex when !string.IsNullOrWhiteSpace(pex.UserMessage)
            => ProviderPillTextForTranscription(pex),
        // A 403 with nothing to say stays NEUTRAL: 403 covers key-lacks-permission, region
        // blocks, model access, and policy denials, so naming the key would diagnose one cause
        // and send the user to regenerate a working key (Codex diff review round 4,
        // 2026-07-25). Still actionable, just not a false diagnosis — only 401 asserts the key.
        HttpRequestException { StatusCode: global::System.Net.HttpStatusCode.Forbidden }
            => "Access denied — check key or permissions",
        // The provider responded, just not usefully. "Try again" is promised ONLY for statuses
        // the repo classifies as transient — a 400/404/413/422 will fail identically on every
        // retry, so it gets rejection copy instead. Not a connectivity problem either way:
        // never advise checking the network here.
        // NOTE the predicate's home is the retry handler, but the transcription client
        // deliberately has NO retry handler (ENH-3: upload streams aren't replayable). We read
        // it purely as the repo's single definition of "a status that resolves on its own" —
        // this path never retries by itself, it only tells the user that retrying is worthwhile.
        HttpRequestException { StatusCode: { } status }
            => Services.Http.RetryingHandler.IsTransientStatus(status)
                ? "Provider error — try again"
                : "Provider rejected the request",
        HttpRequestException => "Network error — check your connection",
        TimeoutException => "Transcription timed out",
        // InvalidOperationException was "API error" until the 2026-07-25 copy review: jargon,
        // and a lie on the local-Whisper path (no API involved) — it collapses into the honest
        // generic, which loses nothing (the log carries the exception type).
        _ => "Transcription failed"
    };

    /// <summary>
    /// Returns a "Provider/Model" label for the active AI enhancement model, or null if none.
    /// </summary>
    private string? GetEnhancementModelLabel(CustomPrompt? prompt)
    {
        var model = _enhancement.ResolveModelForPrompt(prompt);
        if (string.IsNullOrWhiteSpace(model)) return null;
        return $"{ResolvePromptProvider(prompt)}/{model}";
    }

    /// <summary>
    /// Resolve the effective <see cref="AIProvider"/> for a prompt: per-prompt override if set,
    /// else the global text/image provider selection. Used by the picker dialog to gate the
    /// quality control (Gemini hides it).
    /// </summary>
    private AIProvider ResolvePromptProvider(CustomPrompt? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt?.ProviderOverride)
            && Enum.TryParse<AIProvider>(prompt.ProviderOverride, out var overrideProvider))
        {
            return overrideProvider;
        }
        return prompt?.IsImageGeneration == true
            ? _enhancement.SelectedImageProvider
            : _enhancement.SelectedProvider;
    }

    /// <summary>
    /// Save generated image bytes to %LOCALAPPDATA%/VoiceWink/Images/, named for what they
    /// ACTUALLY are (<paramref name="kind"/>, classified once at the service receipt boundary —
    /// providers return WebP and others through this same path).
    /// Returns the file path, or null if saving fails. IMG-3: delegates to the
    /// collision-proof <see cref="Helpers.GeneratedImageSaver"/> (full-GUID name +
    /// CreateNew, one retry) so a batch item can never silently overwrite an earlier
    /// one and the ambiguity probe's path is unique per item; EnsureImages stays inside
    /// THIS fail-soft boundary, as always.
    /// </summary>
    private static async Task<string?> SaveGeneratedImageAsync(byte[] imageBytes, Helpers.ImageBytesKind kind)
    {
        try
        {
            var imagesDir = AppPaths.EnsureImages();
            return await Helpers.GeneratedImageSaver.SaveAsync(
                imageBytes, kind, imagesDir, Guid.NewGuid, () => DateTime.Now);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to save generated image to disk");
            return null;
        }
    }

    public async Task SyncLastTranscriptionWithHistoryAsync()
    {
        var historyId = _lastTranscriptionHistoryId;
        if (!historyId.HasValue)
            return;

        try
        {
            var existing = await _historyService.GetByIdAsync(historyId.Value);
            if (existing != null)
                return;

            _lastTranscriptionHistoryId = null;
            LastTranscription = "";
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to synchronize last transcription with history");
        }
    }

    /// <summary>
    /// IProgress implementation that invokes the callback synchronously on the calling thread.
    /// Unlike <see cref="Progress{T}"/>, this avoids posting through SynchronizationContext,
    /// which in WinUI 3 (no sync context) means an unnecessary thread pool hop.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>
    /// Captures all data needed to re-run an enhancement without re-recording.
    /// </summary>
    public sealed record RedoContext(
        string RawText,              // processed transcript (post text-pipeline, pre-enhancement)
        string? EnhancedText,        // the result from the first enhancement (for reference/logging)
        CustomPrompt? Prompt,        // prompt that was used (may be null for default)
        bool WasImageGeneration,     // true = GenerateImageAsync path, false = EnhanceAsync path
        IntPtr TargetWindow,         // handle of the window to paste into after redo
        bool WasPushToTalk,          // capture for correct send-enter-after-paste on redo
        string TranscriptionModelName, // original Whisper model used for transcription
        // The language the ORIGINAL transcription actually recognised with (TRN-1 step 3, PR A).
        // A redo re-runs ENHANCEMENT on existing text — it never re-transcribes — so its history row
        // must carry the original attempt's language. The two redo history writes previously
        // re-read the LIVE global setting, which is a different value the moment any override
        // applied, or the moment the user changed the setting between recording and redo.
        // The recognition language of the transcription this chain descends from. Null ONLY for a
        // chain with no transcription behind it (IMG-1 text-first entry) — and it stays null there
        // rather than falling back to the live setting, because inventing today's setting records
        // a fact that never happened.
        //
        // REQUIRED, deliberately un-defaulted. A default let a re-arm site omit it and compile
        // silently, so the first redo carried the language and the second dropped it back to the
        // live setting — the very bug this field exists to kill, one redo deeper. Required makes
        // every construction site state its answer, and a text-first chain says `null` out loud.
        string? TranscriptionLanguage,
        AIProvider? PreviousProvider = null,  // provider used in previous generation (for pre-selection)
        string? PreviousModel = null,         // model used in previous generation (for pre-selection)
        string? PreviousImageAspect = null,   // aspect ratio used in previous generation (for pre-selection)
        string? PreviousImageSizeTier = null, // size tier used in previous generation (for pre-selection)
        string? PreviousImageQuality = null,  // image quality used in previous generation (for pre-selection)
        int? PreviousImageCount = null,       // IMG-3: seeds the next dialog's Versions picker AND carries the confirmed run count back to dispatch (dialog-authoritative on confirm, like References); null = fresh chain, seed 1
        bool IsNewGeneration = false,         // IMG-1 text-first entry: no lineage — dialog says "Generate Image"/"Generate", not "Regenerate"/"Run"
        IReadOnlyList<ReferenceImageSelection>? References = null // ENH-6/6f: reference images riding this generation, selection order (dialog is authoritative — its confirm overwrites this; ALWAYS an immutable snapshot, null ≡ empty)
    );

    /// <summary>
    /// Everything a failed transcription's retry needs to re-run the pipeline from the
    /// retained WAV (REL-12). Captures the RECORDING-SCOPED inputs of the failed
    /// attempt; everything else (API keys, globally selected model, vocabulary,
    /// formatting settings) is deliberately re-read live at retry time so a fix-key /
    /// switch-provider retry works. The App-Mode overrides are captured — NOT
    /// re-resolved — matching what a fresh recording in the same target app resolves
    /// (`GetService(modelOverride)` prefers the override unconditionally); editing the
    /// App Mode config frees only NEW recordings (documented edge, plan v3 [R3]).
    ///
    /// <para>TRN-17 added <c>UserModelPick</c> / <c>UserLanguagePick</c>, which OUTRANK those
    /// captured App-Mode overrides — the user chose them against the failure they can see, having
    /// been shown what the attempt would otherwise use, which is later evidence than a config
    /// resolved at recording start. The precedence itself lives in
    /// <see cref="Helpers.RetryAttemptResolution"/>, shared with the picker that pre-selects.</para>
    /// </summary>
    public sealed record TranscriptionRetryContext(
        string WavPath,                       // the failure-retained recording
        string? ModelOverride,                // App-Mode transient model override, if any
        string? LanguageOverride,             // App-Mode transient language override, if any
        string? LinkedEnhancementId,          // App-Mode linked enhancement, if any
        CustomPrompt? HotkeyPromptOverride,   // the hotkey prompt override the attempt consumed
        IntPtr TargetWindow,                  // original target (gates the fresh recapture, like redo)
        bool WasPushToTalk,                   // send-enter-after-paste must not read live hotkey state
        bool SkipEchoGate = false,            // PRM-4: retry armed FROM an echo block — the user's
                                              // explicit "I really said that"; the re-run bypasses
                                              // both echo gates so it can't loop into the same drop
        bool SkipNoSpeechGate = false,        // VAD gate sibling: retry armed FROM a no-speech block —
                                              // the user's explicit "I really spoke"; the re-run
                                              // bypasses the VAD gate (and its RMS fallback) entirely
        string? UserModelPick = null,         // TRN-17: the retry picker's per-run model choice.
                                              // OUTRANKS ModelOverride and the global setting
                                              // (RetryAttemptResolution). Deliberately a SEPARATE
                                              // field: ModelOverride means "what App Mode resolved
                                              // for the failed attempt", and overloading it would
                                              // rewrite that meaning on every re-arm. NEVER written
                                              // back to settings — this is per-run only (owner rule)
        string? UserLanguagePick = null);     // ditto for the recognition language

    /// <summary>
    /// The ONLY pipeline construction point for <see cref="TranscriptionRetryContext"/> —
    /// the gate-bypass carry rules live here so the no-speech block handler and the
    /// pipeline's retry candidate can never diverge. Both Skip flags CARRY from the
    /// consumed retry (PRM-4 round 4: a bypassing retry that fails at a LATER stage must
    /// re-arm still-bypassing, or the same audio gets re-blocked on the next Retry); a
    /// no-speech block additionally SETS its own bypass — the armed retry IS the user's
    /// explicit "I really spoke". internal for test pinning (InternalsVisibleTo).
    /// </summary>
    internal static TranscriptionRetryContext BuildRetryContext(
        string wavPath,
        string? modelOverride,
        string? languageOverride,
        string? linkedEnhancementId,
        CustomPrompt? hotkeyPromptOverride,
        IntPtr targetWindow,
        bool wasPushToTalk,
        TranscriptionRetryContext? consumedRetry,
        bool armFromNoSpeechBlock = false)
        => new(wavPath, modelOverride, languageOverride, linkedEnhancementId,
            hotkeyPromptOverride, targetWindow, wasPushToTalk,
            SkipEchoGate: consumedRetry?.SkipEchoGate ?? false,
            SkipNoSpeechGate: armFromNoSpeechBlock || (consumedRetry?.SkipNoSpeechGate ?? false),
            // TRN-17: the picks carry too, and for a reason the Skip flags do not share. They do not
            // change what the NEXT attempt does — the dialog always opens and always returns a fresh
            // pick — they are what that dialog PRE-SELECTS. Without the carry, a second failure
            // re-opens defaulted back to the global setting the user just chose against.
            UserModelPick: consumedRetry?.UserModelPick,
            UserLanguagePick: consumedRetry?.UserLanguagePick);

    /// <summary>
    /// The user's selections from the shared enhancement-options dialog
    /// (<c>ShowEnhancementOptionsDialogAsync</c>) — returned by both the redo/regenerate flow
    /// and the initial image-generation options picker. Aspect/SizeTier/Quality are null for
    /// text enhancement (or when an image control is collapsed = "auto").
    /// </summary>
    public sealed record EnhancementDialogSelection(
        string EditedText,
        AIProvider? Provider,
        string? Model,
        string? Aspect,
        string? SizeTier,
        string? Quality,
        IReadOnlyList<ReferenceImageSelection>? References = null, // ENH-6/6f: the dialog's reference strip (null/empty = none; snapshot, never the dialog's working list)
        int Count = 1); // IMG-3: the Versions picker (image dialogs only; text dialogs pass 1) — per-run choice, clamped at dispatch

    /// <summary>
    /// What the transcription-retry picker needs to render (TRN-17). Plain data, resolved before
    /// the dialog opens, so the dialog itself resolves no services and locates nothing.
    /// </summary>
    /// <param name="Models">
    /// The models that can run right now (<see cref="Helpers.RunnableTranscriptionModels"/>) —
    /// NON-EMPTY by construction: the empty case is refused before the dialog slot is taken, since
    /// an empty picker is a dead end the caller already knew about.
    /// </param>
    /// <param name="PreselectedModelName">
    /// The catalogue name to select, or <c>null</c> when the attempt's model is no longer runnable.
    /// Null leaves the combo UNSELECTED and the confirm button disabled — never index 0, which
    /// would silently commit whatever presentation order puts at the top.
    /// </param>
    /// <param name="RequestedModelDisplayName">
    /// What the attempt would otherwise have used, for the "can't run right now" line. Already
    /// resolved through <see cref="Helpers.ModelDisplayName"/>, so no raw id can reach the dialog.
    /// </param>
    /// <param name="PreselectedLanguage">The language the attempt would request — an ISO code or <c>"auto"</c>.</param>
    public sealed record TranscriptionRetryPickerRequest(
        IReadOnlyList<TranscriptionModelInfo> Models,
        string? PreselectedModelName,
        string RequestedModelDisplayName,
        string PreselectedLanguage);

    /// <summary>
    /// The retry picker's result (TRN-17). Per-run only — it rides the retry context and is NEVER
    /// written back to <c>selectedModelName</c> / <c>selectedLanguage</c> (owner decision).
    ///
    /// <para><see cref="Language"/> is the user's ASK, not what the chosen model will recognise
    /// with: a model that cannot offer the asked language clamps the combo's DISPLAY to Auto while
    /// the ask survives, so switching to a model that CAN offer it restores it — across dialog
    /// sessions as well as within one. <c>EffectiveTranscriptionLanguage</c> still clamps at the
    /// wire, so committing the ask cannot make a run request something its model rejects.</para>
    /// </summary>
    public sealed record TranscriptionRetrySelection(string ModelName, string Language);
}
