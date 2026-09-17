using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Services.Audio;
using VoiceWink.Services.Data;
using VoiceWink.Services.System;
using VoiceWink.Services.TextProcessing;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Audio file transcription page — polished dark drop zone + transcription result.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class AudioTranscribePage : Page
{
    private static ILogger Logger => Log.ForContext<AudioTranscribePage>();

    private readonly AudioFileProcessor _processor;
    private readonly TranscriptionServiceRegistry _registry;
    private readonly LocalModelPreparer _localModels;
    private readonly CustomVocabularyService _customVocabulary;
    private readonly SettingsService _settings;
    private readonly ClipboardService _clipboard;
    private readonly TranscriptionOutputFilter _outputFilter;
    private readonly FillerWordManager _fillerWordManager;
    private readonly TranscriptionTextFormatter _textFormatter;
    private readonly WordReplacementService _wordReplacement;
    private TextBlock? _resultText;
    private ProgressRing? _progress;
    private TextBlock? _statusText;
    private TextBlock? _modelText;

    /// <summary>UI-3: the Select/Cancel toggle. Its face is driven from the same places that used to
    /// show and hide the old separate Cancel button, including RefreshFromStaticState — a transcription survives page
    /// recreation (F27), so a re-attached page must show the right face, not the default one.</summary>
    private AppTheme.ActionToggleButton? _actionButton;

    private Border? _copyButton;

    // Static so transcription survives page recreation when navigating away.
    // Only the Cancel button should cancel — not page navigation.
    private static CancellationTokenSource? _cts;
    // volatile: AudioTranscribeMaintenanceSource exposes this field to
    // IMaintenanceGate.Check() callers, which may run on any thread (e.g. the
    // future ApplyAndRestartAsync path). Without volatile, a non-UI thread
    // could read a stale `false` after the UI thread set it `true`, defeating
    // the gate's purpose.
    private static volatile bool _isTranscribing;
    private static string? _lastResult;
    private static string? _lastStatus;

    // Raised after the static snapshot above is updated at transcription completion, so the
    // CURRENTLY-DISPLAYED page instance (possibly a different one than started the job —
    // pages are recreated per navigation) refreshes live instead of showing "Transcription
    // in progress..." until the next navigation (F27). Static event: every subscriber MUST
    // unsubscribe in Unloaded or the page instance leaks (the F13 bug class).
    private static event Action? TranscriptionStateChanged;

    // Exposed for AudioTranscribeMaintenanceSource (UPD-1 / IMaintenanceGate): the
    // gate consults this to block ApplyAndRestartAsync / Reset-all-data while a
    // file transcription is in flight. internal so the source factory in
    // App.xaml.cs can wire its probe lambda without going through reflection.
    internal static bool IsTranscribing => _isTranscribing;

    public AudioTranscribePage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        _processor = App.Services.GetRequiredService<AudioFileProcessor>();
        _registry = App.Services.GetRequiredService<TranscriptionServiceRegistry>();
        _localModels = App.Services.GetRequiredService<LocalModelPreparer>();
        _customVocabulary = App.Services.GetRequiredService<CustomVocabularyService>();
        _settings = App.Services.GetRequiredService<SettingsService>();
        _clipboard = App.Services.GetRequiredService<ClipboardService>();
        _outputFilter = App.Services.GetRequiredService<TranscriptionOutputFilter>();
        _fillerWordManager = App.Services.GetRequiredService<FillerWordManager>();
        _textFormatter = App.Services.GetRequiredService<TranscriptionTextFormatter>();
        _wordReplacement = App.Services.GetRequiredService<WordReplacementService>();
        BuildUI();
        Loaded += (_, _) =>
        {
            _isUnloaded = false;
            UpdateModelDisplay();
            // Detach-before-attach: Loaded can refire without a paired Unloaded, and a
            // duplicate STATIC subscription would permanently retain the page (Codex R1).
            TranscriptionStateChanged -= OnTranscriptionStateChanged;
            TranscriptionStateChanged += OnTranscriptionStateChanged;
            RefreshFromStaticState();
        };
        Unloaded += (_, _) =>
        {
            _isUnloaded = true;
            // Balanced unsubscribe — a static event otherwise pins every past page instance.
            TranscriptionStateChanged -= OnTranscriptionStateChanged;
        };
    }

    // Fences a queued refresh that lands after this instance unloaded (Codex R1).
    private bool _isUnloaded;

    private void OnTranscriptionStateChanged()
    {
        // The raiser runs on the shared UI thread, but marshal defensively — the handler
        // touches this instance's controls.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isUnloaded) return;
            RefreshFromStaticState();
        });
    }

    // Shared by Loaded (restore after navigating back) and the completion event (live update
    // of whichever instance is currently displayed). The what-to-render decision is the pure,
    // matrix-tested AudioTranscribeRestoreState (F27, Codex R1+R2) — including the both-ways
    // result-card rule (visible IFF non-empty text; an already-visible card COLLAPSES for an
    // empty/failed/cancelled run instead of rendering a blank card that reads as success).
    /// <summary>
    /// Put the action button on the face that matches the current work (UI-3).
    ///
    /// <para>The single place that used to set the old Cancel button's visibility, so the toggle cannot
    /// drift from the state — including from <see cref="RefreshFromStaticState"/>, which matters
    /// because a transcription survives page recreation (F27) and a re-attached page would otherwise
    /// show "Select Audio File" while work was still running.</para>
    /// </summary>
    private void SetTranscribing(bool transcribing)
    {
        if (_actionButton is null) return;

        if (transcribing) _actionButton.ShowCancel();
        else _actionButton.ShowPrimary();
    }

    private void RefreshFromStaticState()
    {
        if (AudioTranscribeRestoreState.Decide(_isTranscribing, _lastResult, _lastStatus) is not { } plan)
            return; // fresh — leave the page in its default state

        _progress!.IsActive = plan.ProgressActive;
        SetTranscribing(plan.CancelVisible);
        if (plan.StatusText != null)
            _statusText!.Text = plan.StatusText;
        if (plan.ResultText != null)
            _resultText!.Text = plan.ResultText;
        if (plan.ResultCardVisible is { } cardVisible)
            _resultCard!.Visibility = cardVisible ? Visibility.Visible : Visibility.Collapsed;
        if (plan.CopyVisible is { } copyVisible)
            _copyButton!.Visibility = copyVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildUI()
    {
        // ── Drop zone icon ──────────────────────────────────────────
        var dropIcon = new FontIcon
        {
            Glyph = "\uE896",
            FontSize = 48,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };

        // ── "Drop audio file here" text ─────────────────────────────
        var dropText = new TextBlock
        {
            Text = "Drop audio file here",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };

        // ── "or" separator ──────────────────────────────────────────
        var orText = new TextBlock
        {
            Text = "or",
            FontSize = 14,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 12)
        };

        // ── Select file / Cancel — ONE button (UI-3) ────────────────
        //
        // The owner's §91.5 rule is universal, and this surface qualified: during a transcription
        // "Select Audio File" stayed ACTIVE, opened the picker, and then discarded the chosen file
        // on the _isTranscribing guard with only a Warning log. An active control that throws away
        // input is worse than one that has become the action you actually want.
        _actionButton = AppTheme.CreateActionToggleButton("Select Audio File", "Cancel Transcription");
        _actionButton.Element.HorizontalAlignment = HorizontalAlignment.Center;
        _actionButton.Element.Padding = new Thickness(24, 10, 24, 10);
        _actionButton.Element.Tapped += async (_, _) =>
        {
            if (_actionButton.IsCancelling)
            {
                // NO arming guard here, deliberately. The onboarding download button morphs to
                // Cancel INSTANTLY under the cursor, which is the accident the guard exists for.
                // This page has no such window: every route to the cancel face goes through a file
                // picker, a drag-and-drop, a size-warning dialog, or a page restore — none of which
                // is a click on THIS button. Guarding here suppressed the user's FIRST deliberate
                // Cancel while a cloud upload was already starting (Codex diff review r4).
                CancelTranscription();
                return;
            }
            await SelectAndTranscribeAsync();
        };
        var selectBtn = _actionButton.Element;

        // ── Supported formats text ──────────────────────────────────
        var formatsText = new TextBlock
        {
            Text = "Supported formats: WAV, MP3, M4A, FLAC, OGG",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };

        // ── Drop zone inner content ─────────────────────────────────
        var dropZoneContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dropIcon, dropText, orText, selectBtn, formatsText }
        };

        // ── Dashed-style drop zone border ───────────────────────────
        // WinUI 3 doesn't support dashed borders natively, so we emulate with
        // a thicker border + larger corner radius on a transparent card look.
        var dropZoneBorder = new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(48, 56, 48, 56),
            MinHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            AllowDrop = true,
            Child = dropZoneContent
        };

        // Hover highlight brushes for drag-over visual feedback
        var defaultBorderBrush = AppTheme.Brush(AppTheme.CardBorderColor);
        var defaultBackground = AppTheme.Brush(AppTheme.CardBg);
        var highlightBorderBrush = AppTheme.Brush(AppTheme.AccentBlue);
        var highlightBackground = AppTheme.Brush(ColorHelper.FromArgb(20, 0, 122, 255));

        // DragOver: show cursor feedback, highlight the drop zone, clear stale errors
        dropZoneBorder.DragOver += (s, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to transcribe";
            e.DragUIOverride.IsCaptionVisible = true;
            dropZoneBorder.BorderBrush = highlightBorderBrush;
            dropZoneBorder.Background = highlightBackground;
            _statusText!.Text = "";
        };

        // DragLeave: restore default appearance when drag exits the zone
        dropZoneBorder.DragLeave += (s, e) =>
        {
            dropZoneBorder.BorderBrush = defaultBorderBrush;
            dropZoneBorder.Background = defaultBackground;
        };

        // Drop: restore appearance and transcribe the dropped file
        dropZoneBorder.Drop += async (s, e) =>
        {
            dropZoneBorder.BorderBrush = defaultBorderBrush;
            dropZoneBorder.Background = defaultBackground;

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.OfType<Windows.Storage.StorageFile>().FirstOrDefault();
                if (file != null)
                {
                    var ext = global::System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
                    if (ext is ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg")
                        await TranscribeFileAsync(file.Path);
                    else
                        _statusText!.Text = "Unsupported file format. Use WAV, MP3, M4A, FLAC, or OGG.";
                }
            }
        };

        // ── Model display text ──────────────────────────────────────
        _modelText = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };

        // ── Progress ring (centered, hidden by default) ─────────────
        _progress = new ProgressRing
        {
            IsActive = false,
            Width = 36,
            Height = 36,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0)
        };

        // The old separate Cancel button is gone (UI-3) — the action button above carries both
        // faces. Nothing else referenced it, so removing it removes a second source of truth.

        // ── Status text ─────────────────────────────────────────────
        _statusText = new TextBlock
        {
            FontSize = 14,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };

        // ── Result text inside a card ───────────────────────────────
        _resultText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            IsTextSelectionEnabled = true
        };

        // "Copy to Clipboard" button — copies transcription result text.
        // Hidden until a successful transcription populates _resultText.
        _copyButton = AppTheme.CreateSecondaryButton("Copy to Clipboard", async (_, _) =>
        {
            await CopyResultToClipboardAsync();
        });
        _copyButton.HorizontalAlignment = HorizontalAlignment.Center;
        _copyButton.Margin = new Thickness(0, 12, 0, 0);
        _copyButton.Visibility = Visibility.Collapsed;

        var resultCardContent = new StackPanel
        {
            Children = { _resultText, _copyButton }
        };

        var resultCard = AppTheme.CreateCard(resultCardContent);
        resultCard.Visibility = Visibility.Collapsed;
        resultCard.Margin = new Thickness(0, 16, 0, 0);

        // Keep a reference so we can show/hide the card
        _resultCard = resultCard;

        // ── Speaker Diarization toggle (only for file transcription) ──
        var diarizationToggle = AppTheme.CreateToggleSetting(
            "Speaker Diarization",
            "Identify different speakers (Deepgram and ElevenLabs only)",
            _settings.GetBool(AppDefaults.IsDiarizationEnabled, false),
            v => _settings.SetBool(AppDefaults.IsDiarizationEnabled, v));
        diarizationToggle.Margin = new Thickness(0, 16, 0, 0);

        // ── Page layout ─────────────────────────────────────────────
        var pageContent = new StackPanel
        {
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                AppTheme.CreatePageHeader("Transcribe Audio File", centered: true),
                dropZoneBorder,
                diarizationToggle,
                _modelText,
                _progress,
                _statusText,
                resultCard
            }
        };

        AppTheme.SetPageScrollContent(this, pageContent);
    }

    private Border? _resultCard;

    /// <summary>
    /// Known upload file size limits (in MB) for cloud transcription providers.
    /// Returns 0 for providers with no known limit or no applicable limit (local models,
    /// Mistral, Gemini, Soniox). A return of 0 means "do not warn".
    /// </summary>
    private static int GetProviderFileSizeLimitMB(Models.Enums.ModelProvider provider) => provider switch
    {
        Models.Enums.ModelProvider.Groq => 25,
        Models.Enums.ModelProvider.OpenAI => 25,
        Models.Enums.ModelProvider.Deepgram => 2000,
        Models.Enums.ModelProvider.ElevenLabs => 3000,
        _ => 0  // no known limit or not applicable — skip warning
    };

    /// <summary>
    /// Refresh the model display text from current settings.
    /// Called from BuildUI (initial value) and from Loaded event (refresh on navigation).
    /// </summary>
    private void UpdateModelDisplay()
    {
        var selectedModel = _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel);
        _modelText!.Text = $"Model: {ModelDisplayName.Resolve(selectedModel)}";
    }

    /// <summary>
    /// Cancel the in-flight transcription. Does NOT reset UI directly --
    /// the OperationCanceledException catch block and finally block in
    /// TranscribeFileAsync handle all UI cleanup.
    /// </summary>
    private void CancelTranscription()
    {
        Logger.Information("User requested transcription cancellation");
        _cts?.Cancel();
    }

    /// <summary>
    /// Copy the current transcription result to the clipboard.
    /// Uses the Win32 clipboard API via ClipboardService for consistency
    /// with the rest of the app.
    /// </summary>
    private async Task CopyResultToClipboardAsync()
    {
        var text = _resultText?.Text;
        if (string.IsNullOrEmpty(text)) return;

        if (await _clipboard.SetClipboardAsync(text))
        {
            Logger.Information("Transcription result copied to clipboard ({Length} chars)", text.Length);
            _statusText!.Text = "Copied to clipboard.";
        }
        else
        {
            Logger.Warning("Failed to copy transcription result to clipboard");
            _statusText!.Text = "Could not copy to clipboard. Try selecting the text above and copying manually.";
        }
    }

    private async Task SelectAndTranscribeAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".flac");
        picker.FileTypeFilter.Add(".ogg");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;

        var mainWindow = App.MainWindow;
        if (mainWindow == null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        await TranscribeFileAsync(file.Path);
    }

    private async Task TranscribeFileAsync(string filePath)
    {
        // Prevent concurrent transcriptions (e.g. rapid double-drop)
        if (_isTranscribing)
        {
            Logger.Warning("Transcription already in progress, ignoring drop");
            // UI-3 review: the refusal used to be LOG-ONLY, so "Select Audio File" stayed active
            // during a transcription, opened the picker, took the user's choice — and dropped it
            // silently. An active control that discards input is worse than a disabled one; say so.
            if (_statusText is not null)
                _statusText.Text = "Already transcribing — cancel the current file first.";
            return;
        }

        // Update-apply gate (UPD-1 Phase 3): refuse a new file transcription while an update is being
        // applied — it would be interrupted by the restart.
        if (App.IsExclusiveMaintenanceActive())
        {
            Logger.Information("File transcription blocked: an update is being applied.");
            _statusText!.Text = "Update in progress — please wait before transcribing";
            return;
        }

        // TRN-49: file transcription is real work too — without this, a first-launch file
        // transcribe queues behind the Whisper warm decode's model lock (up to the full compile)
        // and shares CPU/RAM with the warm Parakeet child (self-review, regression lens). Same
        // session-permanent cancel recording admission uses.
        GpuWarmup.Instance.Cancel(GpuWarmupCancelReason.AudioTranscribe);

        // Create a fresh CTS for this transcription
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _isTranscribing = true;
        _lastResult = null;
        _lastStatus = null;
        var ct = _cts.Token;

        _progress!.IsActive = true;
        SetTranscribing(true);
        _statusText!.Text = "Converting audio...";
        _resultText!.Text = "";
        _resultCard!.Visibility = Visibility.Collapsed;
        _copyButton!.Visibility = Visibility.Collapsed;

        // Phase 1: Transcription — errors here are file/model problems.
        string? transcribedText = null;
        string? wavPath = null;
        try
        {
            // Convert to WAV. The token IS threaded (AUD-9) — the decode itself is uninterruptible,
            // but the normalization after it observes cancellation, and a cancelled call deletes its
            // conversion rather than stranding it in the recordings root.
            wavPath = await _processor.ConvertToWavAsync(filePath, ct);

            ct.ThrowIfCancellationRequested();

            // Ensure model is ready — cloud models don't need a local download.
            // Canonicalize (PRM-3): a retired or oddly-cased persisted name must
            // classify as its cloud successor — an exact lookup would treat it as a
            // local model and refuse with "not downloaded" (Codex diff review).
            var selectedModel = Models.CloudModels.Canonicalize(
                _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel));
            var cloudModel = Models.CloudModels.Models.FirstOrDefault(m => m.Name == selectedModel);
            var isCloudModel = cloudModel != null;

            // Warn if WAV exceeds the cloud provider's known file size limit
            if (isCloudModel)
            {
                var wavSize = new global::System.IO.FileInfo(wavPath).Length;
                var limitMB = GetProviderFileSizeLimitMB(cloudModel!.Provider);
                if (limitMB > 0 && wavSize > limitMB * 1024L * 1024L)
                {
                    _progress!.IsActive = false;
                    SetTranscribing(false);
                    var sizeMB = wavSize / (1024.0 * 1024.0);
                    var dialog = new ContentDialog
                    {
                        Title = "File may be too large",
                        Content = $"The converted audio is {sizeMB:F0} MB, which exceeds {cloudModel.Provider}'s {limitMB} MB upload limit. The transcription will likely fail.\n\nYou can try a shorter audio file, or switch to a provider with a higher limit (Deepgram or ElevenLabs).",
                        PrimaryButtonText = "Try Anyway",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = AppTheme.ElementTheme
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        _statusText!.Text = "";
                        return;
                    }
                    _progress!.IsActive = true;
                    SetTranscribing(true);
                }
            }

            // Resolved ONCE, before preparation, and used for BOTH prepare and transcribe below.
            //
            // It was previously resolved here and then the RAW setting was re-read at the transcribe
            // call — so an English-only model prepared as `en` and then transcribed as `nl`, making
            // WhisperTranscriptionService rebuild its processor under the language the prepare step
            // had already ruled out, and rebuild again on the next recording. That is precisely the
            // prepare-vs-transcribe disagreement this helper exists to prevent, reintroduced one
            // file over; a diff reviewer caught it twice, in two different places.
            // The REQUESTED language is kept beside the effective one: the filler stage below gates
            // on what the user asked for (Parakeet's effective language is always `auto`, which
            // would re-apply the English list to a German file — the live-dictation site has the
            // same split, and this page mirrors it).
            var requestedLanguage = _settings.GetString(AppDefaults.SelectedLanguage, "auto");
            var effectiveLanguage = Helpers.EffectiveTranscriptionLanguage
                .ForModelName(selectedModel, requestedLanguage).Language;

            if (!isCloudModel)
            {
                _statusText.Text = "Loading model...";
                // Same model constraint the recording path applies: an English-only model cannot
                // honour another language, and preparing it with one costs a rebuild for nothing.
                var outcome = await _localModels.PrepareAsync(
                    selectedModel, effectiveLanguage == "auto" ? null : effectiveLanguage, ct);
                if (outcome == PrepareOutcome.NotDownloaded)
                {
                    _statusText.Text = $"Model '{ModelDisplayName.Resolve(selectedModel)}' is not downloaded. Please download it from the Models page.";
                    return;
                }
                if (outcome == PrepareOutcome.UnknownModel)
                {
                    // New outcome (Parakeet 1/3): a selected model no runtime serves. Deliberately
                    // does not interpolate the stored name — settings import validates that field
                    // only as a string, so it can carry control or bidi characters, and this is a
                    // page the user reads to decide what to do next.
                    _statusText.Text = "That transcription model isn't available. Pick one on the Models page.";
                    return;
                }
                if (outcome == PrepareOutcome.Unavailable)
                {
                    // Parakeet 3/3: the runtime owns this model but cannot run it here — compiled
                    // out, or unsupported on this CPU. Handled explicitly because everything past
                    // these guards falls through to transcription, which would then fail inside the
                    // engine rather than saying so here.
                    _statusText.Text = "That transcription model can't run on this version of VoiceWink. Pick one on the Models page.";
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();

            _statusText.Text = "Transcribing...";

            // Transcribe — pass vocabulary hints, language, and diarization to all providers.
            // The EXACT model prepared above is passed in: GetService() with no argument re-reads
            // Settings, which could route to a different model than the one just loaded.
            var transcriber = _registry.GetService(selectedModel);
            var language = effectiveLanguage;      // the SAME value the model was prepared with
            var vocabContext = await _customVocabulary.GetVocabularyContextAsync(ct);
            var hints = Models.TranscriptionHints.FromTerms(vocabContext.Terms);
            var diarize = _settings.GetBool(AppDefaults.IsDiarizationEnabled, false)
                && isCloudModel
                && (cloudModel!.Provider is Models.Enums.ModelProvider.Deepgram
                    or Models.Enums.ModelProvider.ElevenLabs);
            // NET-1: one automatic retry on a connect-phase failure (nothing uploaded, so a
            // replay is provably safe). The notice is THIS page's status line, not the recorder
            // pill — a shared notice route would surface a recording pill for file work.
            transcribedText = await Services.Transcription.TranscriptionConnectRetry.ExecuteAsync(
                token => transcriber.TranscribeAsync(wavPath, language, hints, diarize, token),
                onRetrying: () =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isUnloaded) return;
                        _statusText!.Text = "Poor connection — retrying...";
                    });
                    return Task.CompletedTask;
                },
                ct);
            // Same raw-output trace as the live pipeline (MainViewModel) — "(file)"
            // marks the Audio Transcribe page's file mode.
            Helpers.PromptTraceLog.WriteOutput(Helpers.PromptTraceOp.FileTranscriptionOutput,
                new Helpers.TraceMeta(Model: selectedModel),
                $"transcription output (file) · {selectedModel}", transcribedText);

            // Phase 2: Text processing pipeline (same as live recording)
            var text = transcribedText ?? "";
            if (!string.IsNullOrEmpty(text))
            {
                text = _outputFilter.Filter(text);
                text = _fillerWordManager.Filter(text, requestedLanguage);
                text = _textFormatter.Format(text);

                // The same machine/user boundary TextPipelineRunner enforces, and for the same
                // reason: the three stages above are machine-owned and can MANUFACTURE a
                // contentless string from input that had words ("[music]," -> ",", "um," -> ","),
                // while WordReplacementService below is user-authored and may produce punctuation
                // on purpose. Without this, a file transcription of near-silence renders a card
                // that reads as success and overwrites the clipboard with a comma (Codex diff
                // review r2).
                //
                // This page deliberately re-implements the stages rather than calling
                // TextPipelineRunner — a pre-existing divergence, out of scope here — so the guard
                // is applied at the equivalent point. Both sites call the SAME helper, so the rule
                // itself is not duplicated, only its placement.
                if (Helpers.TranscriptContent.IsEmpty(text))
                {
                    Logger.Warning("File transcription left no content after the machine stages");
                    text = string.Empty;
                }
                else
                {
                    try
                    {
                        text = _wordReplacement.ApplyReplacements(text);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Word replacement failed, continuing with unmodified text");
                    }
                }
            }

            // Phase 3: Show result and copy to clipboard. Card visible IFF non-empty —
            // same both-ways rule as AudioTranscribeRestoreState: an empty transcription
            // (silent/failed audio) must not render a blank card that reads as success,
            // and a card left visible from a previous run must collapse (Codex PR-3 R2).
            _resultText.Text = text;
            _resultCard!.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _copyButton.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Phase 5: Copy to clipboard — don't auto-paste because VoiceWink itself has focus.
            // The user can Ctrl+V into their target app, or use the "Copy" button below the result.
            //
            // NOTHING TO COPY MEANS DON'T TOUCH THE CLIPBOARD. SetClipboardAsync calls
            // EmptyClipboard() first, so passing "" does not write nothing — it ERASES whatever the
            // user had, and then reports "Done — 0 characters (copied to clipboard)". The content
            // guard above made this reachable where it previously was not: it converts a
            // punctuation-only result to empty, so a near-silent file would have destroyed the
            // user's clipboard as a side effect of the fix that was meant to protect it (Codex diff
            // review r3). Guarding a destructive call is the point of the guard, not an extra.
            if (string.IsNullOrEmpty(text))
            {
                _statusText.Text = "Done — no speech detected";
                Logger.Information("File transcription produced no content; clipboard left untouched");
            }
            else if (await _clipboard.SetClipboardAsync(text))
            {
                _statusText.Text = $"Done — {text.Length} characters (copied to clipboard)";
            }
            else
            {
                _statusText.Text = $"Done — {text.Length} characters";
                Logger.Warning("Failed to copy audio transcription result to clipboard");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Information("Audio file transcription cancelled by user");
            _statusText.Text = "Transcription cancelled.";
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient timeout throws TaskCanceledException (subclass of OperationCanceledException)
            // when the request exceeds the configured timeout.
            //
            // LOG-1 shape (NET-6). This catch is UNFILTERED, but what actually reaches it is one
            // thing: the cloud transcription client's HttpClient.Timeout, a TaskCanceledException.
            // A first draft claimed the parakeet-local deadline and the CPU-fallback chunk budget
            // land here too; they do not — both are swallowed into typed failure results well
            // before this frame (opus self-review, traced). So {ErrorType} is a constant today and
            // is kept for LOG-1's house shape, and because an unfiltered arm can gain a source
            // later without anyone revisiting this line. LogFileTranscriptionFailure 80-odd lines
            // below already logs this exact shape for HttpRequestException and TimeoutException;
            // the two arms of one failure surface disagreed until now.
            Logger.Warning("Audio file transcription timed out: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            _statusText.Text = "Transcription timed out. The file may be too large for the selected provider.";
        }
        catch (Exception ex)
        {
            LogFileTranscriptionFailure(ex);

            // TRN-70: this fallback used to name a cause it does not know — "Please check the file
            // format and try again." for EVERY failure it had no arm for, including a network drop,
            // a provider 5xx and a 401. Two unrelated slices had already paid one `catch` each just
            // to get past that sentence (ENH-15's malformed-key arm and TRN-64's GPU refusal, whose
            // structured log fields now live in LogFileTranscriptionFailure); the network case was
            // the third and still landed on it. A fourth arm would have been guard N+1, so the
            // fallback stops diagnosing instead.
            //
            // `wavPath` is assigned ONLY by a COMPLETED ConvertToWavAsync, so null here means the
            // failure happened while READING the file — an unsupported extension, a corrupt or
            // truncated source, a decoder that could not open it. That is the one case the old
            // sentence was always right about, so it keeps a file-shaped message. Everything past
            // the conversion is a transcription failure and takes the pipeline's own copy.
            //
            // DescribeTranscriptionFailure is the repo's SINGLE definition of that copy — pure,
            // internal, and test-pinned (MainViewModelGateTests), including the SEC-1 rule that a
            // provider's 401 body never reaches a user-visible surface. Reused rather than copied:
            // a local switch here would be a second definition to keep in step, and the duplicate
            // is what would drift. Deliberately NOT extracted out of MainViewModel — that move is a
            // refactor this card does not need, and the method is already pure and internal.
            _statusText.Text = wavPath is null
                ? "Couldn't read this audio file — check the format and try again."
                : ViewModels.MainViewModel.DescribeTranscriptionFailure(ex);
        }
        finally
        {
            _isTranscribing = false;
            _lastResult = _resultText?.Text;
            _lastStatus = _statusText?.Text;
            _progress.IsActive = false;
            SetTranscribing(false);
            _cts?.Dispose();
            _cts = null;
            // Snapshot updated — let the currently-displayed instance (which may not be
            // this one) refresh its UI now instead of at its next navigation (F27).
            TranscriptionStateChanged?.Invoke();

            // Clean up converted WAV temp file
            if (wavPath != null)
            {
                try { File.Delete(wavPath); }
                catch { /* file in use or already gone */ }
            }
        }
    }

    /// <summary>
    /// Log level and detail for a failed file transcription (TRN-70).
    /// </summary>
    /// <remarks>
    /// <para><b>This is the half of the deleted catch arms that was never about copy.</b> ENH-15's
    /// and TRN-64's arms each did two jobs: dodge a fallback sentence that blamed the file format,
    /// and log their own structured fields at Warning so Sentry does not take one event per attempt
    /// (a malformed stored key repeats until it is re-entered; a GPU refusal repeats until a
    /// restart). The copy half is gone — the fallback no longer needs dodging — and the log half
    /// lives here, fields intact.</para>
    ///
    /// <para><b>Warning vs Error is "environment" vs "our defect", the same split
    /// <c>MainViewModel.HandleTranscribePipelineFailure</c> makes on the dictation path.</b>
    /// <see cref="Helpers.ProviderApiException"/> and <c>ConnectTimeoutException</c> both subclass
    /// <see cref="global::System.Net.Http.HttpRequestException"/>, so a provider error, a DNS
    /// failure and a connect-phase timeout all land in that arm — none of them is an app defect,
    /// and at Error each would ship a Sentry event for a dropped Wi-Fi connection. Everything
    /// unrecognised stays Error WITH the stack, including a conversion failure: a corrupt source
    /// file and a bug in our own resampler arrive as the same exception types, and silencing the
    /// second to quieten the first is the wrong trade.</para>
    /// </remarks>
    private static void LogFileTranscriptionFailure(Exception ex)
    {
        switch (ex)
        {
            case Helpers.InvalidApiKeyFormatException invalid:
                Logger.Warning("Stored transcription API key for {Provider} has an invalid format: {Verdict}",
                    invalid.Provider, invalid.Verdict);
                break;
            case Helpers.GpuSelfTestRefusedException refused:
                Logger.Warning("Audio file transcription refused: {Reason} (TRN-64)", refused.Kind);
                break;
            case global::System.Net.Http.HttpRequestException or TimeoutException:
                Logger.Warning("Audio file transcription failed: {ErrorType}: {ErrorMessage}",
                    ex.GetType().Name, ex.Message);
                break;
            default:
                Logger.Error(ex, "Audio file transcription failed");
                break;
        }
    }
}
