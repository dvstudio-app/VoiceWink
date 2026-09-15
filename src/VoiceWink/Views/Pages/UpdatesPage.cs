using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Updates;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Updates page — surfaces the current version, last-checked timestamp,
/// a "Check for updates" button, and the "Check for updates automatically"
/// toggle (UPD-1 v1 scope per Plan 4E with channel selector deferred to
/// UPD-1b per Codex 2026-05-06 override item 3).
///
/// <para>Code-behind only per the project-wide WinUI 3 constraint
/// (CLAUDE.md: "All UI in code-behind"). The page subscribes to
/// <see cref="UpdateViewModel.PropertyChanged"/> while loaded and
/// repaints the status row + button-enabled state in place rather than
/// rebuilding the whole tree on every change.</para>
///
/// <para>In the default build (<c>&lt;UpdateCheckEnabled&gt;false&lt;/UpdateCheckEnabled&gt;</c>)
/// the "Check for updates" button still works — it just returns
/// <see cref="UpdateCheckOutcome.Disabled"/> and shows the
/// "Updates aren't enabled in this version yet" message. The button is
/// deliberately not hidden in that state: the user clicking it and
/// seeing the message is more informative than wondering why the
/// button is missing.</para>
/// </summary>
public sealed class UpdatesPage : Page
{
    private static ILogger Logger => Log.ForContext<UpdatesPage>();

    private readonly UpdateViewModel _vm;

    // Bound UI elements. Mutated on PropertyChanged.
    private TextBlock? _currentVersionBlock;
    private TextBlock? _lastCheckedBlock;
    private TextBlock? _statusTextBlock;
    private Border? _checkButton;
    private Border? _applyButton;
    private TextBlock? _applyStatusBlock;
    private ToggleSwitch? _checkToggleSwitch;
    private ToggleSwitch? _installToggleSwitch;
    private TextBlock? _toggleWarningBlock;

    /// <summary>
    /// The one wording for the automatic-install option. Shared verbatim with the onboarding
    /// Updates step (ONB-3's "one option, one description" rule), and it names the restart
    /// because that is the part a user would otherwise be surprised by.
    /// </summary>
    internal const string InstallToggleDescription =
        "Download and install updates in the background. VoiceWink restarts to finish — " +
        "never while you're recording, transcribing, or generating an image.";

    public UpdatesPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _vm = App.Services.GetRequiredService<UpdateViewModel>();
        BuildUI();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void BuildUI()
    {
        var header = AppTheme.CreatePageHeader(
            "Updates",
            "Check for new versions of VoiceWink.");

        // Status card — version, last-checked, current status line, button.
        _currentVersionBlock = new TextBlock
        {
            Text = $"Currently on version {_vm.CurrentVersion}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
        };

        _lastCheckedBlock = new TextBlock
        {
            Text = _vm.LastCheckedDisplay,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Margin = new Thickness(0, 2, 0, 0),
        };

        _statusTextBlock = new TextBlock
        {
            Text = _vm.StatusText,
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            // Collapsed while empty so it reserves NO vertical space until a check runs
            // (an empty TextBlock still occupies a line box otherwise).
            Visibility = string.IsNullOrWhiteSpace(_vm.StatusText) ? Visibility.Collapsed : Visibility.Visible,
        };

        _checkButton = AppTheme.CreateAccentButton(
            "Check for updates",
            (_, _) => _ = _vm.CheckForUpdatesCommand.ExecuteAsync(null));
        // Initial enabled state matches the command's CanExecute.
        AppTheme.SetButtonEnabled(_checkButton, _vm.CheckForUpdatesCommand.CanExecute(null));

        // "Apply & restart" — downloads + applies the pending update, then restarts. Always visible;
        // enabled only when a check found an update and nothing else is running (CanApplyAndRestart).
        // A blocked apply (recording / transcribe / model-download / paste-restore in flight) shows
        // its reason in _applyStatusBlock instead of silently doing nothing.
        _applyButton = AppTheme.CreateAccentButton(
            "Apply & restart",
            (_, _) => _ = _vm.ApplyAndRestartCommand.ExecuteAsync(null));
        AppTheme.SetButtonEnabled(_applyButton, _vm.ApplyAndRestartCommand.CanExecute(null));

        // Both buttons sit in a single horizontal row.
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { _checkButton, _applyButton },
        };

