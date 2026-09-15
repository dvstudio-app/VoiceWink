using global::System.Diagnostics;
using global::System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Licensing;

namespace VoiceWink.ViewModels;

/// <summary>
/// ViewModel for the License page. Translates <see cref="LicenseService"/> state into
/// observable properties + user commands (activate, deactivate, start the free trial, buy,
/// check now, open customer portal). Registered as
/// transient in DI so a fresh query runs on every navigation to the page.
///
/// <para>The commands delegate directly to <see cref="LicenseService"/> and never touch
/// settings directly — license state persistence is the service's job.</para>
/// </summary>
public partial class LicenseViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<LicenseViewModel>();

    private readonly LicenseService _license;

    // Cross-command in-flight flag. Interlocked so concurrent entry from two different
    // commands (e.g., an OnLoaded refresh + a user-clicked Activate) is fully serialized.
    // Two separately-initiated async commands can interleave at every await regardless
    // of which SynchronizationContext they resume on, so the gate has to be atomic.
    private int _busyFlag;

    [ObservableProperty] private LicenseStatus _status = LicenseStatus.Unlicensed;
    [ObservableProperty] private string? _maskedKey;
    [ObservableProperty] private DateTimeOffset? _lastValidatedUtc;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrialTimeRemainingDisplay))]
    private TimeSpan? _trialTimeRemaining;
    [ObservableProperty] private string _licenseKeyInput = string.Empty;
    /// <summary>
    /// What the most recent user command reported as a clean or local failure — an empty key, a
    /// refused activation (including the activation-limit case, whose typed result says no seat was
    /// consumed), an unexpected exception — and nothing else (LIC-19). It belongs to the panel the
    /// user was on when the command ran, and it is CLEARED before every panel-plan input change
    /// (<see cref="Status"/>, <see cref="ActivateFormRequested"/>, <see cref="TryoutEnded"/>,
    /// <see cref="StoredKeyExpired"/>), so a refusal can never render on a panel it does not
    /// describe — owner UAT 176.2 (2026-09-05) found "maximum number of devices" standing on the
    /// tryout card. A command that produces one writes it AFTER its own status assignment, so the
    /// assignment's clear cannot swallow the result being reported.
    /// </summary>
    [ObservableProperty] private string? _attemptMessage;
    /// <summary>
    /// The durable seat warning — strictly the outcomes whose typed result says a seat may remain
    /// held or state may return after a restart (<c>SeatMayBeConsumed</c> on activate; an unconfirmed
    /// release or a failed clear on deactivate). Mirrors <c>LicenseService.SeatNotice</c> so it
    /// outlives this TRANSIENT VM, survives every panel change and every network blip, and is cleared
    /// only by the command that resolves it (<see cref="PublishSeatNotice"/>). Never a clean refusal:
    /// before LIC-19 one property carried both, and every panel rendered whatever was in it.
    /// </summary>
    [ObservableProperty] private string? _seatNotice;
    /// <summary>
    /// The user asked for the key form over a live tryout ("Activate now" on the tryout card). A
    /// panel-plan INPUT (<see cref="LicensePanelPlan.Resolve"/>), owned here rather than by the page
    /// since LIC-19 so this VM's own commands can clear it: a status transition always wins
    /// (<c>OnStatusChanged</c>), and <see cref="ReturnToTryout"/> is the user's way back.
    /// </summary>
    [ObservableProperty] private bool _activateFormRequested;
    /// <summary>
    /// What a user-initiated command is doing, or what it just did — including "nothing changed" and
    /// "the server could not be reached" (LIC-15). Rendered by the page's activity line, which lives
    /// OUTSIDE the rebuilt panel subtree, so this can carry busy state without making
    /// <see cref="IsBusy"/> a rebuild trigger (a rebuild mid-keystroke resets the key box's focus and
    /// caret — the documented reason it is excluded).
    ///
    /// <para><b>Deliberately NOT <see cref="SeatNotice"/>.</b> That property is the durable
    /// seat-notice surface, so sharing one property would let a transient "couldn't reach the license
    /// server" overwrite "your seat may still be consumed — contact support": a warning about money
    /// replaced by a warning about wifi. Every non-null write goes through <see cref="Announce"/> so
    /// <see cref="ActivityKind"/> can never be stale.</para>
    /// </summary>
    [ObservableProperty] private string? _activityMessage;
    /// <summary>
    /// How the page colours <see cref="ActivityMessage"/> (LIC-19): a failure reads in the failure
    /// colour, progress and success in the secondary text colour. Written BEFORE the message by
    /// <see cref="Announce"/>, so the page reads the right kind when the message notifies.
    /// </summary>
    [ObservableProperty] private LicenseActivityKind _activityKind;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int? _activationLimit;
    [ObservableProperty] private int? _activationUsage;
    [ObservableProperty] private string? _deviceCountLabel;
    /// <summary>
    /// "This device: VoiceWink-9baf50bb" — the activation label the dashboard lists for THIS install
    /// (LIC-30), so a customer can name the living devices and support frees the right seat. Null
    /// until the install has activated once; rendered on the three stored-key panels only.
    /// </summary>
    [ObservableProperty] private string? _instanceLine;
    /// <summary>True when the most recent activation failed because every seat was taken (LIC-6).</summary>
    [ObservableProperty] private bool _activationLimitReached;
    /// <summary>
    /// True when the stored key carries a persisted LS expiry that has passed (LIC-4 built it for
    /// the trial key that LIC-21 retired; a refunded key arrives the same way, and it is the live
    /// expiry path now). Drives the Invalid panel's "This license key has expired" copy variant
    /// and the wizard's Buy weighting.
    /// </summary>
    [ObservableProperty] private bool _storedKeyExpired;
    /// <summary>
    /// True when the free trial was started on this device and its window has run out
    /// (<c>LicenseService.HasFirstRunGraceEnded</c>; onboarding License step v2, 2026-09-03).
    /// Drives the wizard's "trial has ended" card variant and the License page's used-up-trial
    /// button hierarchy; refreshed with every other local projection.
    /// </summary>
    [ObservableProperty] private bool _tryoutEnded;
    /// <summary>
    /// The stored key's LS-reported expiry when it has one — only expiring keys do (LIC-4; a
    /// refunded key, or one of the retired trial keys). Renders the Activated panel's
    /// "This key expires …" line; null hides it.
    /// </summary>
    [ObservableProperty] private DateTimeOffset? _keyExpiresAt;

    /// <summary>
    /// Human-readable countdown string for the FirstRunGrace panel — "Free trial — 2 days 5h
    /// remaining." Days-aware so a multi-day window reads in days rather than "317h 42m
    /// remaining."; falls back to hours+minutes precision once we drop below a day, where minute
    /// granularity matters. Returns the singular "day" when only one day remains.
    ///
    /// <para>"Free trial" again since LIC-21 (owner decision 2026-09-06): the local window is the
    /// ONLY try path — the Lemon Squeezy trial key is gone — so the 2026-09-02 vocabulary split
    /// that kept "trial" for the key and called this window a key-less tryout has nothing left
    /// to separate.</para>
    ///
    /// <para>Centralized here so <see cref="Views.Pages.LicensePage"/> and
    /// <see cref="Views.Pages.OnboardingPage"/> render identical strings without duplicating
    /// the formatting logic. Re-fires PropertyChanged via the
    /// <see cref="NotifyPropertyChangedForAttribute"/> on <see cref="TrialTimeRemaining"/>.</para>
    /// </summary>
    public string TrialTimeRemainingDisplay => FormatTrialRemaining(TrialTimeRemaining);

    /// <summary>
    /// Extracted so <see cref="RefreshLocalState"/> can compare the RENDERED string before assigning
    /// <see cref="TrialTimeRemaining"/> — see the gate there for why that matters (LIC-15). The
    /// format itself lives in <see cref="TryoutWindowCopy.FormatRemaining"/> since LNC-11, where
    /// the Home page reads the same string.
    /// </summary>
    private static string FormatTrialRemaining(TimeSpan? remaining) => TryoutWindowCopy.FormatRemaining(remaining);

    /// <summary>
    /// The length of the free-trial window in words ("7 days"), or <c>null</c> when it must not be
    /// quoted — <see cref="TryoutWindowCopy.Describe"/> applied to
    /// <see cref="LicenseService.FirstRunGraceDuration"/>. Both pages read this ONE expression, so no
    /// page can hard-code a day count the shipped constant contradicts (7 days since LIC-21 PR A,
    /// where this says "7 days" with no copy edit; under the earlier 3650-day tester scaffold it
    /// was null and the sentences carried no length).
    /// </summary>
    public static string? TryoutLengthDescription => TryoutWindowCopy.Describe(LicenseService.FirstRunGraceDuration);

    public LicenseViewModel(LicenseService license)
    {
        _license = license;
        // Read-only state first so the page can render without waiting for network.
        // Status is seeded from the synchronous GetCachedStatus so a freshly-resolved
        // VM doesn't briefly flash "Unlicensed" to the user before the async refresh
        // runs — matters for LicensePage's initial render and for callers (like
        // OnboardingPage.BuildLicenseStep) that read Status at construction time.
        Status = _license.GetCachedStatus();
        RefreshLocalState();
        // Carry over a seat-critical warning from an operation whose page is gone. This VM is
        // TRANSIENT — every navigation to the License page builds a new one — so without this
        // a "your seat may still be consumed" message raised during a slow call would simply
        // vanish when the user navigated away, and they'd meet the activation limit later with
        // no explanation (Codex round 14).
        SeatNotice = _license.SeatNotice;
    }

    /// <summary>
    /// Cache-respecting refresh — used by the page's OnLoaded lifecycle. Honors
    /// <see cref="LicenseService"/>'s 24h validation cache so every page navigation
    /// doesn't hit the LemonSqueezy API.
    /// </summary>
    [RelayCommand]
    public Task RefreshAsync() => DoCheckAsync(force: false, announce: false);

    /// <summary>
    /// Force-revalidate — the user-facing "Check now": since LIC-25 the refresh icon beside the
    /// status chip, shown on the five keyed panels per <c>LicensePanelPlan.ShowsCheckNow</c>
    /// (Activated, OfflineGrace, NeedsRevalidation, Invalid and Disabled — the last two have it as
    /// the mistaken-verdict recovery path); before LIC-25 a button on each of those panels. Bypasses
    /// the 24h cache so the click actually reaches the server (otherwise it is a visible no-op when
    /// the cache is fresh) — and since LIC-15 it also REPORTS its outcome, which is what makes "no
    /// visible reaction" impossible rather than merely unlikely.
    /// </summary>
    [RelayCommand]
    public Task ForceRefreshAsync() => DoCheckAsync(force: true, announce: true);

    /// <summary>
    /// The launch reconcile for a problem-state launch (LIC-22): the page the startup redirect
    /// builds runs this as its first refresh instead of <see cref="RefreshAsync"/>. Forced, so it
    /// reaches the server past the 24h cache AND past every persisted verdict flag — a key
    /// re-enabled or extended on the merchant side heals here without a click — and silent, for
    /// LIC-15's reason: it is not user-initiated, so "Checking…" on the activity line would be
    /// noise. The forced path's offline catch keeps every persisted verdict, so an offline launch
    /// leaves the user exactly where the cached chain puts them.
    /// </summary>
    public Task StartupReconcileAsync() => DoCheckAsync(force: true, announce: false);

    /// <param name="announce">
    /// Write <see cref="ActivityMessage"/> for this run. TRUE only for the user-clicked "Check now":
    /// the unforced OnLoaded refresh is not user-initiated, and flashing "Checking…" on every
    /// navigation to the page is noise. This is why the message is written by the two public wrappers
    /// rather than unconditionally in here (LIC-15).
    /// </param>
    /// <summary>
    /// What a click turned away by the busy gate says. The gate is an <c>Interlocked</c> CAS, so such
    /// a click is already a safe no-op — but returning SILENTLY is exactly the "I clicked it and
    /// nothing happened" report, and it is worst during the unforced OnLoaded refresh, which can hold
    /// the slot for the full licensing timeout while Deactivate, Activate and Check now all look dead
    /// (LIC-15).
    /// </summary>
    internal const string BusyGateNotice = "Still finishing the previous action — one moment.";

    private async Task DoCheckAsync(bool force, bool announce)
    {
        if (!TryEnterBusy())
        {
            // Only a user-initiated run speaks: the OnLoaded refresh being gated is invisible to the
            // user and should stay that way.
            if (announce) Announce(BusyGateNotice, LicenseActivityKind.Progress);
            return;
        }
        if (announce) Announce("Checking…", LicenseActivityKind.Progress);
        var statusBefore = Status;
        var failed = false;
        // Whether the server was actually consulted comes from the SERVICE, not from inference here.
        // An earlier version of this method deduced it from the last-validated stamp advancing, and
        // that is wrong in both directions: a fingerprint mismatch returns Invalid before any HTTP
        // (so the stamp never moves though the machine is online), and a server-confirmed refusal
        // need not move it either (so a completed check read as unreachable). Either way the page
        // would have asserted a cause it could not establish — the exact defect class LIC-13 spent
        // four panels fixing. Caught by the Codex plan round, 2026-09-04.
        var outcome = LicenseCheckOutcome.NotAttempted;
        try
        {
            var result = await _license.CheckWithOutcomeAsync(force: force);
            outcome = result.Outcome;
            // A refresh that lands on Activated obsoletes any prior "activation limit
            // reached" signal from a stale activate attempt — otherwise the flag could
            // outlive the condition it describes. Activation/Deactivation paths reset
            // it directly; this covers the refresh path for completeness.
            if (result.Status == LicenseStatus.Activated) ActivationLimitReached = false;
            Status = result.Status;
            RefreshLocalState();
            // A check that REACHED the server has a fresh verdict, so a stale attempt message from an
            // earlier command is spent — it used to survive onto whatever panel came next, e.g. "That
            // license key wasn't found." rendered on the freshly Activated panel. Keyed on the OUTCOME,
            // not on the stamp advancing: a completed check that changes nothing (a refusal that
            // re-confirms Invalid) moves no stamp, and the plan-input clear only fires when Status
            // actually changed (Codex plan round, LIC-19). Unreachable and NotAttempted clear nothing,
            // and no check ever touches SeatNotice — a seat warning must outlive a network blip.
            if (outcome == LicenseCheckOutcome.Completed) AttemptMessage = null;
        }
        catch (Exception ex)
        {
            failed = true;
            Logger.Warning(ex, "License refresh failed (force={Force})", force);
        }
        finally { ExitBusy(); }

        if (announce)
        {
            var (message, kind) = DescribeCheckOutcome(failed, outcome, statusBefore);
            Announce(message, kind);
        }
        else if (ActivityMessage == BusyGateNotice)
        {
            // This silent run HELD the busy slot that a gated click was turned away from. A silent
            // run must not narrate — but it must not leave "Still finishing the previous action"
            // standing after that action HAS finished either. Nothing else ever clears the notice:
            // the announced path overwrites it, activate/deactivate clear it in their finally, and
            // the reprojection tick deliberately never touches this property — so on a slow network
            // the line would keep claiming an in-flight action indefinitely, to a user who had just
            // clicked Deactivate (Kimi diff round, 2026-09-04).
            //
            // LIC-22: when the silent run was the FORCED launch reconcile, the click it turned away
            // wanted exactly what this run just did — a server check — so it gets that run's result
            // line instead of nothing. Clearing to null here would have re-created the "I clicked it
            // and nothing happened" outcome LIC-15 exists to prevent, on the launches where the
            // startup check now holds the slot for a slow network's whole timeout (self-review,
            // correctness lens). The unforced OnLoaded refresh keeps the clear: DescribeCheckOutcome's
            // NotAttempted wording is safe only for a forced check (see its doc), and an unforced run
            // that held the slot long enough to gate a click had reached the server anyway.
            if (force)
            {
                var (message, kind) = DescribeCheckOutcome(failed, outcome, statusBefore);
                Announce(message, kind);
            }
            else
            {
                ActivityMessage = null;
            }
        }
    }

    /// <summary>
    /// What "Check now" just did, in the user's terms — every branch a statement the code can
    /// actually establish (LIC-15).
    ///
    /// <para>Only ever rendered for a FORCED check: <c>announce</c> is true solely for
    /// <see cref="ForceRefreshAsync"/>, which is what makes the <c>NotAttempted</c> wording safe to
    /// write as it is. Force skips the four persisted-verdict flags and the fresh-cache return, so on
    /// a forced check that outcome means only "no key on file" or "the fingerprint no longer matches
    /// this machine" — for both of which re-activating here is the fix.</para>
    /// </summary>
    private (string Message, LicenseActivityKind Kind) DescribeCheckOutcome(bool failed, LicenseCheckOutcome outcome, LicenseStatus statusBefore) =>
        failed ? ("Couldn't complete the check. Your license is unchanged.", LicenseActivityKind.Failure)
        : outcome switch
        {
            // The one case where blaming connectivity is truthful: the request went out and could
            // not be completed.
            LicenseCheckOutcome.Unreachable =>
                ("Couldn't reach the license server. Your license is unchanged.", LicenseActivityKind.Failure),
            // No request was sent, so say nothing about the network.
            LicenseCheckOutcome.NotAttempted =>
                ("This device's stored license couldn't be checked. Activate a license key on this device.", LicenseActivityKind.Failure),
            // A response arrived and was applied. The status carries the verdict, so comparing it is
            // enough — and "no change" is true whether the server validated the key, refused it
            // without changing the verdict, or sent something stale that was discarded. When it DID
            // change, name the state it changed to: "this page has been updated" told the user nothing
            // the new panel did not already show (owner UAT 2026-09-06, Phase B).
            _ => Status != statusBefore
                ? ($"Checked just now — {DescribeChangedStatusForActivityLine(Status)}.", LicenseActivityKind.Success)
                : ("Checked just now — no change.", LicenseActivityKind.Success),
        };

    /// <summary>The clause the activity line falls back to for a status outside the closed vocabulary
    /// below — never reached (the enum sweep in <c>LicenseViewModelTests</c> pins that every status
    /// has its own clause), kept so a future status cannot print an enum name to a user.</summary>
    internal const string ChangedStatusFallback = "this license has changed";

    /// <summary>
    /// The clause after "Checked just now — " when a completed check CHANGED the status: a closed
    /// vocabulary over the seven statuses. The key-bearing states read "this license is now …"; the
    /// two no-key states do not call the trial a state of "this license" (there is no licence there
    /// — self-review, docs lens; both are unreachable from a completed check today, since a no-key
    /// check returns NotAttempted, and stay total for the enum sweep). The expired-key nuance reads
    /// the same fact the Invalid panel reads (<see cref="StoredKeyExpired"/>), so the line and the
    /// panel cannot disagree; GraceExpired uses the panel's own words ("checked").
    /// </summary>
    internal string DescribeChangedStatusForActivityLine(LicenseStatus status) => status switch
    {
        LicenseStatus.Activated        => "this license is now active",
        LicenseStatus.OfflineGrace     => "this license is now working offline",
        LicenseStatus.GraceExpired     => "this license now needs to be checked again",
        LicenseStatus.Invalid          => StoredKeyExpired ? "this license has now expired" : "this license is now invalid",
        LicenseStatus.DisabledReadOnly => "this license is now disabled",
        LicenseStatus.FirstRunGrace    => "this device is on the free trial",
        LicenseStatus.Unlicensed       => "no license is stored on this device",
        _                              => ChangedStatusFallback,
    };

    /// <summary>
    /// Re-read the licence state from the service WITHOUT touching the network, for the page's
    /// periodic tick (LIC-15). Three things make an open page go stale, and none raises any event:
    /// the launch reconcile is a fire-and-forget <c>CheckAsync</c> on a pool thread that can persist a
    /// disable or a refusal, the LIC-23 daily in-session tick is the same forced check hours into an
    /// open session, and every time-driven boundary — trial expiry, the 24h revalidation
    /// line, the 30-day offline line, the tryout window — is computed at read time. Left open, the
    /// page could keep rendering ACTIVE with a fresh timestamp while the recording gate, reading the
    /// same cache, blocked the hotkey.
    ///
    /// <para>A no-op while busy: a tick landing mid-command would overwrite <see cref="Status"/> from
    /// the cache moments before the command writes the real outcome. It never touches
    /// <see cref="ActivityMessage"/> — a background tick must never narrate — and it never DECIDES a
    /// message: <see cref="SeatNotice"/> is copied from the service (a previous page instance may
    /// have raised or cleared it), and <see cref="AttemptMessage"/> is cleared only by the plan-input
    /// rule when the panel it described is itself about to change (LIC-19).</para>
    /// </summary>
    public void ReprojectLocalState()
    {
        if (IsBusy) return;
        Status = _license.GetCachedStatus();
        RefreshLocalState();
        SeatNotice = _license.SeatNotice;
    }

    [RelayCommand]
    public async Task ActivateAsync()
    {
        // The empty-key guard sits INSIDE the busy gate (LIC-15). Outside it, clicking Activate with
        // a cleared box while an activation was already in flight stamped "Enter your license key to
        // activate." — and the in-flight run's only clear happens on ENTRY, so that sentence then
        // rendered as a warning on the freshly Activated panel it had nothing to do with.
        if (!TryEnterBusy()) { Announce(BusyGateNotice, LicenseActivityKind.Progress); return; }

        var key = LicenseKeyInput?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            // This command completed through local validation, so a PRIOR command's activity line
            // must not be left standing beside the new error as though it described this
            // submission — e.g. "Couldn't reach the license server." from an earlier Check now,
            // rendered above "Enter your license key to activate." (Codex plan round, 2026-09-04;
            // the "every exit writes ActivityMessage" rule had no row for this path.)
            ActivityMessage = null;
            AttemptMessage = "Enter your license key to activate.";
            ExitBusy();
            return;
        }

        AttemptMessage = null;
        ActivationLimitReached = false;
        // Cleared rather than confirmed on the way out (see the finally): a panel transition or the
        // amber banner already tells the user what happened, and two surfaces narrating one event is
        // how "Activated" ends up rendered beside "Activation failed" (LIC-15).
        Announce("Activating…", LicenseActivityKind.Progress);
        try
        {
            var result = await _license.ActivateAsync(key);
            if (result.Success)
            {
                LicenseKeyInput = string.Empty;
                Status = result.Status;
                // The service clears its seat notice on a durable activation (a new key is a new
                // contract); mirror it NOW, not on the next 5 s tick — otherwise the previous
                // attempt's "may still count toward your device limit" renders under the freshly
                // Activated panel, the exact banner-outlives-its-cause class LIC-19 closes
                // (self-review pass, both lenses).
                SeatNotice = _license.SeatNotice;
                // RefreshLocalState reads masked key / timestamp from settings — if it
                // throws (e.g., a transient settings read fault), the activation itself
                // still succeeded server-side. Don't overwrite Status or show an error
                // banner in that case; the next CheckAsync will reconcile the UI.
                try { RefreshLocalState(); }
                catch (Exception refreshEx)
                {
                    Logger.Warning(refreshEx, "RefreshLocalState threw after successful activation; UI may show stale key/timestamp until next refresh");
                }
            }
            else
            {
                ActivationLimitReached = result.ActivationLimitReached;
                // When the server refuses because every seat is taken, substitute a
                // more actionable message than LS's raw "This license key has reached
                // the activation limit." — the user needs to know what to do next.
                var refusal = result.ActivationLimitReached
                    ? "This license is already active on the maximum number of devices. Deactivate VoiceWink on one of them from its License page, or contact support to free a lost device's slot or upgrade to a larger license."
                    : FriendlyActivationError(result.ErrorMessage);
                // Only adopt the service's "Invalid" signal when we weren't holding a
                // richer state. A failed activation doesn't modify any stored license
                // state — CheckAsync would return the original value on next call — so
                // clobbering FirstRunGrace / OfflineGrace / Activated / DisabledReadOnly
                // with Invalid desyncs the VM from persistent state and flashes a wrong
                // chip until the next Refresh reconciles.
                if (Status == LicenseStatus.Unlicensed)
                    Status = result.Status;
                // Published AFTER the status assignment: the assignment clears the attempt message as
                // a panel-plan input change, and a refusal written before it would be swallowed by
                // the very transition it explains (Codex plan round, LIC-19). An outcome that may
                // have left a seat behind must outlive this page — and ONLY that outcome. The
                // activation-limit refusal is a clean "no" (SeatMayBeConsumed is false), so it is an
                // attempt message like any other; making it durable is how it came to stand on the
                // tryout card (owner UAT 176.2).
                if (result.SeatMayBeConsumed)
                {
                    AttemptMessage = null;
                    PublishSeatNotice(refusal);
                }
                else
                {
                    AttemptMessage = refusal;
                }
            }
        }
        catch (Exception ex)
        {
            // LicenseService catches network and JSON errors internally; anything
            // that reaches here is an unexpected defect (NRE, OOM, etc.). Surface a
            // generic message so the user isn't left with a blank UI after clicking
            // Activate.
            Logger.Warning(ex, "License activation threw unexpectedly");
            AttemptMessage = "Activation failed unexpectedly. Please try again.";
        }
        finally
        {
            ExitBusy();
            ActivityMessage = null;
        }
    }

    /// <summary>
    /// Rewrites LemonSqueezy's raw, technical activation error strings (e.g. the lowercase
    /// token-shaped "license_key not found.") into clear, user-facing copy. The limit-reached
    /// case is handled separately by the caller via <c>ActivationLimitReached</c>. Messages we
    /// don't have a friendlier phrasing for pass through unchanged — LS already returns several
    /// readable ones (e.g. "Invalid license key.") that don't need rewording.
    /// </summary>
    internal static string FriendlyActivationError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Activation failed. Please try again.";

        var lower = raw.ToLowerInvariant();
        if (lower.Contains("not found"))
            return "That license key wasn't found. Double-check it for typos, or use “Lost your key?” below to look it up in your Lemon Squeezy orders.";
        if (lower.Contains("expired"))
            // No seven-day claim about trial keys any more (LIC-21 retired the trial key): the
            // server said only that THIS key has expired — a refunded key, or one of the old keys.
            return "This key has expired. Buy a license to keep using VoiceWink.";
        if (lower.Contains("disabled") || lower.Contains("deactivated"))
            return "This license key has been disabled. If you did not expect this, contact support.";

        // Already-readable LS message — surface it as-is rather than masking detail.
        return raw;
    }

    [RelayCommand]
    public async Task DeactivateAsync()
    {
        if (!TryEnterBusy()) { Announce(BusyGateNotice, LicenseActivityKind.Progress); return; }
        // Deactivate has no confirmation dialog and its outcome copy is written below, so the one
        // thing missing was any sign it had started: during a stale-cache refresh the click was a
        // silent no-op, indistinguishable from success (LIC-15).
        Announce("Deactivating…", LicenseActivityKind.Progress);
        try
        {
            var result = await _license.DeactivateAsync();
            // Derive, don't assume: when a newer activation landed while the deactivate
            // was in flight, the service's epoch guard preserved that key — forcing
            // Unlicensed here would render an Unlicensed panel over a live activation.
            // The normal path still lands on Unlicensed via the cleared state.
            Status = _license.GetCachedStatus();
            // The local projections BEFORE the message: TryoutEnded and StoredKeyExpired are
            // panel-plan inputs whose setters clear the attempt message, and the clear wipes
            // LicenseFirstRunGraceStartedUtc, so a projection landing after the publish could
            // erase the outcome this click just reported (self-review pass; StartTrial's order).
            RefreshLocalState();
            // An UNCONFIRMED release must be said out loud: local state is cleared either way,
            // so a silent "Unlicensed" would teach the user their seat is free when the server
            // may still hold it — they would then hit the activation limit on the next machine
            // with no idea why. Support is the only party who can free it (LS My Orders cannot
            // deactivate instances).
            // Each outcome needs advice that is correct FOR IT. Collapsing them told the
            // healthy newer-activation race that settings couldn't be saved and to "deactivate
            // again" — which would tear down the good activation the epoch guard just protected.
            var outcome = (result.SeatReleaseConfirmed, result.ClearOutcome);
            var message = outcome switch
            {
                (true, LicenseClearOutcome.Cleared) => null,

                // A newer activation arrived mid-flight and was deliberately kept. Never invite
                // another deactivation; only mention the PREVIOUS seat when it wasn't confirmed.
                (true, LicenseClearOutcome.NewerActivationPreserved) =>
                    "A newer activation for this device arrived while deactivating, so it was kept — " +
                    "this device is still licensed.",
                (false, LicenseClearOutcome.NewerActivationPreserved) =>
                    "A newer activation for this device arrived while deactivating, so it was kept. " +
                    "The PREVIOUS activation wasn't confirmed as released and may still count toward " +
                    $"your device limit — contact {VoiceWinkUrls.SupportEmail} if you can't activate elsewhere.",

                // Released server-side but the clear didn't land: this install can come back
                // thinking it is licensed, on a seat that is now free.
                (true, LicenseClearOutcome.PersistenceFailed) =>
                    "The license was released, but this device's licence state couldn't be saved — " +
                    "it may reappear as activated after a restart. Deactivate again once the app " +
                    "can write its settings.",
                (false, LicenseClearOutcome.PersistenceFailed) =>
                    "The license server didn't confirm the release AND this device's licence state " +
                    "couldn't be saved, so the activation may still count toward your device limit and " +
                    $"may reappear here after a restart. Contact {VoiceWinkUrls.SupportEmail}.",

                // Cleared locally, server unconfirmed — the seat may still be held.
                (false, LicenseClearOutcome.Cleared) =>
                    "This device was deactivated locally, but the license server didn't confirm it — " +
                    $"the activation may still count toward your device limit. Contact {VoiceWinkUrls.SupportEmail} " +
                    "if you can't activate on another device.",

                // Defensive default for a future outcome: say the cautious thing rather than
                // nothing, and never invite an action that could tear down good state.
                _ => "Deactivation finished with an unexpected result — if you can't activate on " +
                     $"another device, contact {VoiceWinkUrls.SupportEmail}.",
            };
            // Same carry-over rule as activation: a seat that may still be held, or state that may
            // return after a restart, must not disappear when this page does — and ONLY those
            // outcomes are durable (LIC-19). A confirmed, persisted release clears the previous
            // notice, and the one informational outcome (a newer activation kept, the previous seat
            // confirmed free) is an attempt message: it describes this click and the Activated panel
            // it lands on, and it must not outlive either.
            var seatMayRemain = outcome switch
            {
                (true, LicenseClearOutcome.Cleared) => false,
                (true, LicenseClearOutcome.NewerActivationPreserved) => false,
                _ => true,
            };
            if (seatMayRemain)
            {
                AttemptMessage = null;
                PublishSeatNotice(message);
            }
            else
            {
                PublishSeatNotice(null);
                AttemptMessage = message;
            }
        }
        catch (Exception ex)
        {
            // Same rationale as ActivateAsync: LicenseService already handles expected
            // network failures; anything else shouldn't leave the UI silent.
            Logger.Warning(ex, "License deactivation threw unexpectedly");
            AttemptMessage = "Deactivation failed unexpectedly. Your local state may already be cleared.";
        }
        finally
        {
            ExitBusy();
            ActivityMessage = null;
        }
    }

    /// <summary>
    /// Atomically claim the in-flight slot. Returns true if the caller holds the slot
    /// and must call <see cref="ExitBusy"/>; false if someone else is already busy and
    /// the caller should short-circuit. Also updates <see cref="IsBusy"/> for the UI.
    /// </summary>
    private bool TryEnterBusy()
    {
        if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0) return false;
        IsBusy = true;
        return true;
    }

    private void ExitBusy()
    {
        Interlocked.Exchange(ref _busyFlag, 0);
        IsBusy = false;
    }

    // ── LIC-19: message ownership ─────────────────────────────────────

    // The panel-plan inputs (LicensePanelPlan.Resolve's four arguments). A change to any of them
    // means the panel the last attempt message described is about to be replaced, so the message
    // goes first — one rule, four setters, no per-site reasoning. The toolkit invokes these only
    // when the value actually changes, so a tick that changes nothing clears nothing.
    partial void OnStatusChanging(LicenseStatus value) => ClearAttemptForPlanInputChange();
    partial void OnActivateFormRequestedChanging(bool value) => ClearAttemptForPlanInputChange();
    partial void OnTryoutEndedChanging(bool value) => ClearAttemptForPlanInputChange();
    partial void OnStoredKeyExpiredChanging(bool value) => ClearAttemptForPlanInputChange();

    private void ClearAttemptForPlanInputChange() => AttemptMessage = null;

    // A status transition always wins over the user's request for the key form: a successful
    // activation, a refresh or the tick landing a new status snaps the page back to the real state.
    partial void OnStatusChanged(LicenseStatus value) => ActivateFormRequested = false;

    /// <summary>
    /// Leave the key form and return to the tryout card. A COMMAND behind the busy gate, not a page
    /// flag flip (Codex revised-plan round, LIC-19): flipping the flag while an activation from the
    /// form was still in flight let its late refusal — published after its own status assignment, by
    /// the rule above — land on the tryout card, which is the exact defect this change removes. An
    /// overlapping click is turned away with the same notice as every other command.
    /// </summary>
    [RelayCommand]
    public void ReturnToTryout()
    {
        if (!TryEnterBusy()) { Announce(BusyGateNotice, LicenseActivityKind.Progress); return; }
        try
        {
            // Completed locally, so a prior command's activity line must not stand beside the card as
            // though it described this click (the empty-key rule in ActivateAsync).
            ActivityMessage = null;
            ActivateFormRequested = false;
        }
        finally { ExitBusy(); }
    }

    /// <summary>
    /// The ONE writer of the durable seat notice: the service first (so the next VM instance inherits
    /// it), then this VM's property, synchronously. Null or empty clears both.
    /// </summary>
    private void PublishSeatNotice(string? notice)
    {
        if (string.IsNullOrEmpty(notice))
        {
            _license.ClearSeatNotice();
            SeatNotice = null;
        }
        else
        {
            _license.RememberSeatNotice(notice);
            SeatNotice = notice;
        }
    }

    /// <summary>
    /// Write the activity line with its kind — the kind FIRST, so the page never reads a stale one.
    /// Re-announcing an IDENTICAL string raises no notification, so no message may ever map to two
    /// kinds; every string here maps to exactly one, and the switch in DescribeCheckOutcome is where
    /// that invariant lives.
    /// </summary>
    private void Announce(string message, LicenseActivityKind kind)
    {
        ActivityKind = kind;
        ActivityMessage = message;
    }

    /// <summary>
    /// What a caller with ONE line to show (the onboarding wizard) should show. A seat-critical
    /// warning takes precedence over a clean attempt failure — the plan-round B3 contract; a joined
    /// "both" was tried after the self-review pass and REJECTED by the diff round, because one line
    /// carrying both dilutes the warning about money rather than preferring it. Null leaves the
    /// caller's fallback.
    /// </summary>
    public string? OutcomeMessage => SeatNotice ?? AttemptMessage;

    /// <summary>
    /// Start the free trial — the local <see cref="LicenseStatus.FirstRunGrace"/> window, the ONLY
    /// try path since LIC-21 (owner decision 2026-09-06; the Lemon Squeezy trial key is retired).
    /// The user-facing vocabulary is "Start free trial" / "Free trial — … remaining" ("signing up"
    /// was retired from every user-facing string on 2026-09-02 — there is no account anywhere).
    /// </summary>
    [RelayCommand]
    public void StartTrial()
    {
        // Behind the busy gate like every other command (PR A self-review, correctness lens): the
        // key box and "Start free trial" share a row on both surfaces, and a click here while an
        // activation is still in flight (the licensing client allows 30 s) used to start the window
        // under the key that then landed. Pre-PR-A the activation persist block cleared the stamp,
        // so that self-healed; since PR A licensing never writes the trial, so it would have run to
        // Ended under the paid key and "Your free trial has ended" would have met the user at their
        // first deactivation — the untouched-trial invariant the migration marker exists for, lost
        // to a double click. A trial started by mistake cannot be un-started, so the click waits.
        if (!TryEnterBusy()) { Announce(BusyGateNotice, LicenseActivityKind.Progress); return; }
        try
        {
            _license.StartFirstRunGrace();
            // Populate TrialTimeRemaining BEFORE flipping Status — on the first transition
            // (Unlicensed → FirstRunGrace) the opposite order would rebuild the FirstRunGrace
            // panel from a still-null countdown, rendering "… — 0h 00m remaining"
            // for one dispatcher frame. On later idempotent calls the Status assignment is
            // a no-op (ObservableProperty skips equal values) so order is irrelevant.
            RefreshLocalState();
            // Re-read from the service rather than assuming success. StartFirstRunGrace is
            // idempotent — if a previous trial start timestamp exists and has already aged
            // past the window, the call no-ops and the cached status remains
            // Unlicensed. The recording gate (MainViewModel) reads GetCachedStatus(), so the
            // VM must reflect that same source of truth or the UI silently disagrees with
            // the gate (screen flips to FirstRunGrace, but the hotkey path still blocks).
            var actual = _license.GetCachedStatus();
            Status = actual;
            if (actual == LicenseStatus.FirstRunGrace)
            {
                // Clear any stale error from a prior failed activate attempt (the status assignment
                // already did when it changed; this covers the idempotent re-click).
                AttemptMessage = null;
            }
            else
            {
                // After the status assignment, like every other refusal (LIC-19).
                AttemptMessage =
                    "The free trial has already been used on this device. Enter a license key to continue, or buy one.";
            }
        }
        finally { ExitBusy(); }
    }

    /// <summary>
    /// Re-seed <see cref="Status"/> and the local projections from the service's cached state,
    /// without touching the network — the same read the constructor performs. Exists for the
    /// onboarding License step's trial button (LIC-21 PR A, owner UAT 2026-09-06): a failed
    /// activation from <c>Unlicensed</c> adopts the service's <c>Invalid</c> signal into this
    /// TRANSIENT view-model (LIC-1 — nothing was persisted), and the button's click-time guard then
    /// read that UI-only status as a stored-key machine, re-rendered the step, and needed a second
    /// click. The guard resolves from this instead, so only a REAL stored key re-renders.
    /// <para>A no-op while busy, for <see cref="ReprojectLocalState"/>'s reason: overwriting
    /// <see cref="Status"/> from the cache under an in-flight command would land moments before the
    /// command writes the real outcome, and the status assignment's plan-input rule would clear an
    /// attempt message that command is about to own. The caller's next step, <see cref="StartTrial"/>,
    /// is behind the busy gate itself, so nothing starts on a stale read.</para>
    /// </summary>
    public void ReconcileStatusFromCache()
    {
        if (IsBusy) return;
        Status = _license.GetCachedStatus();
        RefreshLocalState();
    }

    [RelayCommand]
    public void Buy() => OpenUrl(VoiceWinkUrls.Buy);

    [RelayCommand]
    public void OpenCustomerPortal() => OpenUrl(VoiceWinkUrls.CustomerPortal);

    [RelayCommand]
    public void ForgotKey() => OpenUrl(VoiceWinkUrls.LostKey);

    private void RefreshLocalState()
    {
        MaskedKey = _license.GetMaskedLicenseKey();
        LastValidatedUtc = _license.GetLastValidatedUtc();
        // GetTrialTimeRemaining is recomputed from the wall clock on EVERY call, so it can never
        // compare equal — and an unconditional assignment therefore fired PropertyChanged on every
        // 5-second reprojection tick. TrialTimeRemaining is in LicensePage's rebuild-trigger list,
        // so the WHOLE panel subtree — key box included — was being destroyed and recreated every
        // 5 s while the tryout was live, dropping focus and caret mid-keystroke. That is the exact
        // defect the activity line's page-level design exists to avoid, reintroduced on a timer;
        // and under the LNC-6 3650-day scaffold every tester machine is in the tryout permanently,
        // so it was universal rather than rare (Kimi diff round, 2026-09-04).
        //
        // Gate on the RENDERED string, which advances at most once a minute (hourly above a day;
        // under the retired 3650-day scaffold it never moved at all): the page rebuilds only when the user could
        // actually see a change. The window's END stays covered without this property — TryoutEnded
        // flips and Status leaves FirstRunGrace on the same tick, and both are triggers themselves.
        var trialRemaining = _license.GetTrialTimeRemaining();
        if (FormatTrialRemaining(trialRemaining) != TrialTimeRemainingDisplay)
            TrialTimeRemaining = trialRemaining;
        TryoutEnded = _license.HasFirstRunGraceEnded();
        StoredKeyExpired = _license.IsStoredKeyExpired();
        KeyExpiresAt = _license.GetKeyExpiresAt();
        var (limit, usage) = _license.GetActivationCounts();
        ActivationLimit = limit;
        ActivationUsage = usage;
        DeviceCountLabel = FormatDeviceCountLabel(limit, usage);
        InstanceLine = FormatInstanceLine(_license.GetInstanceLabel());
    }

    /// <summary>
    /// Render the "Activated on N of M devices" line shown on the Activated panel (LIC-6).
    /// Returns null for three cases, and a limit-only sentence for a fourth:
    /// <list type="bullet">
    /// <item><c>limit</c> is null — pre-LIC-6 activation that ran before counts were persisted,
    /// or an LS response that omitted the field.</item>
    /// <item><c>limit</c> is 0 — LS contract violation (a valid license must have a positive
    /// limit); omitting the line is preferable to rendering "X of 0 devices".</item>
    /// <item><c>limit</c> is negative — paranoia guard against a future encoding drift where
    /// the sentinel value leaks through the service read.</item>
    /// <item><c>usage</c> is null while <c>limit</c> is known — a real state, because
    /// <c>ParseActivationCounts</c> reads the two fields independently and the persist block writes
    /// each only when present. It used to default to 0 and render "Activated on 0 of 3 devices." on
    /// the Activated panel, directly under "Activated key: …" — a count that is provably wrong,
    /// since this device is one of them. Omitting the line for an unknown usage is the same rule the
    /// list above already applies to an unknown limit (LIC-13).</item>
    /// </list>
    /// </summary>
    private static string? FormatDeviceCountLabel(int? limit, int? usage)
    {
        if (!limit.HasValue || limit.Value <= 0) return null;
        if (limit.Value == 1) return "Activated on this device (1-device license).";
        if (!usage.HasValue) return $"This license covers up to {limit.Value} devices.";
        return $"Activated on {usage.Value} of {limit.Value} devices.";
    }

    /// <summary>
    /// Render the "This device: VoiceWink-9baf50bb" line (LIC-30). Null when the install has no
    /// stored label — it has never attempted an activation — so the panels omit the line rather
    /// than print "This device: " with nothing after it. (A stored label without a stored key is
    /// possible after a refused attempt; no panel that renders the line is reachable then.) The label is opaque (a random per-install string,
    /// never the hostname — LIC-9), so showing it discloses nothing about the machine or the user.
    /// </summary>
    private static string? FormatInstanceLine(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : $"This device: {label}";

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to open URL {Url}", url);
        }
    }
}

/// <summary>
/// How the License page's activity line should read (LIC-19): the owner's "error should be in red"
/// (UAT 2026-09-05) needs the VM to say WHICH lines are errors, because the page must not classify
/// prose. Progress and Success share a colour; only Failure is distinct.
/// </summary>
public enum LicenseActivityKind
{
    Progress,
    Success,
    Failure,
}
