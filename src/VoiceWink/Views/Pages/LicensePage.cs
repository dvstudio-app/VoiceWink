using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.Licensing;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// License page — key entry, activation / deactivation, status, buy / recover links.
///
/// <para>The page renders a state-specific panel based on <see cref="LicenseViewModel.Status"/>.
/// The seven panels correspond one-to-one to the <see cref="LicenseStatus"/> enum per the
/// LIC-1 backlog spec. On each PropertyChanged(Status) we rebuild the state panel in place
/// so the UI stays in sync after activation / deactivation / refresh.</para>
///
/// <para>All UI is code-behind — the XAML file is a minimal stub, per the project-wide
/// "All UI in code-behind" constraint in CLAUDE.md (bypasses PRI/XAML resource loading).</para>
/// </summary>
public sealed class LicensePage : Page
{
    private static ILogger Logger => Log.ForContext<LicensePage>();

    private readonly LicenseViewModel _vm;
    private ContentControl? _statePanelHost;
    private TextBlock? _titleStatusChip;
    // True only until the first RebuildStatePanel call completes (invoked from BuildUI
    // in the ctor, before Loaded fires). The first call flips this to false; from then
    // on _statePanelHost.IsLoaded gates rebuilds against detached-element callbacks.
    private bool _isInitialBuild = true;

    // The activity line, and the deliberate reason it is NOT part of a panel (LIC-15):
    // RebuildStatePanel replaces the panel subtree wholesale, and IsBusy is excluded from the rebuild
    // triggers because a rebuild mid-keystroke resets the key box's focus and caret. Living beside the
    // status chip and updated by assigning .Text, this can report busy state and command outcomes
    // without any rebuild at all — so the exclusion stays intact instead of being argued with. Since
    // LIC-25 it sits ON the chip's line (one status row: chip · refresh icon · this), single-line and
    // trimmed, in a row whose height never changes — so a message appearing or disappearing cannot
    // move the content below it (owner UAT 2026-09-07). Since LIC-26 that row is the CARD's header
    // rather than a page-level band above it, which changes where it is built and nothing else: the
    // card is built once too, so this element is still created exactly once and never rebuilt.
    private TextBlock? _activityLine;

    // The page's ONE "Check now" (LIC-25): a refresh icon beside the chip, firing the same forced
    // validate the five per-panel buttons used to. Built once with the row and never rebuilt; the
    // plan decides whether it is visible (ShowsCheckNow — only where a key is stored). Deliberately
    // NOT disabled while a command runs: the VM's busy gate turns a second click into the
    // BusyGateNotice on the same line (LIC-15), and an IsBusy-keyed disable here would strand the
    // icon after ActivateAsync's empty-key arm, which nulls ActivityMessage while still inside the
    // busy slot — and this icon is the LIC-2a / LIC-4 recovery that force-validates without
    // re-entering a key (Grok plan round, 2026-09-07).
    private Border? _checkNowButton;

    // Nothing tells an OPEN page that the licence changed. The launch reconcile is a fire-and-forget
    // CheckAsync on a pool thread that can persist a disable or a refusal, and every time-driven
    // boundary (trial expiry, the 24h line, the 30-day line, the tryout window) is computed at read
    // time — so no event exists to subscribe to for half of it. This tick re-projects LOCAL state
    // only: no network, and a no-op while a command is in flight.
    private DispatcherTimer? _reprojectTimer;

    // Long enough to be invisible in CPU terms (a handful of settings reads), short enough that a
    // user who is watching the page does not see it contradict the hotkey for more than a moment.
    private static readonly TimeSpan ReprojectInterval = TimeSpan.FromSeconds(5);

    // LIC-22: the startup redirect's forced first refresh is the launch reconcile for a
    // problem-state launch (MainWindow.ApplyLicenseStartupRoute fires no separate background check
    // on that branch, so the launch itself sends one validate). The flag is the WINDOW's, handed in
    // as a take-once accessor and asked in OnLoaded — not a constructor bool — because
    // NavigateTo's COMException retry and the theme-change rebuild construct a brand-new page;
    // the first instance whose OnLoaded runs takes it, a re-Load of the same instance and every
    // later visit get false and refresh unforced.
    private readonly Func<bool>? _takeForcedInitialRefresh;

    public LicensePage() : this(takeForcedInitialRefresh: null) { }

