using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Support;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// "Report a problem" dialog (REL-3, extended by REL-17). Gathers a subject + description
/// and, per the user's per-report choices, bundles the local logs (secrets redacted), the
/// prompt trace (redacted sidecars by default; RAW only behind an explicit advisory
/// checkbox), and a size-budgeted, consent-time-snapshotted selection of retained
/// recordings — then hands the send to <see cref="ReportSendFlow"/> (MAPI auto-attach,
/// <c>mailto:</c>+reveal fallback with a path-free body). Fail-closed: if the bundle can't
/// be written, the email is not opened. The form rides
/// <see cref="AppTheme.CreateDialogScroller(UIElement)"/> — a bare ContentDialog stack
/// CLIPS when it outgrows the dialog (repo invariant). All UI in code-behind per the
/// WinUI 3 constraints.
///
/// <para><b>REL-29 (2026-09-02):</b> a MAPI decline no longer drops straight to the mailto
/// fallback. The dialog enters a DECLINED state — form locked, status says the report could not
/// be handed to the mail app, primary becomes <b>Try again</b> (the MAPI leg on the same kept
/// bundle) and a secondary <b>Attach it myself</b> runs the old fallback. A tester's Outlook
/// add-in vetoed three compose windows in a row before a fourth went through; each veto used to
/// cost a mailto compose plus an Explorer window the user never asked for. A host that refuses
/// Simple MAPI outright (REL-27) never reaches the declined state: the flow takes the fallback
/// itself there, and this dialog shows the pre-REL-29 result.</para>
/// </summary>
public sealed class ReportProblemDialog : ContentDialog
{
    private static ILogger Logger => Log.ForContext<ReportProblemDialog>();

    private readonly TextBox _subject;
    private readonly TextBox _description;
    private readonly CheckBox _includeLogs;
    private readonly CheckBox _includeRawPrompts;
    private readonly CheckBox _includeRecordings;
    private readonly TextBlock _rawPromptsAdvisory;
    private readonly TextBlock _status;
    private readonly bool _rawTracesExist;
    private readonly IReadOnlyList<RecordingCandidate> _recordingsSnapshot;

    // REL-29: the send that a MAPI decline left pending. Non-null flow == declined state. The
    // bundle (if any) was built from the form's state at the FIRST submit and is kept on disk, so
    // a retry must reuse exactly these values and never rebuild — which is also why the form is
    // locked once this is set (an edit the retry would silently ignore is worse than no edit).
    private ReportSendFlow? _pendingFlow;
    private string _pendingSubject = string.Empty;
    private string _pendingBody = string.Empty;
    private string _pendingFooter = string.Empty;
    private string? _pendingBundlePath;
    private bool _pendingHadAttachment;

    // REL-29: one send at a time. Both handlers take a deferral and then block off-thread on a
    // compose window that can stay open for minutes, and nothing in this file has measured
    // whether ContentDialog suppresses further button clicks while a deferral is pending. Without
    // this flag a second press would re-enter OnSubmit from the top (a second bundle, a second
    // compose window) or interleave the manual fallback with an in-flight retry that then DELETES
    // the very file the fallback just told the user to attach (self-review, both lenses).
    private bool _sendInFlight;