        _applyStatusBlock = new TextBlock
        {
            Text = _vm.ApplyStatus,
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            // Collapsed while empty so it reserves no space above the buttons.
            Visibility = string.IsNullOrWhiteSpace(_vm.ApplyStatus) ? Visibility.Collapsed : Visibility.Visible,
        };

        // UPD-3b (user request): the live apply feedback ("Downloading update… 18%",
        // retrying, failure text) renders ABOVE the buttons, next to the check-status line,
        // instead of below them.
        var statusCardContent = new StackPanel
        {
            Children =
            {
                _currentVersionBlock,
                _lastCheckedBlock,
                _statusTextBlock,
                _applyStatusBlock,
                buttonRow,
            },
        };
        var statusCard = AppTheme.CreateCard(statusCardContent);

        // "Check for updates automatically" toggle. Uses the shared
        // CreateToggleSetting helper so the visual matches every other
        // Settings-style toggle in the app. The out-ToggleSwitch overload is used because the VM
        // can FORCE the effective value off when a write doesn't reach disk (UPD-4b) — the
        // control has to follow, or it shows a state the app is not in.
        var autoToggle = AppTheme.CreateToggleSetting(
            title: "Check for updates automatically",
            description: "Automatically check for new VoiceWink versions in the background.",
            isOn: _vm.AutomaticUpdateCheckEnabled,
            onChanged: on => _vm.AutomaticUpdateCheckEnabled = on,
            out _checkToggleSwitch);

        // UPD-4b. Copy is character-identical to the onboarding step's — ONB-3's rule: one
        // option, one description, so the wizard and Settings cannot describe it differently.
        var installToggle = AppTheme.CreateToggleSetting(
            title: "Install updates automatically",
            description: InstallToggleDescription,
            isOn: _vm.AutomaticUpdateInstallEnabled,
            onChanged: on => _vm.AutomaticUpdateInstallEnabled = on,
            out _installToggleSwitch);