    /// <param name="takeForcedInitialRefresh">
    /// MainWindow's take-once accessor. Returns true for the first OnLoaded after the startup
    /// redirect set it: that refresh reaches the server whatever the cache age and whatever
    /// persisted verdict is on file, so a key re-enabled or extended on the merchant side heals at
    /// the next start without a click, and a disable landed between sessions is confirmed at the
    /// next start (LIC-22). Null (a page built outside the window's factory) means never forced.
    /// </param>
    public LicensePage(Func<bool>? takeForcedInitialRefresh)
    {
        _takeForcedInitialRefresh = takeForcedInitialRefresh;
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _vm = App.Services.GetRequiredService<LicenseViewModel>();
        BuildUI();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void BuildUI()
    {
        // One static line for all seven panels (LIC-13): "Deactivate this machine" promised an action
        // that exists on the Activated panel alone, and "Manage your VoiceWink license" presumes one,
        // which Unlicensed and the free trial do not have. The header offers no trial start: that is
        // rendered only where the plan says it is honest. Since LIC-21 (2026-09-06) there is no trial
        // KEY to mention either — the free trial is local and needs no key.
        var header = AppTheme.CreatePageHeader("License", "Activate a license key, or check this machine's license.");

        // Title status chip (small colored badge next to the header) — surfaces state at a glance
        _titleStatusChip = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
        };

        // The refresh icon: the one "Check now" on the page (LIC-25). Visibility is set per plan in
        // RebuildStatePanel; the control itself is built once here and never rebuilt.
        //
        // glyphSize 10 against the factory's default 12 (LIC-26, owner: "it's too big; its height
        // should be the same as the active title"). The BORDER stays 24 DIP — the factory separates
        // the two for exactly this — because this icon is the only control that force-validates a
        // stored key without re-entering it, i.e. the LIC-2a / LIC-4 recovery on Invalid,
        // DisabledReadOnly and OfflineGrace, and a 16 DIP target would have traded that recovery for
        // a visual preference (Codex plan round). The 12 px box already rendered ink at roughly the
        // chip's cap height; a solid closed arrow simply reads heavier than letterforms at the same
        // measure, so the fix is the usual optical correction — draw the glyph slightly UNDER it.
        //
        // glyphOffsetY 1: with the size right, the arrow still rode about 1 DIP HIGH against the
        // chip's letters (owner, the same evening: "sticking out from the top a little bit"). A
        // centred glyph sits on the row's mid-line; a capital letter sits below the mid-line of its
        // own line box. The factory's doc comment carries the metrics. The glyph moves, the 24 DIP
        // box does not.
        _checkNowButton = AppTheme.CreateIconButton("\uE72C" /* Refresh */, "Check now",
            (_, _) => _ = _vm.ForceRefreshCommand.ExecuteAsync(null), glyphSize: 10, glyphOffsetY: 1);
        // A LEADING margin only. The row's horizontal spacing used to live entirely on this element
        // (8 left AND 8 right) — and this is the element that COLLAPSES. A collapsed child
        // contributes no width and no margin, so on every panel where the plan hides it (the key
        // box, the trial panel, the activate-form override, the unmapped fallback) the chip and the
        // activity line abutted with no gap at all: "NOT ACTIVATEDActivating…" (owner, 2026-09-08).
        // Each element now owns the gap to ITS left, so spacing between two permanent elements can
        // never again depend on a conditional third.
        _checkNowButton.Margin = new Thickness(6, 0, 0, 0);

        // Sits ON the chip's line and is NEVER rebuilt — see the field's comment. Single-line and
        // trimmed rather than wrapped, so the row keeps one height whatever the message; the full
        // sentence is one hover away. Its own leading margin, per the note above: with the icon
        // collapsed this is the only thing separating it from the chip.
        _activityLine = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        // One status row — chip · refresh icon · activity text — at a FIXED height, so the content
        // below never moves when a message appears or clears (owner UAT 2026-09-07: the line used to
        // insert itself between the chip and the card and push the card down). 24 is the tallest
        // child (the icon's border), so the row measures the same whether the icon renders or is
        // collapsed. The gap BELOW the row belongs to the divider, not here.
        var statusRow = new Grid
        {
            MinHeight = 24,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        Grid.SetColumn(_titleStatusChip, 0);
        Grid.SetColumn(_checkNowButton, 1);
        Grid.SetColumn(_activityLine, 2);
        statusRow.Children.Add(_titleStatusChip);
        statusRow.Children.Add(_checkNowButton);
        statusRow.Children.Add(_activityLine);

        _statePanelHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        RebuildStatePanel();
        UpdateActivityLine();

        // ONE card for the whole page, built here and never rebuilt (LIC-26, owner: "it would look
        // better if all of that stuff was just inside the card"). The status row is the card's
        // header; RebuildStatePanel swaps only _statePanelHost.Content beneath it.
        //
        // Hoisting the card is what makes that possible WITHOUT re-parenting. The seven panels used
        // to wrap themselves in AppTheme.CreateCard, so moving the row into "the current card" would
        // have meant detaching two live elements from one parent and attaching them to a new one on
        // every rebuild — and those two (_activityLine, _checkNowButton) are precisely the ones this
        // page needs to survive untouched, since reporting busy state without a rebuild is the whole
        // reason they are built once (LIC-15). Each Build*Panel returns its bare panel instead.
        var cardBody = new StackPanel
        {
            Children = { statusRow, StatusDivider(), _statePanelHost }
        };

        var pageContent = new StackPanel
        {
            Spacing = 4,
            Children = { header, AppTheme.CreateCard(cardBody) }
        };

        AppTheme.SetPageScrollContent(this, pageContent);
    }

    /// <summary>
    /// The hairline between the status row and the panel below it, which is what makes the row read
    /// as the card's HEADER rather than as a stray grey line above the body text (LIC-26). It owns
    /// the vertical gaps on BOTH sides — 10 above, 14 below — so the panel body is never flush
    /// against the rule (Codex plan round) and neither the row nor the panel host carries layout
    /// that belongs between them. Inset within the card's padding rather than bled to its edges, so
    /// it does not depend on <see cref="AppTheme.CardPadding"/>.
    /// </summary>
    private static UIElement StatusDivider() => new Border
    {
        Height = 1,
        Margin = new Thickness(0, 10, 0, 14),
        Background = AppTheme.Brush(AppTheme.CardBorderColor),
    };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        if (_takeForcedInitialRefresh?.Invoke() == true)
        {
            _ = _vm.StartupReconcileAsync();
        }
        else
        {
            _ = _vm.RefreshCommand.ExecuteAsync(null);
        }

        _reprojectTimer ??= new DispatcherTimer { Interval = ReprojectInterval };
        // Unsubscribe before subscribing: `??=` guards the timer's CREATION but not the handler, and
        // this file's own RebuildStatePanel comment records that WinUI 3's Unloaded "is not always
        // reliable" — so a Loaded without an intervening Unloaded would subscribe twice and tick
        // twice (Kimi diff round advisory, 2026-09-04). Idempotent either way, but a duplicated
        // handler on a detached page is not worth carrying.
        _reprojectTimer.Tick -= OnReprojectTick;
        _reprojectTimer.Tick += OnReprojectTick;
        _reprojectTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;

        // Stopped AND unsubscribed: a DispatcherTimer holds a strong reference to its handler, so a
        // page swapped out of the ContentControl would otherwise keep ticking against a dead VM for
        // the life of the window.
        if (_reprojectTimer != null)
        {
            _reprojectTimer.Stop();
            _reprojectTimer.Tick -= OnReprojectTick;
        }
    }