    public ReportProblemDialog()
    {
        Title = "Report a problem";
        PrimaryButtonText = "Open email";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;

        _subject = new TextBox { PlaceholderText = "Subject" };
        _description = new TextBox
        {
            PlaceholderText = "Describe what happened…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
        };

        // Copy names exactly the credential classes RedactString covers — no blanket
        // "secrets" claim; logs deliberately keep provider response content for
        // troubleshooting (diagnostics.md posture, Codex plan round 3).
        _includeLogs = new CheckBox
        {
            // "Recognized" is load-bearing (final verification round): RedactString
            // scrubs known key/header formats — an unrecognized custom credential in
            // provider content is not guaranteed removed, and the copy must not claim it.
            Content = "Include logs (recognized API keys and license keys removed)",
            IsChecked = true,
        };

        _rawTracesExist = RawTraceFilesExist();
        // Always PRESENT, even with nothing to send (owner UAT 2026-08-10). It used to
        // collapse, so a user who had never switched prompt tracing on saw no row and could
        // not tell whether the report carried that data — the advisory below now says which
        // Settings toggle would populate it. Kept indented: this option genuinely depends on
        // "Include logs", and the 24 px is what says so.
        _includeRawPrompts = new CheckBox
        {
            Content = "Include raw prompt logs",
            IsChecked = false,
            Margin = new Thickness(24, 0, 0, 0),
            IsEnabled = ReportOptionAvailability.RawPromptsSelectable(_rawTracesExist, logsIncluded: true),
        };
        _rawPromptsAdvisory = Advisory(
            ReportOptionAvailability.RawPromptsAdvisory(_rawTracesExist, logsIncluded: true),
            indent: true);
        // The raw trace rides WITH the logs (SupportBundle validates the same rule), so the
        // row's enabled state AND its sentence both follow the parent box.
        _includeLogs.Unchecked += (_, _) =>
        {
            _includeRawPrompts.IsChecked = false;
            SyncRawPromptsRow(logsIncluded: false);
        };
        _includeLogs.Checked += (_, _) => SyncRawPromptsRow(logsIncluded: true);

        // Consent-time snapshot (REL-17): what the checkbox PROMISES is what the bundle
        // gets — the build revalidates this exact snapshot and never picks up new files.
        _recordingsSnapshot = SupportBundle.SelectRecordings(
            SupportBundle.EnumerateRecordingCandidates(AppPaths.RecordingsDir),
            SupportBundle.RecordingsBudgetBytes);
        var totalMb = _recordingsSnapshot.Sum(r => r.Bytes) / (1024.0 * 1024.0);
        // Always present for the same reason as the raw-prompt row above. Deliberately NOT
        // indented: the 24 px on "Include raw prompt logs" means "depends on Include logs",
        // and recordings do not — indenting this to match would draw a dependency that does
        // not exist. Its advisory is flush for the same reason (owner decision 2026-08-10).
        _includeRecordings = new CheckBox
        {
            Content = ReportOptionAvailability.RecordingsLabel(_recordingsSnapshot.Count, totalMb),
            IsChecked = false,
            IsEnabled = ReportOptionAvailability.RecordingsSelectable(_recordingsSnapshot.Count),
        };

        _status = new TextBlock
        {
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    // CONDITIONAL, not a promise (REL-27, Codex plan r1 Blocker 3). The old wording
                    // said the report "is attached automatically" full stop. Attaching goes through
                    // Simple MAPI, which VoiceWink now refuses to use on an emulated host because it
                    // crashes the process there — so on those machines the promise was guaranteed
                    // false, and a user could believe it and send the mail with no report in it. The
                    // dialog already tells you, in place, which path actually ran (the declined and
                    // manual-attach branches below); this sentence just stops contradicting it
                    // in advance.
                    Text = $"Send a problem report to {VoiceWinkUrls.SupportEmail}. This opens your email app — " +
                           "if you include logs, VoiceWink tries to attach the report file. If it can't, " +
                           "it opens the report's folder so you can attach it yourself.",
                    Foreground = AppTheme.Brush(AppTheme.TextSecondary),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
                _subject,
                _description,
                _includeLogs,
                // Rewritten 2026-08-10 (owner UAT: "a sentence I do not understand at all").
                // It was "Prompt summaries ride along with all text replaced by lengths" —
                // accurate but unreadable. Lives in the helper as a const so its wording is
                // pinnable; see the scoping note there, which a first draft got wrong.
                Advisory(ReportOptionAvailability.LogsAdvisory),
                _includeRawPrompts,
                _rawPromptsAdvisory,
                _includeRecordings,
                Advisory(ReportOptionAvailability.RecordingsAdvisory(_recordingsSnapshot.Count)),
                _status,
            },
        };

        // Height-flexible dialog scroller (repo invariant): fills the offered height and
        // scrolls only on overflow — a bare StackPanel clips the button row instead.
        Content = AppTheme.CreateDialogScroller(form);