        _toggleWarningBlock = new TextBlock
        {
            Text = _vm.ToggleWarning,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 4),
            Visibility = string.IsNullOrWhiteSpace(_vm.ToggleWarning) ? Visibility.Collapsed : Visibility.Visible,
        };

        // Installing automatically is meaningless while automatic checks are off — an install is
        // only ever triggered from a scheduler tick. Disable the CONTROL, never the stored value,
        // so turning checks back on restores the user's own install preference rather than
        // silently resetting it.
        ApplyInstallToggleEnabled();

        var pageContent = new StackPanel
        {
            Spacing = 12,
            Children = { header, statusCard, autoToggle, installToggle, _toggleWarningBlock },
        };

        AppTheme.SetPageScrollContent(this, pageContent);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        // UPD-2: the transient VM subscribes to the singleton IUpdateService's apply-state
        // events in its ctor; without this the service's event list would pin every VM ever
        // constructed by a page visit.
        _vm.Detach();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Dispatch every state propagation through the UI thread — a
        // VM command resuming on the threadpool would otherwise hit
        // WinUI's "wrong thread" check on the TextBlock writes.
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(UpdateViewModel.CurrentVersion):
                    if (_currentVersionBlock != null)
                        _currentVersionBlock.Text = $"Currently on version {_vm.CurrentVersion}";
                    break;
                case nameof(UpdateViewModel.LastCheckedDisplay):
                case nameof(UpdateViewModel.LastCheckedUtc):
                    if (_lastCheckedBlock != null)
                        _lastCheckedBlock.Text = _vm.LastCheckedDisplay;
                    break;
                case nameof(UpdateViewModel.StatusText):
                case nameof(UpdateViewModel.LastOutcome):
                    if (_statusTextBlock != null)
                    {
                        _statusTextBlock.Text = _vm.StatusText;
                        // Tint the status line by outcome: green for UpToDate,
                        // amber for UpdateAvailable, red for Error, secondary
                        // text color otherwise. Subtle but lets the user scan
                        // the page state at a glance.
                        _statusTextBlock.Foreground = _vm.LastOutcome switch
                        {
                            UpdateCheckOutcome.UpToDate => AppTheme.Brush(AppTheme.AccentGreen),
                            UpdateCheckOutcome.UpdateAvailable => AppTheme.Brush(AppTheme.WarningText),
                            UpdateCheckOutcome.Error => AppTheme.Brush(AppTheme.AccentRed),
                            _ => AppTheme.Brush(AppTheme.TextSecondary),
                        };
                        _statusTextBlock.Visibility = string.IsNullOrWhiteSpace(_vm.StatusText)
                            ? Visibility.Collapsed : Visibility.Visible;
                    }
                    RefreshApplyButton(); // CanApplyAndRestart depends on LastOutcome
                    break;
                case nameof(UpdateViewModel.IsBusy):
                    RefreshCheckButton(); // CanCheckForUpdates depends on !IsBusy
                    RefreshApplyButton(); // CanApplyAndRestart depends on !IsBusy
                    break;
                case nameof(UpdateViewModel.IsApplying):
                    // CanCheckForUpdates also depends on !IsApplying — re-gate the check button here
                    // too, or it stays clickable during an apply (the Tapped handler calls
                    // ExecuteAsync directly, which ignores CanExecute; the enabled state is enforced
                    // only through SetButtonEnabled / IsHitTestVisible).
                    RefreshCheckButton();
                    RefreshApplyButton();
                    break;
                case nameof(UpdateViewModel.ApplyStatus):
                    if (_applyStatusBlock != null)
                    {
                        _applyStatusBlock.Text = _vm.ApplyStatus;
                        _applyStatusBlock.Visibility = string.IsNullOrWhiteSpace(_vm.ApplyStatus)
                            ? Visibility.Collapsed : Visibility.Visible;
                    }
                    break;
                // UPD-4b: the VM forces a toggle OFF when its write didn't reach disk, so the
                // controls follow VM truth rather than the user's last gesture. Assigning IsOn
                // re-raises Toggled → the VM setter, but with the value it already holds, so the
                // ObservableProperty equality check ends the loop there.
                case nameof(UpdateViewModel.AutomaticUpdateCheckEnabled):
                    if (_checkToggleSwitch != null) _checkToggleSwitch.IsOn = _vm.AutomaticUpdateCheckEnabled;
                    ApplyInstallToggleEnabled();
                    break;
                case nameof(UpdateViewModel.AutomaticUpdateInstallEnabled):
                    if (_installToggleSwitch != null) _installToggleSwitch.IsOn = _vm.AutomaticUpdateInstallEnabled;
                    break;
                case nameof(UpdateViewModel.ToggleWarning):
                    if (_toggleWarningBlock != null)
                    {
                        _toggleWarningBlock.Text = _vm.ToggleWarning;
                        _toggleWarningBlock.Visibility = string.IsNullOrWhiteSpace(_vm.ToggleWarning)
                            ? Visibility.Collapsed : Visibility.Visible;
                    }
                    break;
            }
        });
    }

    // Greys the install toggle while automatic checks are off. The stored preference is left
    // alone on purpose — see the call site in BuildUI.
    private void ApplyInstallToggleEnabled()
    {
        if (_installToggleSwitch == null) return;
        _installToggleSwitch.IsEnabled = _vm.AutomaticUpdateCheckEnabled;
    }

    private void RefreshCheckButton()
    {
        if (_checkButton == null) return;
        AppTheme.SetButtonEnabled(
            _checkButton,
            _vm.CheckForUpdatesCommand.CanExecute(null),
            text: _vm.IsBusy ? "Checking..." : "Check for updates");
    }

    private void RefreshApplyButton()
    {
        if (_applyButton == null) return;
        AppTheme.SetButtonEnabled(
            _applyButton,
            _vm.ApplyAndRestartCommand.CanExecute(null),
            text: _vm.IsApplying ? "Applying..." : "Apply & restart");
    }
}
