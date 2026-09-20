using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Theme mode setting — Dark, Light, or follow the Windows system setting.
/// </summary>
public enum ThemeMode { Dark, Light, System }

/// <summary>
/// Centralized theme colors and UI factory methods.
/// Supports Dark, Light, and System (follows Windows setting) modes.
/// </summary>
public static class AppTheme
{
    private static ILogger Logger => Log.ForContext(typeof(AppTheme));

    // ── Color palette (dynamic — reads from current theme) ──────
    private static Palette Current => _current ?? DarkPalette;

    public static Windows.UI.Color ContentBg => Current.ContentBg;
    public static Windows.UI.Color SidebarBg => Current.SidebarBg;
    public static Windows.UI.Color CardBg => Current.CardBg;
    public static Windows.UI.Color CardBorderColor => Current.CardBorderColor;
    public static Windows.UI.Color AccentBlue => Current.AccentBlue;
    public static Windows.UI.Color AccentGreen => Current.AccentGreen;
    public static Windows.UI.Color AccentRed => Current.AccentRed;
    public static Windows.UI.Color AccentAmber => Current.AccentAmber;
    /// <summary>
    /// Failure TEXT at small sizes (LIC-19) — chosen for ≥ 4.5:1 against both the page and the card
    /// background in BOTH themes. The accents were tuned for chips and borders: at 12 px on the light
    /// page, amber measures about 2:1 and the accent red about 3.2:1, so neither can carry a sentence.
    /// </summary>
    public static Windows.UI.Color FailureText => Current.FailureText;
    /// <summary>
    /// Warning TEXT at small sizes (LIC-20) — the colour of amber SENTENCES (a refused key, a seat that
    /// may still be held, the wizard's status lines — and since UI-15, 2026-09-08, every amber sentence
    /// in the views and dialogs: the advisory under a control, a status line, a callout title, the log
    /// viewer's WRN lines), chosen for ≥ 4.5:1 against both the page and the card background in BOTH
    /// themes. <see cref="AccentAmber"/> stays the colour of borders, badges and the pill tones: as
    /// 12 px text it measures 1.97:1 on the light page. In the dark theme this IS the accent value, so
    /// nothing there changed. The values live in <see cref="ThemeSwatches"/>, where
    /// <c>ThemeTextContrastTests</c> pins them — and pins that no view under <c>src/VoiceWink</c> builds
    /// a <c>Foreground</c> from the accent (the direct form; a colour reaching text through a variable
    /// or a parameter is the reviewer's to catch).
    /// </summary>
    public static Windows.UI.Color WarningText => Current.WarningText;

    /// <summary>
    /// The SINGLE mapping from a MiniRecorder pill tone to its accent colour — used by the
    /// pill body (status text/dot/glow/border) AND the redo/retry action button, so the button
    /// can never diverge from the rest of the pill (e.g. a green retry button on an amber
    /// "paste from clipboard" pill). Success → green, Warning → amber, Error (and any other) → red.
    /// </summary>
    public static Windows.UI.Color AccentFor(Models.Enums.MiniRecorderTone tone) => tone switch
    {
        Models.Enums.MiniRecorderTone.Success => AccentGreen,
        Models.Enums.MiniRecorderTone.Warning => AccentAmber,
        _ => AccentRed,
    };
    public static Windows.UI.Color TextPrimary => Current.TextPrimary;
    public static Windows.UI.Color TextSecondary => Current.TextSecondary;
    public static Windows.UI.Color SubtleText => Current.SubtleText;
    public static Windows.UI.Color DimText => Current.DimText;
    public static Windows.UI.Color ActivePillBg => Current.ActivePillBg;
    public static Windows.UI.Color HoverBg => Current.HoverBg;
    public static Windows.UI.Color RowBg => Current.RowBg;
    public static Windows.UI.Color RowHoverBg => Current.RowHoverBg;

    /// <summary>True when the current effective theme is dark.</summary>
    public static bool IsDark => _effectiveMode == ThemeMode.Dark;

    /// <summary>The WinUI ElementTheme matching the current effective mode.</summary>
    public static ElementTheme ElementTheme => IsDark ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>Raised when the theme changes. MainWindow listens to rebuild UI.</summary>
    public static event Action? ThemeChanged;

    private static ThemeMode _mode = ThemeMode.Dark;
    private static ThemeMode _effectiveMode = ThemeMode.Dark;
    private static Palette? _current;

    /// <summary>Set the theme mode. Call once at startup and when user changes setting.</summary>
    public static void SetTheme(ThemeMode mode)
    {
        _mode = mode;
        var effective = mode == ThemeMode.System ? DetectSystemTheme() : mode;
        if (effective == _effectiveMode) return;

        _effectiveMode = effective;
        _current = effective == ThemeMode.Dark ? DarkPalette : LightPalette;
        _brushCache.Clear();
        TransparentBrush = new SolidColorBrush(Colors.Transparent);
        Logger.Information("Theme changed to {Mode} (effective: {Effective})", mode, effective);
        ThemeChanged?.Invoke();
    }

    /// <summary>Force-refresh brush cache and palette without firing ThemeChanged.
    /// Use after onboarding to ensure BuildUI picks up the correct palette.</summary>
    public static void EnsurePalette()
    {
        var effective = _mode == ThemeMode.System ? DetectSystemTheme() : _mode;
        _effectiveMode = effective;
        _current = effective == ThemeMode.Dark ? DarkPalette : LightPalette;
        _brushCache.Clear();
        TransparentBrush = new SolidColorBrush(Colors.Transparent);
    }

    /// <summary>Re-evaluate system theme (call when OS theme changes).</summary>
    public static void RefreshSystemTheme()
    {
        if (_mode != ThemeMode.System) return;
        var effective = DetectSystemTheme();
        if (effective == _effectiveMode) return;

        _effectiveMode = effective;
        _current = effective == ThemeMode.Dark ? DarkPalette : LightPalette;
        _brushCache.Clear();
        TransparentBrush = new SolidColorBrush(Colors.Transparent);
        Logger.Information("System theme changed -- now {Effective}", effective);
        ThemeChanged?.Invoke();
    }

