using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using VoiceWink.Controls;
using VoiceWink.Helpers;
using VoiceWink.Models.Enums;
using VoiceWink.ViewModels;
using WinRT.Interop;

namespace VoiceWink.Views;

/// <summary>
/// Floating tool window for recording status and controls.
/// Always-on-top, no taskbar entry, borderless. Default position is top-center of the
/// active monitor; the user can DRAG the pill by its background (PILL-4) and the spot is
/// remembered as work-area fractions (MiniRecorderUserPlacement).
/// Shows waveform visualizer during Recording, status text during Transcribing/Enhancing.
/// Content built in code to bypass PRI/XAML resource loading issues on CLI-only builds.
/// </summary>
public sealed partial class MiniRecorderWindow : Window, IMiniRecorderRenderer
{
    private static ILogger Logger => Log.ForContext<MiniRecorderWindow>();

    // Pill dimensions in device-independent pixels. The window is sized slightly larger
    // than the pill and the pill uses Margin(+Stretch) to float inside — giving the pill
    // a small visible frame of rootGrid color on each side. We deliberately avoid fixed
    // Width/Height on the pill and centering-via-HorizontalAlignment because WinUI's
    // layout can cache stale measurements for fixed-size centered children across DPI
    // changes. Stretch + Margin is DPI-safe: rootGrid's size follows the window, and
    // the pill always stretches to (window - 2×margin).
    // PILL-1 widened 280→360; UAT 2026-07-08 widened 360→460 (label readability),
    // then 460→500 (PILL-2: bigger waveform).
    // Recording-layout budget (recompute if any piece changes):
    // content = PillWidth − 2×border − padding(14+10) = PillWidth − 26;
    // fixed columns = dot (20+6) + timer ("10:00" ≈ 31 + 8) + target label (160 + 8)
    //               + stop (28+8) = 269. The waveform no longer constrains the pill
    // width: since 2026-07-10 its bar count is DYNAMIC (WaveformLayout — the control
    // stretches into the star column and derives the count from its actual width, so
    // overflow is impossible by construction; the old fixed 198-DIP/40-bar canvas
    // required PillWidth ≥ 493). Redo-layout text budget: content − dot (26) − redo
    // (28+8+8) ≈ 404 DIP at 12 px ≈ 60 chars — MainViewModel's 55-char truncation cap
    // (ComposeFallbackFailureStatus/TruncateForMiniRecorder) stays comfortably
    // inside it. Placement defaults to top-centered and follows the user's dragged
    // fractions when saved (PILL-4) — always clamped inside the work area, so the
    // 520-DIP window fits any work area wider than 520 DIP.
    // All placement/park/reveal machinery derives from these constants, so the change
    // propagates through CLK-1/DSP-1 geometry automatically.
    private const int PillWidthDips = 500;
    private const int PillHeightDips = 48;
    private const int WindowPaddingDipsH = 10;
    private const int WindowPaddingDipsV = 6;
    private const int WindowWidthDips = PillWidthDips + 2 * WindowPaddingDipsH;   // 520
    private const int WindowHeightDips = PillHeightDips + 2 * WindowPaddingDipsV; // 60

    private readonly MainViewModel _viewModel;
    private readonly Services.System.SettingsService? _settings;
    private readonly PropertyChangedEventHandler _vmPropertyChanged;
    private readonly Ellipse RecordingDot;
    private readonly Ellipse _dotGlow;
    private readonly AudioVisualizerControl Visualizer;
    private readonly TextBlock StatusText;
    // Invisible mirror of StatusText (same font, no constraints) — its measured width is
    // the text's DESIRED width, which the visible StatusText can't report once margins or
    // trimming constrain it. Feeds MiniRecorderStatusPlacement.
    private readonly TextBlock _statusMeasure;
    // PRM-6: the two-line height probe (see its construction comment). Distinct from _statusMeasure,
    // which must stay unwrapped/unconstrained for the placement rule's natural-width contract.
    private readonly TextBlock _statusHeightProbe;
    private readonly TextBlock _timerText;
    private readonly TextBlock _targetAppText;
    private readonly Grid _dotContainer;
    private readonly Grid _innerGrid;
    // PILL-1: true only in Recording/Transcribing/Enhancing — the target label
    // describes an in-flight recording's destination, nothing else.
    private bool _targetLabelAllowed;
    private readonly Border _pillBorder;
    private readonly Border _stopButton;
    private readonly FontIcon _stopIcon;
    private readonly Border _redoButton;
    private readonly FontIcon _redoIcon;
    // ERR-PERSIST: corner × to manually dismiss a persistent error pill (owner 2026-07-20). Only ever
    // visible on a persistent error; collapsed in every other state.
    private readonly Border _dismissButton;
    private readonly FontIcon _dismissIcon;
    private readonly Border _rootContainer;
    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly Action _themeChangedHandler;
    private DateTime _recordingStartTime;
    private bool _dotPulseUp = true;
    // The redo/retry action button tracks the CURRENT pill tone's accent (set by every
    // RenderAffordance) so its icon + hover tint always match the rest of the pill — no more
    // green button on an amber/red pill. Default green (the Success "Done" redo pill).
    private Windows.UI.Color _redoButtonAccent = AppTheme.AccentGreen;
    /// <summary>The stop icon's CURRENT semantic accent (Codex diff review P1): hover exit and
    /// theme re-apply restore this instead of hardcoding neutral, so a tinted state's icon
    /// survives a mouse-over. Set at every render site that shows the stop button.</summary>
    private Windows.UI.Color _stopButtonAccent = AppTheme.TextPrimary;
    /// <summary>Last presentation delivered to <see cref="Render"/> (null = hidden). Replayed
    /// after a live theme change (Codex diff review P2): ApplyTheme resets chrome to neutral, and
    /// without a replay the active pill kept neutral text/icon until its next natural render.</summary>
    private PillPresentation? _lastPresentation;
    // REL-20: these two tooltips sit on the SAME ↻ button and must not both promise
    // transcription — the button does one of two different things depending on which affordance
    // is armed, and the user cannot otherwise tell which.
    //
    // Redo re-runs ENHANCEMENT (or image generation) over text that was ALREADY transcribed; it
    // never re-transcribes, as RedoContext's own comment states — "A redo re-runs ENHANCEMENT on
    // existing text — it never re-transcribes" (MainViewModel.RedoContext). It previously read
    // "Transcribe again with a different model", which named the wrong operation AND the wrong
    // model: the picker it opens chooses the ENHANCEMENT/image model, not the transcription one.
    // Wording covers both redo kinds, since AffordanceKind.Redo serves text and image alike.
    //
    // Retry is the one that genuinely re-transcribes (RetryTranscriptionAsync replays the
    // failure-retained WAV) — but it arms only on FAILURE, which is why the redo string being
    // wrong was what users actually met after a success.
    //
    // TRN-17 made the retry copy name its picker too: the tap now opens a dialog choosing the
    // TRANSCRIPTION model and language, so "Retry transcription" understated it in exactly the
    // direction the redo string had overstated. Both now say which model they choose, which is
    // the only thing that ever distinguished them to a user.
    private const string RedoTooltip = "Run again with a different AI model";
    private const string RetryTooltip = "Retry with a different transcription model";
    private bool _isOffScreen = true; // suppresses UpdateState while hidden so visual reset sticks
    private bool _closingForReplacement;

    // Cross-monitor DPI reconciliation state — see EnsureXamlRootMatchesTargetDpi.
    private DispatcherTimer? _dpiSyncTimer;
    private int _dpiSyncTickCount;
    private MonitorPlacement _pendingDpiSyncPlacement;
    private IntPtr _pendingDpiSyncHwnd;
    private XamlRoot? _dpiSyncXamlRoot;
    // PILL-4: set when the user drags the pill while a DPI sync is pending — the
    // pending placement's X/Y are then stale and must not be re-asserted.
    private bool _draggedSinceDpiSyncArmed;

    // Active CompositionTarget.Rendering handler from an in-flight WaitTwoFramesThenShow.
    // Tracked here so CancelDpiSync can unsubscribe and prevent a stale handler from
    // firing SetWindowPos at a previous show's placement if a new Show interrupts.
    private EventHandler<object>? _waitTwoFramesHandler;

    // True while a show is in flight awaiting DPI sync before the terminal
    // reveal — the window sits at the target coordinates either DWM-cloaked
    // (cloak path) or WS_VISIBLE-off (legacy path). ApplyDpiReconciliation
    // completes MoveAndResize + layout + reveal when XamlRoot scale catches up.
    // Set/cleared on UI thread only. (Renamed from _pendingHiddenShow when
    // cloak parking extended the same flow beyond SWP_HIDEWINDOW.)
    private bool _pendingReveal;

    // CLK-1 cloak parking. _cloakAvailable is probed once at construction (at
    // the safe off-screen park) and flips OFF one-way on any later cloak
    // failure — every touched site then takes the legacy branch. _isCloaked
    // mirrors the VERIFIED DWM state (read back via DWMWA_CLOAKED after each
    // apply). Lifecycle states: Parked (_isOffScreen ∧ cloaked-at-real-coords |
    // legacy-at--10000), RevealPending (!_isOffScreen ∧ _isCloaked),
    // Visible (!_isOffScreen ∧ fully uncloaked). See MiniRecorderShowTransition.
    private bool _cloakAvailable;
    private bool _isCloaked;

    // The user-visible show position computed by PositionWindow at construction
    // (primary's pill spot, or the recreate target's). The cloak path parks the
    // fresh window THERE (cloaked) so its DPI association is real from birth.
    private int _initialShowX, _initialShowY;

    internal readonly record struct MonitorPlacement(
        IntPtr Monitor,
        string Source,
        string ForegroundStatus,
        IntPtr ForegroundHwnd,
        NativeInterop.RECT MonitorRect,
        NativeInterop.RECT WorkArea,
        uint MonitorDpi,
        double Scale,
        int X,
        int Y,
        int Width,
        int Height,
        int Padding,
        uint WindowDpiBefore);

    // ERR-PERSIST controller refactor (2026-07-21): the window is now an IMiniRecorderRenderer. It
    // raises these four handle-carrying requests (handle captured at pointer-DOWN, ABA guard) and
    // NEVER acts on the VM/controller itself — App validates the handle against the controller's
    // current presentation and routes. The controller owns all timing/dismissal; the window renders.
    /// <summary>Corner × tapped — request to dismiss the current presentation.</summary>
    public event Action<PillHandle>? DismissRequested;
    /// <summary>Redo/retry action button tapped.</summary>
    public event Action<PillHandle>? ActionRequested;
    /// <summary>Stop button tapped (pipeline/download/image-job pills).</summary>
    public event Action<PillHandle>? StopRequested;
    /// <summary>Pill right-clicked — request to hide it while its owner keeps running (2026-07-30).</summary>
    public event Action<PillHandle>? HideRequested;
    /// <summary>The body of a message pill was clicked (LNC-11): a left press on the background,
    /// released without crossing the drag threshold. The controller decides what the handle's
    /// message action is; a plain message resolves to None there.</summary>
    public event Action<PillHandle>? MessageActionRequested;

    // The handle of the presentation currently rendered, and the handle captured at pointer-DOWN on
    // whichever button is being pressed (emitted on Tapped so a press spanning a render dismisses the
    // ORIGINAL surface, not the swapped-in one).
    private PillHandle _renderedHandle;
    // Per-button pointer-down captures (Codex finding 7): a single shared field let a second button's
    // press (multi-touch / a pill swap between two pointers) overwrite the first's captured handle, so
    // the first button's Tapped could emit the wrong presentation's handle. One field per button.
    private PillHandle _stopPressHandle;
    private PillHandle _actionPressHandle;
    private PillHandle _dismissPressHandle;
    // Right-press capture for the hide gesture. ONE-SHOT: cleared at the top of every pointer press
    // (so a press that is not an admitted right-press can never leave a stale handle behind) and
    // consumed by the RightTapped that follows. Without the reset, a hide → tray restore → drag
    // sequence could re-emit the pre-hide handle.
    private PillHandle _hidePressHandle;
    // Left-press capture for the message-body click (LNC-11): the handle rendered when the drag
    // handle's press landed, emitted by the release that did NOT move (a moved release is a drag —
    // PILL-4 — and persists a position instead). Cleared with the drag state so a cancelled drag
    // can never emit it later.
    private PillHandle _messagePressHandle;

    /// <summary>
    /// Single-subscriber callback (NOT a multicast event). Raised with a trigger
    /// string when this window wants the host to recreate it for the target
    /// monitor: "cross-dpi-show" (legacy mismatch path, recreate-first as
    /// always), "cloak-timeout" (cloaked reconciliation never converged), or
    /// "uncloak-failure" (DWM refused to reveal a cloaked window). Returns true
    /// if the host accepted and will recreate — the caller abandons its
    /// in-place flow. Returns false (in-flight recreate, cooldown) — the caller
    /// falls through to its own fallback.
    /// </summary>
    internal Func<MonitorPlacement, string, bool>? CrossDpiRecreateRequested;

    /// <summary>
    /// Optional target placement supplied at construction (recreate path). When
    /// provided, PositionWindow parks the new window adjacent to the target
    /// monitor. NOTE: live evidence (2026-07-06) showed an off-screen park does
    /// NOT re-assign the window's DPI, so XamlRoot does not pre-initialize at
    /// the target scale from parking alone — the visible Show()'s reconciliation
    /// handles the scale correction; the park keeps nearest-monitor-based
    /// placement fallbacks pointing at the intended monitor.
    /// </summary>
    private readonly MonitorPlacement? _initialTarget;

    public MiniRecorderWindow(MainViewModel viewModel) : this(viewModel, null) { }

    internal MiniRecorderWindow(MainViewModel viewModel, MonitorPlacement? initialTarget)
    {
        _viewModel = viewModel;
        _initialTarget = initialTarget;
        // Fail-soft: without settings the pill simply keeps its default placement and
        // dragging isn't persisted (PILL-4). Never let DI resolution break the pill.
        try
        {
            _settings = App.Services?.GetService(typeof(Services.System.SettingsService))
                as Services.System.SettingsService;
        }
        catch
        {
            _settings = null;
        }

        // Recording dot glow — soft halo behind the dot
        _dotGlow = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = AppTheme.Brush(ColorHelper.FromArgb(50, 255, 69, 58)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6
        };

        // Recording dot — pulsing red indicator
        RecordingDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = AppTheme.Brush(AppTheme.AccentRed),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Dot container (glow + dot layered)
        _dotContainer = new Grid
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Children = { _dotGlow, RecordingDot }
        };

        // Elapsed timer text (shows "0:05" style)
        _timerText = new TextBlock
        {
            Text = "0:00",
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            // Right margin matches stop button's left margin (8) so the STAR
            // column is symmetric — visualizer ends up on the pill's visual midpoint.
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };

        // Paste-target app label (PILL-1): "→ AppName" so the user SEES where the
        // paste will go while speaking — focus follows the last click, not eyes.
        _targetAppText = new TextBlock
        {
            FontSize = 10,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // Part of the PillWidthDips recording-layout budget (see the constant's
            // comment): 160 + 8 margin. Widen the pill first if you widen this.
            // 160 DIP at 10 px fits the sanitizer's full 24-char cap
            // (MiniRecorderTargetLabel.MaxLength) without truncation.
            MaxWidth = 160,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };

        // Audio visualizer — STRETCHED so its dynamic bar count (WaveformLayout) derives
        // from the star column's real width and the waveform fills the free space between
        // the target label and the stop button (owner 2026-07-10). The control centers its
        // canvas internally, so the bars stay visually centered.
        Visualizer = new AudioVisualizerControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        // Status text (for Transcribing/Enhancing states). Spans the WHOLE pill (see the
        // column assignments below) so it can center on the pill's midpoint rather than in
        // the leftover space right of the target label; display-only, so it must never
        // steal pointer input from the stop/redo buttons it may share columns with.
        StatusText = new TextBlock
        {
            Text = "Recording...",
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // PRM-6 static two lines: Wrap (NOT WrapWholeWords — an unbreakable token must break
            // rather than overflow), lines centred (HorizontalAlignment centres only the ELEMENT;
            // wrapped lines inside it would render left-aligned without this), and MaxLines
            // explicitly 1 — WinUI's default is 0 = UNLIMITED, so leaving it unset until the first
            // layout pass would not be the single-line fail-safe it looks like. Promotion to 2 is
            // height-gated per layout pass in UpdateStatusPlacement.
            TextWrapping = TextWrapping.Wrap,
            HorizontalTextAlignment = TextAlignment.Center,
            MaxLines = 1,
            IsHitTestVisible = false
        };

        // Desired-width mirror for the placement rule (never visible, never hit-testable;
        // same font metrics as StatusText, deliberately no trimming/wrapping/MaxWidth so its
        // measured width is the text's natural SINGLE-LINE width — that natural width is what
        // pushes a long message into the CenteredBetweenZones fallback, whose margins reserve
        // the label and button zones. Do not wrap this mirror; the placement contract needs it
        // unconstrained.
        _statusMeasure = new TextBlock
        {
            Text = "Recording...",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = 0,
            IsHitTestVisible = false
        };

        // PRM-6 height probe: mirrors the status text's CONTENT in the PROMOTED configuration
        // (Wrap + MaxLines 2) at the exact box width the visible text gets (assigned per layout
        // pass in UpdateStatusPlacement). Its ActualHeight is therefore the true height of THIS
        // message's up-to-two rendered lines — real fallback fonts (CJK/emoji line boxes are
        // taller than Latin) at the current text scale. A fixed Latin probe or 2×-single-line
        // arithmetic under-measures those and would promote two lines that clip (Codex plan
        // review, 2026-07-26). MiniRecorderStatusLines.Decide consumes it.
        _statusHeightProbe = new TextBlock
        {
            Text = "Recording...",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0,
            IsHitTestVisible = false
        };

        // Stop button — rounded square with stop icon
        _stopIcon = new FontIcon
        {
            Glyph = "\uE71A",
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };

        _stopButton = new Border
        {
            Background = StopNormalBg(),
            CornerRadius = new CornerRadius(14),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            // Left margin matches timer's right margin (8) — see _timerText.
            Margin = new Thickness(8, 0, 0, 0),
            Child = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _stopIcon }
            }
        };
        _stopButton.PointerEntered += (_, _) =>
        {
            // White-on-red hover stays deliberate (contrast on the destructive-action red).
            _stopButton.Background = AppTheme.Brush(ColorHelper.FromArgb(80, 255, 69, 58));
            _stopIcon.Foreground = AppTheme.Brush(AppTheme.TextPrimary);
        };
        _stopButton.PointerExited += (_, _) =>
        {
            _stopButton.Background = StopNormalBg();
            // Restore the state's semantic accent, not neutral (Codex diff review P1).
            _stopIcon.Foreground = AppTheme.Brush(_stopButtonAccent);
        };
        // Capture the rendered handle at pointer-DOWN; raise the request on Tapped. App validates the
        // handle against the controller's current presentation and routes via MiniRecorderStopRouting.
        _stopButton.PointerPressed += (_, _) => _stopPressHandle = _renderedHandle;
        _stopButton.Tapped += (_, _) => StopRequested?.Invoke(_stopPressHandle);

        // Redo button — refresh icon, same style as stop button, hidden by default
        _redoIcon = new FontIcon
        {
            Glyph = "\uE72C",
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };

        _redoButton = new Border
        {
            Background = StopNormalBg(),
            CornerRadius = new CornerRadius(14),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _redoIcon }
            }
        };
        // Hover/rest colours follow the CURRENT pill tone (_redoButtonAccent), not a hardcoded
        // green — so the button matches the pill in every state (amber decline, red error,
        // green success).
        _redoButton.PointerEntered += (_, _) =>
        {
            var a = _redoButtonAccent;
            _redoButton.Background = AppTheme.Brush(ColorHelper.FromArgb(80, a.R, a.G, a.B));
            _redoIcon.Foreground = AppTheme.Brush(a);
        };
        _redoButton.PointerExited += (_, _) =>
        {
            _redoButton.Background = StopNormalBg();
            _redoIcon.Foreground = AppTheme.Brush(_redoButtonAccent);
        };
        // The one refresh button serves BOTH armed affordances (REL-12): redo (open the
        // model picker) and retry (re-transcribe the failure-retained recording). Its tooltip +
        // accent are (re)written by every `RenderAffordance`, and the button is only ever visible on
        // an affordance pill, so the tap can never act on a stale mode.
        // Capture the rendered handle at pointer-DOWN; App validates it against the controller's
        // current affordance and routes via RedoOrRetryLastAsync (redo picker or retry).
        _redoButton.PointerPressed += (_, _) => _actionPressHandle = _renderedHandle;
        _redoButton.Tapped += (_, _) => ActionRequested?.Invoke(_actionPressHandle);
        ToolTipService.SetToolTip(_redoButton, RedoTooltip);

        // ERR-PERSIST corner × — dismisses a persistent error pill. Collapsed except on such a pill.
        _dismissIcon = new FontIcon
        {
            Glyph = "\uE711", // Cancel (×)
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };
        _dismissButton = new Border
        {
            Background = StopNormalBg(),
            CornerRadius = new CornerRadius(14),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _dismissIcon }
            }
        };
        _dismissButton.PointerEntered += (_, _) =>
        {
            var a = AppTheme.AccentFor(MiniRecorderTone.Error);
            _dismissButton.Background = AppTheme.Brush(ColorHelper.FromArgb(80, a.R, a.G, a.B));
        };
        _dismissButton.PointerExited += (_, _) => _dismissButton.Background = StopNormalBg();
        // Capture the rendered handle at pointer-DOWN; raise a dismissal REQUEST on Tapped so a press
        // spanning a render dismisses the ORIGINAL presentation, not the swapped-in one (ABA guard).
        // The window never hides itself — the controller validates the handle and hides.
        _dismissButton.PointerPressed += (_, _) => _dismissPressHandle = _renderedHandle;
        _dismissButton.Tapped += (_, _) => DismissRequested?.Invoke(_dismissPressHandle);
        ToolTipService.SetToolTip(_dismissButton, "Dismiss");
        AutomationProperties.SetName(_dismissButton, "Dismiss");

        // Inner layout: [dot] [timer] [→ target] [visualizer] [stop] [redo].
        // StatusText is a full-span overlay (display-only, hit-test-transparent) so it can
        // center on the pill's midpoint; UpdateStatusPlacement gives it zone-sized margins
        // when the text is too wide to pill-center without touching the label or buttons.
        _innerGrid = new Grid();
        _innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // dot
        _innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // timer
        _innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // target label
        _innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // center
        _innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // stop
        _innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // redo
        _innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // dismiss (×)

        // The mirror lives in a zero-size Canvas: Canvas children measure with an INFINITE
        // constraint (so ActualWidth is the text's true natural width, uncapped by the cell)
        // and contribute nothing to the grid's own layout. Raw accessibility view keeps the
        // duplicate text out of UIA/screen readers (Codex diff-review residual).
        var statusMeasureHost = new Canvas
        {
            Width = 0,
            Height = 0,
            IsHitTestVisible = false,
            Children = { _statusMeasure, _statusHeightProbe }
        };
        AutomationProperties.SetAccessibilityView(statusMeasureHost, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_statusMeasure, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_statusHeightProbe, AccessibilityView.Raw);

        Grid.SetColumn(_dotContainer, 0);
        Grid.SetColumn(_timerText, 1);
        Grid.SetColumn(_targetAppText, 2);
        Grid.SetColumn(Visualizer, 3);
        Grid.SetColumn(statusMeasureHost, 0);
        Grid.SetColumn(StatusText, 0);
        Grid.SetColumnSpan(StatusText, 7);
        Grid.SetColumn(_stopButton, 4);
        Grid.SetColumn(_redoButton, 5);
        Grid.SetColumn(_dismissButton, 6);

        _innerGrid.Children.Add(_dotContainer);
        _innerGrid.Children.Add(_timerText);
        _innerGrid.Children.Add(_targetAppText);
        _innerGrid.Children.Add(Visualizer);
        _innerGrid.Children.Add(statusMeasureHost);
        _innerGrid.Children.Add(StatusText);
        _innerGrid.Children.Add(_dismissButton);
        _innerGrid.Children.Add(_stopButton);
        _innerGrid.Children.Add(_redoButton);

        // Keep the mirrors' text in lockstep with StatusText (its many assignment sites
        // stay untouched), and re-decide placement after every layout pass — the writes in
        // UpdateStatusPlacement are change-guarded, so this converges instead of looping.
        StatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty,
            (_, _) =>
            {
                _statusMeasure.Text = StatusText.Text;
                _statusHeightProbe.Text = StatusText.Text;
            });
        _innerGrid.LayoutUpdated += (_, _) => UpdateStatusPlacement();

        // Pill-shaped container — theme-aware with subtle border
        var pillBg = AppTheme.IsDark
            ? ColorHelper.FromArgb(240, 22, 22, 26)
            : ColorHelper.FromArgb(240, 255, 255, 255);
        var pillBorderColor = AppTheme.IsDark
            ? ColorHelper.FromArgb(80, 42, 42, 46)
            : ColorHelper.FromArgb(80, 180, 180, 185);
        // Pill stretches to fill the window minus Margin — no fixed Width/Height so DPI
        // changes don't leave a stale layout. The margin gives the pill a small frame
        // of rootGrid color around it.
        _pillBorder = new Border
        {
            // Half the pill height so the ends render as full semicircles (true capsule).
            // Tied to PillHeightDips so the capsule shape tracks any future height change.
            CornerRadius = new CornerRadius(PillHeightDips / 2),
            Background = AppTheme.Brush(pillBg),
            BorderBrush = AppTheme.Brush(pillBorderColor),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 0, 10, 0),
            Child = _innerGrid
        };

        _rootContainer = new Border
        {
            // Pill color so the margin around the pill reads as a uniform frame of
            // pill color, and so there's no white-clear flash during first Show().
            // Using Border.Padding (not Margin on the pillBorder) ensures the pillBorder
            // stretches to exactly (Window − 2×Padding) DIPs — essential for the capsule
            // shape to render correctly (CornerRadius == PillHeightDips/2).
            Background = AppTheme.Brush(PillBgColor()),
            Padding = new Thickness(WindowPaddingDipsH, WindowPaddingDipsV, WindowPaddingDipsH, WindowPaddingDipsV),
            Child = _pillBorder
        };
        Content = _rootContainer;

        // PILL-4: the pill background is a drag handle — pressing anywhere except the
        // stop/redo buttons starts a manual captured-pointer drag (live window moves
        // on every PointerMoved; see the drag-state fields for why not the native loop).
        _rootContainer.PointerPressed += OnRootPointerPressed;
        _rootContainer.PointerMoved += OnRootPointerMoved;
        _rootContainer.PointerReleased += OnRootPointerReleased;
        _rootContainer.PointerCaptureLost += OnRootPointerCaptureLost;
        // Right-click anywhere on the pill (buttons included — those act on left Tapped, so there is
        // no conflict) asks the controller to hide it while its owner keeps running.
        _rootContainer.RightTapped += OnRootRightTapped;

        ResetToStartingVisuals(); // Set initial visual state before window is positioned
        ConfigureWindow();
        PositionWindow();

        // Start off-screen. The window stays "shown" to WinUI so the compositor
        // renders and caches the content, making subsequent Show() calls instant.
        // The very first Show() has a brief white flash — WinUI 3 compositor
        // limitation (first render hasn't completed by the time SetWindowPos runs).
        //
        // Recreate flow (_initialTarget set): PositionWindow already parked the
        // window adjacent to the target monitor (see the truth note there — the
        // park keeps nearest-monitor placement fallbacks correct; it does NOT
        // pre-initialize XamlRoot's DPI). Moving to -10000,-10000 here would
        // undo the parking. Preserve the parked position via SWP_NOMOVE | SWP_NOSIZE.
        var initHwnd = WindowNative.GetWindowHandle(this);
        if (_initialTarget != null)
        {
            NativeInterop.SetWindowPos(
                initHwnd, IntPtr.Zero,
                0, 0, 0, 0,
                NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER
                | NativeInterop.SWP_NOMOVE | NativeInterop.SWP_NOSIZE);
        }
        else
        {
            NativeInterop.SetWindowPos(
                initHwnd, IntPtr.Zero,
                -10000, -10000, _windowWidth, _windowHeight,
                NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
        }

        // CLK-1: probe DWM cloaking at the SAFE park (a failed/slow cloak here can
        // never flash — the window is off-screen/adjacent). Only on a VERIFIED
        // cloak does the window move onto its real show position, where its DPI
        // association — the thing off-screen parking can never provide — becomes
        // real, letting XamlRoot converge while hidden. Probe failure leaves the
        // window exactly where the legacy flow expects it.
        _cloakAvailable = TryApplyCloak(true);
        if (_cloakAvailable)
        {
            NativeInterop.SetWindowPos(
                initHwnd, IntPtr.Zero,
                _initialShowX, _initialShowY, _windowWidth, _windowHeight,
                NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
            Logger.Information(
                "MiniRecorder cloak parking active: parked cloaked at show position ({X},{Y})",
                _initialShowX, _initialShowY);
        }

        // Pulse animation for recording dot
        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _pulseTimer.Tick += OnPulseTick;

        // Re-assert topmost periodically so the MiniRecorder isn't hidden by other windows
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Tick += (_, _) =>
        {
            try
            {
                if (ShouldSkipUiWork()) return;
                if (_isOffScreen) return;
                var hwnd = WindowNative.GetWindowHandle(this);
                NativeInterop.SetWindowPos(
                    hwnd, NativeInterop.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeInterop.SWP_NOMOVE | NativeInterop.SWP_NOSIZE | NativeInterop.SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Topmost timer tick failed");
            }
        };

        // Elapsed time counter
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += OnElapsedTick;

        // No SEMANTIC timer is wired up here, and that is the invariant rather than an omission:
        // every timer this window owns is MECHANICAL — pulse, elapsed and topmost above, plus the
        // on-demand _dpiSyncTimer (a DPI-reconcile safety net). What the pill SAYS and how long it
        // stays belongs to MiniRecorderPresentationController, which schedules and re-renders.
        // (The comment this replaces named a per-presentation "warning-dismiss timer in ShowError";
        // neither the timer nor that method has existed since the ERR-PERSIST controller refactor.)

        // Wire up AudioLevel binding from ViewModel -> Visualizer
        _vmPropertyChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AudioLevel) && !_isOffScreen)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ShouldSkipUiWork()) return;
                    Visualizer.AudioLevel = _viewModel.AudioLevel;
                });
            }
            else if (e.PropertyName == nameof(MainViewModel.PasteTargetAppName))
            {
                // Fires from the resolution worker thread — marshal, then pull.
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ShouldSkipUiWork()) return;
                    ApplyTargetAppLabel();
                });
            }
        };
        _viewModel.PropertyChanged += _vmPropertyChanged;

        // PILL-1: pull the current label at construction — a window RECREATED after a
        // display change must rehydrate state that changed before it existed (the
        // PropertyChanged event already fired at the old window).
        ApplyTargetAppLabel();

        // React to live theme changes
        _themeChangedHandler = () => DispatcherQueue.TryEnqueue(() =>
        {
            if (ShouldSkipUiWork()) return;
            ApplyTheme();
            // ApplyTheme resets text/icons/border to neutral chrome; replay the active
            // presentation so its semantic accents survive a live theme switch (Codex
            // diff review P2 — pre-existing for message pills, widened by the
            // accent-matched-text rule). A hidden pill (_lastPresentation null) stays
            // hidden: replaying only a delivered visible render never resurrects content.
            if (_lastPresentation != null)
                Render(_lastPresentation);
        });
        AppTheme.ThemeChanged += _themeChangedHandler;

        this.Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        CancelActiveDrag();
        _pulseTimer.Stop();
        _elapsedTimer.Stop();
        _topmostTimer.Stop();
        // Stops AudioVisualizerControl's internal 60fps DispatcherTimer via the IsActive
        // dependency-property callback — nothing else stops it once the window is discarded,
        // so a mid-recording teardown pinned the whole old window graph at 60fps forever
        // (F14). Idempotent when already inactive.
        Visualizer.IsActive = false;
        CancelDpiSync();
        _viewModel.PropertyChanged -= _vmPropertyChanged;
        AppTheme.ThemeChanged -= _themeChangedHandler;
    }

    private void OnPulseTick(object? sender, object e)
    {
        if (ShouldSkipUiWork()) return;

        // Smoothly pulse dot opacity between 0.5 and 1.0
        var opacity = RecordingDot.Opacity;
        if (_dotPulseUp)
        {
            opacity += 0.05;
            if (opacity >= 1.0) { opacity = 1.0; _dotPulseUp = false; }
        }
        else
        {
            opacity -= 0.05;
            if (opacity <= 0.5) { opacity = 0.5; _dotPulseUp = true; }
        }
        RecordingDot.Opacity = opacity;
        _dotGlow.Opacity = opacity * 0.6;
    }

    private void OnElapsedTick(object? sender, object e)
    {
        if (ShouldSkipUiWork()) return;

        _timerText.Text = MiniRecorderLifecycleState.FormatElapsed(
            _recordingStartTime,
            DateTime.UtcNow);
    }

    private void ConfigureWindow()
    {
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            var exStyle = NativeInterop.GetWindowLongPtr(hwnd, NativeInterop.GWL_EXSTYLE);
            var newStyle = (long)exStyle
                | NativeInterop.WS_EX_TOOLWINDOW
                | NativeInterop.WS_EX_NOACTIVATE;
            NativeInterop.SetWindowLongPtr(hwnd, NativeInterop.GWL_EXSTYLE, new IntPtr(newStyle));

            Logger.Information("MiniRecorderWindow configured as tool window");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to configure MiniRecorderWindow as tool window");
        }

        // Applied here so a RECREATED window (cross-DPI / display change) inherits the preference.
        ApplyCaptureExclusion();
    }

    // Last capture-affinity value successfully applied to THIS window (null = never applied), and a
    // log latch so a persistently failing call still retries but warns once per FAILURE EPISODE (it
    // re-arms on the next success — see ApplyCaptureExclusion).
    private bool? _captureAffinityApplied;
    private bool _captureAffinityWarned;

    /// <summary>
    /// Apply the capture-exclusion preference (<see cref="AppDefaults.HidePillFromCapture"/>, default
    /// ON) to this window. Called from <see cref="ConfigureWindow"/> (construction, hence every
    /// recreate) and from every <see cref="Show"/> — so a Settings change takes effect at the pill's
    /// next appearance without a subscription whose lifetime would have to survive the recreate swap.
    /// Idempotent, and fail-soft: capture exclusion must never be able to break the pill.
    /// </summary>
    private void ApplyCaptureExclusion()
    {
        try
        {
            var exclude = _settings?.GetBool(AppDefaults.HidePillFromCapture, true) ?? true;
            var hwnd = WindowNative.GetWindowHandle(this);
            var outcome = CaptureAffinity.Apply(hwnd, exclude, ref _captureAffinityApplied,
                NativeInterop.SetWindowDisplayAffinity);
            switch (outcome)
            {
                case CaptureAffinity.ApplyOutcome.Applied:
                    // Re-arm the warn latch: it bounds one warning per FAILURE EPISODE, not per window
                    // lifetime, so an independent later failure (e.g. failing to disable exclusion) is
                    // still reported once instead of silently retried forever.
                    _captureAffinityWarned = false;
                    Logger.Information("MiniRecorder capture exclusion {State}", exclude ? "enabled" : "disabled");
                    break;
                case CaptureAffinity.ApplyOutcome.Failed when !_captureAffinityWarned:
                    _captureAffinityWarned = true;
                    // State-NEUTRAL wording: "stays capturable" would be false when a previously
                    // APPLIED exclusion fails to be turned off — the pill then stays excluded.
                    // _captureAffinityApplied is the honest previous state (null = never applied).
                    Logger.Warning(
                        "SetWindowDisplayAffinity failed (requested exclude: {Exclude}, LastError: {Err}) — affinity unchanged, previous state {Previous} retained",
                        exclude, global::System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                        _captureAffinityApplied?.ToString() ?? "unset");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Applying MiniRecorder capture exclusion failed");
        }
    }

    /// <summary>Cached window size (physical pixels) for Show/Hide.</summary>
    private int _windowWidth, _windowHeight;
    // Last successfully resolved on-screen show position (F23) — the fallback target when
    // monitor placement resolution fails on a later Show(). Never a park coordinate: written
    // only in the TryResolveMonitorPlacement success branch.
    private int _lastResolvedX;
    private int _lastResolvedY;
    private bool _hasLastResolvedPosition;

    // PILL-4 live-drag state. The drag is a MANUAL captured-pointer move: the native
    // WM_NCLBUTTONDOWN/HTCAPTION modal loop receives no live pointer updates under
    // WinUI 3 (input arrives as WM_POINTER at the XAML input-site child window), so
    // the pill only jumped to its drop point at release (live UAT 2026-07-10).
    private bool _dragPointerActive;
    private uint _dragPointerId;
    private bool _dragMoved;
    private NativeInterop.POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartWindow;

    /// <summary>
    /// PILL-4 drag handle: a press on the pill background (not the stop/redo buttons —
    /// they're Borders whose presses BUBBLE, so without the exclusion a drag would eat
    /// their Tapped events) captures the pointer; <see cref="OnRootPointerMoved"/> moves
    /// the window live once the system drag threshold is crossed, and release persists
    /// the position as work-area fractions so every subsequent Show lands there.
    /// </summary>
    private void OnRootPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            // One-shot hide capture: every press invalidates a previous, unconsumed right-press.
            _hidePressHandle = PillHandle.None;
            if (_dragPointerActive) return;
            var props = e.GetCurrentPoint(_rootContainer).Properties;
            if (props.IsRightButtonPressed)
            {
                // Right-click = hide (2026-07-30). Capture the rendered handle at pointer-DOWN like
                // every other pill affordance; OnRootRightTapped consumes it. Deliberately does NOT set
                // e.Handled — marking the press handled suppresses the RightTapped that follows.
                _hidePressHandle = _renderedHandle;
                return;
            }
            if (!props.IsLeftButtonPressed) return;
            if (IsWithin(e.OriginalSource as DependencyObject, _stopButton)
                || IsWithin(e.OriginalSource as DependencyObject, _redoButton)
                || IsWithin(e.OriginalSource as DependencyObject, _dismissButton)) return;
            if (!NativeInterop.GetCursorPos(out var cursor)) return;

            if (!_rootContainer.CapturePointer(e.Pointer)) return;
            _dragPointerActive = true;
            _dragPointerId = e.Pointer.PointerId;
            _dragMoved = false;
            _dragStartCursor = cursor;
            _dragStartWindow = AppWindow.Position;
            // LNC-11: the same press may turn out to be a CLICK on a message body. Capture the
            // rendered handle now (pointer-DOWN, like every other pill affordance); the release
            // decides drag vs click by whether the threshold was crossed.
            _messagePressHandle = _renderedHandle;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MiniRecorder drag start failed");
        }
    }

    private void OnRootPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (!_dragPointerActive || e.Pointer.PointerId != _dragPointerId) return;
            // Hidden mid-drag (dismiss timer / pipeline): stop fighting the hide's
            // park immediately; FinishDrag re-parks on release.
            if (_isOffScreen) return;
            if (!NativeInterop.GetCursorPos(out var cursor)) return;

            var dx = cursor.X - _dragStartCursor.X;
            var dy = cursor.Y - _dragStartCursor.Y;
            if (!_dragMoved)
            {
                // System drag threshold: a press that wobbles a couple of pixels is a
                // click on the background, not a drag. SM_C{X,Y}DRAG are the FULL
                // dimensions of the drag rectangle centered on the press point, so the
                // per-axis half-extent is metric/2 (Codex round 1).
                if (Math.Abs(dx) < Math.Max(2, NativeInterop.GetSystemMetrics(NativeInterop.SM_CXDRAG) / 2)
                    && Math.Abs(dy) < Math.Max(2, NativeInterop.GetSystemMetrics(NativeInterop.SM_CYDRAG) / 2))
                    return;
                _dragMoved = true;
                // A pending DPI reconciliation's placement is now stale — don't let
                // it snap the pill back (see ApplyDpiReconciliation).
                _draggedSinceDpiSyncArmed = true;
            }

            // Physical-pixel deltas from GetCursorPos sidestep every DIP/scale
            // conversion, including mid-drag monitor crossings.
            AppWindow.Move(new Windows.Graphics.PointInt32(
                _dragStartWindow.X + dx, _dragStartWindow.Y + dy));
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MiniRecorder drag move failed");
        }
    }

    private void OnRootPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragPointerActive || e.Pointer.PointerId != _dragPointerId) return;
        e.Handled = _dragMoved;
        // LNC-11: a release that never crossed the drag threshold is a CLICK on the pill body.
        // Read both facts before FinishDrag resets them; the capture is consumed here whatever the
        // outcome, so a later capture-lost cannot emit it a second time.
        var isClick = !_dragMoved;
        var clickHandle = _messagePressHandle;
        _messagePressHandle = PillHandle.None;
        // Releasing capture raises PointerCaptureLost, which finishes the drag;
        // FinishDrag is idempotent so the direct call below is a safety net for
        // the event not firing (observed platform variance).
        try { _rootContainer.ReleasePointerCaptures(); }
        catch (Exception ex) { Logger.Debug(ex, "ReleasePointerCaptures failed"); }
        FinishDrag();
        if (isClick && !clickHandle.IsNone)
            MessageActionRequested?.Invoke(clickHandle);
    }

    private void OnRootPointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragPointerActive || e.Pointer.PointerId != _dragPointerId) return;
        // A lost capture is not a click — the pointer never released on the pill (hide, close,
        // a system gesture took it). Drop the message capture with the drag.
        _messagePressHandle = PillHandle.None;
        FinishDrag();
    }

    /// <summary>
    /// Right-click hide (2026-07-30): raise <see cref="HideRequested"/> with the handle captured at
    /// pointer-DOWN, CONSUMING it so one press can only ever hide once. A None handle means this
    /// RightTapped had no admitted right-press behind it (mid-drag press, or a synthesized tap) — inert.
    /// App validates the handle against the controller and applies the hide; the window never decides
    /// its own visibility.
    /// </summary>
    private void OnRootRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var handle = _hidePressHandle;
        _hidePressHandle = PillHandle.None;
        if (handle.IsNone) return;
        e.Handled = true;
        HideRequested?.Invoke(handle);
    }

    /// <summary>
    /// Cancel an active drag WITHOUT persisting — used by the hide/replacement/close
    /// paths, where a PointerCaptureLost is not guaranteed to be delivered and a stuck
    /// <see cref="_dragPointerActive"/> would silently ignore every future background
    /// press (Codex round 1).
    /// </summary>
    private void CancelActiveDrag()
    {
        if (!_dragPointerActive) return;
        _dragPointerActive = false;
        _dragMoved = false;
        _messagePressHandle = PillHandle.None; // LNC-11: a cancelled press is not a click
        try { _rootContainer.ReleasePointerCaptures(); }
        catch (Exception ex) { Logger.Debug(ex, "ReleasePointerCaptures during drag cancel failed"); }
    }

    private void FinishDrag()
    {
        if (!_dragPointerActive) return;
        _dragPointerActive = false;
        if (!_dragMoved) return;
        _dragMoved = false;
        try
        {
            if (_isOffScreen)
            {
                // The pill was hidden while the user was mid-drag; our manual moves may
                // have fought the hide's park before the _isOffScreen early-out kicked
                // in. Re-park so the logical hidden state matches reality, and don't
                // persist a position the user couldn't see land.
                Logger.Information("MiniRecorder hidden mid-drag — re-parking, position not saved");
                HideWindow();
                return;
            }
            PersistUserPosition(WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MiniRecorder drag finish failed");
        }
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject container)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, container)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// Persist the dragged position as fractions of the CURRENT monitor's work-area
    /// span (see <see cref="MiniRecorderUserPlacement"/>) — monitor- and DPI-independent,
    /// so the spot carries over to whichever monitor the pill shows on next — and snap
    /// the live window fully back inside the work area if the drop left it hanging out.
    /// </summary>
    private void PersistUserPosition(IntPtr hwnd)
    {
        if (_settings == null) return;
        var monitor = NativeInterop.MonitorFromWindow(hwnd, NativeInterop.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;
        var mi = new NativeInterop.MONITORINFO
        {
            cbSize = global::System.Runtime.InteropServices.Marshal.SizeOf<NativeInterop.MONITORINFO>()
        };
        if (!NativeInterop.GetMonitorInfo(monitor, ref mi)) return;

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        var (fx, fy) = MiniRecorderUserPlacement.ComputeFraction(
            pos.X, pos.Y,
            mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom,
            size.Width, size.Height);
        _settings.SetDouble(AppDefaults.MiniRecorderPosFractionX, fx);
        _settings.SetDouble(AppDefaults.MiniRecorderPosFractionY, fy);
        Logger.Information("MiniRecorder position saved: fx={Fx:F3} fy={Fy:F3}", fx, fy);

        // Snap the LIVE window fully back into the work area — the native move loop
        // happily drops the pill partly outside it, which would leave the stop/redo
        // buttons unreachable until the next Show (Codex round 1). The saved fractions
        // above are already clamped; this makes the visible window agree with them.
        var (snapX, snapY) = MiniRecorderUserPlacement.ComputePosition(
            mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom,
            size.Width, size.Height, padding: 0, fx, fy);
        if (snapX != pos.X || snapY != pos.Y)
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(snapX, snapY, size.Width, size.Height));
            Logger.Information("MiniRecorder snapped into work area: ({FromX},{FromY}) -> ({ToX},{ToY})",
                pos.X, pos.Y, snapX, snapY);
        }
    }

    /// <summary>
    /// Read the persisted drag fractions; (null, null) when never dragged (or reset),
    /// which yields the default top-center placement. The NaN default is load-bearing:
    /// SettingsService returns the caller's default for absent AND malformed values, so
    /// a corrupt entry (e.g. a hand-edited "oops") reads as NaN and Sanitize degrades it
    /// to the default — GetDouble's usual 0.0 default would instead turn corruption into
    /// a valid top-left position (Codex round 1).
    /// </summary>
    private (double? Fx, double? Fy) ReadUserFractions()
    {
        if (_settings == null) return (null, null);
        return (
            MiniRecorderUserPlacement.Sanitize(
                _settings.GetDouble(AppDefaults.MiniRecorderPosFractionX, double.NaN)),
            MiniRecorderUserPlacement.Sanitize(
                _settings.GetDouble(AppDefaults.MiniRecorderPosFractionY, double.NaN)));
    }

    private void PositionWindow()
    {
        try
        {
            // Recreate path: caller supplied a target monitor. Park the new
            // window adjacent to it, off the work area, so the user never sees
            // the warm-up phase and nearest-monitor placement fallbacks point at
            // the intended monitor. (Historic ambition — WM_DPICHANGED-driven
            // XamlRoot pre-init from the park — is NOT realized; see the truth
            // note below.)
            if (_initialTarget is { } target)
            {
                _windowWidth = target.Width;
                _windowHeight = target.Height;
                // Park adjacent to the target monitor on a side whose NEAREST
                // monitor is still the target — a fixed "above the top edge"
                // park lands on the physically-above monitor in bottom-arranged
                // setups (observed 2026-07-05: parkY=1270 vs monitorRect.Top=1440
                // resolved to the 1.25-scale monitor above).
                //
                // TRUTH NOTE (2026-07-06 live evidence): a fully OFF-SCREEN park
                // does NOT re-assign the window's DPI on this Windows build —
                // XamlRoot.RasterizationScale stayed at the creation-time scale
                // on BOTH park sides, so the "WM_DPICHANGED pre-init" ambition
                // of this parking is not realized. The nearest-monitor-correct
                // park is still kept: it keeps MonitorFromWindow-based fallbacks
                // (TryResolveMonitorPlacement's primary chain) pointing at the
                // intended monitor, and the visible Show()'s reconciliation path
                // handles the scale correction regardless.
                var (parkX, parkY) = MiniRecorderParkPlacement.ChooseParkPosition(
                    target.MonitorRect,
                    target.Width,
                    target.Height,
                    gap: 50,
                    rect => NativeInterop.MonitorFromRect(ref rect, NativeInterop.MONITOR_DEFAULTTONEAREST),
                    target.Monitor);
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    parkX, parkY, _windowWidth, _windowHeight));
                // Stash the real show position — the ctor's cloak path parks THERE
                // (cloaked) so the fresh window's DPI association is real from birth.
                _initialShowX = target.X;
                _initialShowY = target.Y;
                Logger.Information(
                    "MiniRecorderWindow positioned at target monitor (recreate): parkX={X} parkY={Y} w={W} h={H} scale={Scale}",
                    parkX, parkY, _windowWidth, _windowHeight, target.Scale);
                return;
            }

            // Default (first launch, no recreate target) — use the primary display.
            var displayArea = DisplayArea.Primary;
            if (displayArea == null) return;

            var workArea = displayArea.WorkArea;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = Helpers.NativeInterop.GetDpiForWindow(hwnd);
            var scale = dpi / 96.0;

            _windowWidth = (int)(WindowWidthDips * scale);
            _windowHeight = (int)(WindowHeightDips * scale);
            int padding = (int)(20 * scale);

            var (fracX, fracY) = ReadUserFractions();
            var (x, y) = MiniRecorderUserPlacement.ComputePosition(
                workArea.X, workArea.Y, workArea.X + workArea.Width, workArea.Y + workArea.Height,
                _windowWidth, _windowHeight, padding, fracX, fracY);

            _initialShowX = x;
            _initialShowY = y;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, _windowWidth, _windowHeight));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to position MiniRecorderWindow");
        }
    }

    /// <summary>
    /// Apply/remove the DWM cloak and VERIFY via the DWMWA_CLOAKED read-back.
    /// Returns true only when the verified state matches the request: cloaking
    /// requires our APP bit set; UNcloaking requires the FULL mask to be zero
    /// (a SHELL/INHERITED bit — e.g. another virtual desktop — still means not
    /// visible, and reveal must not be recorded). `_isCloaked` mirrors the
    /// verified mask, never the intent. Never throws.
    /// </summary>
    private bool TryApplyCloak(bool cloak)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var value = cloak ? 1 : 0;
            var hr = NativeInterop.DwmSetWindowAttribute(
                hwnd, NativeInterop.DWMWA_CLOAK, ref value, sizeof(int));
            if (hr != 0)
            {
                Logger.Warning("DwmSetWindowAttribute(DWMWA_CLOAK, {Cloak}) failed: hr=0x{Hr:X8}", cloak, hr);
                return false;
            }

            // Fail-CLOSED verification: a failed read-back is "not verified",
            // NEVER "mask is zero" — _isCloaked is only updated on a verified
            // outcome so it always reflects a state we actually observed.
            if (!TryGetCloakedMask(hwnd, out var mask))
            {
                Logger.Warning("DWMWA_CLOAKED read-back failed after cloak={Cloak} — not verified", cloak);
                return false;
            }

            bool verified;
            if (cloak)
            {
                verified = MiniRecorderShowTransition.VerifiedAppCloak(true, mask);
                if (verified) _isCloaked = true;
            }
            else
            {
                verified = MiniRecorderShowTransition.VerifiedVisible(true, mask);
                if (verified) _isCloaked = false;
            }

            if (!verified)
            {
                Logger.Warning(
                    "Cloak verification failed: requested cloak={Cloak}, DWMWA_CLOAKED mask=0x{Mask:X}", cloak, mask);
            }
            return verified;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "TryApplyCloak({Cloak}) threw", cloak);
            return false;
        }
    }

    private static bool TryGetCloakedMask(IntPtr hwnd, out uint mask)
    {
        try
        {
            return NativeInterop.DwmGetWindowAttribute(
                hwnd, NativeInterop.DWMWA_CLOAKED, out mask, sizeof(uint)) == 0;
        }
        catch (Exception)
        {
            mask = 0;
            return false;
        }
    }

    /// <summary>
    /// The ONE terminal reveal for every show path (fast paths, reconciliation,
    /// 2-frame wait, timeout force). Centralizes the Visible-state rule: the
    /// latency probe fires and the reveal is recorded ONLY when the window is
    /// verified FULLY uncloaked (DWMWA_CLOAKED == 0 — a SHELL bit from e.g.
    /// another virtual desktop blocks the probe on the legacy path too, keeping
    /// the numbers honest). A failed uncloak escalates to an "uncloak-failure"
    /// recreate; if the gate rejects that, the window returns to Parked
    /// (cloaked-hidden) — a missing pill for one recording beats a stale ghost,
    /// and the next Show retries from scratch.
    /// </summary>
    private void RevealAtGeometry(
        IntPtr hwnd, int x, int y, MonitorPlacement placement, bool hasPlacement, string probeSite)
    {
        if (_cloakAvailable || _isCloaked)
        {
            if (!TryApplyCloak(false))
            {
                // The uncloak SET may have succeeded with only the read-back
                // failing — presentation state is UNKNOWN (possibly visible at
                // the target coords). Before abandoning the reveal, actively
                // make the window non-presentable by means that do NOT depend
                // on cloak state: best-effort re-cloak, then the legacy
                // fail-safe (WS_VISIBLE off + off-screen move). `_isCloaked`
                // must not be trusted as proof of invisibility after this —
                // clear it so CloseForReplacement won't skip parking on stale
                // state. The next Show recovers: its reveal re-sets
                // SWP_SHOWWINDOW and re-verifies.
                MakeNonPresentableAfterUnknownCloakState(hwnd);

                if (hasPlacement && CrossDpiRecreateRequested?.Invoke(placement, "uncloak-failure") == true)
                {
                    Logger.Warning("Uncloak failed — recreate accepted; abandoning this reveal");
                    return;
                }
                Logger.Error(
                    "Uncloak failed and recreate unavailable — returning to Parked (fail-safe hidden); next Show retries");
                _isOffScreen = true;
                return;
            }
        }

        NativeInterop.SetWindowPos(
            hwnd, NativeInterop.HWND_TOPMOST,
            x, y, _windowWidth, _windowHeight,
            NativeInterop.SWP_SHOWWINDOW | NativeInterop.SWP_NOACTIVATE);

        // Central honesty gate for the probe, fail-CLOSED: the mask read must
        // succeed AND be zero. Any nonzero bit (whoever set it) or a failed
        // read means we cannot claim the user saw the pill.
        var maskRead = TryGetCloakedMask(hwnd, out var cloakMask);
        if (MiniRecorderShowTransition.VerifiedVisible(maskRead, cloakMask))
        {
            MiniRecorderShowLatencyProbe.Instance.MarkVisible(probeSite);
        }
        else
        {
            Logger.Warning(
                "Reveal completed but visibility not verified (readOk={ReadOk}, mask=0x{Mask:X}) — probe suppressed",
                maskRead, cloakMask);
        }
    }

    /// <summary>
    /// Fail-safe for an UNKNOWN presentation state (a mutating cloak call whose
    /// verification failed): best-effort re-cloak, then force the window
    /// non-presentable via primitives that work regardless of cloak state —
    /// WS_VISIBLE off + off-screen move. Clears <c>_isCloaked</c> because after
    /// this point it must not be used as proof of invisibility. Recoverable:
    /// every reveal path re-sets SWP_SHOWWINDOW and re-verifies.
    /// </summary>
    private void MakeNonPresentableAfterUnknownCloakState(IntPtr hwnd)
    {
        try
        {
            var one = 1;
            NativeInterop.DwmSetWindowAttribute(hwnd, NativeInterop.DWMWA_CLOAK, ref one, sizeof(int));

            NativeInterop.SetWindowPos(
                hwnd, IntPtr.Zero,
                0, 0, 0, 0,
                NativeInterop.SWP_HIDEWINDOW
                | NativeInterop.SWP_NOMOVE | NativeInterop.SWP_NOSIZE
                | NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
            NativeInterop.SetWindowPos(
                hwnd, IntPtr.Zero,
                -10000, -10000, _windowWidth, _windowHeight,
                NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Fail-safe hide after unknown cloak state failed");
        }
        _isCloaked = false;
    }

    /// <summary>
    /// One-way runtime fallback after a cloak(true) failure. NOTE: "failed" is
    /// not always "verifiably uncloaked" — the SET can succeed with only the
    /// read-back failing — so this clears DWMWA_CLOAK and then VERIFIES the
    /// mask; an unverifiable/still-cloaked outcome routes through the same
    /// unknown-state fail-safe as uncloak failures instead of trusting
    /// SWP_SHOWWINDOW to reveal a possibly-cloaked window later. Parks
    /// off-screen when currently hidden so the legacy Parked-state invariant
    /// (at -10000) is restored.
    /// </summary>
    /// <param name="wasOffScreen">The call site's PRE-FLIP hidden/parked state (F24): Show()
    /// flips <c>_isOffScreen</c> to false near its top, so reading the field here would skip
    /// the -10000 re-park for a pill that is NOT actually presentable yet — pass the value
    /// captured before any mutation instead. Decision rule pinned in
    /// <see cref="MiniRecorderShowTransition.ShouldReParkAfterCloakFailure"/>.</param>
    private void DisableCloakAfterRuntimeFailure(string where, bool wasOffScreen)
    {
        _cloakAvailable = false;
        Logger.Warning("Cloak parking disabled after runtime failure at {Where} — legacy off-screen parking from here on", where);
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);

            var zero = 0;
            NativeInterop.DwmSetWindowAttribute(hwnd, NativeInterop.DWMWA_CLOAK, ref zero, sizeof(int));

            var readOk = TryGetCloakedMask(hwnd, out var mask);
            if (readOk && (mask & NativeInterop.DWM_CLOAKED_APP) == 0)
            {
                _isCloaked = false;
            }
            else
            {
                Logger.Warning(
                    "Cloak clear could not be verified at {Where} (readOk={ReadOk}, mask=0x{Mask:X}) — applying unknown-state fail-safe",
                    where, readOk, mask);
                MakeNonPresentableAfterUnknownCloakState(hwnd);
                return; // fail-safe already parked off-screen
            }

            if (MiniRecorderShowTransition.ShouldReParkAfterCloakFailure(wasOffScreen))
            {
                NativeInterop.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    -10000, -10000, _windowWidth, _windowHeight,
                    NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Legacy re-park after cloak failure failed");
        }
    }

    /// <summary>
    /// IMiniRecorderRenderer: the single render entry point (controller refactor 2026-07-21). Records
    /// the rendered handle (for the buttons' pointer-down capture) and dispatches to the per-content
    /// render helper; null hides. Owns the Show-before-update ordering (each helper calls Show()); the
    /// controller owns all timing/dismissal, so no semantic timers are armed here. Inline FIFO
    /// discipline is preserved (each helper marshals via RunOnUiThread, inline when on-thread).
    /// </summary>
    public void Render(PillPresentation? presentation)
    {
        RunOnUiThread(() =>
        {
            if (ShouldSkipUiWork()) return;
            _renderedHandle = presentation?.Handle ?? PillHandle.None;
            _lastPresentation = presentation; // theme-change replay source (null = hidden)
            // LNC-11: the click cue is decided HERE, for every branch, so a message's tooltip and
            // underline can never outlive the message on the pipeline pill that replaces it.
            ApplyMessageActionCue(presentation?.MessageAction ?? PillMessageAction.None);
            switch (presentation?.Content)
            {
                case null:
                    HideWindow();
                    break;
                case PillContent.Message m:
                    RenderMessage(m.Text, m.Tone, presentation.CanDismiss);
                    break;
                case PillContent.Pipeline p:
                    UpdateState(p.State, p.StartedAtUtc, p.Notice);
                    break;
                case PillContent.Download d:
                    RenderDownload(d.Fraction, d.ModelName);
                    break;
                case PillContent.ImageJob j:
                    RenderImageJob(j.Notice, j.Progress);
                    break;
                case PillContent.Affordance a:
                    RenderAffordance(a.Kind, a.Text, a.Tone, presentation.CanDismiss);
                    break;
            }
        });
    }

    // ERR-PERSIST controller refactor: driven by Render(Pipeline). The controller only ever passes a
    // non-Idle state (Idle → ClearPipeline → a different surface / hidden), so there is no stale-Idle
    // clobber to guard and no per-window semantic timers here.
    private void UpdateState(RecordingState state, DateTime? recordingStartTimeUtc, string? notice = null)
    {
        RunOnUiThread(() =>
        {
            if (ShouldSkipUiWork()) return;

            // Reset any error/redo display state before applying the pipeline state.
            _stopButton.Visibility = Visibility.Visible;
            _redoButton.Visibility = Visibility.Collapsed;
            _dismissButton.Visibility = Visibility.Collapsed;
            StatusText.Foreground = AppTheme.Brush(AppTheme.TextPrimary);

            switch (state)
            {
                case RecordingState.Starting:
                    // Starting is NOT instant. Measured 2026-08-02, hotkey→capture-live ran
                    // 162-4002 ms (see the "Hotkey→capture live" log line) — dominated by WASAPI
                    // start-up under system contention. This comment previously claimed
                    // "Recording fires ~50ms later", and on that assumption the state collapsed
                    // the status text, the timer AND the waveform, so a multi-second Starting
                    // rendered as a bare red dot on a 500-DIP pill. That is what the owner
                    // reported as "the mini recorder takes a couple of seconds to appear": the
                    // window was up in 4 ms, but nothing on it said so until audio flowed.
                    //
                    // We can't just leave the current visuals alone: Starting can fire from redo
                    // or error pill states (no HideWindow in between), so the source state is not
                    // guaranteed to be ResetToStartingVisuals. Calling it explicitly here ensures
                    // stale "Done"/error text and stale green/red-error dot colors are
                    // cleared — and it is also what collapses StatusText, so this state's own
                    // text must be applied AFTER it.
                    _pulseTimer.Stop();
                    _elapsedTimer.Stop();
                    // The recording phase is the ONE accent-matching exception (owner
                    // 2026-07-31): its chrome stays neutral, so un-tint a stop icon a
                    // prior accent-matched state may have colored.
                    SetStopAccent(AppTheme.TextPrimary);
                    ResetToStartingVisuals();
                    // Waveform and timer deliberately stay collapsed — both would imply audio
                    // that is not flowing yet. ResetToStartingVisuals collapses them and the
                    // AUD-26 block after this switch is what KEEPS them collapsed for Starting.
                    // The foreground stays neutral (ResetToStartingVisuals set TextPrimary)
                    // rather than taking an accent, per the same rule above.
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Starting...";
                    break;

                case RecordingState.Recording:
                    // Waveform + timer visibility is decided once for state AND notice together,
                    // after this switch (AUD-26) — see MiniRecorderPipelineSignals.
                    StatusText.Visibility = Visibility.Collapsed;
                    _targetLabelAllowed = true;
                    ApplyTargetAppLabel();
                    SetStopAccent(AppTheme.TextPrimary); // recording phase stays neutral
                    RecordingDot.Fill = AppTheme.Brush(AppTheme.AccentRed);
                    RecordingDot.Opacity = 1.0;
                    _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, 255, 69, 58));
                    _dotGlow.Visibility = Visibility.Visible;
                    _pillBorder.BorderBrush = AppTheme.Brush(
                        ColorHelper.FromArgb(50, 255, 69, 58));
                    // Start pulse and timer
                    _recordingStartTime = recordingStartTimeUtc ?? DateTime.UtcNow;
                    OnElapsedTick(null, EventArgs.Empty);
                    _pulseTimer.Start();
                    _elapsedTimer.Start();
                    break;

                case RecordingState.Transcribing:
                    _pulseTimer.Stop();
                    _elapsedTimer.Stop();
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Transcribing...";
                    // Text + stop icon match the pill accent in every non-recording state
                    // (owner 2026-07-31 — the recording phase alone stays neutral).
                    StatusText.Foreground = AppTheme.Brush(AppTheme.AccentBlue);
                    SetStopAccent(AppTheme.AccentBlue);
                    _targetLabelAllowed = true;
                    ApplyTargetAppLabel();
                    RecordingDot.Fill = AppTheme.Brush(AppTheme.AccentBlue);
                    RecordingDot.Opacity = 1.0;
                    _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, 0, 122, 255));
                    _dotGlow.Visibility = Visibility.Visible;
                    _pillBorder.BorderBrush = AppTheme.Brush(
                        ColorHelper.FromArgb(50, 0, 122, 255));
                    break;

                case RecordingState.Enhancing:
                    _pulseTimer.Stop();
                    _elapsedTimer.Stop();
                    StatusText.Visibility = Visibility.Visible;
                    // Enhancing shows ONE thing for its whole duration (owner, 2026-08-11). A 10 s
                    // "Stop button to skip" hint used to replace this text: it spent the state
                    // indicator to point at a button already on screen, and never said the part
                    // that isn't obvious — that stopping HERE skips the enhancement and still
                    // pastes the raw transcript, rather than cancelling the dictation. Two
                    // rewordings (2026-07-08) failed to make it self-explaining; removed instead.
                    // Stop-during-Enhancing keeps skipping — only the hint is gone.
                    StatusText.Text = "Enhancing...";
                    StatusText.Foreground = AppTheme.Brush(AppTheme.AccentGreen);
                    SetStopAccent(AppTheme.AccentGreen);
                    _targetLabelAllowed = true;
                    ApplyTargetAppLabel();
                    RecordingDot.Fill = AppTheme.Brush(AppTheme.AccentGreen);
                    RecordingDot.Opacity = 1.0;
                    _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, 48, 209, 88));
                    _dotGlow.Visibility = Visibility.Visible;
                    _pillBorder.BorderBrush = AppTheme.Brush(
                        ColorHelper.FromArgb(50, 48, 209, 88));
                    break;

                default:
                    _pulseTimer.Stop();
                    _elapsedTimer.Stop();
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Ready";
                    StatusText.Foreground = AppTheme.Brush(AppTheme.SubtleText);
                    SetStopAccent(AppTheme.SubtleText);
                    _targetLabelAllowed = false;
                    ApplyTargetAppLabel();
                    RecordingDot.Fill = AppTheme.Brush(AppTheme.SubtleText);
                    RecordingDot.Opacity = 1.0;
                    _dotGlow.Visibility = Visibility.Collapsed;
                    _pillBorder.BorderBrush = AppTheme.Brush(AppTheme.IsDark
                        ? ColorHelper.FromArgb(80, 42, 42, 46)
                        : ColorHelper.FromArgb(80, 180, 180, 185));
                    break;
            }

            // AUD-1: a transient pipeline-scoped notice (mic-fallback) overrides the state's status
            // line + accents with the amber Warning treatment (same whole-pill-amber rule as the
            // image-job notice — mixed accent signals read ambiguously, owner 2026-07-17). Applied
            // AFTER the state switch so expiry re-renders (Notice=null) restore the plain state
            // visuals with no extra reset path.
            if (notice != null)
            {
                var amber = AppTheme.AccentAmber;
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = notice;
                StatusText.Foreground = AppTheme.Brush(amber);
                SetStopAccent(amber);
                RecordingDot.Fill = AppTheme.Brush(amber);
                RecordingDot.Opacity = 1.0;
                _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, amber.R, amber.G, amber.B));
                _dotGlow.Visibility = Visibility.Visible;
                _pillBorder.BorderBrush = AppTheme.Brush(ColorHelper.FromArgb(80, amber.R, amber.G, amber.B));
            }

            // AUD-26: the waveform and the elapsed timer are decided HERE — for the state and the
            // notice TOGETHER — instead of by each arm above, because the status text is a
            // full-span overlay and the waveform is stretched underneath it. Split across two code
            // paths, that geometry produced the owner's 2026-08-25 report: the amber notice rendered
            // on top of live, animating bars and the message was lost in them. The waveform yields
            // for the notice's 8 s and the timer (its own column, kept clear by
            // UpdateStatusPlacement) carries the liveness instead. Rule + rationale:
            // MiniRecorderPipelineSignals. Nothing here needs an expiry path — the state arm plus
            // this block reproduce the plain look on the Notice=null re-render.
            var hasNotice = notice != null;
            var showWaveform = Helpers.MiniRecorderPipelineSignals.ShowsWaveform(state, hasNotice);
            Visualizer.IsActive = showWaveform;
            // AUD-31 (Codex diff r1): REHYDRATE on every activation. `StartAnimation` resets the
            // control's level to the silence sentinel so a stale one can never be rendered, and
            // that reset is correct — but it leaves the control waiting for the next AudioLevel
            // change event, and this block re-enables the waveform in cases that need not produce
            // one: a pipeline notice expiring mid-recording, a tray hide/restore, a DPI recreate.
            // The VM's level is never stale (the meter timer writes it unconditionally; it is only
            // the BRIDGE to the control that `_isOffScreen` gates), so pushing it here closes the
            // window without depending on an event arriving.
            if (showWaveform)
                Visualizer.AudioLevel = _viewModel.AudioLevel;
            Visualizer.Visibility = showWaveform ? Visibility.Visible : Visibility.Collapsed;
            _timerText.Visibility =
                Helpers.MiniRecorderPipelineSignals.ShowsElapsedTimer(state, hasNotice)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            // Un-park + reveal AFTER the content is set (mirrors RenderMessage/Download/ImageJob/
            // Affordance, which each own their Show()). Replaces the old ApplyMiniRecorderState's
            // Show()-before-UpdateState ordering that the removed `if (_isOffScreen) return` relied on —
            // without this, a pipeline render on a parked/hidden window never became visible (Codex
            // diff review finding 1). The controller only sends a Pipeline render when the pill should
            // show, and Show() is idempotent on an already-visible window.
            Show();
        });
    }

    /// <summary>
    /// PILL-1: apply the paste-target label from the CURRENT view-model value
    /// (pull-based — a recreated window rehydrates without a PropertyChanged event)
    /// gated by the state flag. Must run on the UI thread. Called from the
    /// constructor, every UpdateState arm, ResetToStartingVisuals, and the
    /// error/download/redo states (which all disallow the label).
    /// </summary>
    private void ApplyTargetAppLabel()
    {
        var label = Helpers.MiniRecorderTargetLabel.Compose(_viewModel.PasteTargetAppName);
        _targetAppText.Text = label ?? "";
        _targetAppText.Visibility = _targetLabelAllowed && label != null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Re-decides where the status text sits after every layout pass: centered on the
    /// pill's midpoint when it fits between the side zones (pure rule in
    /// <see cref="Helpers.MiniRecorderStatusPlacement"/>), else centered between them —
    /// zone-sized margins reproduce the pre-2026-07-09 star-column geometry exactly, so
    /// the fallback can never overlap the label or the buttons. The margin write is
    /// change-guarded because this runs from LayoutUpdated.
    /// </summary>
    private void UpdateStatusPlacement()
    {
        static double Zone(FrameworkElement el) => el.Visibility == Visibility.Visible
            ? el.Margin.Left + el.ActualWidth + el.Margin.Right
            : 0;

        var left = Zone(_dotContainer) + Zone(_timerText) + Zone(_targetAppText);
        // ERR-PERSIST: the corner × occupies the rightmost column on a persistent pill (message
        // error AND Error-toned redo/retry) — count it, or a near-cap message's tail renders
        // under the opaque × circle (both zone-mode branches would otherwise give it 0 clearance).
        var right = Zone(_stopButton) + Zone(_redoButton) + Zone(_dismissButton);
        var mode = Helpers.MiniRecorderStatusPlacement.Decide(
            _innerGrid.ActualWidth, left, right, _statusMeasure.ActualWidth);

        var (marginLeft, marginRight) =
            mode == Helpers.MiniRecorderStatusPlacement.Mode.CenteredOnPill
                ? (0d, 0d)
                : (left, right);
        if (StatusText.Margin.Left != marginLeft || StatusText.Margin.Right != marginRight)
            StatusText.Margin = new Thickness(marginLeft, 0, marginRight, 0);

        // PRM-6 static two lines: gate MaxLines on whether TWO RENDERED lines of THIS text fit
        // the pill's inner height. The probe measures the truth (real fallback fonts at the
        // current text scale) once it has the box width the visible text gets.
        //
        // Two deliberate fail-safes from the plan review (Codex round 3, 2026-07-26):
        // - A non-finite/non-positive box width — the side zones CAN exceed the measured grid
        //   width mid-layout, a case MiniRecorderStatusPlacementTests models — is never assigned
        //   (WinUI rejects a negative Width) and gates to one line.
        // - ActualHeight lags a Width change, so the pass that (re)sizes the probe does NOT read
        //   it — it holds one line and lets the next LayoutUpdated pass consume the settled
        //   measurement. Same change-guarded convergence as the margin write above; the lag
        //   direction is safe (one line until proven to fit).
        var probeBoxWidth = _innerGrid.ActualWidth - marginLeft - marginRight;
        int statusLines;
        if (!double.IsFinite(probeBoxWidth) || probeBoxWidth <= 0)
        {
            statusLines = 1;
        }
        else if (_statusHeightProbe.Width != probeBoxWidth)
        {
            _statusHeightProbe.Width = probeBoxWidth;
            statusLines = 1;
        }
        else
        {
            statusLines = Helpers.MiniRecorderStatusLines.Decide(
                _innerGrid.ActualHeight, _statusHeightProbe.ActualHeight);
        }
        if (StatusText.MaxLines != statusLines)
            StatusText.MaxLines = statusLines;
    }

    private static Windows.UI.Color PillBgColor() => AppTheme.IsDark
        ? ColorHelper.FromArgb(240, 22, 22, 26)
        : ColorHelper.FromArgb(240, 255, 255, 255);

    private static SolidColorBrush StopNormalBg() => AppTheme.IsDark
        ? AppTheme.Brush(ColorHelper.FromArgb(40, 255, 255, 255))
        : AppTheme.Brush(ColorHelper.FromArgb(40, 0, 0, 0));

    /// <summary>
    /// Reset all visual elements to the "recording-ready" state (red dot, no
    /// text, no waveform). Called during construction (pre-renders for first
    /// show) and in HideWindow (so the off-screen window is always ready for
    /// the next Show).
    ///
    /// <para>This is the BARE reset, not the whole Starting look: the Starting case in
    /// <c>UpdateState</c> re-shows the status line immediately afterwards, because Recording
    /// does NOT follow within ~50 ms as this comment used to claim — measured 162-4002 ms on
    /// 2026-08-02 — and a multi-second bare red dot reads as "nothing happened".</para>
    /// </summary>
    private void ResetToStartingVisuals()
    {
        // A fresh recording is never a persistent × surface (the controller owns all timing/dismissal).
        _dismissButton.Visibility = Visibility.Collapsed;
        _redoButton.Visibility = Visibility.Collapsed;
        _stopButton.Visibility = Visibility.Visible;
        _targetLabelAllowed = false;
        ApplyTargetAppLabel();
        Visualizer.IsActive = false;
        Visualizer.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        StatusText.Foreground = AppTheme.Brush(AppTheme.TextPrimary);
        _timerText.Visibility = Visibility.Collapsed;
        _timerText.Text = "0:00";
        RecordingDot.Fill = AppTheme.Brush(AppTheme.AccentRed);
        RecordingDot.Opacity = 1.0;
        _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, 255, 69, 58));
        _dotGlow.Visibility = Visibility.Visible;
        _pillBorder.BorderBrush = AppTheme.Brush(
            ColorHelper.FromArgb(50, 255, 69, 58));
    }

    /// <summary>Sets the stop icon's color AND records it as the current semantic accent, so the
    /// hover-exit handler and theme replay restore the right tint (Codex diff review P1). Every
    /// render path that shows the stop button routes its icon color through here.</summary>
    private void SetStopAccent(Windows.UI.Color accent)
    {
        _stopButtonAccent = accent;
        _stopIcon.Foreground = AppTheme.Brush(accent);
    }

    private void ApplyTheme()
    {
        var pillBg = AppTheme.Brush(PillBgColor());
        var pillBorderColor = AppTheme.IsDark
            ? ColorHelper.FromArgb(80, 42, 42, 46)
            : ColorHelper.FromArgb(80, 180, 180, 185);

        // Root grid matches pill bg so the margin around the pill (Margin on pillBorder)
        // looks like a uniform frame of pill color rather than a differently-colored
        // window clear, which would vary by theme.
        _rootContainer.Background = pillBg;
        _pillBorder.Background = pillBg;
        _pillBorder.BorderBrush = AppTheme.Brush(pillBorderColor);

        // Stop button and icon
        _stopButton.Background = StopNormalBg();
        _stopIcon.Foreground = AppTheme.Brush(AppTheme.TextPrimary);

        // Redo button and icon
        _redoButton.Background = StopNormalBg();
        _redoIcon.Foreground = AppTheme.Brush(AppTheme.TextPrimary);

        // Text colors
        StatusText.Foreground = AppTheme.Brush(AppTheme.TextPrimary);
        _timerText.Foreground = AppTheme.Brush(AppTheme.TextSecondary);
        // Target label is always plain neutral (the PILL-2 editability tint was retired
        // 2026-07-09 — colors over-promised; see MainViewModel.PasteTargetEditableConfirmed).
        _targetAppText.Foreground = AppTheme.Brush(AppTheme.SubtleText);
        // Visualizer bar colors
        Visualizer.UpdateTheme();
    }

    /// <summary>
    /// LNC-11: the visible cue that a message body is clickable — a tooltip naming the destination
    /// and an underline on the text — or nothing. Called from <see cref="Render"/> for EVERY
    /// presentation (null included), so the cue is reset by whatever replaces the message.
    /// </summary>
    private void ApplyMessageActionCue(PillMessageAction action)
    {
        var tooltip = action switch
        {
            PillMessageAction.OpenLicensePage => "Click to open the License page",
            _ => null,
        };
        ToolTipService.SetToolTip(_pillBorder, tooltip);
        StatusText.TextDecorations = tooltip is null
            ? Windows.UI.Text.TextDecorations.None
            : Windows.UI.Text.TextDecorations.Underline;
    }

    /// <summary>
    /// Render a MESSAGE pill (Render → PillContent.Message): red for a real Error, amber for a
    /// Warning. <paramref name="showDismiss"/> (⇔ the presentation is UntilDismissed / persistent)
    /// shows the corner ×. The controller owns ALL timing — the window arms no dismiss timer.
    /// The message's click action (LNC-11) is not a parameter here on purpose: its cue is applied
    /// by <see cref="Render"/> for every presentation, so no per-helper reset can be forgotten.
    /// </summary>
    private void RenderMessage(string message, MiniRecorderTone tone, bool showDismiss)
    {
        RunOnUiThread(() =>
        {
            if (ShouldSkipUiWork()) return;

            _pulseTimer.Stop();
            _elapsedTimer.Stop();

            Visualizer.IsActive = false;
            Visualizer.Visibility = Visibility.Collapsed;
            _timerText.Visibility = Visibility.Collapsed;
            _targetLabelAllowed = false;
            ApplyTargetAppLabel();

            _redoButton.Visibility = Visibility.Collapsed;
            _stopButton.Visibility = Visibility.Collapsed; // nothing to cancel on a message pill

            var accent = AppTheme.AccentFor(tone);
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = message;
            StatusText.Foreground = AppTheme.Brush(accent);
            RecordingDot.Fill = AppTheme.Brush(accent);
            RecordingDot.Opacity = 1.0;
            _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, accent.R, accent.G, accent.B));
            _dotGlow.Visibility = Visibility.Visible;
            _pillBorder.BorderBrush = AppTheme.Brush(
                ColorHelper.FromArgb(80, accent.R, accent.G, accent.B));

            // Corner × only for a persistent (Error) message; tone-tinted like the pill.
            _dismissButton.Visibility = showDismiss ? Visibility.Visible : Visibility.Collapsed;
            if (showDismiss)
                _dismissIcon.Foreground = AppTheme.Brush(accent);

            Show();
        });
    }

    /// <summary>Render a DOWNLOAD-progress pill (Render → PillContent.Download). Owned by the live
    /// download; stop button visible (cancels), no ×. Owns its Show().</summary>
    private void RenderDownload(double progressFraction, string modelName)
    {
        RunOnUiThread(() =>
        {
            if (ShouldSkipUiWork()) return;

            _pulseTimer.Stop();
            _elapsedTimer.Stop();
            Visualizer.IsActive = false;
            Visualizer.Visibility = Visibility.Collapsed;
            _timerText.Visibility = Visibility.Collapsed;
            _targetLabelAllowed = false;
            ApplyTargetAppLabel();

            StatusText.Visibility = Visibility.Visible;
            var pct = (int)(progressFraction * 100);
            StatusText.Text = $"Downloading model... {pct}%";
            // Accent-matched text + stop icon, same rule as the pipeline states.
            StatusText.Foreground = AppTheme.Brush(AppTheme.AccentBlue);
            SetStopAccent(AppTheme.AccentBlue);

            RecordingDot.Fill = AppTheme.Brush(AppTheme.AccentBlue);
            RecordingDot.Opacity = 1.0;
            _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, 0, 122, 255));
            _dotGlow.Visibility = Visibility.Visible;
            _pillBorder.BorderBrush = AppTheme.Brush(
                ColorHelper.FromArgb(50, 0, 122, 255));

            _redoButton.Visibility = Visibility.Collapsed;
            _dismissButton.Visibility = Visibility.Collapsed;
            _stopButton.Visibility = Visibility.Visible; // user can cancel
            Show();
        });
    }

    /// <summary>
    /// IMG-BG: render the background image-generation pill (Render → PillContent.ImageJob) —
    /// "Generating image…" (or an amber job-scoped notice) with a working stop button (cancels the
    /// job). Owned by the live job; no ×. Owns its Show().
    /// </summary>
    private void RenderImageJob(string? notice, string? progress)
    {
        RunOnUiThread(() =>
        {
            if (ShouldSkipUiWork()) return;

            _pulseTimer.Stop();
            _elapsedTimer.Stop();

            Visualizer.IsActive = false;
            Visualizer.Visibility = Visibility.Collapsed;
            _timerText.Visibility = Visibility.Collapsed;
            _targetLabelAllowed = false;
            ApplyTargetAppLabel();

            _redoButton.Visibility = Visibility.Collapsed;
            _dismissButton.Visibility = Visibility.Collapsed;

            StatusText.Visibility = Visibility.Visible;
            if (notice != null)
            {
                // Whole pill goes amber with the notice (owner request 2026-07-17): amber
                // text on blue chrome read as a mixed signal — match ShowError's Warning
                // treatment (dot/glow/border) so the pill state is unambiguous.
                var amber = AppTheme.AccentAmber;
                StatusText.Text = notice;
                StatusText.Foreground = AppTheme.Brush(amber);
                SetStopAccent(amber);
                RecordingDot.Fill = AppTheme.Brush(amber);
                _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, amber.R, amber.G, amber.B));
                _pillBorder.BorderBrush = AppTheme.Brush(ColorHelper.FromArgb(80, amber.R, amber.G, amber.B));
            }
            else
            {
                // Blue working dot — same family as Transcribing/Download. IMG-3: a batch
                // shows its position ("Generating image 2 of 4…"), same visual treatment.
                // Accent-matched text + stop icon, same rule as the pipeline states.
                StatusText.Text = progress ?? "Generating image...";
                StatusText.Foreground = AppTheme.Brush(AppTheme.AccentBlue);
                SetStopAccent(AppTheme.AccentBlue);
                RecordingDot.Fill = AppTheme.Brush(AppTheme.AccentBlue);
                _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, 0, 122, 255));
                _pillBorder.BorderBrush = AppTheme.Brush(ColorHelper.FromArgb(50, 0, 122, 255));
            }

            RecordingDot.Opacity = 1.0;
            _dotGlow.Visibility = Visibility.Visible;

            // Stop button stays visible — it cancels the job (owner: "working stop button
            // whenever the pipeline is idle").
            _stopButton.Visibility = Visibility.Visible;

            Show();
        });
    }

    private bool TryResolveMonitorPlacement(IntPtr hwnd, out MonitorPlacement placement)
    {
        var foreground = NativeInterop.GetForegroundWindow();
        var foregroundStatus = foreground == IntPtr.Zero
            ? "none"
            : foreground == hwnd
                ? "foreground=self"
                : "foreground";

        if (foreground != IntPtr.Zero && foreground != hwnd)
        {
            var foregroundMonitor = NativeInterop.MonitorFromWindow(
                foreground, NativeInterop.MONITOR_DEFAULTTONEAREST);
            if (TryBuildMonitorPlacement(
                    foregroundMonitor, "foreground", foregroundStatus, foreground, hwnd, out placement))
                return true;
        }

        if (NativeInterop.GetCursorPos(out var cursorPoint))
        {
            var cursorMonitor = NativeInterop.MonitorFromPoint(
                cursorPoint, NativeInterop.MONITOR_DEFAULTTONEAREST);
            if (TryBuildMonitorPlacement(
                    cursorMonitor, "cursor", foregroundStatus, foreground, hwnd, out placement))
                return true;
        }

        var primaryMonitor = NativeInterop.MonitorFromWindow(
            hwnd, NativeInterop.MONITOR_DEFAULTTOPRIMARY);
        return TryBuildMonitorPlacement(
            primaryMonitor, "primary", foregroundStatus, foreground, hwnd, out placement);
    }

    private bool TryBuildMonitorPlacement(
        IntPtr monitor,
        string source,
        string foregroundStatus,
        IntPtr foreground,
        IntPtr hwnd,
        out MonitorPlacement placement)
    {
        placement = default;

        if (monitor == IntPtr.Zero)
            return false;

        var mi = new NativeInterop.MONITORINFO
        {
            cbSize = global::System.Runtime.InteropServices.Marshal.SizeOf<NativeInterop.MONITORINFO>()
        };
        if (!NativeInterop.GetMonitorInfo(monitor, ref mi))
            return false;

        uint monitorDpi = 96;
        double scale = 1.0;
        if (NativeInterop.GetDpiForMonitor(
                monitor, NativeInterop.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            monitorDpi = dpiX;
            scale = dpiX / 96.0;
        }

        var workArea = mi.rcWork;
        var width = Math.Max(1, (int)(WindowWidthDips * scale));
        var height = Math.Max(1, (int)(WindowHeightDips * scale));
        var padding = (int)(20 * scale);

        var (fracX, fracY) = ReadUserFractions();
        var (x, y) = MiniRecorderUserPlacement.ComputePosition(
            workArea.Left, workArea.Top, workArea.Right, workArea.Bottom,
            width, height, padding, fracX, fracY);

        placement = new MonitorPlacement(
            monitor,
            source,
            foregroundStatus,
            foreground,
            mi.rcMonitor,
            workArea,
            monitorDpi,
            scale,
            x,
            y,
            width,
            height,
            padding,
            NativeInterop.GetDpiForWindow(hwnd));
        return true;
    }

    private void LogPlacementDiagnostics(string phase, IntPtr hwnd, MonitorPlacement placement)
    {
        try
        {
            // Privacy guard: placement diagnostics must stay numeric and geometry-only.
            // Never log window titles, process names, executable paths, headers,
            // request/response bodies, API keys, license keys, or raw transcription text.
            var xamlRoot = _rootContainer.XamlRoot;
            var xamlRootScale = xamlRoot?.RasterizationScale;
            var xamlRootWidth = xamlRoot?.Size.Width;
            var xamlRootHeight = xamlRoot?.Size.Height;
            var appWindowPosition = AppWindow.Position;
            var appWindowSize = AppWindow.Size;
            var windowDpiAfter = NativeInterop.GetDpiForWindow(hwnd);

            // Diagnostic: is WS_VISIBLE actually set? Distinguishes "hidden as intended"
            // from "supposedly hidden but actually visible (AppWindow.MoveAndResize
            // bug)" in the pending-hidden-show path.
            var wsStyle = NativeInterop.GetWindowLongPtr(hwnd, NativeInterop.GWL_STYLE).ToInt64();
            var wsVisible = (wsStyle & NativeInterop.WS_VISIBLE) != 0;

            Logger.Information(
                "MiniRecorder placement diagnostics: phase={Phase} source={Source} foregroundStatus={ForegroundStatus} foregroundHwnd={ForegroundHwnd} miniRecorderHwnd={MiniRecorderHwnd} monitorHwnd={MonitorHwnd} monitorRect=({MonitorLeft},{MonitorTop},{MonitorRight},{MonitorBottom}) workArea=({WorkLeft},{WorkTop},{WorkRight},{WorkBottom}) monitorDpi={MonitorDpi} windowDpiBefore={WindowDpiBefore} windowDpiAfter={WindowDpiAfter} scale={Scale} expectedPhysicalSize=({ExpectedWidth},{ExpectedHeight}) padding={Padding} target=({TargetX},{TargetY}) appWindow=({AppWindowX},{AppWindowY},{AppWindowWidth},{AppWindowHeight}) xamlRoot=({XamlRootWidth},{XamlRootHeight}, scale={XamlRootScale}) wsVisible={WsVisible} pendingReveal={PendingReveal} isCloaked={IsCloaked} cloakMask=0x{CloakMask:X}",
                phase,
                placement.Source,
                placement.ForegroundStatus,
                placement.ForegroundHwnd.ToInt64(),
                hwnd.ToInt64(),
                placement.Monitor.ToInt64(),
                placement.MonitorRect.Left,
                placement.MonitorRect.Top,
                placement.MonitorRect.Right,
                placement.MonitorRect.Bottom,
                placement.WorkArea.Left,
                placement.WorkArea.Top,
                placement.WorkArea.Right,
                placement.WorkArea.Bottom,
                placement.MonitorDpi,
                placement.WindowDpiBefore,
                windowDpiAfter,
                placement.Scale,
                placement.Width,
                placement.Height,
                placement.Padding,
                placement.X,
                placement.Y,
                appWindowPosition.X,
                appWindowPosition.Y,
                appWindowSize.Width,
                appWindowSize.Height,
                xamlRootWidth,
                xamlRootHeight,
                xamlRootScale,
                wsVisible,
                _pendingReveal,
                _isCloaked,
                TryGetCloakedMask(hwnd, out var diagCloakMask) ? diagCloakMask : uint.MaxValue);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to log MiniRecorder placement diagnostics");
        }
    }

    public void Show()
    {
        // Marshal to UI thread — callers may invoke from async pipeline completions
        // (transcription/enhancement) that resume on a thread-pool thread, and every
        // downstream call here (AppWindow.MoveAndResize, InvalidateMeasure, UpdateLayout,
        // DispatcherTimer.Start) must run on the window's UI thread.
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(Show);
            return;
        }

        if (ShouldSkipUiWork()) return;

        // PILL-4: the pill is being dragged — it is visible, in the user's hand, and
        // any geometry re-assert here would yank it away (states arriving mid-drag —
        // error/redo/pipeline completion — call Show() unconditionally; their visual
        // mutations apply on their own, only the reposition/reveal is skipped). The
        // content detach/reattach below would also drop the pointer capture mid-drag.
        if (_dragPointerActive && !_isOffScreen)
        {
            Logger.Debug("MiniRecorder Show() skipped — drag in progress");
            return;
        }

        // Re-read the capture-exclusion preference so a Settings change applies at the pill's next
        // appearance (no-op when unchanged — see ApplyCaptureExclusion).
        ApplyCaptureExclusion();

        // Latency probe: whether this Show is an actual hidden→visible
        // transition. When the pill is already on screen (redo/error states)
        // the measurement is still logged but tagged so it can't be read as a
        // cold-show number.
        var wasOffScreenAtShow = _isOffScreen;

        // Force layout-cache reset before any positioning. InvalidateMeasure can
        // short-circuit when WinUI 3's internal caches say "nothing changed I
        // care about," even though a cross-monitor move did change scale and
        // window metrics. Detaching + reattaching _rootContainer is a hard
        // reset that WinUI cannot ignore: the visual tree is forced to
        // re-measure against the current XamlRoot.RasterizationScale and
        // window client size. Runs while window is at -10000,-10000 (between
        // recordings, courtesy of HideWindow), so no user-visible flicker.
        var contentToReset = _rootContainer;
        Content = null;
        Content = contentToReset;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        _isOffScreen = false; // Re-enable UpdateState processing

        // Tear down any in-flight DPI reconciliation from a prior Show. Must run
        // unconditionally — EnsureXamlRootMatchesTargetDpi below only runs when
        // placement resolves, but a queued sync from a prior cross-DPI show
        // would otherwise SetWindowPos to stale monitor coordinates.
        CancelDpiSync();

        // Position on the monitor where the foreground app (target window) is — not always
        // the primary monitor. Uses MonitorFromWindow + GetMonitorInfo (Win32) instead of
        // DisplayArea.Primary (which only knows about the primary display).
        var hwnd = WindowNative.GetWindowHandle(this);

        int x = 0, y = 0;
        var hasPlacement = false;
        MonitorPlacement placement = default;
        try
        {
            if (TryResolveMonitorPlacement(hwnd, out placement))
            {
                hasPlacement = true;
                _windowWidth = placement.Width;
                _windowHeight = placement.Height;
                x = placement.X;
                y = placement.Y;
                // Remember the last GOOD on-screen position so a later resolution failure
                // re-shows the pill where the user last saw it, not at physical (0,0) (F23).
                _lastResolvedX = x;
                _lastResolvedY = y;
                _hasLastResolvedPosition = true;
            }
            else
            {
                Logger.Warning(
                    "MiniRecorder monitor placement resolution failed; using last known window geometry");
                if (_windowWidth <= 0) _windowWidth = WindowWidthDips;
                if (_windowHeight <= 0) _windowHeight = WindowHeightDips;
                if (_hasLastResolvedPosition) { x = _lastResolvedX; y = _lastResolvedY; }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to compute MiniRecorder placement");
            if (_windowWidth <= 0) _windowWidth = WindowWidthDips;
            if (_windowHeight <= 0) _windowHeight = WindowHeightDips;
            if (_hasLastResolvedPosition) { x = _lastResolvedX; y = _lastResolvedY; }
        }

        // Detect cross-DPI mismatch. If the target monitor's DPI scale doesn't match
        // XamlRoot's current scale, AppWindow.MoveAndResize would surface the window
        // at the target with stale-scale layout. The simple fix: defer the visible
        // show until layout is correct. We use raw SetWindowPos to move the window
        // to the target monitor with WS_VISIBLE OFF — this triggers WM_DPICHANGED
        // for the target DPI without making anything visible. AppWindow.MoveAndResize
        // is NOT called here because it re-sets WS_VISIBLE, exposing the stale frame.
        // Reconciliation calls MoveAndResize + UpdateLayout + SHOWWINDOW once the
        // XamlRoot scale catches up to target.
        var currentXamlScale = _rootContainer.XamlRoot?.RasterizationScale ?? 0;
        var willFlashOnTarget = hasPlacement
            && currentXamlScale > 0
            && Math.Abs(currentXamlScale - placement.Scale) >= 0.01;

        var plan = MiniRecorderShowTransition.Decide(_cloakAvailable, scaleMatches: !willFlashOnTarget);

        // The cloak-mismatch plan cloaks UNCONDITIONALLY before any move — the exact
        // analog of the legacy path's unconditional SWP_HIDEWINDOW, and equally valid
        // when Show() runs on an ALREADY-VISIBLE pill (error/redo/download rehydration):
        // the pill vanishes for the transition, same UX as today. A runtime cloak
        // failure means the window is verifiably uncloaked — downgrade to the legacy
        // plan for this show and disable cloaking one-way.
        if (plan == MiniRecorderShowTransition.ShowPlan.CloakedMoveReconcile && !TryApplyCloak(true))
        {
            DisableCloakAfterRuntimeFailure("Show/CloakedMoveReconcile", wasOffScreenAtShow);
            plan = MiniRecorderShowTransition.FallbackAfterCloakFailure(plan);
        }

        switch (plan)
        {
            case MiniRecorderShowTransition.ShowPlan.CloakedMoveReconcile:
            {
                // Show-time recreate is deliberately NOT requested on this plan:
                // a cloaked window sits at REAL on-monitor coordinates, so — unlike
                // every off-screen parking scheme (disproven 2026-07-06) — its
                // WM_DPICHANGED fires and XamlRoot genuinely converges. The
                // reconciliation timeout escalates to a recreate if it doesn't.
                _pendingReveal = true;

                // Move cloaked to the target — the cloak IS the hide, so this is one
                // SetWindowPos (no SWP_HIDEWINDOW dance needed; nothing can flash).
                NativeInterop.SetWindowPos(
                    hwnd, NativeInterop.HWND_TOPMOST,
                    x, y, _windowWidth, _windowHeight,
                    NativeInterop.SWP_NOACTIVATE);

                if (hasPlacement)
                    LogPlacementDiagnostics("show-cloaked-move", hwnd, placement);

                // Reconciliation does InvalidateMeasure/UpdateLayout at the converged
                // scale, then the verified uncloak — the only visible transition.
                EnsureXamlRootMatchesTargetDpi(hwnd, placement);
                break;
            }

            case MiniRecorderShowTransition.ShowPlan.UncloakInPlace:
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, _windowWidth, _windowHeight));

                _rootContainer.InvalidateMeasure();
                _rootContainer.InvalidateArrange();
                _rootContainer.UpdateLayout();

                RevealAtGeometry(hwnd, x, y, placement, hasPlacement,
                    wasOffScreenAtShow ? "cloak-uncloak" : "cloak-uncloak(already-visible)");

                if (hasPlacement)
                    LogPlacementDiagnostics("show", hwnd, placement);

                // Same defensive safety net as the legacy same-scale path.
                if (hasPlacement)
                    EnsureXamlRootMatchesTargetDpi(hwnd, placement);
                break;
            }

            case MiniRecorderShowTransition.ShowPlan.LegacyRecreateThenInPlace:
            {
                // First try to escalate to a full window recreate. The host (App) decides
                // whether to accept (typically: yes, unless a recreate is already in
                // flight or within cooldown). If accepted, we abort this Show — App's
                // recreate flow will construct a new window targeted at the right
                // monitor and show it via state rehydration. If refused, fall through
                // to in-place hide-then-show + reconciliation as today's behavior.
                //
                // This exists because in-place reconciliation has demonstrated limits
                // on a window that has lived across multiple monitor changes: WinUI 3
                // caches layout/compositor state below the level InvalidateMeasure /
                // UpdateLayout can reach, so a freshly-constructed window targeted at
                // the right monitor is the reliable fix.
                var recreateCallback = CrossDpiRecreateRequested;
                if (recreateCallback != null && recreateCallback(placement, "cross-dpi-show"))
                {
                    Logger.Information(
                        "Cross-DPI Show: recreate accepted (current={Current} target={Target}); aborting in-place flow",
                        currentXamlScale, placement.Scale);
                    return;
                }
                Logger.Information(
                    "Cross-DPI Show: recreate not accepted; falling back to in-place hide-then-show");

                _pendingReveal = true;

                // CRITICAL: hide BEFORE move, as two separate SetWindowPos calls.
                // Combining SWP_HIDEWINDOW with a position change in ONE call causes
                // Windows to briefly present the window at the new (wrong-scale)
                // target position before applying the hide — that's the visible
                // flash the user sees. Doing the hide first while the window is
                // still safely at -10000,-10000 guarantees no visible intermediate
                // state. The subsequent move happens while WS_VISIBLE=false so
                // nothing reaches the screen.
                NativeInterop.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    0, 0, 0, 0,
                    NativeInterop.SWP_HIDEWINDOW
                    | NativeInterop.SWP_NOMOVE
                    | NativeInterop.SWP_NOSIZE
                    | NativeInterop.SWP_NOACTIVATE
                    | NativeInterop.SWP_NOZORDER);

                // Now move to target while hidden. WM_DPICHANGED fires for target
                // DPI but DWM doesn't composite because WS_VISIBLE is false.
                NativeInterop.SetWindowPos(
                    hwnd, NativeInterop.HWND_TOPMOST,
                    x, y, _windowWidth, _windowHeight,
                    NativeInterop.SWP_NOACTIVATE);

                if (hasPlacement)
                    LogPlacementDiagnostics("show-deferred-hidden", hwnd, placement);

                // Reconciliation will do InvalidateMeasure/UpdateLayout (with the
                // now-correct XamlRoot scale) and the reveal — the first and only
                // visible transition.
                EnsureXamlRootMatchesTargetDpi(hwnd, placement);
                break;
            }

            case MiniRecorderShowTransition.ShowPlan.LegacyShow:
            default:
            {
                // No mismatch expected — same-monitor show or first-ever show.
                // MoveAndResize syncs WinUI's internal layout with the target size.
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, _windowWidth, _windowHeight));

                _rootContainer.InvalidateMeasure();
                _rootContainer.InvalidateArrange();
                _rootContainer.UpdateLayout();

                RevealAtGeometry(hwnd, x, y, placement, hasPlacement,
                    wasOffScreenAtShow ? "same-dpi" : "same-dpi(already-visible)");

                if (hasPlacement)
                    LogPlacementDiagnostics("show", hwnd, placement);

                // Defensive safety net: scale could still drift post-MoveAndResize on
                // pathological edge cases. Without an alpha gate this would show a
                // brief flash, but reconciliation corrects it.
                if (hasPlacement)
                    EnsureXamlRootMatchesTargetDpi(hwnd, placement);
                break;
            }
        }

        _topmostTimer.Start();

        sw.Stop();
        if (sw.ElapsedMilliseconds > 10)
            Logger.Information("MiniRecorder Show() took {Elapsed}ms", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Detects WinUI 3 cross-monitor DPI race after Show() and schedules a
    /// reconciliation layout pass once XamlRoot.RasterizationScale catches up
    /// to the target monitor's scale. Uses both XamlRoot.Changed (low-latency)
    /// and a DispatcherTimer (safety net) — whichever observes the matching
    /// scale first triggers the layout pass and cancels the other.
    /// </summary>
    private void EnsureXamlRootMatchesTargetDpi(IntPtr hwnd, MonitorPlacement placement)
    {
        CancelDpiSync(); // Tear down any in-flight reconciliation from a prior Show

        var xamlRoot = _rootContainer.XamlRoot;

        // Critical: when _pendingReveal is true, the window is currently not
        // presentable (SWP_HIDEWINDOW'd on the legacy path, DWM-cloaked on the
        // cloak path). Both early-return paths below (xamlRoot null AND scale
        // already matched) would leave the window invisible forever.
        // Force-apply reconciliation in those cases so the reveal always fires.
        if (xamlRoot == null)
        {
            if (_pendingReveal)
            {
                _pendingDpiSyncHwnd = hwnd;
                _pendingDpiSyncPlacement = placement;
                ApplyDpiReconciliation("xamlRoot-null-immediate");
            }
            return;
        }

        if (Math.Abs(xamlRoot.RasterizationScale - placement.Scale) < 0.01)
        {
            if (_pendingReveal)
            {
                _pendingDpiSyncHwnd = hwnd;
                _pendingDpiSyncPlacement = placement;
                ApplyDpiReconciliation("scale-already-matched-immediate");
            }
            return; // Same-monitor show — no scheduled reconciliation needed
        }

        Logger.Information(
            "MiniRecorder XamlRoot DPI mismatch detected: current={Current} expected={Expected}; scheduling reconciliation",
            xamlRoot.RasterizationScale,
            placement.Scale);

        _pendingDpiSyncHwnd = hwnd;
        _pendingDpiSyncPlacement = placement;
        _dpiSyncXamlRoot = xamlRoot;
        _dpiSyncTickCount = 0;
        // PILL-4: a user drag while this sync is pending makes placement.X/Y stale —
        // the flag stops ApplyDpiReconciliation's visible path from teleporting the
        // pill out of the user's hand back to the pre-drag spot.
        _draggedSinceDpiSyncArmed = false;

        xamlRoot.Changed += OnDpiSyncXamlRootChanged;

        // Safety net: poll every 50ms for up to 2s. Belt-and-suspenders coverage
        // in case XamlRoot.Changed doesn't fire (observed on some driver/SDK
        // combos) — 50ms is well below human perception of layout snap.
        _dpiSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _dpiSyncTimer.Tick += OnDpiSyncTimerTick;
        _dpiSyncTimer.Start();
    }

    private void OnDpiSyncXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (ShouldSkipUiWork()) return;
        if (Math.Abs(sender.RasterizationScale - _pendingDpiSyncPlacement.Scale) > 0.01)
            return; // Still mid-transition; wait for the next Changed
        ApplyDpiReconciliation("xamlRoot.Changed");
    }

    private void OnDpiSyncTimerTick(object? sender, object e)
    {
        if (ShouldSkipUiWork()) return;
        _dpiSyncTickCount++;
        var currentScale = _rootContainer.XamlRoot?.RasterizationScale ?? 0;

        if (Math.Abs(currentScale - _pendingDpiSyncPlacement.Scale) < 0.01)
        {
            ApplyDpiReconciliation("timer");
            return;
        }

        const int maxTicks = 40; // 40 × 50ms = 2s
        if (_dpiSyncTickCount >= maxTicks)
        {
            Logger.Warning(
                "MiniRecorder DPI sync timed out after {Ms}ms; scale stuck at {Scale}, expected {Expected}",
                maxTicks * 50,
                currentScale,
                _pendingDpiSyncPlacement.Scale);

            // Cloak path escalation: a cloaked ON-MONITOR window that still didn't
            // converge is the genuinely stuck case the show-time recreate used to
            // cover — request one now ("cloak-timeout"). Accepted → the recreate
            // flow takes over (this window gets replaced); rejected → force the
            // reconciliation pass below, whose reveal is uncloak-verified anyway.
            if (_pendingReveal && _isCloaked
                && CrossDpiRecreateRequested?.Invoke(_pendingDpiSyncPlacement, "cloak-timeout") == true)
            {
                Logger.Information("Cloaked reconciliation timeout — recreate accepted");
                CancelDpiSync();
                return;
            }

            // Force a reconciliation pass even at stale scale — the surrounding
            // window geometry is still correct, so re-asserting SetWindowPos plus
            // a layout invalidation is the best we can do without recreating the
            // window. Better than leaving the user with broken visuals indefinitely.
            ApplyDpiReconciliation("timeout");
        }
    }

    private void ApplyDpiReconciliation(string trigger)
    {
        if (ShouldSkipUiWork()) return;

        var placement = _pendingDpiSyncPlacement;
        var hwnd = _pendingDpiSyncHwnd;
        var wasPendingReveal = _pendingReveal;
        _pendingReveal = false;
        CancelDpiSync();

        if (MiniRecorderShowTransition.ShouldDropReveal(_isOffScreen))
            return; // Hidden between show and reconciliation — drop the work

        if (hwnd == IntPtr.Zero)
            return; // Defensive: queued tick after CancelDpiSync zeroed state

        // PILL-4: on the VISIBLE path only (the pill can't be dragged while hidden/
        // cloaked, so pendingReveal placements are never stale this way), a drag since
        // the sync was armed means placement.X/Y no longer reflect where the user put
        // the window — keep the layout/scale correction but skip the geometry re-assert.
        var skipGeometry = !wasPendingReveal && _draggedSinceDpiSyncArmed;

        try
        {
            if (!wasPendingReveal && !skipGeometry)
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    placement.X, placement.Y, placement.Width, placement.Height));
            }

            _rootContainer.InvalidateMeasure();
            _rootContainer.InvalidateArrange();
            _rootContainer.UpdateLayout();

            if (wasPendingReveal)
            {
                // CRITICAL: UpdateLayout completes measure/arrange synchronously,
                // but WinUI's actual GPU render is ASYNCHRONOUS — it happens on
                // the next CompositionTarget.Rendering tick. If we reveal now,
                // DWM presents the previous frame from the swap chain (which
                // still holds last show's stale-scale content). That cached
                // presentation IS the user-visible flash.
                //
                // Wait for two render ticks: tick #1 = WinUI commits the fresh
                // layout to the swap chain; tick #2 = DWM has the new frame
                // ready to present. Then reveal (verified uncloak on the cloak
                // path, SHOWWINDOW on the legacy path) — DWM presents the new
                // frame instantly with no cached-frame flash.
                WaitTwoFramesThenShow(hwnd, placement, trigger);
            }
            else
            {
                // Visible safety-net path: window was already visible with stale
                // content but the user has already seen it. SHOWWINDOW just
                // re-asserts geometry; reconciliation re-painted correctly.
                // (Dragged-since-armed: keep the window where the user put it —
                // only re-assert visibility + topmost.)
                if (skipGeometry)
                {
                    NativeInterop.SetWindowPos(
                        hwnd, NativeInterop.HWND_TOPMOST,
                        0, 0, 0, 0,
                        NativeInterop.SWP_NOMOVE | NativeInterop.SWP_NOSIZE
                        | NativeInterop.SWP_SHOWWINDOW | NativeInterop.SWP_NOACTIVATE);
                }
                else
                {
                    NativeInterop.SetWindowPos(
                        hwnd, NativeInterop.HWND_TOPMOST,
                        placement.X, placement.Y, placement.Width, placement.Height,
                        NativeInterop.SWP_SHOWWINDOW | NativeInterop.SWP_NOACTIVATE);
                }
                Logger.Information(
                    "MiniRecorder DPI reconciliation applied via {Trigger} (pendingReveal=false, skipGeometry={SkipGeometry})",
                    trigger, skipGeometry);
                LogPlacementDiagnostics($"show-resync-{trigger}", hwnd, placement);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MiniRecorder DPI reconciliation failed (trigger={Trigger})", trigger);
            if (wasPendingReveal)
            {
                try
                {
                    RevealAtGeometry(hwnd, placement.X, placement.Y, placement, hasPlacement: true,
                        _isCloaked ? "cloak-move-reconcile-fallback" : "cross-dpi-inplace-fallback");
                }
                catch (Exception ex2)
                {
                    Logger.Warning(ex2, "MiniRecorder force-show after reconciliation failure also failed");
                }
            }
        }
    }

    /// <summary>
    /// Wait for two CompositionTarget.Rendering ticks before SetWindowPos(SHOWWINDOW).
    /// Tick #1: WinUI commits the fresh layout to the swap chain.
    /// Tick #2: DWM has the new frame ready to present.
    /// Then SHOWWINDOW presents instantly with no cached-frame flash.
    /// 33ms total at 60fps — below human perception of layout snap.
    /// </summary>
    private void WaitTwoFramesThenShow(IntPtr hwnd, MonitorPlacement placement, string trigger)
    {
        int ticksWaited = 0;
        EventHandler<object>? handler = null;
        handler = (s, e) =>
        {
            ticksWaited++;
            if (ticksWaited < 2)
                return;

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            // Clear field only if it still points to THIS handler — a newer
            // WaitTwoFramesThenShow may have already replaced it via
            // CancelDpiSync+resubscribe.
            if (ReferenceEquals(_waitTwoFramesHandler, handler))
                _waitTwoFramesHandler = null;

            if (MiniRecorderShowTransition.ShouldDropReveal(_isOffScreen))
                return; // Hidden between reconciliation and frame commit — drop

            try
            {
                RevealAtGeometry(hwnd, placement.X, placement.Y, placement, hasPlacement: true,
                    _isCloaked ? "cloak-move-reconcile" : "cross-dpi-inplace");

                Logger.Information(
                    "MiniRecorder DPI reconciliation applied via {Trigger} (pendingReveal=true, waited 2 frames)",
                    trigger);
                LogPlacementDiagnostics($"show-resync-{trigger}", hwnd, placement);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "MiniRecorder post-frame-wait show failed");
            }
        };

        // Replace any prior in-flight handler. CancelDpiSync already unsubscribed
        // any prior handler before this code runs (reconciliation always calls
        // CancelDpiSync first), but be explicit: if a stale handler somehow remains
        // subscribed (e.g. via a new Show that didn't go through reconciliation),
        // remove it now to prevent two handlers ticking together.
        if (_waitTwoFramesHandler != null)
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _waitTwoFramesHandler;
        _waitTwoFramesHandler = handler;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
    }

    private void CancelDpiSync()
    {
        if (_dpiSyncTimer != null)
        {
            _dpiSyncTimer.Stop();
            _dpiSyncTimer.Tick -= OnDpiSyncTimerTick;
            _dpiSyncTimer = null;
        }
        if (_dpiSyncXamlRoot != null)
        {
            try { _dpiSyncXamlRoot.Changed -= OnDpiSyncXamlRootChanged; }
            catch (Exception ex) { Logger.Debug(ex, "Unsubscribe XamlRoot.Changed failed"); }
            _dpiSyncXamlRoot = null;
        }
        // Unsubscribe any in-flight WaitTwoFramesThenShow handler so a stale
        // SetWindowPos doesn't fire at a previous show's placement after a new
        // Show interrupts reconciliation.
        if (_waitTwoFramesHandler != null)
        {
            try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _waitTwoFramesHandler; }
            catch (Exception ex) { Logger.Debug(ex, "Unsubscribe CompositionTarget.Rendering failed"); }
            _waitTwoFramesHandler = null;
        }
        _pendingDpiSyncHwnd = IntPtr.Zero;
        _pendingDpiSyncPlacement = default;
        _dpiSyncTickCount = 0;
    }

    /// <summary>
    /// Render a redo/retry ACTION pill (Render → PillContent.Affordance). The refresh button
    /// dispatches by <paramref name="kind"/> (redo = model picker, retry = re-transcribe) via
    /// App on ActionRequested; <paramref name="showDismiss"/> (⇔ a persistent/Error action) shows
    /// the corner ×. The controller owns auto-dismiss timing — no window timer here.
    /// </summary>
    private void RenderAffordance(AffordanceKind kind, string statusText, MiniRecorderTone tone, bool showDismiss)
    {
        RunOnUiThread(() =>
        {
            if (ShouldSkipUiWork()) return;

            // Tooltip matches the current kind (the button dispatch is App-side via the handle).
            ToolTipService.SetToolTip(_redoButton, kind == AffordanceKind.Retry ? RetryTooltip : RedoTooltip);

            _pulseTimer.Stop();
            _elapsedTimer.Stop();

            Visualizer.IsActive = false;
            Visualizer.Visibility = Visibility.Collapsed;
            _timerText.Visibility = Visibility.Collapsed;
            _targetLabelAllowed = false;
            ApplyTargetAppLabel();

            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = statusText;

            // ONE tone→accent source (AppTheme.AccentFor) drives BOTH the pill body and the
            // action button, so they can never diverge.
            var accent = AppTheme.AccentFor(tone);
            // The corner × rides only on a persistent (Error) action pill, tone-tinted like the pill.
            _dismissButton.Visibility = showDismiss ? Visibility.Visible : Visibility.Collapsed;
            if (showDismiss)
                _dismissIcon.Foreground = AppTheme.Brush(accent);
            // Text takes the tone accent on ALL tones (owner 2026-07-31: green success text
            // beside amber declines and red errors — the old neutral-text Success carve-out made
            // success the one tone whose text didn't match its pill). Success keeps its softer
            // border tint (alpha 50 vs 80) so a routine success stays visually quieter.
            StatusText.Foreground = AppTheme.Brush(accent);
            RecordingDot.Fill = AppTheme.Brush(accent);
            _dotGlow.Fill = AppTheme.Brush(ColorHelper.FromArgb(50, accent.R, accent.G, accent.B));
            _pillBorder.BorderBrush = AppTheme.Brush(ColorHelper.FromArgb(
                tone == MiniRecorderTone.Success ? (byte)50 : (byte)80, accent.R, accent.G, accent.B));

            RecordingDot.Opacity = 1.0;
            _dotGlow.Visibility = Visibility.Visible;

            _stopButton.Visibility = Visibility.Collapsed;
            // The action button matches the pill tone (icon + hover), not a fixed green.
            _redoButtonAccent = accent;
            _redoIcon.Foreground = AppTheme.Brush(accent);
            _redoButton.Background = StopNormalBg();
            _redoButton.Visibility = Visibility.Visible;

            Show();
        });
    }

    /// <summary>
    /// Force WinUI's compositor to do its first paint of this window — at off-screen
    /// coordinates — so the first user-visible Show() doesn't pay the cold-composite
    /// tax. Logs post-resume have shown 500ms+ first-Show under disk/CPU contention
    /// from AV scanning + OneDrive + other wake-time work; warming at startup and
    /// after resume hides that cost where the user isn't waiting. Best-effort —
    /// failures are logged and swallowed.
    /// </summary>
    public void WarmUp()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(WarmUp);
            return;
        }

        if (ShouldSkipUiWork()) return;

        // Critical: skip if the recorder is currently visible to the user. Without this
        // guard, a hotkey press that lands before warm-up fires (startup) or right after
        // resume would be followed by warm-up moving the visible pill off-screen while
        // the user is recording.
        if (!_isOffScreen)
        {
            Logger.Debug("MiniRecorder WarmUp skipped — window is currently visible");
            return;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Don't touch _isOffScreen — UpdateState must stay suppressed, this is
            // compositor priming only, not a user-visible show.
            _rootContainer.InvalidateMeasure();
            _rootContainer.InvalidateArrange();
            _rootContainer.UpdateLayout();

            var hwnd = WindowNative.GetWindowHandle(this);

            // SWP_SHOWWINDOW sets WS_VISIBLE and triggers the compositor's first
            // paint. Cloak path: the window is already parked CLOAKED at its real
            // show position — paint there (invisible: the cloak hides it), which
            // is the first paint with a genuine on-monitor DPI association.
            // Legacy paths: paint at -10000,-10000 (first launch) or preserve the
            // adjacent park (recreate) exactly as before.
            if (_cloakAvailable || _initialTarget != null)
            {
                NativeInterop.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    0, 0, 0, 0,
                    NativeInterop.SWP_SHOWWINDOW
                    | NativeInterop.SWP_NOACTIVATE
                    | NativeInterop.SWP_NOZORDER
                    | NativeInterop.SWP_NOMOVE
                    | NativeInterop.SWP_NOSIZE);
            }
            else
            {
                NativeInterop.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    -10000, -10000, _windowWidth, _windowHeight,
                    NativeInterop.SWP_SHOWWINDOW | NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
            }

            sw.Stop();
            Logger.Information("MiniRecorder WarmUp took {Elapsed}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MiniRecorder WarmUp failed (non-critical)");
        }
    }

    /// <summary>
    /// Tear down this window so a replacement can take over (cross-DPI recreate)
    /// or so the app can shut down. Synchronous body: stops all timers,
    /// unsubscribes view-model/theme handlers, cancels in-flight DPI
    /// reconciliation, marks the window off-screen, and parks the HWND at
    /// -10000,-10000 so no further frames are presented to the user.
    ///
    /// <para><paramref name="deferClose"/> controls when <c>AppWindow.Close()</c>
    /// runs:</para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>true</c> (replacement path, default): the actual close is enqueued at
    /// <see cref="Microsoft.UI.Dispatching.DispatcherQueuePriority.Low"/> so any
    /// in-flight composition / layout / Win32 message-pump work on this window
    /// finishes first. The 2026-05-21 silent termination occurred when Close()
    /// ran synchronously on a window that still had a Content swap + in-place
    /// DPI reconciliation in flight from a Show() that completed milliseconds
    /// before. Deferring avoids that race.
    /// </description></item>
    /// <item><description>
    /// <c>false</c> (shutdown path): the app is exiting, no later dispatcher
    /// ticks are guaranteed. Synchronous close is required so the AppWindow is
    /// actually released before process termination. Safe here because the
    /// recording loop has already been torn down.
    /// </description></item>
    /// </list>
    /// <para>If the deferred enqueue is refused (dispatcher race) we do NOT fall
    /// back to a synchronous close — that is exactly the suspected crash path.
    /// The window is already parked off-screen and detached from its view-model
    /// so an orphan AppWindow is preferable to a native fault.</para>
    /// </summary>
    public void CloseForReplacement(bool deferClose = true)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => CloseForReplacement(deferClose));
            return;
        }

        if (_closingForReplacement)
            return;

        _closingForReplacement = true;
        CancelActiveDrag();
        _pulseTimer.Stop();
        _elapsedTimer.Stop();
        _topmostTimer.Stop();
        Visualizer.IsActive = false; // stop the 60fps visualizer timer with the rest (F14)
        CancelDpiSync();
        _viewModel.PropertyChanged -= _vmPropertyChanged;
        AppTheme.ThemeChanged -= _themeChangedHandler;
        _isOffScreen = true;

        _pendingReveal = false;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (_isCloaked)
            {
                // Already invisible under the cloak — a -10000 park would only
                // churn the window's monitor/DPI association mid-teardown for
                // nothing. A cloaked orphan is quiet by definition. If the pill
                // is VISIBLE when replacement starts, cloak it instead of moving
                // (same no-frame guarantee, cheaper).
                Logger.Debug("MiniRecorder replacement park skipped — window is cloaked");
            }
            else if (_cloakAvailable && TryApplyCloak(true))
            {
                Logger.Debug("MiniRecorder replacement: cloaked in place");
            }
            else
            {
                var width = _windowWidth > 0 ? _windowWidth : WindowWidthDips;
                var height = _windowHeight > 0 ? _windowHeight : WindowHeightDips;
                NativeInterop.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    -10000, -10000, width, height,
                    NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "MiniRecorder replacement park failed");
        }

        if (deferClose)
        {
            Logger.Debug("MiniRecorder Close deferred to dispatcher tick");
            try
            {
                var enqueued = DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        Logger.Debug("MiniRecorder deferred Close starting");
                        try
                        {
                            Close();
                            Logger.Debug("MiniRecorder deferred Close returned");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning(ex, "MiniRecorder deferred Close threw");
                        }
                    });
                if (!enqueued)
                {
                    // Dispatcher refused (shutdown race?). Don't fall back to
                    // synchronous Close — that's the suspected crash path. Window
                    // is already parked off-screen and detached from its
                    // view-model; leaving the AppWindow orphan-but-quiet is
                    // preferable to a synchronous native close.
                    Logger.Warning(
                        "MiniRecorder deferred Close enqueue refused — leaving AppWindow orphaned (parked off-screen, handlers detached)");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "MiniRecorder deferred Close enqueue threw — leaving AppWindow orphaned");
            }
        }
        else
        {
            Logger.Debug("MiniRecorder Close synchronous (shutdown path)");
            try { Close(); }
            catch (Exception ex) { Logger.Warning(ex, "MiniRecorder synchronous Close threw"); }
        }
    }

    /// <summary>
    /// Centralized closing-guard for dispatched UI callbacks. Returns true (and
    /// logs at Debug) when <see cref="_closingForReplacement"/> is set, so the
    /// callback should early-return without touching window state. Used at the
    /// top of every entry point that mutates UI on the dispatcher: Show variants,
    /// UpdateState, HideWindow, WarmUp, timer ticks, theme/property-changed
    /// callbacks, DPI reconciliation. Belt-and-suspenders against late-delivered
    /// dispatcher items that escaped <see cref="CloseForReplacement"/>'s
    /// synchronous teardown (timer Stop, handler unsubscribe, CancelDpiSync).
    /// </summary>
    private bool ShouldSkipUiWork([System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (_closingForReplacement)
        {
            Logger.Debug("Skipping {Caller} — MiniRecorder is closing for replacement", caller);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Run a state-render body on the UI thread — INLINE when already there, enqueued
    /// otherwise (the same discipline <see cref="Show"/> and <see cref="HideWindow"/>
    /// already use). Load-bearing: renders and hides must execute in PROGRAM ORDER.
    /// The old unconditional TryEnqueue deferred render bodies while HideWindow ran
    /// inline, so a redo-teardown's trailing queued render (a `RenderAffordance` ends in
    /// Show()) resurrected the window its own hide had just parked — a permanently
    /// visible pill whose ViewModel said hidden, immune to every flag-gated recovery
    /// sweep (zombie pill №2, live incident 2026-07-11 17:19).
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        DispatcherQueue.TryEnqueue(() => action());
    }

    public void HideWindow()
    {
        // Marshal to UI thread — callers may invoke from background threads (e.g. PropertyChanged handlers)
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(HideWindow);
            return;
        }

        if (ShouldSkipUiWork()) return;

        // Hidden mid-drag: cancel WITHOUT persisting (the user never saw the drop
        // land) and release the capture — a PointerCaptureLost is not guaranteed
        // once the window parks, and a stuck _dragPointerActive would ignore every
        // future background press.
        CancelActiveDrag();

        _pulseTimer.Stop();
        _elapsedTimer.Stop();
        _topmostTimer.Stop();
        CancelDpiSync();

        // Reset visuals and suppress UpdateState so the reset sticks while off-screen.
        _isOffScreen = true;
        ResetToStartingVisuals();

        _pendingReveal = false;

        var hwnd = WindowNative.GetWindowHandle(this);
        if (MiniRecorderShowTransition.Park(_cloakAvailable)
            == MiniRecorderShowTransition.ParkAction.CloakInPlace)
        {
            // Cloak at the CURRENT (last-shown) position — the surface stays
            // composed (the reset visuals above keep rendering under the cloak)
            // and, unlike any off-screen park, the DPI association stays real:
            // the next same-monitor Show is a bare uncloak.
            if (TryApplyCloak(true))
            {
                // Information, not Debug: both 2026-07-11 zombie-pill incidents were
                // needlessly hard to diagnose because hides are invisible in the log
                // (the file sink is Information+) while every Show logs placement.
                Logger.Information("MiniRecorderWindow hidden (cloaked in place)");
                return;
            }
            DisableCloakAfterRuntimeFailure("HideWindow", wasOffScreen: true); // hide path: always re-park
            return; // DisableCloakAfterRuntimeFailure already parked at -10000
        }

        // Move off-screen — keeps WinUI content rendered so Show() doesn't
        // trigger a white flash from first-render delay. The window remains
        // "visible" to the compositor but is not seen by the user.
        NativeInterop.SetWindowPos(
            hwnd, IntPtr.Zero,
            -10000, -10000, _windowWidth, _windowHeight,
            NativeInterop.SWP_NOACTIVATE | NativeInterop.SWP_NOZORDER);
        Logger.Information("MiniRecorderWindow hidden (legacy off-screen park)");
    }
}