        PrimaryButtonClick += OnSubmit;
        // REL-29: the secondary button exists only in the declined state — its text is set there,
        // and a ContentDialog renders no secondary button while the text is empty. This is the
        // app's FIRST use of a ContentDialog secondary button and of assigning its text after
        // ShowAsync; it is plain ContentDialog API (no templated control), but "it renders" is
        // established by owner UAT, not by anything a headless test can prove.
        SecondaryButtonClick += OnAttachManually;
    }

    /// <summary>
    /// Keeps the raw-prompt row and its sentence in step with the parent "Include logs" box.
    /// One place, because the enabled state and the explanation are answers to the same
    /// question — updating one without the other is how a row ends up disabled under a
    /// sentence saying it is available.
    /// </summary>
    private void SyncRawPromptsRow(bool logsIncluded)
    {
        _includeRawPrompts.IsEnabled =
            ReportOptionAvailability.RawPromptsSelectable(_rawTracesExist, logsIncluded);
        _rawPromptsAdvisory.Text =
            ReportOptionAvailability.RawPromptsAdvisory(_rawTracesExist, logsIncluded);
    }

    private static TextBlock Advisory(string text, bool indent = false, bool visible = true) => new()
    {
        Text = text,
        Foreground = AppTheme.Brush(AppTheme.TextSecondary),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(indent ? 24 : 0, -6, 0, 0),
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
    };

    private static bool RawTraceFilesExist()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir))
                return false;
            return Directory.EnumerateFiles(AppPaths.LogsDir, "prompts-*.log")
                .Any(f => !Path.GetFileName(f).StartsWith(
                    PromptTraceLog.RedactedFilePrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Primary button: "Open email" on the first pass (build + MAPI), "Try again" in the
    /// declined state (the MAPI leg only, same kept bundle). One handler because both end in the
    /// same verdict set; which leg ran is decided by whether a send is pending.
    /// </summary>
    private async void OnSubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Async work needs a deferral so the dialog stays open until we know the outcome; we only
        // Cancel (keep it open) on failure.
        var deferral = args.GetDeferral();
        // Kimi diff r1 Blocker: only the invocation that ACQUIRED the flag may clear it. A press
        // rejected by the guard also reaches the finally, and an unconditional clear there would
        // let the NEXT press run concurrently with the send still blocked on the compose window.
        var acquiredSend = false;
        try
        {
            if (_sendInFlight)
            {
                args.Cancel = true;
                return;
            }
            _sendInFlight = true;
            acquiredSend = true;

            ReportSendResult result;
            ReportSendFlow flow;
            string subject;
            string body;
            string footer;
            bool hadAttachment;
            var isRetry = _pendingFlow != null;

            if (_pendingFlow is { } pending)
            {
                // REL-29 "Try again": nothing is re-read from the form (it is locked), nothing is
                // rebuilt — the flow refuses a vanished bundle rather than composing without it.
                flow = pending;
                subject = _pendingSubject;
                body = _pendingBody;
                footer = _pendingFooter;
                hadAttachment = _pendingHadAttachment;
                var bundlePath = _pendingBundlePath;
                result = await Task.Run(() =>
                    flow.RetryMapi(VoiceWinkUrls.SupportEmail, subject, body, bundlePath, footer));
            }
            else
            {
                // Read the UI controls on the UI thread, then do ALL the heavy work (bundle build +
                // the blocking MAPI compose dialog) on a background thread via ReportSendFlow so the
                // WinUI dialog never freezes while "Open email" is processing.
                subject = string.IsNullOrWhiteSpace(_subject.Text) ? "VoiceWink problem report" : _subject.Text.Trim();
                body = _description.Text ?? string.Empty;
                // The version footer ENDS the mail on every transport (owner ask, 2026-09-14 — the
                // same footer the suggestion email carries): a report sent without logs has no
                // report-info.txt to name its build, and support reads the mail before it opens any
                // bundle. Its own argument, never appended to the body here (Codex diff r1 Blocker):
                // the mailto fallback truncates the body's tail to its length budget, and a footer
                // inside the body was exactly what a long description cut off — the flow reserves it
                // instead. Composed once, so the REL-29 retry and the manual fallback carry it unchanged.
                footer = AboutInfo.MailFooter(AboutInfo.CurrentVersion);
                var includeLogs = _includeLogs.IsChecked == true;
                var includeRaw = includeLogs && _includeRawPrompts.IsChecked == true;
                var includeRecordings = _includeRecordings.IsChecked == true && _recordingsSnapshot.Count > 0;
                hadAttachment = includeLogs || includeRecordings;

                var snapshot = _recordingsSnapshot;
                var firstSubject = subject;
                var firstBody = body;
                var firstFooter = footer;
                flow = new ReportSendFlow();
                var firstFlow = flow;
                result = await Task.Run(() =>
                {
                    // Content hashes complete the consent snapshot HERE, off the UI thread
                    // and only for the selected ≤budget set (diff review round 2 — hashing
                    // everything at dialog open froze the dispatcher).
                    var recordings = includeRecordings
                        ? SupportBundle.WithContentHashes(snapshot, AppPaths.RecordingsDir)
                        : (IReadOnlyList<RecordingCandidate>)Array.Empty<RecordingCandidate>();
                    SupportBundleOptions? options = includeLogs || includeRecordings
                        ? new SupportBundleOptions(
                            IncludeApplicationLogs: includeLogs,
                            PromptTrace: !includeLogs ? PromptTraceMode.None
                                : includeRaw ? PromptTraceMode.Raw : PromptTraceMode.Redacted,
                            Recordings: recordings,
                            // The environment/configuration block support asks for first —
                            // allowlisted keys only, read straight from settings.json (no
                            // service lookup here, per AGENTS.md).
                            EnvironmentSummary: DiagnosticSnapshot.RenderBlock(
                                DiagnosticSnapshot.Collect(AppPaths.SettingsFile)))
                        : null;
                    return firstFlow.Run(
                        VoiceWinkUrls.SupportEmail, firstSubject, firstBody, options, AppPaths.LogsDir, AppPaths.ReportsDir,
                        firstFooter);
                });
            }

            // Back on the UI thread (await resumes on WinUI's SynchronizationContext).
            switch (result.Outcome)
            {
                case ReportSendOutcome.SentViaMapi:
                    // Compose window opened with the attachment already in place (bundle
                    // deleted); the dialog closes.
                    return;

                case ReportSendOutcome.BundleFailed when !isRetry:
                    _status.Text = "Couldn't prepare the report file. Uncheck the include options to send without it.";
                    _status.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;

                case ReportSendOutcome.BundleFailed:
                    // The kept bundle is gone (a sweep, or the user removed it). The form is locked,
                    // so "uncheck the options" is not available advice — but the typed description
                    // must not be stranded: the manual path still carries it into a compose window,
                    // now with no file to attach (self-review, flow lens).
                    _pendingBundlePath = null;
                    _status.Text = "The report file is no longer on disk. You can still open an email with your "
                        + "description, or close this and start a new report.";
                    _status.Visibility = Visibility.Visible;
                    IsPrimaryButtonEnabled = false;
                    SecondaryButtonText = "Open email anyway";
                    IsSecondaryButtonEnabled = true;
                    args.Cancel = true;
                    return;

                case ReportSendOutcome.MapiDeclined:
                    // REL-29: the mail client was asked and did not take the message. A choice, not
                    // a consequence — see EnterDeclinedState for the copy rules.
                    EnterDeclinedState(flow, subject, body, footer, result.BundlePath, hadAttachment, again: isRetry);
                    args.Cancel = true;
                    return;

                case ReportSendOutcome.MailtoFallback:
                    // Run took the fallback itself: this host refuses Simple MAPI outright (REL-27),
                    // so no retry is offered — the pre-REL-29 result, presented as REL-25 item 1
                    // asked (say in place which path ran).
                    ShowManualFallbackResult(result, hadAttachment);
                    args.Cancel = true;
                    return;

                default:
                    // No other member exists today; a future one must not close the dialog silently
                    // (self-review, dialog lens) — keep it open with a generic line.
                    _status.Text = $"Couldn't open your email app. Please email {VoiceWinkUrls.SupportEmail} directly.";
                    _status.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to open problem report");
            _status.Text = $"Couldn't open your email app. Please email {VoiceWinkUrls.SupportEmail} directly.";
            _status.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
        finally
        {
            if (acquiredSend)
                _sendInFlight = false;
            deferral.Complete();
        }
    }

    /// <summary>
    /// Secondary button, declined state only: "Attach it myself" (or "Open email anyway" when
    /// nothing was attached, or the kept file is gone) — the mailto + Explorer-reveal fallback
    /// that used to run on its own.
    /// </summary>
    private async void OnAttachManually(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        // Kimi diff r1 Blocker: only the invocation that ACQUIRED the flag may clear it. A press
        // rejected by the guard also reaches the finally, and an unconditional clear there would
        // let the NEXT press run concurrently with the send still blocked on the compose window.
        var acquiredSend = false;
        try
        {
            if (_sendInFlight || _pendingFlow is not { } flow)
            {
                // In flight: see _sendInFlight. No pending flow: unreachable by construction (the
                // button has no text outside the declined state); keeping the dialog open is the
                // harmless answer if it ever is.
                args.Cancel = true;
                return;
            }
            _sendInFlight = true;
            acquiredSend = true;

            var subject = _pendingSubject;
            var body = _pendingBody;
            var footer = _pendingFooter;
            var bundlePath = _pendingBundlePath;
            var result = await Task.Run(() =>
                flow.FallbackToMailto(VoiceWinkUrls.SupportEmail, subject, body, bundlePath, footer));
            ShowManualFallbackResult(result, _pendingHadAttachment);
            args.Cancel = true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to open problem report");
            _status.Text = $"Couldn't open your email app. Please email {VoiceWinkUrls.SupportEmail} directly.";
            _status.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
        finally
        {
            if (acquiredSend)
                _sendInFlight = false;
            deferral.Complete();
        }
    }

    /// <summary>
    /// REL-29: lock the form around the pending send and offer the two ways forward. TOTAL — it
    /// sets every button property it depends on rather than assuming the dialog's initial state,
    /// so a re-entry after any other branch still shows exactly this state (self-review, dialog
    /// lens). A second decline re-enters it with <paramref name="again"/> so the status visibly
    /// changes (the mail app showed its own error; the dialog must not look as if the click did
    /// nothing). "Cancel" stays "Cancel": nothing has been sent.
    ///
    /// <para><b>Copy rules.</b> The subject is the REPORT, never the mail client — the
    /// REL-25/REL-27 cause-neutrality rule: this branch serves 25 MAPI codes, exceptions, and a
    /// machine with no MAPI provider registered at all, and "your email app reported an error"
    /// would assert one cause out of many. The MAPI code is deliberately NOT shown: the user
    /// cannot act on it, the log carries it for support, and surfacing it would mean changing
    /// <c>MapiMailService</c>'s return shape for no user-visible gain. The kept file's location and
    /// lifetime are stated because, unlike the old automatic fallback, no Explorer window has
    /// shown it — a user who cancels here should know a report file exists and when it goes.</para>
    /// </summary>
    private void EnterDeclinedState(
        ReportSendFlow flow, string subject, string body, string footer, string? bundlePath,
        bool hadAttachment, bool again)
    {
        _pendingFlow = flow;
        _pendingSubject = subject;
        _pendingBody = body;
        _pendingFooter = footer;
        _pendingBundlePath = bundlePath;
        _pendingHadAttachment = hadAttachment;

        // Read-only, not disabled, for the two text boxes: the typed description stays selectable
        // and copyable in every declined path (self-review, flow lens). The checkboxes are what
        // decided the bundle's contents, so they lock outright.
        _subject.IsReadOnly = true;
        _description.IsReadOnly = true;
        _includeLogs.IsEnabled = false;
        _includeRawPrompts.IsEnabled = false;
        _includeRecordings.IsEnabled = false;

        var lead = again
            ? "VoiceWink still couldn't hand the report to your email app."
            : "VoiceWink couldn't hand the report to your email app.";
        _status.Text = bundlePath != null
            ? lead + " You can try again, or attach the report file yourself — it stays in VoiceWink's "
                   + "Reports folder for 7 days."
            : lead + " You can try again, or open an email anyway.";
        _status.Visibility = Visibility.Visible;

        PrimaryButtonText = "Try again";
        IsPrimaryButtonEnabled = true;
        SecondaryButtonText = bundlePath != null ? "Attach it myself" : "Open email anyway";
        IsSecondaryButtonEnabled = true;
        CloseButtonText = "Cancel";
    }

    /// <summary>
    /// The terminal state after the mailto path ran — chosen by the user, or taken by the flow on
    /// a host that refuses Simple MAPI. REL-25 item 1 (owner UAT 2026-08-17, §32.3/§32.4): say in
    /// place which path ran. The FILENAME is not repeated here on purpose: the mailto body already
    /// leads with it (ReportSendFlow builds `[Please attach the report file "…"]`), and that compose
    /// window is the surface the user is actually looking at. The copy follows the RESULT, not
    /// what was requested: a bundle that vanished before the fallback ran comes back with a null
    /// path, and telling the user to attach it would be wrong-output.
    /// </summary>
    private void ShowManualFallbackResult(ReportSendResult result, bool attachmentRequested)
    {
        _status.Text = result.BundlePath != null
            // Cause-NEUTRAL wording, deliberately (REL-25, REL-27): "couldn't be attached
            // automatically" is true whichever side declined — the mail client, or VoiceWink's
            // own refusal to ask it on an emulated host.
            ? "The report file couldn't be attached automatically, so the email opened WITHOUT it. "
              + "We opened the folder containing it — please attach it to that email before sending."
            : attachmentRequested
                ? "The report file was no longer available, so the email opened with your message only."
                : "Your email app opened with your message. Nothing needed attaching.";
        _status.Visibility = Visibility.Visible;
        // Disable BOTH action buttons, or this branch ARMS a defect: args.Cancel leaves the dialog
        // interactive, and a second press would open ANOTHER compose window and ANOTHER Explorer
        // window (the fallback keeps its zip by design, so a rebuild would also accumulate bundles
        // until the 7-day sweep — the §32.5 multiple-zips symptom). Before REL-25 the dialog closed
        // here, so one session could produce at most one bundle; nothing must reintroduce that.
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        // "Cancel" reads as undoing the report, which is wrong now that the email is open and the
        // only remaining action is to dismiss.
        CloseButtonText = "Close";
    }
}