    private static ThemeMode DetectSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 1 ? ThemeMode.Light : ThemeMode.Dark;
        }
        catch { return ThemeMode.Dark; }
    }

    // ── Palettes ────────────────────────────────────────────────────

    private sealed class Palette
    {
        public Windows.UI.Color ContentBg;
        public Windows.UI.Color SidebarBg;
        public Windows.UI.Color CardBg;
        public Windows.UI.Color CardBorderColor;
        public Windows.UI.Color AccentBlue;
        public Windows.UI.Color AccentGreen;
        public Windows.UI.Color AccentRed;
        public Windows.UI.Color AccentAmber;
        public Windows.UI.Color FailureText;
        public Windows.UI.Color WarningText;
        public Windows.UI.Color TextPrimary;
        public Windows.UI.Color TextSecondary;
        public Windows.UI.Color SubtleText;
        public Windows.UI.Color DimText;
        public Windows.UI.Color ActivePillBg;
        public Windows.UI.Color HoverBg;
        public Windows.UI.Color RowBg;
        public Windows.UI.Color RowHoverBg;
    }

    // The five contrast-bearing fields (the two backgrounds text lands on, the accent that used to
    // be misused as text, and the two *Text colours) come from the WinRT-free ThemeSwatches so the
    // test host can pin the shipped values (LIC-20); ThemeTextContrastTests also pins that every one
    // of these ten lines names its own theme and field. The rest of each palette stays inline.
    private static Windows.UI.Color FromSwatch(Rgb s) => ColorHelper.FromArgb(255, s.R, s.G, s.B);

    private static readonly Palette DarkPalette = new()
    {
        ContentBg       = FromSwatch(ThemeSwatches.Dark.ContentBg),     // #0D0D0F
        SidebarBg       = ColorHelper.FromArgb(255, 26, 26, 30),        // #1A1A1E
        CardBg          = FromSwatch(ThemeSwatches.Dark.CardBg),        // #1E1E22
        CardBorderColor = ColorHelper.FromArgb(255, 42, 42, 46),        // #2A2A2E
        AccentBlue      = ColorHelper.FromArgb(255, 0, 122, 255),       // #007AFF
        AccentGreen     = ColorHelper.FromArgb(255, 48, 209, 88),       // #30D158
        AccentRed       = ColorHelper.FromArgb(255, 255, 69, 58),       // #FF453A
        AccentAmber     = FromSwatch(ThemeSwatches.Dark.AccentAmber),   // #FF9500
        FailureText     = FromSwatch(ThemeSwatches.Dark.FailureText),   // #FF6B6B — 7.0:1 on ContentBg, 6.0:1 on CardBg
        WarningText     = FromSwatch(ThemeSwatches.Dark.WarningText),   // = the accent in the dark theme (8.8:1 / 7.6:1)
        TextPrimary     = ColorHelper.FromArgb(255, 255, 255, 255),     // white
        TextSecondary   = ColorHelper.FromArgb(255, 200, 200, 200),
        SubtleText      = ColorHelper.FromArgb(255, 142, 142, 147),     // #8E8E93
        DimText         = ColorHelper.FromArgb(255, 99, 99, 102),       // #636366
        ActivePillBg    = ColorHelper.FromArgb(51, 0, 122, 255),        // AccentBlue 20%
        HoverBg         = ColorHelper.FromArgb(255, 38, 38, 42),        // #26262A
        RowBg           = ColorHelper.FromArgb(255, 24, 24, 28),        // #18181C
        RowHoverBg      = ColorHelper.FromArgb(255, 34, 34, 38),        // #222226
    };

    private static readonly Palette LightPalette = new()
    {
        ContentBg       = FromSwatch(ThemeSwatches.Light.ContentBg),    // #F2F2F7
        SidebarBg       = ColorHelper.FromArgb(255, 230, 230, 235),     // #E6E6EB
        CardBg          = FromSwatch(ThemeSwatches.Light.CardBg),       // white
        CardBorderColor = ColorHelper.FromArgb(255, 210, 210, 215),     // #D2D2D7
        AccentBlue      = ColorHelper.FromArgb(255, 0, 122, 255),       // #007AFF
        AccentGreen     = ColorHelper.FromArgb(255, 40, 185, 75),       // #28B94B
        AccentRed       = ColorHelper.FromArgb(255, 255, 59, 48),       // #FF3B30
        AccentAmber     = FromSwatch(ThemeSwatches.Light.AccentAmber),  // #FF9500 — borders and badges only: 1.97:1 as text
        FailureText     = FromSwatch(ThemeSwatches.Light.FailureText),  // #B3261E — 5.9:1 on ContentBg, 6.5:1 on CardBg
        WarningText     = FromSwatch(ThemeSwatches.Light.WarningText),  // #9A5B00 — 4.86:1 on ContentBg, 5.43:1 on CardBg
        TextPrimary     = ColorHelper.FromArgb(255, 0, 0, 0),           // black
        TextSecondary   = ColorHelper.FromArgb(255, 60, 60, 67),        // #3C3C43
        SubtleText      = ColorHelper.FromArgb(255, 142, 142, 147),     // #8E8E93
        DimText         = ColorHelper.FromArgb(255, 174, 174, 178),     // #AEAEB2
        ActivePillBg    = ColorHelper.FromArgb(40, 0, 122, 255),        // AccentBlue 15%
        HoverBg         = ColorHelper.FromArgb(255, 220, 220, 225),     // #DCDCE1
        RowBg           = ColorHelper.FromArgb(255, 245, 245, 248),     // #F5F5F8
        RowHoverBg      = ColorHelper.FromArgb(255, 235, 235, 238),     // #EBEBEE
    };

    // ── Spacing constants ──────────────────────────────────────────
    public const double PagePadding = 32;
    public const double CardPadding = 20;
    public const double CardCornerRadius = 12;
    public const double SectionSpacing = 24;

    // ── Brush helpers ──────────────────────────────────────────────
    private static readonly Dictionary<uint, SolidColorBrush> _brushCache = new();

    /// <summary>Returns a cached SolidColorBrush for the given color. Cache is cleared on theme change.</summary>
    public static SolidColorBrush Brush(Windows.UI.Color c)
    {
        var key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (!_brushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(c);
            _brushCache[key] = brush;
        }
        return brush;
    }

    /// <summary>A cached transparent brush -- avoids repeated new SolidColorBrush(Colors.Transparent).</summary>
    public static SolidColorBrush TransparentBrush { get; private set; } = new(Colors.Transparent);

    // ── Factory methods ────────────────────────────────────────────

    /// <summary>Large title + dim subtitle for page header.</summary>
    public static StackPanel CreatePageHeader(string title, string? subtitle = null, bool centered = false)
    {
        var align = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var textAlign = centered ? TextAlignment.Center : TextAlignment.Left;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, SectionSpacing) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(TextPrimary),
            HorizontalAlignment = align,
            TextAlignment = textAlign
        });

        if (subtitle != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 14,
                Foreground = Brush(SubtleText),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = align,
                TextAlignment = textAlign
            });
        }

        return panel;
    }

    /// <summary>Card container: dark bg, rounded corners, subtle border.</summary>
    public static Border CreateCard(UIElement content, double padding = CardPadding)
    {
        return new Border
        {
            Background = Brush(CardBg),
            BorderBrush = Brush(CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CardCornerRadius),
            Padding = new Thickness(padding),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };
    }

    /// <summary>
    /// Flattens a setting row's own card chrome so it can sit as one line INSIDE a
    /// <see cref="CreateCard"/> group instead of being a card of its own. The row factories
    /// (<see cref="CreateToggleSetting"/> and friends) each return a self-contained card, which is
    /// right for a lone row and wrong for a grouped one.
    ///
    /// <para>Lives here rather than on a page because two pages group rows this way: the Settings
    /// page's section cards, and (UI-11) the Models page's GPU acceleration card.</para>
    /// </summary>
    public static void StripCardBorder(Border border)
    {
        border.Background = null;
        border.BorderBrush = null;
        border.BorderThickness = new Thickness(0);
        border.CornerRadius = new CornerRadius(0);
        border.Padding = new Thickness(0, 8, 0, 8);
        border.Margin = new Thickness(0);
    }

    /// <summary>Section header text — 18px semibold with top margin.</summary>
    public static TextBlock CreateSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(TextPrimary),
            Margin = new Thickness(0, SectionSpacing, 0, 12)
        };
    }

    /// <summary>
    /// A label with a hyperlink beside it, as ONE TextBlock. Returns the block plus an updater
    /// that re-targets the link — pass a null/unparsable URL to drop it entirely.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a TextBlock next to a HyperlinkButton. A HyperlinkButton is a Button, so
    /// its text sits in its own box carrying the control's padding and 32 px minimum height, and
    /// centring two boxes vertically is not the same as putting the text inside them on a common
    /// baseline — the wider the font-size gap, the more the link visibly floats. Padding, MinHeight
    /// and VerticalAlignment were each tried against that and none can fix it from outside the box
    /// (owner: "still not properly aligned", three rounds, 2026-07-31). Inlines share the line
    /// box's baseline by construction, at any mix of sizes and weights, so it cannot come back.
    /// </remarks>
    public static (TextBlock block, Action<string?, string> applyLink) CreateLabelWithLink(
        string labelText,
        double labelFontSize,
        Brush labelForeground,
        double linkFontSize,
        Windows.UI.Text.FontWeight? labelWeight = null)
        => BuildLinkedText(labelText, labelFontSize, labelForeground, linkFontSize, labelWeight);

    /// <summary>
    /// A hyperlink on its own, as a TextBlock — for a link that sits beside other elements rather
    /// than beside its own label. Same updater contract as <see cref="CreateLabelWithLink"/>.
    /// </summary>
    public static (TextBlock block, Action<string?, string> applyLink) CreateInlineLink(double fontSize)
        => BuildLinkedText(labelText: null, fontSize, labelForeground: null, fontSize, labelWeight: null);

    private static (TextBlock block, Action<string?, string> applyLink) BuildLinkedText(
        string? labelText,
        double labelFontSize,
        Brush? labelForeground,
        double linkFontSize,
        Windows.UI.Text.FontWeight? labelWeight)
    {
        var hasLabel = !string.IsNullOrEmpty(labelText);
        var labelRun = new Run { Text = labelText ?? string.Empty };
        if (labelWeight.HasValue) labelRun.FontWeight = labelWeight.Value;

        // Spaces, not a Margin: the gap has to live INSIDE the text flow, or it would reintroduce
        // the box this method exists to avoid. Removed together with the link.
        var gap = new Run { Text = "   " };

        var linkRun = new Run();
        var link = new Hyperlink
        {
            FontSize = linkFontSize,
            Foreground = Brush(AccentBlue),
            UnderlineStyle = UnderlineStyle.None // matches the HyperlinkButton look this replaced
        };
        link.Inlines.Add(linkRun);

        var block = new TextBlock
        {
            FontSize = labelFontSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (labelForeground != null) block.Foreground = labelForeground;
        if (hasLabel) block.Inlines.Add(labelRun);

        void ApplyLink(string? url, string text)
        {
            linkRun.Text = text;

            Uri? target = null;
            var show = !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out target);
            if (show) link.NavigateUri = target;

            var present = block.Inlines.Contains(link);
            if (show && !present)
            {
                if (hasLabel) block.Inlines.Add(gap);
                block.Inlines.Add(link);
            }
            else if (!show && present)
            {
                if (hasLabel) block.Inlines.Remove(gap);
                block.Inlines.Remove(link);
            }
        }

        return (block, ApplyLink);
    }

    /// <summary>Returns a stat card Border and the value TextBlock for updates.</summary>
    public static (Border card, TextBlock valueBlock) CreateStatCard(string label, string value, string glyph = "")
    {
        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 32,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush(SubtleText),
            Margin = new Thickness(0, 4, 0, 0)
        };

        var content = new StackPanel { Children = { valueBlock, labelBlock } };

        if (!string.IsNullOrEmpty(glyph))
        {
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 20,
                Foreground = Brush(AccentBlue),
                Margin = new Thickness(0, 0, 0, 8)
            };
            content.Children.Insert(0, icon);
        }

        var card = new Border
        {
            Background = Brush(CardBg),
            BorderBrush = Brush(CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CardCornerRadius),
            Padding = new Thickness(CardPadding),
            MinWidth = 170,
            Child = content
        };

        return (card, valueBlock);
    }

    /// <summary>Toggle switch inside a card-styled row.</summary>
    public static Border CreateToggleSetting(string title, string description, bool isOn, Action<bool> onChanged)
        => CreateToggleSetting(title, description, isOn, onChanged, out _);

    /// <summary>Overload exposing the inner <see cref="ToggleSwitch"/> — REL-17's
    /// diagnostics consent toggles must be able to REVERT the visible control when
    /// persistence fails (a switch showing a state the app is not in is a consent lie).</summary>
    public static Border CreateToggleSetting(string title, string description, bool isOn, Action<bool> onChanged, out ToggleSwitch toggleSwitch)
    {
        var grid = BuildToggleRow(title, description, isOn, onChanged, out toggleSwitch);
        return new Border
        {
            Background = Brush(CardBg),
            BorderBrush = Brush(CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    /// <summary>The label/description + switch grid behind the card factory above. Extracted when
    /// a nested-child variant existed; kept after that variant was deleted because it is also what
    /// makes the empty-description skip below a single, testable place.</summary>
    private static Grid BuildToggleRow(
        string title, string description, bool isOn, Action<bool> onChanged,
        out ToggleSwitch toggleSwitch)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (s, e) => onChanged(toggle.IsOn);
        AllowParentScroll(toggle);

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(TextPrimary)
        };

        var leftPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        leftPanel.Children.Add(titleBlock);
        // An EMPTY description adds no TextBlock at all — a self-explanatory title should not pay
        // for a blank line plus its 2 px margin (owner, 2026-08-04: the descriptions were
        // squeezing the Log Viewer's actual log pane out of the page).
        if (!string.IsNullOrEmpty(description))
        {
            leftPanel.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = Brush(SubtleText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(toggle);

        toggleSwitch = toggle;
        return grid;
    }

    /// <summary>
    /// Populates a ComboBox with hotkey options grouped by category — Modifier, Function, Extra,
    /// and (only when <paramref name="includeTypingKeys"/>) Space, Letters, Digits — with visual
    /// separator headers. This is the single source of truth for hotkey lists.
    /// </summary>
    /// <param name="includeTypingKeys">
    /// Whether to offer letters, digits and Space (HKY-3). <b>Only true where a modifier picker
    /// sits beside this control</b>: those keys are invalid on their own \u2014 a bare "D" binding
    /// would suppress every D the user types \u2014 so the prompt dialog and onboarding, which are
    /// single-key surfaces, must leave this false. <c>HotkeyBinding.Validate</c> is the
    /// enforcing gate; this flag is what keeps an invalid choice from being offered at all.
    /// </param>
    /// <remarks>
    /// Items are DISPLAY names (<see cref="HotkeyKeyDisplay"/> — "Right Alt", not "RightAlt"), so
    /// every reader of these combos converts a selection back through
    /// <c>HotkeyKeyDisplay.ToCanonicalToken</c>/<c>FromDisplay</c> before storing or comparing it.
    /// </remarks>
    public static void PopulateHotkeyCombo(ComboBox combo, bool includeNone, bool includeTypingKeys = false)
    {
        const string noneLabel = "None (disabled)";
        if (includeNone)
            combo.Items.Add(noneLabel);

        foreach (var key in Services.Input.HotkeyService.ModifierKeys)
            combo.Items.Add(HotkeyKeyDisplay.ToDisplayToken(key));

        AddHotkeyGroupHeader(combo, "Function Keys");
        foreach (var key in Services.Input.HotkeyService.FunctionKeys)
            combo.Items.Add(HotkeyKeyDisplay.ToDisplayToken(key));

        AddHotkeyGroupHeader(combo, "Extra Keys");
        foreach (var key in Services.Input.HotkeyService.ExtraKeys)
            combo.Items.Add(HotkeyKeyDisplay.ToDisplayToken(key));

        if (!includeTypingKeys) return;

        // Three sections rather than one "Letters, Digits & Space" heap (owner, 2026-08-30).
        // Space leads: it is the most likely combo trigger, so it should not sit behind 36 rows.
        AddHotkeyGroupHeader(combo, "Space");
        foreach (var key in Services.Input.HotkeyService.SpaceKeys)
            combo.Items.Add(HotkeyKeyDisplay.ToDisplayToken(key));

        AddHotkeyGroupHeader(combo, "Letters");
        foreach (var key in Services.Input.HotkeyService.LetterKeys)
            combo.Items.Add(HotkeyKeyDisplay.ToDisplayToken(key));

        AddHotkeyGroupHeader(combo, "Digits");
        foreach (var key in Services.Input.HotkeyService.DigitKeys)
            combo.Items.Add(HotkeyKeyDisplay.ToDisplayToken(key));
    }

    private static void AddHotkeyGroupHeader(ComboBox combo, string label) => combo.Items.Add(new ComboBoxItem
    {
        Content = $"\u2500\u2500\u2500 {label} \u2500\u2500\u2500",
        IsEnabled = false,
        Foreground = Brush(DimText),
        FontSize = 11
    });

    /// <summary>
    /// Populate an aspect-ratio combo with shape-indicator boxes; pre-select <paramref name="selectedAspect"/>.
    /// When <paramref name="allowedAspects"/> is non-null, only those aspect tags are shown
    /// (Auto is always shown). Used by EnhancementPage / picker dialog to gate aspects per provider.
    /// The dropdown has three groups: square, landscape (widening), portrait (tallening), and the
    /// 4 extreme ratios that only <c>gemini-3.1-flash-image-preview</c> accepts.
    /// </summary>
    public static void PopulateImageAspectComboWithIndicators(
        ComboBox combo,
        string? selectedAspect,
        global::System.Collections.Generic.IReadOnlyCollection<string>? allowedAspects = null)
    {
        // Rows live in ImageOptions (pure data) so a unit test can pin their tag set against
        // ImageOptions.AllKnownAspects — AppTheme's own static initializer needs the WinUI runtime
        // and cannot be touched from a test.
        PopulateIndicatorCombo(
            combo, IndicatorComboRows.Filter(ImageOptions.AspectComboRows, allowedAspects), selectedAspect);
    }

    /// <summary>
    /// Populate a size-tier combo with size-graded indicator boxes. <paramref name="allowedTiers"/>
    /// gates which tiers are shown (Auto is always shown). Empty list → only Auto.
    /// </summary>
    public static void PopulateImageSizeTierComboWithIndicators(
        ComboBox combo,
        string? selectedTier,
        global::System.Collections.Generic.IReadOnlyCollection<string>? allowedTiers = null)
    {
        // Rows live in ImageOptions (pure data) so a unit test pins their tag set against
        // ImageOptions.AllTiers — the same reason the aspect and quality tables moved there.
        PopulateIndicatorCombo(
            combo, IndicatorComboRows.Filter(ImageOptions.TierComboRows, allowedTiers), selectedTier);
    }

    /// <summary>
    /// Populate a quality (detail) combo with weight-graded indicator boxes.
    /// <paramref name="allowedQualities"/> gates which tiers are shown (Auto is always shown) —
    /// added by IMG-12, because a model that publishes <c>quality</c> does not necessarily publish
    /// all of them (<c>x-ai/grok-imagine-image-2.0</c> tops out at <c>medium</c>, and offering
    /// the <c>high</c> tier there earned an HTTP 400). Rows live in <see cref="ImageOptions.QualityComboRows"/>
    /// since IMG-14 (five tiers; the two gpt-image-2.5-only ones arrive through the same gate), so a
    /// unit test pins the tag set against <see cref="ImageOptions.AllQualities"/>.
    /// </summary>
    public static void PopulateImageQualityComboWithIndicators(
        ComboBox combo,
        string? selectedQuality,
        global::System.Collections.Generic.IReadOnlyCollection<string>? allowedQualities = null)
    {
        PopulateIndicatorCombo(
            combo, IndicatorComboRows.Filter(ImageOptions.QualityComboRows, allowedQualities), selectedQuality);
    }

    /// <summary>
    /// Shared helper for populating a ComboBox with indicator-box items. Atomically replaces the
    /// rows, so callers can re-populate freely (e.g. when the filter list changes after a provider
    /// switch); preserves <paramref name="selectedTag"/> when it survived filtering, otherwise falls
    /// back to Auto. Row filtering + pre-select live in <see cref="IndicatorComboRows"/>.
    /// </summary>
    private static void PopulateIndicatorCombo(ComboBox combo, (string Label, string? Tag, double Width, double Height)[] options, string? selectedTag)
    {
        var selectedIndex = IndicatorComboRows.IndexForTag(options, selectedTag);
        var template = IndicatorRowTemplate();

        // UI-21 (2026-09-20): a re-gate that produces the SAME rows, against a combo whose closed
        // box is already built, must not swap ItemsSource.
        //
        // WinUI builds the CLOSED box from the item container at SelectedIndex, and every swap
        // invalidates the containers — so the dialog's three-to-four identical re-gates per open
        // (builder, ContentDialog.Opened, the model combo's SelectionChanged once BindModels lands,
        // any provider change) were tearing down and rebuilding the rows for no change at all. On a
        // redo from History that left a stale container still rendering as selected beside the real
        // selection, and the stale one could not be clicked because the Selector had already
        // deselected it (owner, 2026-09-20). Aspect looked singled out only because a redo is the
        // one case that pre-selects a NON-ZERO index.
        //
        // The template is part of the identity check, not an afterthought: its brush is baked into
        // the parsed markup and cached per colour, so a theme change yields a NEW instance and must
        // still re-render.
        if (ReferenceEquals(combo.ItemTemplate, template)
            && IndicatorComboRows.SameRows(
                combo.ItemsSource as global::System.Collections.Generic.IReadOnlyList<IndicatorRow>, options))
        {
            if (selectedIndex >= 0)
            {
                if (combo.SelectedIndex != selectedIndex)
                {
                    combo.SelectedIndex = selectedIndex;
                }
                else
                {
                    // UI-18's cure, minus the swap (Kimi + Gemini, UI-21 round 1 — both found this
                    // independently, as did the writer's own self-review). The ContentDialog.Opened
                    // re-gate exists BECAUSE the builder's pre-show populate swaps on a DETACHED
                    // control and can leave the closed box blank with the selection itself correct;
                    // its cure was landing a LIVE swap afterwards. Skip the swap and a same-value
                    // SelectedIndex assignment is a WinUI no-op, so the box would stay blank until
                    // the user's first dropdown close — and ComboSelectionBoxGuard cannot cover
                    // that, because it hooks DropDownClosed and never runs on a dialog whose
                    // dropdowns the user has not touched.
                    //
                    // So re-assert the selection the way the guard does — −1 and back — which makes
                    // WinUI re-derive the box from the container without invalidating it. That
                    // leaves UI-18's remedy intact while removing the REPEATED swaps, which is what
                    // left a stale container rendering as selected beside the real selection.
                    //
                    // Unconditional rather than gated on a blankness probe: SelectionBoxItem alone
                    // reports only ComboSelectionBoxHealth's LogicallyBlank, and a box that is
                    // VisuallyBlank (item set, presenter not laid out) would pass such a gate and
                    // stay blank — while a layout-based probe at Opened cannot tell "not laid out
                    // yet" from "never will be" and would swap on every re-gate, which is the churn
                    // being removed. Cost of always nudging: two SelectionChanged events. The
                    // quality row's tracker is scoped by every caller's BeginPopulate, and it is the
                    // only one of the three rows with a subscriber at all (verified in both files).
                    // ComboRegateDeferral keeps both re-gate paths off an open dropdown.
                    // try/finally, the same shape ComboSelectionBoxGuard.ReassertCore carries for
                    // this identical operation: a SelectionChanged subscriber throwing on the −1
                    // phase must not leave the row AT −1, because the confirm path reads it and the
                    // user's pick would be lost. No subscriber throws today; the shape is what
                    // keeps that true of the next one, and the asymmetry would otherwise invite a
                    // future subscriber author to assume a protection that was not here.
                    try
                    {
                        combo.SelectedIndex = -1;
                    }
                    finally
                    {
                        combo.SelectedIndex = selectedIndex;
                    }
                }
            }
            return;
        }

        // DATA rows + an ItemTemplate — never pre-built visuals as ComboBoxItem.Content.
        //
        // A closed ComboBox renders the SELECTED item's content in its own selection-box presenter,
        // and the dropdown's container wants that same visual when the popup opens. With a UIElement
        // as item content those are one instance with two would-be parents, and the hand-off raised
        // COMException 0x800F1000 "Element is already the child of another element" — fatally, every
        // time the aspect dropdown was opened (live, 2026-08-03, four occurrences).
        //
        // Evidence it is the CONTENT and not the repopulation: the model combo in the same dialog
        // repopulates on every provider switch and has never crashed — its items are plain strings.
        // An earlier fix that only made repopulation atomic (one ItemsSource swap instead of
        // Items.Clear()+Add) did NOT stop the crash. Do not reintroduce UIElement item content here.
        var items = new global::System.Collections.Generic.List<IndicatorRow>(options.Length);
        foreach (var (label, tag, w, h) in options)
        {
            items.Add(new IndicatorRow
            {
                Label = label,
                Tag = tag,
                IndicatorWidth = w,
                IndicatorHeight = h
            });
        }

        // Reached only when the rows (or the theme's template) actually CHANGED — a model switch
        // that regates the row set, or the first populate of a fresh combo. Both callers still
        // re-gate on ContentDialog.Opened (UI-18, 2026-09-17), which under UI-21 is a selection
        // re-assert on an unchanged row set rather than another swap.
        combo.ItemTemplate = template;
        UsePlainListPanel(combo);
        combo.ItemsSource = items;

        if (selectedIndex >= 0) combo.SelectedIndex = selectedIndex;
    }

    /// <summary>
    /// The indicator row's visual, as a template the framework instantiates per presenter.
    /// <para>WinUI 3 has no code-first <see cref="DataTemplate"/> API — a template with content can
    /// only be produced by parsing markup — so this is the one <see cref="XamlReader"/> use in the
    /// app. It parses a fixed literal, never anything user- or provider-supplied.</para>
    /// <para>Cached per theme colour: the brush is baked into the markup (a runtime-parsed template
    /// cannot reach <c>AppTheme</c>'s statics), so a theme change must produce a new template rather
    /// than keep painting the old colour.</para>
    /// </summary>
    private static DataTemplate IndicatorRowTemplate()
    {
        var c = SubtleText;
        var hex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        if (_indicatorTemplate != null && _indicatorTemplateColor == hex) return _indicatorTemplate;

        var xaml =
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<StackPanel Orientation=\"Horizontal\" VerticalAlignment=\"Center\">" +
            "<Border Width=\"24\" VerticalAlignment=\"Center\" Margin=\"0,0,8,0\">" +
            "<Border Width=\"{Binding IndicatorWidth}\" Height=\"{Binding IndicatorHeight}\" " +
            $"BorderBrush=\"{hex}\" BorderThickness=\"1.5\" CornerRadius=\"2\" " +
            "HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" />" +
            "</Border>" +
            "<TextBlock Text=\"{Binding Label}\" VerticalAlignment=\"Center\" />" +
            "</StackPanel>" +
            "</DataTemplate>";

        _indicatorTemplate = (DataTemplate)XamlReader.Load(xaml);
        _indicatorTemplateColor = hex;
        return _indicatorTemplate;
    }

    private static DataTemplate? _indicatorTemplate;
    private static string? _indicatorTemplateColor;

    /// <summary>
    /// Give a ComboBox a plain, non-wrapping dropdown (UI-20, 2026-09-19).
    /// <para>WinUI's default <c>ItemsPanel</c> for a ComboBox is <c>CarouselPanel</c>, whose purpose is
    /// to WRAP: when the popup opens in place — the selected row over the closed box — and there is room
    /// beyond an end of the list, it fills that room with rows from the other end, and scrolling never
    /// stops. The owner saw the Aspect ratio list open as <c>3:4 / 2:3 / 9:16 / 1:2</c>, a gap, then
    /// <c>Auto / 1:1 / …</c>. A <c>StackPanel</c> panel has no wrap; the popup template's own
    /// <c>ScrollViewer</c> still scrolls a list taller than the popup.</para>
    /// <para>Applied to the four image-option rows (aspect / size / quality through
    /// <see cref="PopulateIndicatorCombo"/>, Versions at construction); every other ComboBox keeps the
    /// default. Idempotent — a repopulate must not re-template the panel, so the same cached template
    /// is assigned once and compared by reference after that. Parsed with <see cref="XamlReader"/>
    /// like <see cref="IndicatorRowTemplate"/> (WinUI 3 has no code-first template API) — but that
    /// proof covers a <c>DataTemplate</c> root; an <c>ItemsPanelTemplate</c> root is NEW here, and the
    /// call sits on the dialog-open path, so the parse is fail-soft: a throw is logged once and the
    /// row keeps WinUI's default panel (the wrap) rather than taking the dialog down.</para>
    /// <para>Not for long lists: a plain <c>StackPanel</c> does not virtualise and gives up the
    /// carousel's popup-height clamping. These rows hold at most 18 items — the prompt editor's
    /// ungated aspect list (Auto plus the 17 tags); the options dialog gates per model.</para>
    /// </summary>
    public static void UsePlainListPanel(ComboBox combo)
    {
        var template = PlainListPanelTemplate();
        if (template == null) return;
        if (!ReferenceEquals(combo.ItemsPanel, template)) combo.ItemsPanel = template;
    }

    private static ItemsPanelTemplate? PlainListPanelTemplate()
    {
        if (_plainListPanel != null || _plainListPanelFailed) return _plainListPanel;
        const string xaml =
            "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<StackPanel /></ItemsPanelTemplate>";
        try
        {
            _plainListPanel = (ItemsPanelTemplate)XamlReader.Load(xaml);
        }
        catch (global::System.Exception ex)
        {
            _plainListPanelFailed = true;
            Logger.Warning(ex, "Plain list panel template failed to parse; dropdowns keep the default panel");
        }
        return _plainListPanel;
    }

    private static ItemsPanelTemplate? _plainListPanel;
    private static bool _plainListPanelFailed;

    /// <summary>
    /// The tag of an indicator combo's current selection, or null for Auto / no selection.
    /// <para>The ONE reader for these combos. It exists so the item type is named in a single place:
    /// ten call sites carry the user's choice across a regate, decide what reaches the provider, and
    /// decide what is persisted — and when the item type changed, a missed site would have silently
    /// reset the user's aspect to Auto rather than failing loudly.</para>
    /// </summary>
    public static string? SelectedIndicatorTag(ComboBox? combo)
        => (combo?.SelectedItem as IndicatorRow)?.Tag;

    /// <summary>
    /// The same tag, but distinguishing <b>"the user selected Auto"</b> from <b>"nothing is selected
    /// yet"</b> — which <see cref="SelectedIndicatorTag"/> cannot, because both answer null.
    ///
    /// <para><b>Why that distinction is load-bearing (IMG-12 self-review).</b> A caller that carries a
    /// selection across a repopulate writes <c>SelectedIndicatorTag(combo) ?? previous ?? saved</c> —
    /// and an explicit Auto then falls straight through to the saved value. For the quality row that
    /// is a spend decision: set Quality to Auto, change the model (the only thing that regates the
    /// row), and the prompt's stored "maximum" would be re-selected and confirmed. The row used to
    /// populate exactly once, so the bug had nowhere to appear until it started repopulating.</para>
    ///
    /// <para>Returns false only when no row is selected at all, which is the genuine
    /// "fall back to the saved value" case: a combo the caller has not populated yet.</para>
    /// </summary>
    public static bool TryGetSelectedIndicatorTag(ComboBox? combo, out string? tag)
    {
        if (combo?.SelectedItem is IndicatorRow row)
        {
            tag = row.Tag;
            return true;
        }

        tag = null;
        return false;
    }

    /// <summary>ComboBox inside a card-styled row.</summary>
    public static Border CreateComboSetting(string title, string[] options, string currentValue, Action<string> onChanged)
    {
        var combo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        AllowParentScroll(combo);
        const string noneLabel = "None (disabled)";
        foreach (var option in options)
            combo.Items.Add(string.IsNullOrEmpty(option) ? noneLabel : option);
        combo.SelectedItem = string.IsNullOrEmpty(currentValue) ? noneLabel : currentValue;
        combo.SelectionChanged += (s, e) =>
        {
            if (combo.SelectedItem is string selected)
                onChanged(selected == noneLabel ? "" : selected);
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(combo, 1);
        grid.Children.Add(titleBlock);
        grid.Children.Add(combo);

        return new Border
        {
            Background = Brush(CardBg),
            BorderBrush = Brush(CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    /// <summary>Right padding reserved for the WinUI overlay vertical scrollbar in dialog scrollers.</summary>
    public const double DialogScrollbarClearance = 16;

    /// <summary>
    /// Wraps dialog/modal content in a fixed-height vertical <see cref="ScrollViewer"/> with right
    /// padding so the WinUI overlay scrollbar (which renders on top of content, ~12px) never hides
    /// the right edge of the text. Extracted so every scrollable dialog shares ONE fix — callers
    /// (LegalAcceptanceDialog, WhatsNewDialog, …) must not hand-roll their own ScrollViewer. The
    /// padding lives on a wrapper <see cref="Border"/> so the caller's content element is untouched.
    /// </summary>
    public static ScrollViewer CreateDialogScroller(UIElement content, double height) => new()
    {
        Height = height,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new Border
        {
            Padding = new Thickness(0, 0, DialogScrollbarClearance, 0),
            Child = content,
        },
    };

    /// <summary>
    /// Height-flexible variant: takes whatever height the ContentDialog offers and
    /// scrolls only when the content exceeds it — for FORM dialogs whose natural height
    /// varies (the enhancement/image-options dialog clipped its button row once the
    /// reference thumbnail appeared, 2026-07-11). The fixed-height overload above stays
    /// for text documents that want deterministic sizing. Same scrollbar-clearance
    /// wrapper — callers must not hand-roll ScrollViewers.
    /// </summary>
    public static ScrollViewer CreateDialogScroller(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new Border
        {
            Padding = new Thickness(0, 0, DialogScrollbarClearance, 0),
            Child = content,
        },
    };

    /// <summary>
    /// The hover tints for the two filled-button palettes, named once.
    ///
    /// <para>They were literals in FIVE places before UI-3 (<see cref="CreateAccentButton"/>,
    /// <see cref="CreateCompactButton"/>'s accent branch, <c>HomePage</c>'s record button, and both
    /// faces of <see cref="BrushFor"/>), so an accent tweak could land on one face of a button that
    /// swaps between them and not the other — the drift Kimi flagged at r7 and Codex finished
    /// counting at r8, after a first pass caught only three of the five. Only the HOVER values need
    /// naming: the resting colours are already the shared <see cref="AccentBlue"/> /
    /// <see cref="AccentRed"/> constants.</para>
    ///
    /// <para><see cref="AccentBlueHover"/> is public because <c>HomePage</c>'s record button is
    /// outside this class and shares the palette deliberately — it is the same blue affordance.
    /// <see cref="AccentRedHover"/> stays private: nothing outside uses the danger FILL (the danger
    /// COMPACT button uses a translucent wash, a different treatment), and widening it would invite
    /// exactly the copy this pair exists to stop.</para>
    /// </summary>
    public static readonly Windows.UI.Color AccentBlueHover = ColorHelper.FromArgb(255, 30, 144, 255);
    private static readonly Windows.UI.Color AccentRedHover = ColorHelper.FromArgb(255, 220, 80, 80);

    /// <summary>Blue filled accent button.</summary>
    public static Border CreateAccentButton(string text, TappedEventHandler? onTapped = null)
    {
        var normalBg = Brush(AccentBlue);
        var hoverBg = Brush(AccentBlueHover);

        var border = new Border
        {
            Background = normalBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 8, 16, 8),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        border.PointerEntered += (_, _) => border.Background = hoverBg;
        border.PointerExited += (_, _) => border.Background = normalBg;

        if (onTapped != null) border.Tapped += onTapped;

        return border;
    }

    /// <summary>
    /// One button whose LABEL, PALETTE and action follow the state of a long operation — "Download
    /// X" while idle, "Cancel Download" while it runs (UI-3).
    ///
    /// <para><b>Why a type rather than two calls.</b> <see cref="CreateAccentButton"/> captures its
    /// palette in the pointer-handler CLOSURES, so re-tinting an existing button leaves hover
    /// restoring the old colour on the next mouse-out. This holds the palette in a FIELD the
    /// handlers read at event time, which is what makes a live swap correct instead of
    /// almost-correct.</para>
    ///
    /// <para>Owner rule (UAT §91.5): *"instead of a separate cancel download button, the already
    /// existing Download button should simply change to 'Cancel Download' … This should be universal
    /// wherever it applies in the app."* A shared type is how the second and third site get the
    /// behaviour without a hand-rolled copy — the [[UI-2]] lesson.</para>
    ///
    /// <para>A <see cref="Border"/> rather than a templated control, per the repo's standing WinUI 3
    /// constraint: templated controls have failed CLI-launched builds on missing template resources,
    /// and a Border carrying a TextBlock needs none.</para>
    /// </summary>
    public sealed class ActionToggleButton
    {
        private readonly ActionToggleCore _core;
        private readonly TextBlock _label;
        private bool _pointerOver;

        internal ActionToggleButton(Border element, TextBlock label, ActionToggleCore core)
        {
            Element = element;
            _label = label;
            _core = core;

            // Resolve from the CORE at event time, never from a value captured when the handlers
            // were attached — that capture is the bug this design exists to prevent.
            element.PointerEntered += (_, _) => { _pointerOver = true; Repaint(); };
            element.PointerExited += (_, _) => { _pointerOver = false; Repaint(); };

            Repaint();
        }

        public Border Element { get; }

        /// <summary>True while the button is showing its cancel face — what callers branch on.</summary>
        public bool IsCancelling => _core.IsCancelling;

        /// <summary>Idle: the primary action, accent-tinted. Its label was bound at construction.</summary>
        public void ShowPrimary()
        {
            _core.ShowPrimary();
            Repaint();
        }

        /// <summary>Running: the cancel action, danger-tinted so it cannot read as "start again".</summary>
        public void ShowCancel()
        {
            _core.ShowCancel();
            Repaint();
        }

        private void Repaint()
        {
            _label.Text = _core.Label;
            var (palette, hovered) = _core.BackgroundFor(_pointerOver);
            Element.Background = BrushFor(palette, hovered);
        }
    }

    /// <summary>
    /// Maps a toggle state to its brush. Re-read on every repaint, so a theme change mid-operation
    /// gives the post-swap face the NEW palette — something captured closures can never do.
    /// </summary>
    internal static Brush BrushFor(TogglePalette palette, bool hovered) => palette switch
    {
        TogglePalette.Cancel => Brush(hovered ? AccentRedHover : AccentRed),
        _ => Brush(hovered ? AccentBlueHover : AccentBlue),
    };

    /// <summary>
    /// Builds an <see cref="ActionToggleButton"/> in its primary state. BOTH labels are bound here,
    /// and the transitions take no arguments, so a caller structurally cannot put the cancel label
    /// on the primary state (Codex diff review — the earlier API allowed exactly that while its doc
    /// claimed labels could not drift). The caller wires ONE <c>Tapped</c> handler and branches on
    /// <see cref="ActionToggleButton.IsCancelling"/>.
    /// </summary>
    public static ActionToggleButton CreateActionToggleButton(string primaryText, string cancelText)
    {
        var core = new ActionToggleCore(primaryText, cancelText);
        var label = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 8, 16, 8),
            Child = label
        };
        return new ActionToggleButton(border, label, core);
    }

    /// <summary>Subtle secondary button with border.</summary>
    public static Border CreateSecondaryButton(string text, TappedEventHandler? onTapped = null)
    {
        var normalBg = TransparentBrush;
        var hoverBg = Brush(HoverBg);

        var border = new Border
        {
            Background = normalBg,
            BorderBrush = Brush(CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 8, 16, 8),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Brush(TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        border.PointerEntered += (_, _) => border.Background = hoverBg;
        border.PointerExited += (_, _) => border.Background = normalBg;

        if (onTapped != null) border.Tapped += onTapped;

        return border;
    }

    /// <summary>
    /// A glyph-only button — a 24×24 <see cref="Border"/> around a <see cref="Viewbox"/> holding a
    /// <see cref="FontIcon"/> — for an action that belongs BESIDE a line of text rather than in a
    /// button row (LIC-25: the License page's refresh beside the status chip). History's per-row
    /// copy/redo controls are the same shape, built inline before this existed and left as they are:
    /// their hover colours carry meaning of their own. Neutral hover (<see cref="HoverBg"/>), never a
    /// tinted one — green means success in this app. The accessible name doubles as the tooltip, and
    /// the tooltip is load-bearing: a <see cref="Border"/> is not a <c>Control</c>, so UIA may not
    /// announce the name, and a glyph alone names nothing to a pointer user.
    ///
    /// <para><paramref name="glyphSize"/> tunes the GLYPH only; the 24 DIP border is fixed and is the
    /// pointer target (LIC-26, Codex plan round). The two were separated rather than scaled together
    /// because the License page's caller sits beside 12 px text and needed a smaller ARROW, while that
    /// same icon is the only control that force-validates a stored key without re-entering it — the
    /// documented Invalid / DisabledReadOnly / OfflineGrace recovery — so shrinking what the user has
    /// to hit would have traded a recovery path for a visual preference.</para>
    ///
    /// <para><paramref name="glyphOffsetY"/> shifts the GLYPH down (positive) or up inside the fixed
    /// border, as a translation only — the border, its hover square and the pointer target do not
    /// move. It exists because "centred" is not "aligned with text" (LIC-26 follow-up, owner
    /// 2026-09-08: the arrow "appears to be sticking out from the top a little bit"). A capital
    /// letter sits BELOW the centre of its own line box — the descender space beneath it counts
    /// toward the box and a capital uses none of it — while an icon font's em has no descender, so a
    /// centred glyph lands on the row's exact mid-line, about 0.75 DIP above 12 px capitals. Layout
    /// rounding cannot place anything at 0.75, so the License page passes 1.</para>
    /// </summary>
    public static Border CreateIconButton(string glyph, string accessibleName, TappedEventHandler onTapped,
                                          double glyphSize = 12, double glyphOffsetY = 0)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 11,
            Foreground = Brush(SubtleText),
        };
        var normalBg = TransparentBrush;
        var hoverBg = Brush(HoverBg);

        var border = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = normalBg,
            // (0, +y, 0, -y) is a pure translation of a centred fixed-size child: its margin box stays
            // glyphSize tall, so the centring arithmetic is unchanged and only the ink moves.
            Child = new Viewbox
            {
                Width = glyphSize,
                Height = glyphSize,
                Margin = new Thickness(0, glyphOffsetY, 0, -glyphOffsetY),
                Child = icon,
            },
        };

        border.PointerEntered += (_, _) => { border.Background = hoverBg; icon.Foreground = Brush(TextSecondary); };
        border.PointerExited += (_, _) => { border.Background = normalBg; icon.Foreground = Brush(SubtleText); };
        border.Tapped += onTapped;

        ToolTipService.SetToolTip(border, accessibleName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(border, accessibleName);
        return border;
    }

    /// <summary>
    /// Link-style TextBlock (hover-dim + tap action) for an in-page ACTION rather than a URL — the
    /// License page's lost-key / try-without-a-key links and the onboarding License step's lost-key /
    /// buy links share it (moved here from LicensePage on 2026-09-02 so the wizard did not grow a
    /// second copy). Tapped, not PointerPressed: PointerPressed fires on any pointer contact including
    /// right-click and touch-begin-before-release; Tapped is the primary-action gesture (mouse left
    /// click, single-finger tap) and matches link semantics.
    /// </summary>
    public static TextBlock CreateActionLink(string text, Action onTap, string? tooltip = null)
    {
        var link = new TextBlock
        {
            Text = text,
            Foreground = Brush(AccentBlue),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        link.Tapped += (_, _) => onTap();
        link.PointerEntered += (_, _) => link.Opacity = 0.8;
        link.PointerExited += (_, _) => link.Opacity = 1.0;
        if (tooltip != null) ToolTipService.SetToolTip(link, tooltip);
        return link;
    }

    /// <summary>Danger button (red text, subtle bg).</summary>
    public static Border CreateDangerButton(string text, TappedEventHandler? onTapped = null)
    {
        var normalBg = TransparentBrush;
        var hoverBg = Brush(ColorHelper.FromArgb(30, 255, 69, 58));

        var border = new Border
        {
            Background = normalBg,
            BorderBrush = Brush(ColorHelper.FromArgb(80, 255, 69, 58)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 8, 16, 8),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Brush(AccentRed),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        border.PointerEntered += (_, _) => border.Background = hoverBg;
        border.PointerExited += (_, _) => border.Background = normalBg;

        if (onTapped != null) border.Tapped += onTapped;

        return border;
    }

    /// <summary>Compact button for use inside cards and list rows. Supports accent, danger, and default styles.</summary>
    public static Border CreateCompactButton(string text, bool isAccent = false, bool isDanger = false, TappedEventHandler? onTapped = null)
    {
        SolidColorBrush normalBg, hoverBg, textBrush;
        SolidColorBrush? borderBrush = null;

        if (isAccent)
        {
            normalBg = Brush(AccentBlue);
            hoverBg = Brush(AccentBlueHover);
            textBrush = Brush(TextPrimary);
        }
        else if (isDanger)
        {
            normalBg = TransparentBrush;
            hoverBg = Brush(ColorHelper.FromArgb(30, 255, 69, 58));
            textBrush = Brush(AccentRed);
            borderBrush = Brush(ColorHelper.FromArgb(80, 255, 69, 58));
        }
        else
        {
            normalBg = Brush(ColorHelper.FromArgb(128, CardBorderColor.R, CardBorderColor.G, CardBorderColor.B));
            hoverBg = Brush(HoverBg);
            textBrush = Brush(TextSecondary);
        }

        var border = new Border
        {
            Background = normalBg,
            BorderBrush = borderBrush,
            BorderThickness = borderBrush != null ? new Thickness(1) : new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 6, 14, 6),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        border.PointerEntered += (_, _) => border.Background = hoverBg;
        border.PointerExited += (_, _) => border.Background = normalBg;
        border.PointerPressed += (_, _) => border.Background = Brush(ColorHelper.FromArgb(
            (byte)Math.Min(255, hoverBg.Color.A + 30),
            (byte)Math.Max(0, hoverBg.Color.R - 20),
            (byte)Math.Max(0, hoverBg.Color.G - 20),
            (byte)Math.Max(0, hoverBg.Color.B - 20)));
        border.PointerReleased += (_, _) => border.Background = hoverBg;

        if (onTapped != null) border.Tapped += onTapped;

        return border;
    }

    /// <summary>Disable/enable a compact button (or any Border-based button).</summary>
    public static void SetButtonEnabled(Border btn, bool enabled, string? text = null)
    {
        btn.Opacity = enabled ? 1.0 : 0.5;
        btn.IsHitTestVisible = enabled;
        if (text != null && btn.Child is TextBlock tb)
            tb.Text = text;
    }

    /// <summary>
    /// Prevent a control from capturing mouse wheel events so the parent ScrollViewer can scroll.
    /// WinUI 3 ToggleSwitch, ComboBox, and similar controls eat PointerWheelChanged by default.
    ///
    /// <para><b>An OPEN ComboBox popup is exempt</b> (owner-reported 2026-07-31: scrolling up past
    /// "Auto-detect" in the language list kept scrolling the page underneath). The
    /// <c>handledEventsToo: true</c> registration below re-dispatches wheel events the popup's own
    /// list already consumed, so a list that has hit its top/bottom leaks the wheel to the page —
    /// the opposite of standard dropdown behaviour, where the list simply stops. While a combo is
    /// dropped down, its own scrolling owns the wheel, so the un-handling is skipped.</para>
    /// </summary>
    public static void AllowParentScroll(UIElement element)
    {
        element.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((s, e) =>
            {
                if (s is ComboBox { IsDropDownOpen: true }) return; // popup owns the wheel
                e.Handled = false;
            }), true);
    }

    /// <summary>ScrollViewer wrapper with page padding and content bg.</summary>
    public static ScrollViewer CreatePageScrollWrapper(UIElement content)
    {
        var border = new Border
        {
            Background = Brush(ContentBg),
            Padding = new Thickness(PagePadding),
            Child = content
        };

        // Global fix: force all PointerWheelChanged events as unhandled so the
        // ScrollViewer always processes them. WinUI 3 controls (ToggleSwitch,
        // ComboBox, Slider, etc.) eat wheel events, breaking page scrolling.
        border.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((s, e) => e.Handled = false), true);

        return new ScrollViewer { Content = border };
    }

    /// <summary>
    /// Sets a page's content inside the shared page scroll wrapper. On a mid-session
    /// rebuild the existing ScrollViewer + Border wrapper is reused and only the inner
    /// content swaps — replacing the whole wrapper would recreate the ScrollViewer and
    /// snap the page back to the top (the Enhancement activate/deactivate scroll-reset
    /// bug). Every page's build method must set its content through this, never via
    /// Content = CreatePageScrollWrapper(...) directly.
    /// </summary>
    public static void SetPageScrollContent(Page page, UIElement content)
    {
        if (page.Content is ScrollViewer scroller && scroller.Content is Border pageHost)
            pageHost.Child = content;
        else
            page.Content = CreatePageScrollWrapper(content);
    }
}
