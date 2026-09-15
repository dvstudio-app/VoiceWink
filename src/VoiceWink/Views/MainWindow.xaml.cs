using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Views.Pages;

namespace VoiceWink.Views;

/// <summary>
/// Main application window with sidebar navigation.
/// Uses a simple Grid + StackPanel sidebar instead of NavigationView to avoid
/// XAML template resource dependencies that break in CLI-only builds.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static ILogger Logger => Log.ForContext<MainWindow>();

    private Grid? _rootGrid;
    private Grid? _titleBarGrid;
    private TextBlock? _titleBarText;
    private Page? _currentPage;
    private string? _currentTag;
    private bool _isOnboarding;
    /// <summary>True while the setup wizard owns the window (first run, or Settings → Relaunch setup
    /// wizard). <see cref="NavigateToPage"/> refuses while it is set; LNC-11's record gate reads it so
    /// the once-per-session License-page jump is not SPENT on a navigation that cannot land.</summary>
    public bool IsOnboarding => _isOnboarding;
    private bool _isRetryingNavigation;
    private readonly List<Border> _navItems = new();

    // UPD-1b: the "update available" dot on the Updates nav entry. Held as a reference so
    // it can be toggled when a background poll fires; the bool is the source of truth so a
    // sidebar rebuild (CreateNavItem) re-applies the current state to a fresh element.
    private Border? _updatesBadge;
    private bool _updatesBadgeOn;

    private record NavEntry(string Label, string Glyph, string Tag);

    private static readonly NavEntry[] NavEntries =
    [
        new("Home",            "\uE80F", "home"),
        new("History",         "\uE81C", "history"),
        new("Models",          "\uE8F1", "models"),
        new("AI Enhancement",  "\uE945", "enhancement"),
        new("Dictionary",      "\uE82D", "dictionary"),
        new("App Mode",      "\uE7AC", "appmode"),
        new("Transcribe File", "\uE8E6", "transcribe"),
        new("Metrics",         "\uE9D2", "metrics"),
        new("License",         "\uE192", "license"),
        new("Updates",         "\uE895", "updates"),
        new("Settings",        "\uE713", "settings"),
        new("About",           "\uE946", "about"),
        new("Log Viewer", "\uE7BA", "logviewer"),
    ];

    public MainWindow() : this(deferLicenseRoute: false) { }

    /// <summary>
    /// LGL-1: when <paramref name="deferLicenseRoute"/> is <c>true</c>, the constructor
    /// builds UI for onboarded users but does NOT call <see cref="ApplyLicenseStartupRoute"/>.
    /// <c>App.OnLaunched</c> calls <see cref="ApplyLicenseStartupRouteIfReady"/> explicitly
    /// after the legal-acceptance gate succeeds, so license routing can't mutate the
    /// ContentControl mid-modal. The default <c>false</c> preserves the legacy auto-route
    /// behaviour for any caller that still wants it.
    /// </summary>
    public MainWindow(bool deferLicenseRoute)
    {
        // Version deliberately NOT in the title (owner 2026-07-10) — it lives on the
        // About and Updates pages; the "(Debug)" marker stays to distinguish dev builds.
        // MainWindowIdentity keeps this title and the second-instance window matcher
        // (App.ActivateExistingInstance) in lockstep.
        Title = MainWindowIdentity.Title;

        // Custom title bar — extends content into the title bar area so we
        // control icon + title alignment precisely (the OS title bar misaligns them).
        ExtendsContentIntoTitleBar = true;

        var settings = App.Services.GetRequiredService<Services.System.SettingsService>();
        if (settings.GetBool(AppDefaults.HasCompletedOnboarding))
        {
            BuildUI();
            if (!deferLicenseRoute)
                ApplyLicenseStartupRoute();
        }
        else
        {
            ShowOnboarding();
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = Helpers.NativeInterop.GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;

        // The preferred size is DPI-scaled, so it MUST be clamped to the work area before it is
        // applied (owner, 2026-08-02). 730 DIP at 150% scaling is 1095 px against a ~1040 px work
        // area on a 1080p laptop — a Windows default on many 15" machines. Unclamped, three things
        // break at once: the window is taller than the screen, the centering below computes a
        // NEGATIVE y and puts the title bar out of reach, and the enhancement dialog's
        // ContentDialogMaxHeight (derived from this window's height) grows past the screen and
        // takes its buttons with it. The work-area lookup therefore has to happen BEFORE the
        // resize, not only for the centering. (A high scale factor alone is not the trigger — a 4K
        // panel at 200% has the pixels to back it and needs no clamp; see MainWindowPlacement.)
        var preferredWidth = (int)(950 * scale);
        var preferredHeight = (int)(730 * scale);
        var windowWidth = preferredWidth;
        var windowHeight = preferredHeight;
        Windows.Graphics.RectInt32? workAreaOrNull = null;
        try
        {
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            workAreaOrNull = displayArea.WorkArea;
        }
        catch (Exception ex)
        {
            // Fail SOFT to the unclamped preferred size: a window that is too big is recoverable
            // (the user can resize it), whereas refusing to show one is not.
            Logger.Warning(ex, "Failed to read the display work area; using unclamped window size");
        }

        Helpers.WindowPlacement? placementOrNull = null;
        if (workAreaOrNull is { } workArea)
        {
            var placement = Helpers.MainWindowPlacement.ClampToWorkArea(
                preferredWidth, preferredHeight,
                workArea.X, workArea.Y, workArea.Width, workArea.Height);
            placementOrNull = placement;
            windowWidth = placement.Width;
            windowHeight = placement.Height;
            if (windowWidth != preferredWidth || windowHeight != preferredHeight)
                Logger.Information(
                    "Main window clamped to the work area: {PreferredWidth}x{PreferredHeight} -> {Width}x{Height} (dpi={Dpi})",
                    preferredWidth, preferredHeight, windowWidth, windowHeight, dpi);
        }

        // ONE resize for both paths (Kimi diff review r2): duplicating it invited a future edit to
        // update one branch and miss the other.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(windowWidth, windowHeight));

        // Center on the work area (excludes taskbar) so the window isn't clipped. Move keeps its
        // OWN try/catch: before this change the catch covered lookup AND move together, and
        // hoisting the lookup out left Move uncovered, so a display-topology race could escape the
        // constructor and stop startup (Codex diff review r1). The clamped SIZE is already applied
        // by then, so a failed centering costs position only. ShouldMove is false for a work area
        // we could not trust — resize, never move to an untrusted (possibly stale negative) origin,
        // which is the off-screen case this whole helper exists to prevent.
        if (placementOrNull is { ShouldMove: true } toMove)
        {
            try
            {
                AppWindow.Move(new Windows.Graphics.PointInt32(toMove.X, toMove.Y));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to center main window");
            }
        }

        StyleTitleBar();
        SetAppIcon();

        AppTheme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Startup routing gate (LIC-2): after an onboarded launch, check the cached
    /// license status and redirect to the License page for states where the user
    /// needs to act (Unlicensed, GraceExpired, Invalid, DisabledReadOnly). Healthy
    /// states (FirstRunGrace, Activated, OfflineGrace) fall through to the default
    /// Home navigation done by <see cref="BuildUI"/>.
    ///
    /// <para>Uses the synchronous <see cref="Services.Licensing.LicenseService.GetCachedStatus"/>
    /// so the routing decision happens without a network round-trip. <b>Every post-setup launch
    /// with a stored key that matches this device then validates against the server exactly once,
    /// FORCED</b> (LIC-22, owner decision 2026-09-06; a launch with no key, or with a key whose
    /// fingerprint no longer matches, makes no request — force does not skip those two gates — and
    /// this route runs only behind <c>HasCompletedOnboarding</c>, so a launch that resumes an
    /// unfinished wizard validates nothing, which is why the privacy policy says "after setup is
    /// complete"):
    /// on the redirect branch the License page's own first refresh is the forced one (it takes
    /// <see cref="TakeStartupForcedLicenseRefresh"/> in its OnLoaded), so the page shows the
    /// verdict the moment it lands; on the healthy branch a background forced check does it.
    /// Before LIC-22 the redirect branch never reconciled at all (a re-enabled or extended key
    /// healed only through a manual "Check now") and the healthy branch's check was unforced,
    /// so a cache younger than 24 h answered without a server call — a key disabled in the
    /// dashboard inside that window was not caught at the next start.</para>
    /// </summary>
    private void ApplyLicenseStartupRoute()
    {
        try
        {
            var license = App.Services.GetRequiredService<Services.Licensing.LicenseService>();
            var status = license.GetCachedStatus();
            if (Services.Licensing.LicenseService.RequiresStartupRedirect(status))
            {
                Logger.Information("Startup routing: license status {Status} — redirecting to License page", status);
                // The License page carries the launch reconcile: the first LicensePage whose
                // OnLoaded fires takes the pending flag and runs a FORCED, silent refresh, so this
                // branch fires NO separate background check — the launch itself sends one
                // validate, and the panel updates from the result directly instead of waiting for
                // the 5 s reprojection tick (Codex plan round, LIC-22: a separate reconcile beside
                // the page's unforced refresh made two HTTP calls on every stale-cache launch). An
                // unforced page refresh that overlaps an in-flight launch check on a STALE cache
                // can still add one, as it could before LIC-22; the service's generation fence
                // keeps the later-started verdict (Codex diff round — a single-flight was declined
                // as a guard for a rare, harmless, idempotent request). The
                // flag lives HERE, not on a page instance, because NavigateTo's COMException
                // retry and the theme-change rebuild both construct a brand-new page: a flag on
                // the instance that failed to attach would be lost, and that launch would make
                // zero validates (self-review, correctness lens).
                _startupForcedLicenseRefreshPending = true;
                NavigateTo("license");
                // Post-check: a rename / build-ordering bug could silently drop the
                // redirect (NavigateTo matches on the tag string). The earlier
                // Information log would then lie about what happened. Surface a
                // Warning when the nav didn't actually land so log review catches it.
                if (_currentTag != "license")
                {
                    Logger.Warning(
                        "Startup routing: requested License redirect for status {Status} but current nav tag is {Tag}",
                        status, _currentTag ?? "<null>");
                }
            }
            else
            {
                // Healthy cached status (Activated / FirstRunGrace / OfflineGrace). The
                // background check is FORCED so it reaches the server whatever the cache
                // age: `GetCachedStatus` can only surface DisabledReadOnly via the
                // *persisted* disabled flag (LIC-2a), and a server disable that happens
                // between sessions reaches that flag only when a validate actually runs.
                // Fire-and-forget: the user who happens to open the License page this
                // session will see the reconciled state via the page's reprojection tick.
                // We deliberately do NOT mid-session-redirect here — yanking the user
                // from Home to License minutes into a session is more jarring than the
                // extra launch delay.
                StartBackgroundLicenseReconcile(license);
            }
        }
        catch (Exception ex)
        {
            // Fail-open: if the license check throws for any reason, land the user
            // on Home rather than breaking startup. The License page is always
            // reachable from the sidebar.
            Logger.Warning(ex, "License startup route check failed; staying on Home");
        }
    }

    // LIC-22: set by the startup redirect, taken ONCE by the first LicensePage whose OnLoaded
    // runs. Every construction of the License page consults it through the factory below, so
    // it survives a navigation retry and a theme-change rebuild, and an ordinary later visit to
    // the page finds it already taken.
    private bool _startupForcedLicenseRefreshPending;

    /// <summary>
    /// Take-once accessor for the startup redirect's forced first refresh (LIC-22). Returns true
    /// exactly once per launch that set it, from whichever License page instance asks first in
    /// its OnLoaded; false for every page after that and for every launch that did not redirect.
    /// UI-thread only (Loaded handlers and the factory both run there), so a plain field suffices.
    /// </summary>
    private bool TakeStartupForcedLicenseRefresh()
    {
        var pending = _startupForcedLicenseRefreshPending;
        _startupForcedLicenseRefreshPending = false;
        return pending;
    }

    /// <summary>
    /// The healthy-branch half of the launch reconcile (LIC-22): one forced validate on a pool
    /// thread. Offline, the service's own catch chain answers from the persisted state (an
    /// Activated user with a fresh cache stays Activated); a server error is "no verdict" and
    /// local state stands — a Lemon Squeezy incident cannot lock anyone out at startup.
    /// </summary>
    private static void StartBackgroundLicenseReconcile(Services.Licensing.LicenseService license)
    {
        _ = Task.Run(async () =>
        {
            try { await license.CheckAsync(force: true); }
            catch (Exception ex)
            {
                // LicenseService's CheckAsync catches expected network / JSON
                // errors internally, so anything reaching here is unexpected —
                // log at Warning (Debug would be filtered out by the Information
                // minimum level and leave no trace in the file log).
                Logger.Warning(ex, "Background license reconciliation failed");
            }
        });
    }

    /// <summary>
    /// Re-broadcast of the onboarding wizard's completion (LGL-1). App subscribes
    /// here to trigger <c>StartGatedRuntimeServices</c> after a fresh user finishes
    /// the wizard — keeping hotkey/tray/preload dormant until acceptance is captured
    /// by the in-wizard Legal step.
    /// </summary>
    public event Action? OnboardingCompleted;

    public void ShowOnboarding()
    {
        _currentTag = null;
        _currentPage = null;
        _navItems.Clear();
        _isOnboarding = true;

        var page = new OnboardingPage();
        page.OnboardingCompleted += () =>
        {
            _isOnboarding = false;

            // Force-refresh palette and brush cache so BuildUI uses the correct theme
            // (onboarding may have changed the theme while OnThemeChanged was suppressed)
            AppTheme.EnsurePalette();

            // Refresh cached singletons with any settings the wizard changed. (The Enhancement
            // ViewModel's enable switch is resynced by EnhancementPage itself on every open —
            // the page already holds the ViewModel, and AGENTS.md forbids a new locator call here.)
            App.Services.GetRequiredService<ViewModels.SettingsViewModel>().ReloadFromSettings();
            BuildUI();

            // LGL-1: re-broadcast for App.OnLaunched so gated runtime services
            // (hotkey hook, tray Record, preload, cleanup timer) can finally start.
            OnboardingCompleted?.Invoke();
        };
        var onboardingGrid = new Grid
        {
            Background = AppTheme.Brush(AppTheme.ContentBg),
            Children = { page }
        };
        Content = WrapWithTitleBar(onboardingGrid);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = AppTheme.ElementTheme;
        StyleTitleBar();
    }

    private void StyleTitleBar()
    {
        var titleBar = AppWindow.TitleBar;
        var bg = AppTheme.ContentBg;
        var fg = AppTheme.TextPrimary;
        var btnHover = AppTheme.IsDark
            ? ColorHelper.FromArgb(255, 40, 40, 44)
            : ColorHelper.FromArgb(255, 210, 210, 215);
        var btnPressed = AppTheme.IsDark
            ? ColorHelper.FromArgb(255, 55, 55, 60)
            : ColorHelper.FromArgb(255, 190, 190, 195);
        var inactiveFg = AppTheme.DimText;

        titleBar.BackgroundColor = bg;
        titleBar.ForegroundColor = fg;
        titleBar.InactiveBackgroundColor = bg;
        titleBar.InactiveForegroundColor = inactiveFg;
        titleBar.ButtonBackgroundColor = bg;
        titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonHoverBackgroundColor = btnHover;
        titleBar.ButtonHoverForegroundColor = fg;
        titleBar.ButtonPressedBackgroundColor = btnPressed;
        titleBar.ButtonPressedForegroundColor = fg;
        titleBar.ButtonInactiveBackgroundColor = bg;
        titleBar.ButtonInactiveForegroundColor = inactiveFg;

        // Sync custom title bar element with current theme
        if (_titleBarGrid != null)
            _titleBarGrid.Background = AppTheme.Brush(bg);
        if (_titleBarText != null)
            _titleBarText.Foreground = AppTheme.Brush(fg);
    }

    private void OnThemeChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Always restyle the title bar (even during onboarding)
            StyleTitleBar();
            if (_isOnboarding) return; // Onboarding page re-renders its own content
            var savedTag = _currentTag;
            _currentTag = null;
            _currentPage = null;
            _navItems.Clear();
            BuildUI();
            if (savedTag != null)
                NavigateTo(savedTag);
        });
    }

    /// <summary>
    /// Stamp the app icon into both of the window's icon slots.
    ///
    /// <para>The loaded HICONs are held for the PROCESS lifetime and deliberately never
    /// <c>DestroyIcon</c>'d. They used to be freed from a <c>Window.Closed</c> handler, which is
    /// wrong for THIS window: closing to tray CANCELS the close (<c>App.OnLaunched</c> sets
    /// <c>args.Handled</c> in its own <c>Closed</c> handler and hides the window with
    /// <c>SW_HIDE</c>), so the destroy handler ran while the HWND stayed alive and left both icon
    /// slots pointing at destroyed handles. Hiding tears the taskbar button down; the next show
    /// rebuilds it, the shell asks the window for its icon (<c>WM_GETICON</c>), gets the dangling
    /// handles and falls back to the generic placeholder — the "taskbar icon goes generic after
    /// reopening from the tray" bug.</para>
    ///
    /// <para>There is no correct moment to free them: the icons must outlive every taskbar-button
    /// rebuild, so their required lifetime IS the window's, and this is the singleton primary
    /// window whose lifetime is the process. The OS reclaims USER handles at process exit.</para>
    /// </summary>
    private void SetAppIcon()
    {
        // AppWindow.SetIcon(string) loads only one size from the .ico and stamps it into
        // both icon slots, so the taskbar (which wants the BIG icon — 32px @100% DPI,
        // 48px @200%) ends up rendering a scaled-up 16px or, when the shell cache lands
        // on a stale entry, a generic placeholder. Sending WM_SETICON twice with
        // separately-loaded sizes hands Windows the correct bitmap for each slot.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
        if (!File.Exists(iconPath)) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var smallCx = NativeInterop.GetSystemMetrics(NativeInterop.SM_CXSMICON);
        var smallCy = NativeInterop.GetSystemMetrics(NativeInterop.SM_CYSMICON);
        var bigCx = NativeInterop.GetSystemMetrics(NativeInterop.SM_CXICON);
        var bigCy = NativeInterop.GetSystemMetrics(NativeInterop.SM_CYICON);

        var hSmallIcon = NativeInterop.LoadImage(IntPtr.Zero, iconPath, NativeInterop.IMAGE_ICON,
            smallCx, smallCy, NativeInterop.LR_LOADFROMFILE | NativeInterop.LR_DEFAULTCOLOR);
        var hBigIcon = NativeInterop.LoadImage(IntPtr.Zero, iconPath, NativeInterop.IMAGE_ICON,
            bigCx, bigCy, NativeInterop.LR_LOADFROMFILE | NativeInterop.LR_DEFAULTCOLOR);

        if (hSmallIcon != IntPtr.Zero)
            NativeInterop.SendMessage(hwnd, NativeInterop.WM_SETICON, new IntPtr(NativeInterop.ICON_SMALL), hSmallIcon);
        if (hBigIcon != IntPtr.Zero)
            NativeInterop.SendMessage(hwnd, NativeInterop.WM_SETICON, new IntPtr(NativeInterop.ICON_BIG), hBigIcon);
    }

    /// <summary>
    /// Build the custom title bar element (icon + title text) and wrap the given content
    /// in an outer Grid with the title bar in row 0 and content in row 1.
    /// </summary>
    private Grid WrapWithTitleBar(FrameworkElement content)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.png");
        var icon = new Image
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0)
        };
        if (File.Exists(iconPath))
            icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));

        _titleBarText = new TextBlock
        {
            Text = Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };

        // Drag region — fills the title bar row
        _titleBarGrid = new Grid
        {
            Height = 32,
            VerticalAlignment = VerticalAlignment.Top,
            Background = AppTheme.Brush(AppTheme.ContentBg),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Children = { icon, _titleBarText }
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(_titleBarText, 1);
        SetTitleBar(_titleBarGrid);

        var outer = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };
        Grid.SetRow(_titleBarGrid, 0);
        Grid.SetRow(content, 1);
        outer.Children.Add(_titleBarGrid);
        outer.Children.Add(content);

        return outer;
    }

    private void BuildUI()
    {
        // Sidebar brand
        var brand = new TextBlock
        {
            Text = "VoiceWink",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            Margin = new Thickness(20, 20, 20, 24)
        };

        // Sidebar nav stack
        var navStack = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 0) };

        foreach (var entry in NavEntries)
        {
            var item = CreateNavItem(entry.Label, entry.Glyph, entry.Tag);
            navStack.Children.Add(item);
            _navItems.Add(item);
        }

        // Sidebar panel
        var sidebar = new Border
        {
            Width = 210,
            Background = AppTheme.Brush(AppTheme.SidebarBg),
            Child = new StackPanel
            {
                Children = { brand, navStack }
            }
        };

        // Separator
        var separator = new Border
        {
            Width = 1,
            Background = AppTheme.Brush(AppTheme.CardBorderColor)
        };

        _rootGrid = new Grid
        {
            Background = AppTheme.Brush(AppTheme.ContentBg)
        };
        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(separator, 1);

        _rootGrid.Children.Add(sidebar);
        _rootGrid.Children.Add(separator);

        Content = WrapWithTitleBar(_rootGrid);
        ApplyTheme();

        // Select Home by default
        NavigateTo("home");
    }

    private static readonly HashSet<string> DisabledNavTags = [];

    private Border CreateNavItem(string label, string glyph, string tag)
    {
        var isDisabled = DisabledNavTags.Contains(tag);
        var foreground = isDisabled ? AppTheme.DimText : AppTheme.SubtleText;

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            Foreground = AppTheme.Brush(foreground),
            // Explicit Center (default is Stretch) so icon, text, and the Updates dot all
            // share the same centering basis within the row (UPD-3b alignment fix).
            VerticalAlignment = VerticalAlignment.Center
        };

        var text = new TextBlock
        {
            Text = isDisabled ? $"{label} (soon)" : label,
            FontSize = 13,
            Foreground = AppTheme.Brush(foreground),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { icon, text }
        };

        // UPD-1b: append a small "update available" dot to the Updates entry. It's added as
        // an extra child of `inner` (NOT a reshape to a Grid) precisely so NavigateTo's
        // `item.Child as StackPanel` active-styling walk keeps working — that walk only
        // restyles FontIcon / TextBlock children and ignores this Border. Initial visibility
        // follows the persisted bool so a rebuild restores the current state.
        if (tag == "updates")
        {
            _updatesBadge = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = AppTheme.Brush(AppTheme.AccentBlue),
                VerticalAlignment = VerticalAlignment.Center,
                // Optical nudge DOWN: the row's height is driven by the taller FontIcon, and a
                // line box's geometric centre sits ABOVE the lowercase text mass — so a
                // geometrically-centred dot reads as "high" next to "Updates". A TOP margin
                // lowers the dot under VerticalAlignment.Center, toward the text's x-height.
                // (Corrects the inverted UPD-3b lift, which used a bottom margin and pushed the
                // dot further up — the exact misalignment reported on 1.23.304/2026-07-05.)
                Margin = new Thickness(8, 3, 0, 0),
                Visibility = _updatesBadgeOn ? Visibility.Visible : Visibility.Collapsed
            };
            inner.Children.Add(_updatesBadge);
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Background = AppTheme.TransparentBrush,
            Tag = tag,
            Opacity = isDisabled ? 0.5 : 1.0,
            Child = inner
        };

        if (!isDisabled)
        {
            // Hover effect
            border.PointerEntered += (_, _) =>
            {
                if (tag != _currentTag)
                    border.Background = AppTheme.Brush(AppTheme.ActivePillBg);
            };
            border.PointerExited += (_, _) =>
            {
                if (tag != _currentTag)
                    border.Background = AppTheme.TransparentBrush;
            };

            // Click
            border.Tapped += (_, _) => NavigateTo(tag);
        }

        return border;
    }

    /// <summary>
    /// UPD-1b: called (on the UI thread — App marshals via the dispatcher) when a background
    /// poll detects a newer version. Log-only since UPD-3b: the sidebar dot is owned by
    /// <see cref="OnPendingUpdateVersionChanged"/> (state-driven), and an OPEN Updates page
    /// reconciles itself through the same service event
    /// (<c>UpdateViewModel.OnServicePendingVersionChanged</c>) — the old rebuild-the-page
    /// approach is gone, so a user mid-interaction is never yanked to a fresh page.
    /// </summary>
    public void OnUpdateAvailableDetected(string version)
    {
        Logger.Information("Background update available (v{Version})", version);
    }

    /// <summary>
    /// UPD-3b: the SOLE writer of the sidebar "update available" dot. Called (on the UI
    /// thread — App marshals and passes a fresh <c>PendingUpdateVersion</c> snapshot, never
    /// the raw event payload) whenever the pending update transitions. Rule: dot visible iff
    /// an update is pending — on every page, including Updates itself, until the app is
    /// actually updated. Legacy scheduler events must never set badge truth.
    /// </summary>
    public void OnPendingUpdateVersionChanged(string? pendingVersion)
    {
        if (_isOnboarding) return;
        SetUpdatesBadge(pendingVersion != null);
    }

    private void SetUpdatesBadge(bool on)
    {
        _updatesBadgeOn = on;
        if (_updatesBadge != null)
            _updatesBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// LGL-1 deferred license-route entry point. Called from <c>App.OnLaunched</c>
    /// after <c>EnsureLegalAcceptanceAsync</c> returns. No-op when the window is in
    /// onboarding mode (the wizard owns its own license routing via the LIC-2 step).
    /// </summary>
    public void ApplyLicenseStartupRouteIfReady()
    {
        if (_isOnboarding) return;
        ApplyLicenseStartupRoute();
    }

    /// <summary>
    /// Public navigation entry point for non-sidebar callers (Settings → Legal,
    /// LegalAcceptanceDialog "Review now" link, etc). Wraps the private
    /// <see cref="NavigateTo"/> with a known-tag allowlist so external callers
    /// can't navigate to internal-only states. (LGL-1 / Codex round-2 finding.)
    /// </summary>
    public bool NavigateToPage(string tag)
    {
        var allowed = new[] {
            "home", "history", "models", "enhancement", "dictionary",
            "appmode", "transcribe", "metrics", "license", "updates",
            "settings", "about", "logviewer", "legal"
        };
        if (!allowed.Contains(tag))
        {
            Logger.Warning("NavigateToPage rejected unknown tag: {Tag}", tag);
            return false;
        }
        if (_isOnboarding) return false;
        NavigateTo(tag);
        return true;
    }

    /// <summary>
    /// Specialised typed entry for the Legal page so the SettingsPage rows can
    /// preselect EULA vs Privacy without a static-property handshake. LGL-1.
    /// </summary>
    public void NavigateToLegalPage(Pages.LegalDocument initial)
    {
        if (_isOnboarding) return;
        NavigateTo("legal", prebuilt: new Pages.LegalPage(initial));
    }

    private void NavigateTo(string tag, Page? prebuilt = null)
    {
        // UPD-3b: visiting Updates no longer clears the dot — it is state-driven (visible
        // while an update is pending, wherever you are) and owned solely by
        // OnPendingUpdateVersionChanged; it disappears when the pending update is applied
        // or withdrawn, not when the page is viewed.

        if (tag == _currentTag && prebuilt == null) return;
        _currentTag = tag;

        Logger.Debug("Navigation: {Tag}", tag);

        // Update visual state of nav items
        foreach (var item in _navItems)
        {
            var itemTag = item.Tag as string;
            if (itemTag != null && DisabledNavTags.Contains(itemTag)) continue;
            var isActive = itemTag == tag;
            var inner = item.Child as StackPanel;
            if (inner == null) continue;

            if (isActive)
            {
                item.Background = AppTheme.Brush(AppTheme.ActivePillBg);
                foreach (var child in inner.Children)
                {
                    if (child is FontIcon fi) fi.Foreground = AppTheme.Brush(AppTheme.AccentBlue);
                    if (child is TextBlock tb)
                    {
                        tb.Foreground = AppTheme.Brush(AppTheme.AccentBlue);
                        tb.FontWeight = FontWeights.SemiBold;
                    }
                }
            }
            else
            {
                item.Background = AppTheme.TransparentBrush;
                foreach (var child in inner.Children)
                {
                    if (child is FontIcon fi) fi.Foreground = AppTheme.Brush(AppTheme.SubtleText);
                    if (child is TextBlock tb)
                    {
                        tb.Foreground = AppTheme.Brush(AppTheme.SubtleText);
                        tb.FontWeight = FontWeights.Normal;
                    }
                }
            }
        }

        // Create fresh pages on each navigation — ensures all data is current.
        // Pages are lightweight (code-behind only) and handle subscription
        // attach/detach via Loaded/Unloaded events. The `prebuilt` parameter is
        // honoured first so callers (e.g. NavigateToLegalPage) can pass a
        // constructor-args-bearing instance instead of using static handshakes.
        var page = prebuilt ?? tag switch
        {
            "home" => (Page?)new HomePage(),
            "models" => new ModelsPage(),
            "history" => new HistoryPage(),
            "enhancement" => new EnhancementPage(),
            "dictionary" => new DictionaryPage(),
            "appmode" => new AppModePage(),
            "transcribe" => new AudioTranscribePage(),
            "metrics" => new MetricsPage(),
            "logviewer" => new LogViewerPage(),
            "license" => new LicensePage(TakeStartupForcedLicenseRefresh),
            "updates" => new UpdatesPage(),
            "settings" => new SettingsPage(),
            "about" => new AboutPage(),
            "legal" => new Pages.LegalPage(),
            _ => null
        };

        if (page != null && _rootGrid != null)
        {
            try
            {
                if (_currentPage != null)
                {
                    _rootGrid.Children.Remove(_currentPage);
                    _currentPage = null;
                }

                Grid.SetColumn(page, 2);
                _rootGrid.Children.Add(page);
                _currentPage = page;
            }
            catch (global::System.Runtime.InteropServices.COMException ex)
            {
                // WinUI 3 can throw if internal COM state is invalid (e.g. navigating
                // while a page is mid-rebuild). Retry with a fresh page.
                Logger.Warning(ex, "Navigation failed, retrying with fresh page");

                // Best-effort cleanup: remove stale page if Remove was what threw
                if (_currentPage != null)
                {
                    try { _rootGrid.Children.Remove(_currentPage); }
                    catch (global::System.Runtime.InteropServices.COMException cleanupEx)
                    {
                        Logger.Debug(cleanupEx, "Best-effort removal of stale page failed during navigation retry");
                    }
                    _currentPage = null;
                }

                // Reset so user can re-click the same nav item
                _currentTag = null;

                // Retry once with a brand-new page instance (guard against infinite loop)
                if (!_isRetryingNavigation)
                {
                    _isRetryingNavigation = true;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try { NavigateTo(tag); }
                        finally { _isRetryingNavigation = false; }
                    });
                }
            }
        }
    }
}
