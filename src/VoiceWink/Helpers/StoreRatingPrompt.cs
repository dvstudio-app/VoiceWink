using VoiceWink.Services.Data;
using VoiceWink.Services.System;
using VoiceWink.Services.Updates;

namespace VoiceWink.Helpers;

/// <summary>What one look at the displayed Store rating line found (<see cref="StoreRatingPrompt.ObserveDisplayed"/>).</summary>
public enum StoreRatingObservation
{
    /// <summary>It has counted as seen; the caller stops looking.</summary>
    Counted,

    /// <summary>On screen and unobstructed, not yet long enough; look again soon.</summary>
    OnScreen,

    /// <summary>Hidden, minimized or covered by a dialog; the on-screen clock starts again next time.</summary>
    NotOnScreen,
}

/// <summary>
/// LNC-14 (a): whether the Home page shows "Rate VoiceWink in the Microsoft Store" — once, for a
/// Microsoft Store install, after about seven days of use (<see cref="FeedbackLinks.StoreRatingLineDue"/>).
///
/// <para><b>"Once" means one SEEN showing.</b> The line counts as shown — <see cref="AppDefaults.StoreRatingLineShown"/>
/// is written — only after Home has displayed it, with the main window on screen and no dialog over it,
/// for <see cref="SeenAfter"/> in a row within ONE visit to Home, or when it is clicked; from then on it
/// stays for the rest of that session and no later session shows it again. Recording it the moment Home
/// loaded would spend it unseen: with "Start minimized" (on by default) the window is shown while startup
/// finishes and then hidden to the tray, and Home loads in that moment. Until it has been seen it is
/// simply displayed again when due. One instance per main window — one per process — holds the session;
/// <c>MainWindow</c> builds it and hands it to every Home page it creates.</para>
///
/// <para>The Store test is <see cref="InstallSourceReporter.IsStoreInstallation"/>: the record the
/// automatic update check writes (since 1.99.394), and only when it belongs to THIS installation — a
/// record left by an earlier Store install does not make a later website install a Store one. A Store
/// install with automatic checks off never gets a record and never sees the line: both fail toward not
/// showing it.</para>
///
/// <para>Best effort, deliberately: the shown flag is an ordinary debounced settings write, so a crash in
/// the next quarter-second, or a settings file that cannot be written, shows the line once more in a later
/// session — the harmless direction.</para>
/// </summary>
public sealed class StoreRatingPrompt
{
    /// <summary>How long Home must show the line on screen, in one visit, before it counts as seen —
    /// longer than the startup moment before a minimized start hides the window.</summary>
    public static readonly TimeSpan SeenAfter = TimeSpan.FromSeconds(3);

    private readonly SettingsService _settings;
    private readonly UsageDays _usageDays;
    private readonly Func<bool> _isWindowOnScreen;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<DateTime?> _installRootCreatedUtc;

    private bool _seenThisSession;
    private bool _dismissed;
    private DateTime? _onScreenSince;

    /// <param name="isWindowOnScreen">Whether the main window is showing right now (not hidden to the
    /// tray, not minimized).</param>
    public StoreRatingPrompt(SettingsService settings, Func<bool> isWindowOnScreen)
        : this(settings, new UsageDays(settings), isWindowOnScreen, static () => DateTime.UtcNow,
            static () => InstallRoot.CreationTimeUtc(AppContext.BaseDirectory))
    {
    }

    internal StoreRatingPrompt(
        SettingsService settings,
        UsageDays usageDays,
        Func<bool> isWindowOnScreen,
        Func<DateTime> utcNow,
        Func<DateTime?> installRootCreatedUtc)
    {
        _settings = settings;
        _usageDays = usageDays;
        _isWindowOnScreen = isWindowOnScreen;
        _utcNow = utcNow;
        _installRootCreatedUtc = installRootCreatedUtc;
    }

    /// <summary>
    /// Whether Home displays the line now: for the rest of the session that saw it, or whenever it is
    /// due and not yet seen. Writes nothing.
    /// </summary>
    public bool ShouldDisplay()
    {
        if (_dismissed) return false;
        if (_seenThisSession) return true;

        var isStoreInstall = InstallSourceReporter.IsStoreInstallation(_settings, _installRootCreatedUtc());
        var alreadyShown = _settings.GetBool(AppDefaults.StoreRatingLineShown);
        return FeedbackLinks.StoreRatingLineDue(isStoreInstall, _usageDays.Count, alreadyShown);
    }

    /// <summary>A new visit to Home starts displaying the line: the on-screen time counts from here, never
    /// from an earlier visit (time spent on another page is not time spent looking at this line).</summary>
    public void BeginVisit() => _onScreenSince = null;

    /// <summary>
    /// One look while Home displays the line and it has not counted yet. Records it as shown once it has
    /// been on screen, unobstructed, for <see cref="SeenAfter"/> in a row.
    /// </summary>
    /// <param name="unobstructed">Home is the live page and no dialog covers it — the page's half of
    /// "on screen"; the window's half is the constructor's <c>isWindowOnScreen</c>.</param>
    public StoreRatingObservation ObserveDisplayed(bool unobstructed)
    {
        if (_seenThisSession) return StoreRatingObservation.Counted;

        if (!unobstructed || !_isWindowOnScreen())
        {
            _onScreenSince = null;
            return StoreRatingObservation.NotOnScreen;
        }

        var now = _utcNow();
        _onScreenSince ??= now;
        if (now - _onScreenSince.Value < SeenAfter) return StoreRatingObservation.OnScreen;

        RecordSeen();
        return StoreRatingObservation.Counted;
    }

    /// <summary>The user clicked the line: it has certainly been seen. It stays for this session until
    /// <see cref="Dismiss"/> — a click that opened nothing can be tried again.</summary>
    public void RecordSeen()
    {
        _settings.SetBool(AppDefaults.StoreRatingLineShown, true);
        _seenThisSession = true;
    }

    /// <summary>The click opened the Store: the line goes for good.</summary>
    public void Dismiss()
    {
        RecordSeen();
        _dismissed = true;
    }
}