    private void OnReprojectTick(object? sender, object e) => _vm.ReprojectLocalState();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The activity line is updated IN PLACE, never through RebuildStatePanel — which is what lets
        // the page report busy state at all. IsBusy is deliberately NOT subscribed: every command
        // writes ActivityMessage on entry, on exit, and when the busy gate turns it away, so the
        // message alone carries the state and an IsBusy handler would be dead weight — and a rebuild
        // mid-keystroke would reset the key box's focus and caret (LIC-15).
        if (e.PropertyName == nameof(LicenseViewModel.ActivityMessage))
            DispatcherQueue.TryEnqueue(UpdateActivityLine);

        // Rebuild the state panel when any status-visible property changes. The two messages are
        // triggers so a banner appears the moment a command writes it, without a second status flip.
        // ActivateFormRequested is one because it is a panel-plan input; it lives on the VM (LIC-19),
        // which clears it on every status transition, so a service-driven change always wins over
        // the user's request for the key form.
        if (e.PropertyName is nameof(LicenseViewModel.Status)
            or nameof(LicenseViewModel.ActivateFormRequested)
            or nameof(LicenseViewModel.MaskedKey)
            or nameof(LicenseViewModel.LastValidatedUtc)
            or nameof(LicenseViewModel.TrialTimeRemaining)
            or nameof(LicenseViewModel.AttemptMessage)
            or nameof(LicenseViewModel.SeatNotice)
            or nameof(LicenseViewModel.DeviceCountLabel)
            // LIC-30: ActivateAsync assigns Status BEFORE RefreshLocalState sets InstanceLine, so the
            // Status-driven rebuild would render the panel without the label; this entry corrects it.
            or nameof(LicenseViewModel.InstanceLine)
            or nameof(LicenseViewModel.StoredKeyExpired)
            or nameof(LicenseViewModel.KeyExpiresAt)
            or nameof(LicenseViewModel.TryoutEnded))
        {
            DispatcherQueue.TryEnqueue(RebuildStatePanel);
        }
    }

    /// <summary>
    /// Push <c>ActivityMessage</c> onto the page-level line. Assigns text on an element built once in
    /// <see cref="BuildUI"/>, so it costs no rebuild and cannot disturb the key box's focus — the
    /// property that makes reporting busy state possible here at all. The line stays VISIBLE when
    /// empty (LIC-25): its row is fixed-height, so collapsing it would buy nothing and re-flow the
    /// icon's neighbour.
    /// </summary>
    private void UpdateActivityLine()
    {
        if (_activityLine == null) return;
        var message = _vm.ActivityMessage;
        _activityLine.Text = message ?? string.Empty;
        // A failure reads as one (owner UAT, 2026-09-05: "error should be in red"). The VM says which
        // lines are failures; the page never classifies prose. FailureText rather than an accent: the
        // accents were tuned for chips and borders, and at 12 px on the light page amber measures
        // about 2:1 against the 4.5:1 the palette colour is chosen for.
        _activityLine.Foreground = AppTheme.Brush(_vm.ActivityKind == LicenseActivityKind.Failure
            ? AppTheme.FailureText
            : AppTheme.TextSecondary);
        // Trimmed on the row; the whole sentence on hover. Null clears the tooltip rather than leaving
        // a stale one behind an empty line.
        ToolTipService.SetToolTip(_activityLine, string.IsNullOrEmpty(message) ? null : message);
    }

    private void RebuildStatePanel()
    {
        // Guard against a delayed PropertyChanged callback firing after the page has
        // been swapped out of the ContentControl (WinUI 3 Unloaded is not always
        // reliable). _statePanelHost.IsLoaded becomes false when the element is
        // detached from the visual tree; skipping the rebuild in that case avoids
        // wasted work on dead UI.
        if (_statePanelHost == null || _titleStatusChip == null) return;
        if (!_statePanelHost.IsLoaded && !_isInitialBuild) return;
        _isInitialBuild = false;

        // The expiry projection reaches the chip so the two surfaces cannot disagree about one fact:
        // the label is a pure switch over LicenseStatus and could not see StoredKeyExpired, so the
        // panel header said "This license key has expired." while the chip beside the page title
        // said INVALID (LIC-13). The switch itself lives in LicenseStatusChip since LIC-25, where a
        // test can see it — "READ-ONLY (DISABLED)" shipped from here unseen.
        _titleStatusChip.Text = LicenseStatusChip.Label(_vm.Status, _vm.StoredKeyExpired);
        _titleStatusChip.Foreground = AppTheme.Brush(StatusColor(_vm.Status));

        // The chip reports what this machine's licence STATE is, and is deliberately NOT part of the
        // plan below: opening the key form does not change the licence, so a machine inside its
        // free trial still reads TRIAL while the form is open. A chip that tracked the form would be
        // reporting the form rather than the state, which is the worse of the two errors.
        //
        // Everything else — which panel, whether the key-free start may be offered, which CTA leads
        // — is one pure decision (LIC-13). The activate-form request is an INPUT to it, not an `if`
        // ahead of it: as a short-circuit it bypassed every per-status decision and rendered the full
        // no-key panel, tryout offer included, to a user already inside the tryout window. All four
        // inputs are VM state since LIC-19, so the VM can clear the attempt message before any of
        // them changes.
        var plan = LicensePanelPlan.Resolve(
            _vm.Status, _vm.ActivateFormRequested, _vm.TryoutEnded, _vm.StoredKeyExpired);

        // The refresh icon shows exactly where a key is stored (LIC-25) — the plan's call, not the
        // page's. Collapsed, not hidden: the activity text slides left into its place, and the row's
        // MinHeight keeps the card below where it was.
        if (_checkNowButton != null)
            _checkNowButton.Visibility = plan.ShowsCheckNow ? Visibility.Visible : Visibility.Collapsed;

        _statePanelHost.Content = plan.Kind switch
        {
            LicensePanelKind.KeyEntry          => BuildKeyEntryPanel(plan),
            LicensePanelKind.Tryout            => BuildFirstRunGracePanel(),
            LicensePanelKind.Activated         => BuildActivatedPanel(),
            LicensePanelKind.OfflineGrace      => BuildOfflineGracePanel(),
            LicensePanelKind.NeedsRevalidation => BuildNeedsRevalidationPanel(),
            LicensePanelKind.Invalid           => BuildInvalidPanel(plan),
            LicensePanelKind.Disabled          => BuildDisabledReadOnlyPanel(),
            // Unreachable via Resolve, which maps an unknown status to KeyEntry itself; present so a
            // new LicensePanelKind cannot compile into a blank page.
            _                                  => BuildKeyEntryPanel(plan),
        };
    }

    // ── Per-status panels ────────────────────────────────────────────
    //
    // Every one of these returns BARE content, never a card (LIC-26). The page's single card is
    // built once in BuildUI around the status row and _statePanelHost, so a panel that wrapped
    // itself would render a card inside a card. Pinned by
    // LicensePageLayoutSourceContractTests.LicensePage_BuildsExactlyOneCard.

    /// <summary>
    /// The key box panel. Three arrivals share it — no key at all, the activate-form override over a
    /// live tryout, and the resolver's unknown-status fallback — and <paramref name="plan"/> is what
    /// tells them apart. Nothing here re-reads the status: the decisions were all made in
    /// <see cref="LicensePanelPlan.Resolve"/> so they could be tested.
    /// </summary>
    private UIElement BuildKeyEntryPanel(LicensePanelPlan plan)
    {
        // The trial's length is derived, never typed (TryoutWindowCopy via the VM): "7 days" since
        // LIC-21 PR A, null only above the 60-day ceiling — see the onboarding License step.
        var trialLength = LicenseViewModel.TryoutLengthDescription;
        // Three intros for three situations. The middle one is the only place the free trial is
        // offered, and plan.OfferKeyFreeStart is the single authority on whether that is honest here:
        // a user inside the trial window, or one holding a stored key, must not be offered a "start"
        // that would either burn a trial they never received or silently do nothing. The first is the
        // trial-ended intro, where Buy leads (LIC-21: the trial KEY that used to lead here is gone).
        // The trial's LENGTH is stated once on this page — on the explanation line under the buttons,
        // beside the control it describes — never here as well (owner decision 2026-09-07, LIC-25:
        // the header used to repeat "— 7 days").
        var intro = BodyText(plan.BuyIsPrimary
            ? "Your free trial has ended on this device. Enter your license key, or buy one."
            : plan.OfferKeyFreeStart
                ? "Enter your license key to activate VoiceWink on this machine, or start the free trial."
                : "Enter your license key to activate VoiceWink on this machine.");
        var keyBox = CreateKeyEntryTextBox();

        // One row of BUTTONS, every action the same kind of control, explanations beneath — the
        // trial card's layout, adopted here after the owner read the old mix of three buttons plus a
        // blue action sentence as two different kinds of thing (2026-09-05). A FlowPanel so a narrow
        // window wraps the row instead of clipping the last button (every panel's row is one now).
        //
        // The ORDER is LicenseKeyBoxActions' decision, not this method's (LIC-25, owner rule
        // 2026-09-07): Activate → the free-path button → Buy — the free step before the paid one, so
        // the page agrees with the wizard's trial-card-above-key-card structurally. Hand-placed adds
        // here had shipped Activate | Buy | Start free trial. Buy still leads on the used-up-trial key
        // box (the plan's BuyIsPrimary), where no trial button exists to place.
        //
        // The free-path button is exactly one of two, or none. The free-trial start, rendered ONLY
        // when the plan says the offer is honest here — which is what stops it appearing to someone
        // already inside the window (never "sign up": there is no account anywhere, owner
        // 2026-09-02). Or the way BACK to a live trial the user opened this form over: a VM command
        // behind the busy gate, not a page flag flip, because flipping the flag while an activation
        // from this form was in flight let its late refusal land on the trial card (Codex
        // revised-plan round, LIC-19). Neither is the other wearing a disguise — before LIC-13 the
        // return was the trial offer whose handler happened to no-op.
        var buttonRow = ButtonRow();
        TextBlock? freePathExplanation = null;
        foreach (var action in LicenseKeyBoxActions.Order(plan))
        {
            switch (action)
            {
                case LicenseKeyBoxAction.Activate:
                    buttonRow.Children.Add(plan.BuyIsPrimary
                        ? AppTheme.CreateSecondaryButton("Activate", (_, _) => _ = _vm.ActivateCommand.ExecuteAsync(null))
                        : AppTheme.CreateAccentButton("Activate", (_, _) => _ = _vm.ActivateCommand.ExecuteAsync(null)));
                    break;
                case LicenseKeyBoxAction.Buy:
                    buttonRow.Children.Add(CreateBuyButton(primary: plan.BuyIsPrimary));
                    break;
                case LicenseKeyBoxAction.StartTrial:
                    buttonRow.Children.Add(AppTheme.CreateSecondaryButton("Start free trial", (_, _) => _vm.StartTrialCommand.Execute(null)));
                    freePathExplanation = ExplanationText(trialLength is null
                        ? "Start free trial — no key, no email. All features are unlocked while you try VoiceWink."
                        : $"Start free trial — {trialLength}, no key, no email. All features are unlocked while you try VoiceWink.");
                    break;
                case LicenseKeyBoxAction.ReturnToTryout:
                    buttonRow.Children.Add(AppTheme.CreateSecondaryButton("Back to my free trial", (_, _) => _vm.ReturnToTryoutCommand.Execute(null)));
                    freePathExplanation = ExplanationText("Your free trial is still running — keep using VoiceWink and come back here when you have a key.");
                    break;
            }
        }

        var panel = new StackPanel { Spacing = 10, Children = { intro, keyBox } };
        AppendErrorBannerIfAny(panel);
        panel.Children.Add(buttonRow);
        if (freePathExplanation != null) panel.Children.Add(freePathExplanation);
        panel.Children.Add(CreateForgotKeyLink());
        return panel;
    }

    /// <summary>
    /// ONE button-row shape for every panel: a wrap panel, so a narrow window wraps the row instead
    /// of clipping the last button at the card's edge — the key form has been built this way since
    /// LIC-19, while the other panels kept a horizontal StackPanel and clipped (owner UAT 176.29,
    /// 2026-09-06: "View my order" cut mid-word on the Activated panel). Callers pass the buttons in
    /// the order the panel shows them. "Check now" is in no row since LIC-25: it is the refresh icon
    /// beside the status chip, one place for every panel (owner UAT 2026-09-07).
    /// </summary>
    private static FlowPanel ButtonRow(params UIElement[] buttons)
    {
        var row = new FlowPanel
        {
            HorizontalSpacing = 8,
            VerticalSpacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    /// <summary>A one-line explanation beneath a button row (LIC-19): always visible, never a control.</summary>
    private TextBlock ExplanationText(string text) => new()
    {
        Text = text,
        Foreground = AppTheme.Brush(AppTheme.TextSecondary),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    private UIElement BuildFirstRunGracePanel()
    {
        var intro = BodyText(_vm.TrialTimeRemainingDisplay, bold: true);
        var note = BodyText("All features are unlocked during your free trial. Activate a license key before it ends to keep recording.");

        var activateBtn = AppTheme.CreateAccentButton("Activate now", (_, _) => OpenActivateForm());
        var buyBtn = CreateBuyButton();

        var buttonRow = ButtonRow(activateBtn, buyBtn);

        var forgotLink = CreateForgotKeyLink();

        var panel = new StackPanel { Spacing = 10, Children = { intro, note } };
        // Reachable: an activation attempted from this panel's "Activate now" override can end
        // ambiguously, and the resulting seat warning must be visible HERE too — a warning the
        // user cannot see is no warning (Codex round 15).
        AppendErrorBannerIfAny(panel);
        panel.Children.Add(buttonRow);
        panel.Children.Add(forgotLink);
        return panel;
    }

    private UIElement BuildActivatedPanel()
    {
        var keyLine = BodyText($"Activated key: {_vm.MaskedKey ?? "(unknown)"}");
        keyLine.FontFamily = new FontFamily("Consolas");

        var validatedLine = BodyText(_vm.LastValidatedUtc.HasValue
            ? $"Last validated: {_vm.LastValidatedUtc.Value.ToLocalTime():f}"
            : "Not validated yet.");

        // "View my order", not "Manage subscription" — launch licenses are one-time purchases
        // (LIC-8 truthfulness rule: never present a subscription surface that doesn't exist).
        var portalBtn = AppTheme.CreateSecondaryButton("View my order", (_, _) => _vm.OpenCustomerPortalCommand.Execute(null));
        var deactivateBtn = AppTheme.CreateDangerButton("Deactivate on this machine", (_, _) => _ = _vm.DeactivateCommand.ExecuteAsync(null));

        // The destructive Deactivate stays last. "Check now" left this row for the refresh icon beside
        // the status chip (LIC-25) — one place on every panel, which is what "first on every panel"
        // (owner UAT 2026-09-06) was reaching for. A FlowPanel, not a horizontal StackPanel: a narrow
        // window clipped this row at the card's edge with "View my order" cut mid-word (UAT 176.29).
        var buttonRow = ButtonRow(portalBtn, deactivateBtn);

        var panel = new StackPanel { Spacing = 10, Children = { keyLine, validatedLine } };
        // Device-count line only renders when we know the limit — an older activation
        // (pre-LIC-6) or a 0-limit response omits it instead of printing "0 of 0".
        if (!string.IsNullOrEmpty(_vm.DeviceCountLabel))
            panel.Children.Add(BodyText(_vm.DeviceCountLabel));
        // LIC-30: the activation label the Lemon Squeezy dashboard lists for THIS install, so a
        // customer with a dead device can tell support which instances are the living ones. Same
        // guard as the device-count line: absent until the install has activated once.
        if (!string.IsNullOrEmpty(_vm.InstanceLine))
            panel.Children.Add(BodyText(_vm.InstanceLine));
        // Only expiring keys carry an expiry (LIC-4 built it for the retired trial key; a refunded
        // key arrives the same way) — an activated expiring key announces its end date.
        if (_vm.KeyExpiresAt is { } expiresAt)
            panel.Children.Add(BodyText($"This key expires {expiresAt.ToLocalTime():d}."));
        panel.Children.Add(BodyText("Moving to a new machine? Deactivate here first, then activate on the new one."));
        // The Activated panel shows the banner too: a deactivation whose seat release wasn't
        // confirmed can land back here when a newer activation raced it, and a warning the
        // user cannot see is no warning at all.
        AppendErrorBannerIfAny(panel);
        panel.Children.Add(buttonRow);
        return panel;
    }

    /// <summary>
    /// <see cref="LicenseStatus.OfflineGrace"/>. Two claims here were false and are corrected
    /// (LIC-13):
    ///
    /// <para><b>"will revalidate automatically the next time you're online" — it will not.</b>
    /// <c>CheckAsync</c> has exactly four call paths: the launch reconcile, this page's OnLoaded,
    /// "Check now", and since LIC-23 the daily in-session tick of <c>LicenseRevalidationScheduler</c>
    /// — which is time-driven, not connectivity-driven, so it does not make this claim true either.
    /// There is no network-availability LISTENER anywhere in the app — <c>NetworkChange</c> / <c>NetworkAvailabilityChanged</c> / <c>NetworkAddressChanged</c>
    /// have zero hits, while <c>GetIsNetworkAvailable</c> appears twice as a one-shot connectivity
    /// PROBE (<c>RetryingHandler</c>, <c>AIEnhancementService</c>) and never as a change listener that
    /// could trigger a validate. (The first version of this comment claimed zero hits for all four:
    /// the search behind it globbed two directory levels and so covered 245 of 438 source files,
    /// missing both probes. Kimi diff round, 2026-09-04.) A user who reconnects
    /// mid-session gets nothing until the next daily tick (LIC-23) or the app next starts — both of
    /// which DO revalidate, forced (every launch makes one forced validate, on both routing
    /// branches; the timer ticks once a day while the app runs); before LIC-22 the window
    /// could run out into a state that blocks recording, having promised self-healing.</para>
    ///
    /// <para><b>"up to 30 days" is wrong for any key carrying an expiry.</b> The expiry check
    /// precedes the offline-grace branch in both classifiers, so an expired key gets NO offline
    /// grace: an expiring key in this state has until its expiry, which may be days away, not 30. The
    /// window is now derived from <c>LicenseService.OfflineGraceDuration</c> rather than typed, and
    /// the expiry is named whenever the key has one.</para>
    /// </summary>
    private UIElement BuildOfflineGracePanel()
    {
        var header = BodyText("Working offline — this license hasn't been checked recently.", bold: true);

        var offlineDays = (int)LicenseService.OfflineGraceDuration.TotalDays;
        // An expiry, when present, always wins: it is checked BEFORE the grace branch, so the grace
        // window cannot outlast it. Naming both would invite the user to average them.
        // The check is the refresh icon beside the status chip (LIC-25) — this panel used to carry
        // it as its accent button, so the sentence now says where it went.
        var detail = _vm.LastValidatedUtc.HasValue
            ? _vm.KeyExpiresAt is { } expiry
                ? BodyText($"Last successful check: {_vm.LastValidatedUtc.Value.ToLocalTime():f}. This key expires {expiry.ToLocalTime():d} and offline use ends then. When you are back online, use the refresh button beside the status to check again.")
                : BodyText($"Last successful check: {_vm.LastValidatedUtc.Value.ToLocalTime():f}. Offline use continues for up to {offlineDays} days from that date. When you are back online, use the refresh button beside the status to check again.")
            : BodyText("Offline grace is active. Connect to the internet and use the refresh button beside the status to check again.");

        var panel = new StackPanel { Spacing = 10, Children = { header, detail } };
        // LIC-30: an offline laptop still holds its dashboard instance — the label is what lets
        // support tell it from a dead machine's instance.
        if (!string.IsNullOrEmpty(_vm.InstanceLine))
            panel.Children.Add(BodyText(_vm.InstanceLine));
        AppendErrorBannerIfAny(panel); // a pending seat warning must be visible on EVERY panel
        return panel;
    }

    /// <summary>
    /// <see cref="LicenseStatus.GraceExpired"/> — "the stored key needs an online validate before it
    /// is trusted again". It has THREE causes and the panel can see none of them, so it names none
    /// (LIC-13; the LIC-8 truthfulness rule applied to a status with more than one derivation):
    ///
    /// <list type="number">
    /// <item>offline past the 30-day window (<c>LicenseService</c> step 5's final return);</item>
    /// <item>no binding proof under an ARMED manifest — every key activated while the manifest was
    /// STAGED has an empty proof, so arming forces its first online revalidation. That is the whole
    /// pre-arm population at the moment Batch C lands, on machines that are online with a fresh
    /// timestamp;</item>
    /// <item>a stored key with no last-validated stamp at all.</item>
    /// </list>
    ///
    /// <para>The previous copy asserted cause 1 for all three ("Offline for more than 30 days —
    /// please reconnect to revalidate"), which sent an online user to debug their connection. It also
    /// avoids "until VoiceWink can reach the license server": the app can reach it and still be
    /// handed a refusal, so that phrasing names a cause too (Codex plan round, 2026-09-04).</para>
    ///
    /// <para>The key box and the recovery link are the second half of the fix: this is a
    /// recording-blocked screen, and it was the only one offering no way to try a different key.</para>
    /// </summary>
    private UIElement BuildNeedsRevalidationPanel()
    {
        var header = BodyText("This license needs to be revalidated.", bold: true);
        // The "…and this page updates with the result" clause is back, because LIC-15's activity line
        // now makes it true: every user-initiated check reports its outcome, including "no change" and
        // "couldn't reach the license server". LIC-13 had to drop the clause as a promise the page
        // could not keep (Kimi diff round A5, 2026-09-04); this is the change that keeps it.
        var detail = BodyText("Your license key is still stored on this machine. New recordings are paused until it has been checked; History, export, Dictionary and settings stay available. Use the refresh button beside the status to check the stored key — the result appears on that line.");
        var keyBox = CreateKeyEntryTextBox();

        // The check itself is the refresh icon beside the chip (LIC-25); this row keeps the other
        // half of LIC-13's fix — a way to try a DIFFERENT key on a recording-blocked screen — and
        // keeps it secondary: the icon is the revalidate, the button is "different key", and
        // promoting the button would invert the common case (Grok plan round, 2026-09-07).
        var activateBtn = AppTheme.CreateSecondaryButton("Try this key", (_, _) => _ = _vm.ActivateCommand.ExecuteAsync(null));

        var buttonRow = ButtonRow(activateBtn);

        var panel = new StackPanel { Spacing = 10, Children = { header } };
        // LIC-30: the stored key's activation label (see BuildActivatedPanel) — a machine waiting for
        // its revalidation still occupies its dashboard instance. Placed BEFORE the detail sentence,
        // never directly above the key box: prose over an empty box reads as that box's label
        // (LIC-28, owner UAT 181.13), and the box must stay right after the detail.
        if (!string.IsNullOrEmpty(_vm.InstanceLine))
            panel.Children.Add(BodyText(_vm.InstanceLine));
        panel.Children.Add(detail);
        panel.Children.Add(keyBox);
        AppendErrorBannerIfAny(panel); // a pending seat warning must be visible on EVERY panel
        panel.Children.Add(buttonRow);
        panel.Children.Add(CreateForgotKeyLink());
        return panel;
    }

    private UIElement BuildInvalidPanel(LicensePanelPlan plan)
    {
        // LIC-4: a persisted-and-passed LS expiry means the key ran out — say so instead of the
        // generic can't-validate copy. It no longer quotes a seven-day trial-key lifetime:
        // the code establishes only "a persisted expires_at has passed", and trial-ness rests on a
        // catalog invariant enforced in the release preflight rather than anything the app can read
        // (a merchant-extended trial, or any future expiring variant, lands here too). The real date
        // is held in KeyExpiresAt and is now used instead of a typed length (LIC-13).
        // Read the FACT, not the plan's Buy weighting: BuyIsPrimary is true here for exactly this
        // reason today, but since LIC-21 it also covers the used-up-trial key box, so aliasing it
        // would make this panel announce an expiry the next time that disjunction grows (Grok r2).
        var expired = _vm.StoredKeyExpired;
        var header = BodyText(expired
            ? "This license key has expired."
            : "We couldn't validate this license key.", bold: true);
        // The support instruction names no key in prose since LIC-28 (owner UAT 181.13, 2026-09-09):
        // the key box's PLACEHOLDER is the stored key, masked (CreateKeyEntryTextBox), so the sentence
        // points at the box. LIC-13 had rendered the masked key as a sentence above an EMPTY box, which
        // reads as that box's label. Still guarded on a key actually being stored: after a failed
        // activation from Unlicensed this status is adopted with no key on file, and "shown below"
        // would then point at the format hint. Appended to the chosen BODY, after the branch, so the
        // expired and the generic wording cannot drift apart on it.
        var supportClause = _vm.MaskedKey is { Length: > 0 }
            ? $" If it should be valid, contact {VoiceWinkUrls.SupportEmail} and quote the key shown below."
            : string.Empty;
        var body = expired
            ? _vm.KeyExpiresAt is { } expiredOn
                ? $"It expired on {expiredOn.ToLocalTime():d}. Buy a license to keep using VoiceWink, or enter a different key below."
                : "Buy a license to keep using VoiceWink, or enter a different key below."
            // The cause list now leads with the one this panel's most common reachable cause actually
            // is — a fingerprint mismatch after a hardware or OS change (LicenseService step 2) —
            // and drops "activated on another machine", which never produces this status by itself
            // (a consumed seat surfaces through its own activation-limit message).
            : "This can happen if the key was revoked, if this machine's hardware no longer matches the one it was activated on, or if the key was mistyped. Enter it again below to re-activate on this machine.";
        var detail = BodyText(body + supportClause);
        var keyBox = CreateKeyEntryTextBox();
        // An EXPIRED key is one of the two screens where Buy is the primary (onboarding License step
        // v2, 2026-09-03; the used-up free trial's key box is the other since LIC-21): days of use
        // sit behind it, which the research basis names as the highest-intent moment. The generic
        // invalid-key branch keeps "Try this key" primary.
        var activateBtn = expired
            ? AppTheme.CreateSecondaryButton("Try this key", (_, _) => _ = _vm.ActivateCommand.ExecuteAsync(null))
            : AppTheme.CreateAccentButton("Try this key", (_, _) => _ = _vm.ActivateCommand.ExecuteAsync(null));
        var buyBtn = CreateBuyButton(primary: expired);
        // The recovery path for a server-verdict flag that turned out mistaken (LIC-4 binding
        // mismatch, LIC-2a disabled) is the refresh icon beside the status chip (LIC-25): the
        // unforced OnLoaded refresh short-circuits on the persisted flag forever, so without a forced
        // check a recovered key could never leave this panel without re-entering the key (burning a
        // seat). The icon is always offered on this panel (plan.ShowsCheckNow).

        // The weighted pair — the accent is what marks the primary action, not the slot.
        var buttonRow = expired
            ? ButtonRow(buyBtn, activateBtn)
            : ButtonRow(activateBtn, buyBtn);

        var panel = new StackPanel { Spacing = 10, Children = { header, detail, keyBox } };
        AppendErrorBannerIfAny(panel);
        panel.Children.Add(buttonRow);
        panel.Children.Add(CreateForgotKeyLink());
        return panel;
    }

    private UIElement BuildDisabledReadOnlyPanel()
    {
        // LIC-8: "disabled" is a manual merchant action whose cause the app cannot know
        // (refund, fraud, chargeback, replacement, or error) — never claim a refund.
        var header = BodyText("This license has been disabled.", bold: true);
        // Until LIC-27 this panel never said WHICH key it meant (owner UAT 181.8, 2026-09-08); LIC-27
        // rendered it masked as a sentence above the key box, and the owner's next UAT
        // (181.13, 2026-09-09) read that sentence as the label of the EMPTY box beneath it — the exact
        // hazard LIC-27's own self-review had tried to talk around with a second sentence. Since
        // LIC-28 the key box's PLACEHOLDER is the stored key, masked (CreateKeyEntryTextBox), and the
        // support instruction points at the box. The key IS on file in this state (the flag is
        // persisted beside it and the refresh icon revalidates it); the guard is what keeps "shown
        // below" from ever pointing at the format hint. "Otherwise" makes the two instructions the
        // alternatives they are: a placeholder is visible only while the box is EMPTY, so a sentence
        // that asked the user to quote it AND to type into the same box contradicted itself
        // (self-review lens A, 2026-09-09) — the user on the support path has typed nothing, and the
        // user typing a new key is not the one who needs the old one.
        var quoteClause = _vm.MaskedKey is { Length: > 0 } ? " and quote the key shown below" : string.Empty;
        var detail = BodyText($"New recordings are paused, but History export and Dictionary remain accessible so you can extract any data you need. If you did not expect this, contact {VoiceWinkUrls.SupportEmail}{quoteClause}. Otherwise, enter a new license key below to resume normal use.");
        var keyBox = CreateKeyEntryTextBox();

        var activateBtn = AppTheme.CreateAccentButton("Activate new key", (_, _) => _ = _vm.ActivateCommand.ExecuteAsync(null));
        var buyBtn = CreateBuyButton();
        // Same mistaken-verdict recovery as the Invalid panel — the refresh icon beside the status
        // chip force-revalidates, so a mistakenly-disabled key (merchant error, transient LS
        // incident) can clear the persisted flag without consuming another activation (LIC-25: it
        // was this row's third button, then its first; now it is the one place every panel shares).
        var buttonRow = ButtonRow(activateBtn, buyBtn);

        var panel = new StackPanel { Spacing = 10, Children = { header, detail, keyBox } };
        AppendErrorBannerIfAny(panel);
        panel.Children.Add(buttonRow);
        // This panel asks for a new key, so it needs the recovery link its siblings have: it was the
        // only key-box panel without one (LIC-13).
        panel.Children.Add(CreateForgotKeyLink());
        return panel;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private TextBlock BodyText(string text, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        if (bold) tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return tb;
    }

    /// <summary>
    /// The key-entry TextBox pattern (placeholder, typed key, monospace font,
    /// TextChanged → VM.LicenseKeyInput) appears in four panels (KeyEntry,
    /// NeedsRevalidation, Invalid, DisabledReadOnly). Single factory so a later tweak — e.g. a max-
    /// length validator — only needs one edit. The PLACEHOLDER is the stored key, masked, whenever one
    /// is stored (LIC-28, owner UAT 181.13): every panel with a stored key shows it inside the box.
    /// The box's Text stays the user's typed input and nothing else — Text is what Activate sends,
    /// and <see cref="LicenseKeyBoxPlaceholder"/> carries the two hazards that rules out.
    /// </summary>
    private TextBox CreateKeyEntryTextBox()
    {
        var keyBox = new TextBox
        {
            PlaceholderText = LicenseKeyBoxPlaceholder.Resolve(_vm.MaskedKey),
            Text = _vm.LicenseKeyInput,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new FontFamily("Consolas"),
        };
        keyBox.TextChanged += (_, _) => _vm.LicenseKeyInput = keyBox.Text;
        return keyBox;
    }

    /// <summary>
    /// Append the seat notice, then the attempt message, to <paramref name="panel"/> when set (LIC-19).
    /// The seat notice renders on EVERY panel — a warning the user cannot see is no warning — while
    /// the attempt message only ever exists for the panel it describes, because the VM clears it
    /// before any panel-plan input changes.
    /// </summary>
    private void AppendErrorBannerIfAny(StackPanel panel)
    {
        if (!string.IsNullOrEmpty(_vm.SeatNotice))
            panel.Children.Add(ErrorBanner(_vm.SeatNotice));
        if (!string.IsNullOrEmpty(_vm.AttemptMessage))
            panel.Children.Add(ErrorBanner(_vm.AttemptMessage));
    }

    private UIElement ErrorBanner(string message)
    {
        // The TEXT is WarningText, the BORDER the amber accent (LIC-20): the accent at 12 px measures
        // 1.97:1 on the light page, while a 1 px amber line reads fine. The banners stay amber by
        // owner decision (2026-09-05): amber = a state needing attention, red = the thing you just
        // did failed (the activity line).
        var textBlock = new TextBlock
        {
            Text = message,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        return new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderBrush = AppTheme.Brush(AppTheme.AccentAmber),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = textBlock,
        };
    }

    /// <summary>
    /// The Buy button, with its destination tooltip attached by construction. Four panels offer Buy
    /// and two of them had been built without the tooltip — the same omission twice, in a file that
    /// states the reason for it at the two sites that did remember: <c>OpenUrl</c> is fail-soft, so
    /// the tooltip is the one place the destination survives a blocked browser launch. Making the
    /// tooltip part of the constructor removes the chance of forgetting it a third time (LIC-13).
    /// </summary>
    private Border CreateBuyButton(bool primary = false)
    {
        var button = primary
            ? AppTheme.CreateAccentButton("Buy a license", (_, _) => _vm.BuyCommand.Execute(null))
            : AppTheme.CreateSecondaryButton("Buy a license", (_, _) => _vm.BuyCommand.Execute(null));
        ToolTipService.SetToolTip(button, VoiceWinkUrls.Buy);
        return button;
    }

    private UIElement CreateForgotKeyLink()
        => AppTheme.CreateActionLink("Lost your key? Recover it from your Lemon Squeezy orders.",
            () => _vm.ForgotKeyCommand.Execute(null), VoiceWinkUrls.LostKey);

    private void OpenActivateForm()
    {
        // Tryout users who click "Activate now" haven't entered a key yet — ask the VM for the key
        // form. The request is a panel-plan input the VM owns (LIC-19): the VM clears it on any status
        // transition so a service-driven change wins, and its ReturnToTryout command is the way back.
        // The PropertyChanged rebuild renders the form; the direct rebuild below is idempotent and
        // keeps the click alive if WinUI's unreliable Unloaded has already dropped the subscription
        // on a page that is still visible (this file's own RebuildStatePanel comment).
        _vm.ActivateFormRequested = true;
        RebuildStatePanel();
    }

    // The chip is 12 px SemiBold TEXT on the page background, so its amber states take WarningText
    // (LIC-20); the two blue states are left as they are — re-tinting ACTIVE is a design choice the
    // owner has not been asked about (UI-15 carries the question).
    private static Windows.UI.Color StatusColor(LicenseStatus status) => status switch
    {
        LicenseStatus.Activated         => AppTheme.AccentBlue,
        LicenseStatus.FirstRunGrace     => AppTheme.AccentBlue,
        LicenseStatus.OfflineGrace      => AppTheme.WarningText,
        LicenseStatus.GraceExpired      => AppTheme.WarningText,
        LicenseStatus.Invalid           => AppTheme.WarningText,
        LicenseStatus.DisabledReadOnly  => AppTheme.WarningText,
        _                               => AppTheme.TextSecondary,
    };
}
