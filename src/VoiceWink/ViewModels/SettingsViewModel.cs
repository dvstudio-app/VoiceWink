using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Input;
using VoiceWink.Services.System;

namespace VoiceWink.ViewModels;

/// <summary>
/// ViewModel for settings page.
/// Two-way binding to SettingsService.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<SettingsViewModel>();

    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeyService;
    private readonly AutostartRegistrationService _autostart;
    private readonly Services.Audio.AudioDeviceManager _deviceManager;
    private readonly Services.Audio.StandingCaptureService _standingCapture;
    private readonly Services.Updates.IUpdateScheduler? _updateScheduler;
    private readonly Func<bool> _altGrLayoutProbe;

    [ObservableProperty] private string _hotkeyModifier;
    [ObservableProperty] private string _pasteLastHotkeyModifier;
    [ObservableProperty] private string _redoLastHotkeyModifier;
    [ObservableProperty] private string _generateImageHotkeyModifier;
    [ObservableProperty] private bool _isSoundFeedbackEnabled;
    [ObservableProperty] private bool _isSystemMuteEnabled;
    [ObservableProperty] private double _audioResumptionDelay;
    [ObservableProperty] private bool _isTextFormattingEnabled;
    [ObservableProperty] private bool _removeFillerWords;
    [ObservableProperty] private string _fillerWords;
    [ObservableProperty] private bool _appendTrailingSpace;
    [ObservableProperty] private bool _restoreClipboardAfterPaste;
    [ObservableProperty] private bool _sendEnterAfterPaste;
    [ObservableProperty] private double _clipboardRestoreDelay;
    [ObservableProperty] private string _themeMode;
    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private bool _instantRecordingEnabled;

    // UPD-4b: OPTIONAL, defaulting to null, so the existing five-argument constructions in tests
    // and elsewhere keep compiling. It exists because "Reset all settings" restores
    // AutomaticUpdateCheckEnabled but never told the scheduler — see ResetAllSettings.
    //
    // altGrLayoutProbe (2026-09-13): the HKY-4 layout question the reset's hotkey default asks —
    // null resolves to the real probe, tests pass a constant so the answer does not depend on the
    // keyboard layouts installed on the machine running them. DI never sees a Func<bool>
    // registration, so it takes the default.
    public SettingsViewModel(
        SettingsService settings,
        HotkeyService hotkeyService,
        AutostartRegistrationService autostart,
        Services.Audio.AudioDeviceManager deviceManager,
        Services.Audio.StandingCaptureService standingCapture,
        Services.Updates.IUpdateScheduler? updateScheduler = null,
        Func<bool>? altGrLayoutProbe = null)
    {
        _settings = settings;
        _hotkeyService = hotkeyService;
        _autostart = autostart;
        _deviceManager = deviceManager;
        _standingCapture = standingCapture;
        _updateScheduler = updateScheduler;
        // A lambda, not a method group, so KeyboardLayoutProfileTests' routing pin (which counts
        // CALL-shaped references) sees this consumer and can refuse the broad probe here too.
        _altGrLayoutProbe = altGrLayoutProbe ?? (() => KeyboardLayoutProfile.AnyInstalledLayoutNeedsAltGrForTyping());

        // Load current values
        _hotkeyModifier = _settings.GetString(AppDefaults.HotkeyModifier, "RightAlt");
        _pasteLastHotkeyModifier = _settings.GetString(AppDefaults.PasteLastHotkeyModifier, "");
        _redoLastHotkeyModifier = _settings.GetString(AppDefaults.RedoLastHotkeyModifier, "");
        _generateImageHotkeyModifier = _settings.GetString(AppDefaults.GenerateImageHotkeyModifier, "");
        _isSoundFeedbackEnabled = _settings.GetBool(AppDefaults.IsSoundFeedbackEnabled, true);
        _isSystemMuteEnabled = _settings.GetBool(AppDefaults.IsSystemMuteEnabled, true);
        _audioResumptionDelay = _settings.GetDouble(AppDefaults.AudioResumptionDelay, 0.0);
        _isTextFormattingEnabled = _settings.GetBool(AppDefaults.IsTextFormattingEnabled, true);
        _removeFillerWords = _settings.GetBool(AppDefaults.RemoveFillerWords, true);
        _fillerWords = _settings.GetString(AppDefaults.FillerWords, AppDefaults.DefaultFillerWords);
        _appendTrailingSpace = _settings.GetBool(AppDefaults.AppendTrailingSpace, true);
        _restoreClipboardAfterPaste = _settings.GetBool(AppDefaults.RestoreClipboardAfterPaste, false);
        _sendEnterAfterPaste = _settings.GetBoolDefaulted(AppDefaults.SendEnterAfterPaste);
        _clipboardRestoreDelay = _settings.GetDouble(AppDefaults.ClipboardRestoreDelay, 2.0);
        _themeMode = _settings.GetString(AppDefaults.ThemeMode, "System");
        _launchAtLogin = _settings.GetBool(AppDefaults.LaunchAtLogin, true);
        _instantRecordingEnabled = _settings.GetBool(AppDefaults.InstantRecordingEnabled, true);
    }

    /// <summary>Re-read all cached values from settings (e.g. after onboarding wizard changes them).</summary>
    public void ReloadFromSettings()
    {
        HotkeyModifier = _settings.GetString(AppDefaults.HotkeyModifier, "RightAlt");
        PasteLastHotkeyModifier = _settings.GetString(AppDefaults.PasteLastHotkeyModifier, "");
        RedoLastHotkeyModifier = _settings.GetString(AppDefaults.RedoLastHotkeyModifier, "");
        GenerateImageHotkeyModifier = _settings.GetString(AppDefaults.GenerateImageHotkeyModifier, "");
        IsSoundFeedbackEnabled = _settings.GetBool(AppDefaults.IsSoundFeedbackEnabled, true);
        IsSystemMuteEnabled = _settings.GetBool(AppDefaults.IsSystemMuteEnabled, true);
        AudioResumptionDelay = _settings.GetDouble(AppDefaults.AudioResumptionDelay, 0.0);
        IsTextFormattingEnabled = _settings.GetBool(AppDefaults.IsTextFormattingEnabled, true);
        RemoveFillerWords = _settings.GetBool(AppDefaults.RemoveFillerWords, true);
        FillerWords = _settings.GetString(AppDefaults.FillerWords, AppDefaults.DefaultFillerWords);
        AppendTrailingSpace = _settings.GetBool(AppDefaults.AppendTrailingSpace, true);
        RestoreClipboardAfterPaste = _settings.GetBool(AppDefaults.RestoreClipboardAfterPaste, false);
        SendEnterAfterPaste = _settings.GetBoolDefaulted(AppDefaults.SendEnterAfterPaste);
        ClipboardRestoreDelay = _settings.GetDouble(AppDefaults.ClipboardRestoreDelay, 2.0);
        ThemeMode = _settings.GetString(AppDefaults.ThemeMode, "System");
        LaunchAtLogin = _settings.GetBool(AppDefaults.LaunchAtLogin, true);
        InstantRecordingEnabled = _settings.GetBool(AppDefaults.InstantRecordingEnabled, true);
    }

    /// <summary>
    /// The binding for one role. TOTAL over <see cref="HotkeyRole"/> and throws on an unmapped
    /// member — a new role must fail loudly here rather than silently reading as unbound, which is
    /// how onboarding came to be blind to the generate-image hotkey for its whole existence.
    /// </summary>
    internal string HotkeyFor(HotkeyRole role) => role switch
    {
        HotkeyRole.Recording => HotkeyModifier,
        HotkeyRole.PasteLast => PasteLastHotkeyModifier,
        HotkeyRole.RedoLast => RedoLastHotkeyModifier,
        HotkeyRole.GenerateImage => GenerateImageHotkeyModifier,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unmapped hotkey role"),
    };

    /// <summary>Set one role's binding. Total, for the same reason as <see cref="HotkeyFor"/>.</summary>
    internal void SetHotkey(HotkeyRole role, string value)
    {
        switch (role)
        {
            case HotkeyRole.Recording: HotkeyModifier = value; break;
            case HotkeyRole.PasteLast: PasteLastHotkeyModifier = value; break;
            case HotkeyRole.RedoLast: RedoLastHotkeyModifier = value; break;
            case HotkeyRole.GenerateImage: GenerateImageHotkeyModifier = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(role), role, "Unmapped hotkey role");
        }
    }

    /// <summary>Every binding currently held, for a conflict scan.</summary>
    internal HotkeyBindingSnapshot HotkeySnapshot()
    {
        var roles = new Dictionary<HotkeyRole, string?>();
        foreach (var role in HotkeyBindingSnapshot.AllRoles) roles[role] = HotkeyFor(role);
        return new HotkeyBindingSnapshot(roles, HotkeyPrompts.Read());
    }

    partial void OnHotkeyModifierChanged(string value)
    {
        _settings.SetString(AppDefaults.HotkeyModifier, value);
        _hotkeyService.ReloadHotkeys();
        Logger.Information("Hotkey changed to: {Key}", value);
    }

    partial void OnPasteLastHotkeyModifierChanged(string value)
    {
        _settings.SetString(AppDefaults.PasteLastHotkeyModifier, value);
        _hotkeyService.ReloadHotkeys();
        Logger.Information("Paste-last hotkey changed to: {Key}", value);
    }

    partial void OnRedoLastHotkeyModifierChanged(string value)
    {
        _settings.SetString(AppDefaults.RedoLastHotkeyModifier, value);
        _hotkeyService.ReloadHotkeys();
        Logger.Information("Redo-last hotkey changed to: {Key}", value);
    }

    partial void OnGenerateImageHotkeyModifierChanged(string value)
    {
        _settings.SetString(AppDefaults.GenerateImageHotkeyModifier, value);
        _hotkeyService.ReloadHotkeys();
        Logger.Information("Generate-image hotkey changed to: {Key}", value);
    }

    // UI-11: the GPU acceleration toggle lives on the Models page now, and its whole operation —
    // persist, log the flip, re-arm the TRN-50 self-test — moved to GpuAccelerationPreference so
    // both that row and ResetAllSettings below run the same code. This ViewModel deliberately
    // keeps NO cached copy of the key: an [ObservableProperty] here would short-circuit on an
    // equal value, and with a writer outside this VM that cache goes stale — which is exactly how
    // "Reset all settings" would silently skip this one key.

    partial void OnInstantRecordingEnabledChanged(bool value)
    {
        _settings.SetBool(AppDefaults.InstantRecordingEnabled, value);
        // Applies live: ON starts the standing capture (behind its own gate); OFF tears it
        // down — latched until detach if a recording is mid-drain (AUD-6 B5).
        _standingCapture.SetEnabled(value);
        Logger.Information("Instant recording {State}", value ? "enabled" : "disabled");
    }

    partial void OnIsSoundFeedbackEnabledChanged(bool value)
    {
        _settings.SetBool(AppDefaults.IsSoundFeedbackEnabled, value);
    }

    partial void OnIsSystemMuteEnabledChanged(bool value)
    {
        _settings.SetBool(AppDefaults.IsSystemMuteEnabled, value);
    }

    partial void OnAudioResumptionDelayChanged(double value)
    {
        _settings.SetDouble(AppDefaults.AudioResumptionDelay, value);
    }

    partial void OnIsTextFormattingEnabledChanged(bool value)
    {
        _settings.SetBool(AppDefaults.IsTextFormattingEnabled, value);
    }

    partial void OnRemoveFillerWordsChanged(bool value)
    {
        _settings.SetBool(AppDefaults.RemoveFillerWords, value);
    }

    partial void OnFillerWordsChanged(string value)
    {
        _settings.SetString(AppDefaults.FillerWords, value);
    }

    partial void OnAppendTrailingSpaceChanged(bool value)
    {
        _settings.SetBool(AppDefaults.AppendTrailingSpace, value);
    }

    partial void OnRestoreClipboardAfterPasteChanged(bool value)
    {
        _settings.SetBool(AppDefaults.RestoreClipboardAfterPaste, value);
    }

    partial void OnSendEnterAfterPasteChanged(bool value)
    {
        _settings.SetBool(AppDefaults.SendEnterAfterPaste, value);
    }

    partial void OnClipboardRestoreDelayChanged(double value)
    {
        _settings.SetDouble(AppDefaults.ClipboardRestoreDelay, value);
    }

    partial void OnLaunchAtLoginChanged(bool value)
    {
        _settings.SetBool(AppDefaults.LaunchAtLogin, value);
        _autostart.Apply(value);
    }

    partial void OnThemeModeChanged(string value)
    {
        _settings.SetString(AppDefaults.ThemeMode, value);
        if (Enum.TryParse<ThemeMode>(value, out var mode))
            AppTheme.SetTheme(mode);
    }

    [RelayCommand]
    public void ResetFillerWords()
    {
        // The ONE source — this held its own copy of the list until 2026-09-13, so a change to the
        // shipped default and a change to what "Reset" restores were two edits that could disagree.
        FillerWords = AppDefaults.DefaultFillerWords;
    }

    private (string? Id, string? Name) ReadMicrophonePin()
    {
        var id = _settings.GetString(AppDefaults.RecordingDeviceId, "");
        var name = _settings.GetString(AppDefaults.RecordingDeviceName, "");
        return (string.IsNullOrWhiteSpace(id) ? null : id,
                string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>AUD-1: IMMEDIATE Microphone dropdown content — settings only, NO enumeration
    /// (diff review: the initial populate must never wait on a possibly-wedged STA pass to show
    /// even "System default"). A pin renders as its "(unavailable)" entry until the async
    /// snapshot replaces the list.</summary>
    public (IReadOnlyList<MicComboItem> Items, int SelectedIndex, string? ResolvedLabel) BuildMicrophoneListImmediate()
    {
        var (id, name) = ReadMicrophonePin();
        var (items, selected) = MicrophoneListBuilder.Build([], id, name);
        // AUD-17: no enumeration has run, so there is nothing to resolve. The empty list above is
        // a PLACEHOLDER for the builder, not a device snapshot — passing it to Describe would
        // render "No microphone found" on every Settings open until the async refresh lands.
        return (items, selected, null);
    }

    /// <summary>AUD-1: async device snapshot → Microphone dropdown items + selected index
    /// (pure rules in <see cref="MicrophoneListBuilder"/>; enumeration runs off the UI thread,
    /// coalesced by <c>AudioDeviceManager.RefreshDevicesAsync</c>).</summary>
    public async Task<(IReadOnlyList<MicComboItem> Items, int SelectedIndex, string? ResolvedLabel)> BuildMicrophoneListAsync()
    {
        var devices = await _deviceManager.RefreshDevicesAsync().ConfigureAwait(false);
        var (id, name) = ReadMicrophonePin();
        var (items, selected) = MicrophoneListBuilder.Build(devices, id, name);
        // AUD-17: computed from the SAME snapshot the items came from, so the label can never
        // describe a different enumeration than the dropdown is showing.
        return (items, selected, ResolvedMicrophoneLabel.Describe(devices, id));
    }

    /// <summary>AUD-1: persist the Microphone dropdown choice. "System default" (null id)
    /// REMOVES both keys — absence IS the default; a device pins the endpoint id
    /// (authoritative) + the clean device name (display metadata only).</summary>
    public void SetMicrophoneSelection(MicComboItem item)
    {
        if (item.Id == null)
        {
            _settings.Remove(AppDefaults.RecordingDeviceId);
            _settings.Remove(AppDefaults.RecordingDeviceName);
            Logger.Information("Recording device selection cleared (system default)");
            // AUD-6: clearing back to the system default is a selection change too.
            _standingCapture.NotifyDeviceSelectionChanged();
            return;
        }

        _settings.SetString(AppDefaults.RecordingDeviceId, item.Id);
        if (item.PersistName is { } cleanName)
            _settings.SetString(AppDefaults.RecordingDeviceName, cleanName);
        else
            _settings.Remove(AppDefaults.RecordingDeviceName);
        // SEC-3: the persisted name ORIGINATES from the endpoint FriendlyName — users rename
        // endpoints, so it is user-identifying even though the literal "FriendlyName" never
        // appears here (which is exactly why the first SEC-3 sweep missed this site). Unlike the
        // capture-sessions line, there is no adjacent line carrying the useful part, so the name
        // is REDACTED rather than dropped: {RecordingDeviceName} is on the allowlist and the
        // rendered `micName=` token is scrubbed, so the local file keeps it and Sentry does not.
        // Token stays LAST — the rendered scrub is anchored to end-of-line. The token is
        // deliberately distinctive rather than a short `dev=`: over-redaction silently destroys
        // a useful field, and a short token is easy for unrelated prose to collide with.
        Logger.Information("Recording device pinned: micName={RecordingDeviceName}",
            Helpers.LogValueSanitizer.SingleLine(item.PersistName ?? "<id-only>"));

        // AUD-6: the standing capture follows the selection — debounced rebuild onto the new
        // device (also re-evaluates a BT/no-mic park, whose cause this change can remove).
        _standingCapture.NotifyDeviceSelectionChanged();
    }

    [RelayCommand]
    public void ResetAllSettings()
    {
        // HKY-4 reaches the reset too (2026-09-13). The table's "RightAlt" IS AltGr on every layout
        // the first-run seed exists for, so writing it verbatim handed a Belgian/German/French user
        // back the exact key the wizard had steered them away from — and with it the loss of
        // € @ [ ] { }. Same seed, same layout question, and the absent-value form (null = "nothing
        // chosen"): a reset is by definition the moment nothing is chosen.
        HotkeyModifier = HotkeyDefaultSeed.Resolve(null, _altGrLayoutProbe());
        PasteLastHotkeyModifier = (string)AppDefaults.Defaults[AppDefaults.PasteLastHotkeyModifier];
        RedoLastHotkeyModifier = (string)AppDefaults.Defaults[AppDefaults.RedoLastHotkeyModifier];
        GenerateImageHotkeyModifier = (string)AppDefaults.Defaults[AppDefaults.GenerateImageHotkeyModifier];
        IsSoundFeedbackEnabled = (bool)AppDefaults.Defaults[AppDefaults.IsSoundFeedbackEnabled];
        IsSystemMuteEnabled = (bool)AppDefaults.Defaults[AppDefaults.IsSystemMuteEnabled];
        AudioResumptionDelay = (double)AppDefaults.Defaults[AppDefaults.AudioResumptionDelay];
        IsTextFormattingEnabled = (bool)AppDefaults.Defaults[AppDefaults.IsTextFormattingEnabled];
        RemoveFillerWords = (bool)AppDefaults.Defaults[AppDefaults.RemoveFillerWords];
        FillerWords = (string)AppDefaults.Defaults[AppDefaults.FillerWords];
        AppendTrailingSpace = (bool)AppDefaults.Defaults[AppDefaults.AppendTrailingSpace];
        RestoreClipboardAfterPaste = (bool)AppDefaults.Defaults[AppDefaults.RestoreClipboardAfterPaste];
        SendEnterAfterPaste = (bool)AppDefaults.Defaults[AppDefaults.SendEnterAfterPaste];
        ClipboardRestoreDelay = (double)AppDefaults.Defaults[AppDefaults.ClipboardRestoreDelay];
        ThemeMode = (string)AppDefaults.Defaults[AppDefaults.ThemeMode];
        LaunchAtLogin = (bool)AppDefaults.Defaults[AppDefaults.LaunchAtLogin];
        InstantRecordingEnabled = (bool)AppDefaults.Defaults[AppDefaults.InstantRecordingEnabled];

        // Settings not owned by this ViewModel but should still reset
        // UI-11: GPU acceleration is in this group now — the Models page owns its row, so this VM
        // holds no cached copy to assign through. GpuAccelerationPreference.Write is the same call
        // that row makes, so a reset re-arms the self-test exactly like a manual flip, and no-ops
        // when the value is already the default.
        GpuAccelerationPreference.Write(_settings, (bool)AppDefaults.Defaults[AppDefaults.GpuAccelerationEnabled]);
        _settings.SetBool(AppDefaults.IsHistoryEnabled, (bool)AppDefaults.Defaults[AppDefaults.IsHistoryEnabled]);
        _settings.SetBool(AppDefaults.IsTranscriptionCleanupEnabled, (bool)AppDefaults.Defaults[AppDefaults.IsTranscriptionCleanupEnabled]);
        _settings.SetInt(AppDefaults.TranscriptionRetentionMinutes, (int)AppDefaults.Defaults[AppDefaults.TranscriptionRetentionMinutes]);
        _settings.SetBool(AppDefaults.IsDiarizationEnabled, (bool)AppDefaults.Defaults[AppDefaults.IsDiarizationEnabled]);
        // PRM-3: the per-provider "Send dictionary and trigger words" toggles reset to
        // their defaults (both ON, owner decision 2026-07-18) like any other
        // preference. The master Enable Dictionary toggle (Dictionary page) resets the
        // same way.
        _settings.SetBool(AppDefaults.ElevenLabsKeytermsEnabled, (bool)AppDefaults.Defaults[AppDefaults.ElevenLabsKeytermsEnabled]);
        _settings.SetBool(AppDefaults.DeepgramKeytermsEnabled, (bool)AppDefaults.Defaults[AppDefaults.DeepgramKeytermsEnabled]);
        // OpenAI's equivalent, reading the default from the table rather than restating it: this
        // key shipped OFF on 2026-08-01 and ON on 2026-08-02, and a literal here would have been
        // wrong within a day.
        _settings.SetBool(AppDefaults.OpenAIKeywordsEnabled, (bool)AppDefaults.Defaults[AppDefaults.OpenAIKeywordsEnabled]);
        _settings.SetBool(AppDefaults.DictionaryEnabled, (bool)AppDefaults.Defaults[AppDefaults.DictionaryEnabled]);
        // StartMinimized + MinimizeToTray toggles are owned by SettingsPage directly (not VM
        // properties), so reset them through the service like the other non-VM-owned settings above.
        _settings.SetBool(AppDefaults.StartMinimized, (bool)AppDefaults.Defaults[AppDefaults.StartMinimized]);
        _settings.SetBool(AppDefaults.MinimizeToTray, (bool)AppDefaults.Defaults[AppDefaults.MinimizeToTray]);
        _settings.SetBool(AppDefaults.HidePillFromCapture, (bool)AppDefaults.Defaults[AppDefaults.HidePillFromCapture]);

        // Auto-updates (UPD-1). The toggle is a user setting (in Defaults);
        // LastUpdateCheckUtc is an app-managed timestamp (NOT in Defaults,
        // matches LicenseLastValidatedUtc shape) but the user expects
        // "Reset all settings" to also clear visible history-style values
        // like "Last checked: …". Codex 2026-05-20 diff review flagged the
        // missing reset coverage.
        // UPD-4b: BOTH update preferences reset through the DURABLE apply, not bare SetBool —
        // these two are the egress/restart-consequential settings, and the fail-off rule the
        // toggle surfaces enforce must hold here too: a reset whose write never reached disk
        // (suppressed persistence after a failed erasure pass, a disk failure) previously left
        // the in-memory values `true`, armed the scheduler, and let the app check and RESTART
        // on preferences that would silently revert next launch (Codex diff r2). On failure
        // Apply forces the effective value OFF, and the scheduler is notified with what is
        // actually in effect. Defaults still come from the table, never a literal.
        var resetCheck = DiagnosticsConsent.Apply(_settings, AppDefaults.AutomaticUpdateCheckEnabled,
            (bool)AppDefaults.Defaults[AppDefaults.AutomaticUpdateCheckEnabled]);
        DiagnosticsConsent.Apply(_settings, AppDefaults.AutomaticUpdateInstallEnabled,
            (bool)AppDefaults.Defaults[AppDefaults.AutomaticUpdateInstallEnabled]);
        _settings.SetString(AppDefaults.LastUpdateCheckUtc, "");
        // UPD-4b (pre-existing defect, fixed here): restoring the check toggle wrote the setting
        // but never told the scheduler, and there is no SettingsService change event — so a user
        // who had checks OFF and reset got the preference back ON with NO armed poll loop until
        // the next launch. UpdateViewModel's toggle was the only caller of this notify. Notified
        // with the EFFECTIVE value so a failed reset cannot arm polling for a preference that is
        // not on disk. Null only in constructions that don't supply the scheduler (tests).
        _updateScheduler?.OnAutomaticSettingChanged(resetCheck.Effective);

        // MiniRecorder dragged position (PILL-4) — app-managed (NOT in Defaults);
        // absence IS the default (top-center), so reset means removing the keys.
        _settings.Remove(AppDefaults.MiniRecorderPosFractionX);
        _settings.Remove(AppDefaults.MiniRecorderPosFractionY);

        // Pinned recording device (AUD-1) — app-managed (NOT in Defaults);
        // absence IS the default (system default mic), so reset removes the keys.
        _settings.Remove(AppDefaults.RecordingDeviceId);
        _settings.Remove(AppDefaults.RecordingDeviceName);
        // AUD-6 (Codex diff r1): the standing capture must follow the reset off the old pinned
        // mic. The InstantRecordingEnabled property above may already have kicked a start while
        // the old keys were still present — the debounced rebuild here re-resolves AFTER the
        // removal, so the stream converges on the system default either way.
        _standingCapture.NotifyDeviceSelectionChanged();

        // REL-17 diagnostics opt-ins (Settings -> Diagnostics owns the toggles). Device-local
        // consent — reset must switch them off; the trace key was also a pre-existing
        // gap in this list back when it was Debug-only.
        _settings.SetBool(AppDefaults.PromptTraceLoggingOptIn, (bool)AppDefaults.Defaults[AppDefaults.PromptTraceLoggingOptIn]);
        _settings.SetBool(AppDefaults.PromptTraceIncludeImagePrompts, (bool)AppDefaults.Defaults[AppDefaults.PromptTraceIncludeImagePrompts]);
        _settings.SetBool(AppDefaults.KeepRecordingsForDebug, (bool)AppDefaults.Defaults[AppDefaults.KeepRecordingsForDebug]);
        // The Log Viewer's verbose toggle is a preference like the three above and was the one
        // diagnostics switch the reset skipped (2026-09-13). The sink's level switch follows the
        // setting here as it does under the toggle, so the log is back at Information the moment
        // the reset lands. An already-running heartbeat keeps ticking until the Log Viewer toggle
        // is next touched or the app restarts (DebugHeartbeat.Refresh needs a dispatcher this
        // ViewModel does not hold) — a 5 s log line, not a behaviour the user can notice.
        var verboseDefault = (bool)AppDefaults.Defaults[AppDefaults.VerboseLoggingEnabled];
        _settings.SetBool(AppDefaults.VerboseLoggingEnabled, verboseDefault);
        LogLevelControl.Apply(verboseDefault);

        // Cached OpenRouter image capabilities (IMG-5) — app-managed (NOT in Defaults, which is
        // what keeps it out of settings export/import). This list is ENUMERATED, so an unregistered
        // key survives it unless removed explicitly — the same omission Codex flagged for
        // LastUpdateCheckUtc above. Absence is the correct reset state: the next model refresh
        // repopulates it, and until then the static rules answer.
        _settings.Remove(Services.AIEnhancement.AIEnhancementService.ImageCapabilitiesKey);

        Logger.Information("All settings reset to defaults");
    }
}
