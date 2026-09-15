using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VoiceWink.Helpers;

namespace VoiceWink.Controls;

/// <summary>
/// Waveform visualizer for real-time audio level display.
/// Bars animate based on AudioLevel (dB) with sinusoidal wave motion and center boost.
/// The bar COUNT is dynamic (owner 2026-07-10): it derives from the control's actual
/// width via the pure <see cref="WaveformLayout"/> rule, so the waveform fills whatever
/// space the pill's center column provides (the target label's width varies per app) —
/// the bar geometry itself is fixed, preserving the PILL-2 look. Host the control with
/// HorizontalAlignment.Stretch; for any width ≥ WaveformLayout.CanvasWidthFor(MinBars)
/// (38 DIP — the pill's star column is always ≳200) overflow is impossible by
/// construction (the count is computed FROM the width), unlike the old fixed 198-DIP
/// canvas that relied on the pill budget always reserving enough room.
/// </summary>
public sealed partial class AudioVisualizerControl : UserControl
{
    private const double BarMinHeight = 4;
    private const double BarMaxHeight = 32; // fits the pill's ~46-DIP inner height (48 − borders)

    private Rectangle[] _bars = [];
    private double[] _phases = [];
    private readonly DispatcherTimer _animationTimer;

    private DateTime _startTime;
    private bool _isAnimating;

    public static readonly DependencyProperty AudioLevelProperty =
        DependencyProperty.Register(
            nameof(AudioLevel),
            typeof(float),
            typeof(AudioVisualizerControl),
            new PropertyMetadata(-160f));

    public float AudioLevel
    {
        get => (float)GetValue(AudioLevelProperty);
        set => SetValue(AudioLevelProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(AudioVisualizerControl),
            new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (AudioVisualizerControl)d;
        if ((bool)e.NewValue)
            control.StartAnimation();
        else
            control.StopAnimation();
    }

    private readonly Canvas VisualizerCanvas;

    public AudioVisualizerControl()
    {
        // Build UI in code to bypass PRI/XAML resource loading issues on CLI-only builds.
        VisualizerCanvas = new Canvas
        {
            Height = BarMaxHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Content = VisualizerCanvas;

        // Start at the historic default; the first SizeChanged re-derives the count from
        // the real width. Rebuilds run on the UI thread (same thread as the animation
        // tick), are change-guarded, and are cheap (≤ WaveformLayout.MaxBars rectangles),
        // so a mid-recording label-width change just reflows the bars once.
        BuildBars(WaveformLayout.DefaultBars);
        SizeChanged += (_, e) =>
        {
            var count = WaveformLayout.BarCountFor(e.NewSize.Width);
            if (count != _bars.Length)
                BuildBars(count);
        };

        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps
        _animationTimer.Tick += OnAnimationTick;
    }

    /// <summary>(Re)create the bar rectangles for the given count.</summary>
    private void BuildBars(int count)
    {
        VisualizerCanvas.Children.Clear();
        VisualizerCanvas.Width = WaveformLayout.CanvasWidthFor(count);

        _bars = new Rectangle[count];
        _phases = new double[count];
        var brush = AppTheme.Brush(AppTheme.TextPrimary);

        for (int i = 0; i < count; i++)
        {
            _phases[i] = i * 0.4;
            var bar = new Rectangle
            {
                Width = WaveformLayout.BarWidth,
                Height = BarMinHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = brush
            };

            Canvas.SetLeft(bar, i * (WaveformLayout.BarWidth + WaveformLayout.BarSpacing));
            Canvas.SetTop(bar, (BarMaxHeight - BarMinHeight) / 2); // center vertically
            VisualizerCanvas.Children.Add(bar);
            _bars[i] = bar;
        }
    }

    /// <summary>Update bar colors to match current theme.</summary>
    public void UpdateTheme()
    {
        var brush = AppTheme.Brush(AppTheme.TextPrimary);
        foreach (var bar in _bars)
            bar.Fill = brush;
    }

    private void StartAnimation()
    {
        if (_isAnimating) return;
        _isAnimating = true;
        _startTime = DateTime.UtcNow;

        // AUD-31: the level DP is the ONLY state that survives across recordings now, and it must
        // be reset per recording — this is the successor to the deleted _noiseFloor.Reset(), which
        // was neutralising a stale level implicitly (it seeded the floor AT that level, so the
        // deadband returned 0). Three facts make a stale level reachable and durable:
        //   * the VM -> control level bridge is gated on the pill being on-screen
        //     (MiniRecorderWindow's _isOffScreen), so a pill hidden mid-recording — a shipped
        //     right-click feature — swallows StopMeterTimer's -160 write;
        //   * MainViewModel.AudioLevel is an [ObservableProperty], so the next recording's -160
        //     write raises no PropertyChanged when the value is already -160;
        //   * on an AUD-21 latched-silent capture the snap latch never fires, so AveragePower
        //     stays exactly -160 for the whole recording and no update ever arrives to correct us.
        // A stateless mapping renders whatever it is handed, so the pill would animate near-full
        // bars over a digitally silent recording for its full duration — the one guarantee AUD-21
        // reserves. Reset to the sentinel: the live meter re-publishes within a tick, and the
        // safe direction is flat.
        AudioLevel = -160f;

        _animationTimer.Start();
    }

    private void StopAnimation()
    {
        _isAnimating = false;
        _animationTimer.Stop();

        // Reset bars to minimum height
        foreach (var bar in _bars)
        {
            bar.Height = BarMinHeight;
            Canvas.SetTop(bar, (BarMaxHeight - BarMinHeight) / 2);
        }
    }

    private void OnAnimationTick(object? sender, object e)
    {
        var time = (DateTime.UtcNow - _startTime).TotalSeconds;

        // AUD-31: one fixed dB window with a resting amplitude floor — stateless, so the same level
        // always renders the same height. The relative tracker this replaces was built on an
        // active-RMS figure that is not the quantity arriving here; see WaveformAmplitude.
        // NOTE the floor is on this ENVELOPE, not on bar height: `wave` below takes each bar back
        // to BarMinHeight at its own trough. Phase offsets are what keep the waveform rippling.
        var amplitude = Helpers.WaveformAmplitude.FromDb(AudioLevel);

        var barCount = _bars.Length;
        var centerIndex = (barCount - 1) / 2.0;

        for (int i = 0; i < barCount; i++)
        {
            // Per-bar sinusoidal wave: sin(time * 8 + phase[i]) * 0.5 + 0.5
            var wave = Math.Sin(time * 8 + _phases[i]) * 0.5 + 0.5;

            // Center boost: bars near center are taller (1.0 - distance * 0.4)
            var distance = Math.Abs(i - centerIndex) / centerIndex;
            var centerBoost = 1.0 - distance * 0.4;

            // Combined height
            var barAmplitude = amplitude * wave * centerBoost;
            var height = BarMinHeight + barAmplitude * (BarMaxHeight - BarMinHeight);
            height = Math.Clamp(height, BarMinHeight, BarMaxHeight);

            _bars[i].Height = height;
            Canvas.SetTop(_bars[i], (BarMaxHeight - height) / 2); // keep centered
        }
    }
}
