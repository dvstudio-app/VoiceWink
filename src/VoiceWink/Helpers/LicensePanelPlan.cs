using VoiceWink.Services.Licensing;

namespace VoiceWink.Helpers;

/// <summary>
/// Which panel the License page renders. One member per <see cref="LicenseStatus"/> outcome, except
/// that <see cref="KeyEntry"/> serves three arrivals — no key at all, the activate-form override, and
/// an unmapped future status — because all three want the same thing from the user: a key.
/// </summary>
public enum LicensePanelKind
{
    /// <summary>Key box + Activate. Reached from <c>Unlicensed</c>, from the activate-form override, and by the <c>default</c> arm.</summary>
    KeyEntry,
    /// <summary>The free trial (the local <c>FirstRunGrace</c> window) is running: countdown + "Activate now".</summary>
    Tryout,
    /// <summary>A validated licence on this device.</summary>
    Activated,
    /// <summary>
    /// Validated inside the offline window. It heals via "Check now", the next launch's reconcile,
    /// or the next daily in-session tick (LIC-23) once back online — <b>nothing revalidates at the
    /// moment of reconnecting</b> (the timer is time-driven, not a network listener), which is the
    /// claim LIC-13 removed from the user-facing panel, so it must not survive here either (Kimi
    /// diff round, 2026-09-04).
    /// </summary>
    OfflineGrace,
    /// <summary><c>GraceExpired</c> — the stored key needs an online validate before it is trusted again.</summary>
    NeedsRevalidation,
    /// <summary>A server verdict refused the key, or its persisted expiry has passed.</summary>
    Invalid,
    /// <summary>The merchant disabled the key (LIC-2a / LIC-8).</summary>
    Disabled,
}

/// <summary>
/// The License page's layout decision as a pure value, so it is test-pinned rather than re-derived
/// in page code-behind (which the test host cannot construct). The sibling of
/// <see cref="OnboardingLicenseCard"/>, and it exists for the same reason: the two screens share one
/// invariant about the key-free start, and a shared invariant living in two code-behinds drifts.
///
/// <para><b>The key-free start is offered ONLY to <see cref="LicenseStatus.Unlicensed"/> with no
/// tryout behind it, and never while the activate form is open.</b> Every other status either has a
/// STORED key or is already in the window: for those, <c>StartFirstRunGrace</c> would either write a
/// grace timestamp that <c>GetCachedStatus</c> then ignores in favour of the key — burning the one
/// tryout the user never received — or no-op, leaving a control that reads as an offer and behaves as
/// a Back button. The second case shipped and was found in owner testing on 2026-09-04: an
/// active-tryout user who clicked "Activate now" got the full no-key panel, tryout offer included.</para>
///
/// <para>The <c>default</c> arm maps to <see cref="LicensePanelKind.KeyEntry"/> with
/// <see cref="OfferKeyFreeStart"/> false, so a future <see cref="LicenseStatus"/> member fails toward
/// asking for a key rather than toward granting a key-free window. <c>LicensePanelPlanTests</c>
/// sweeps the enum and fails when a new member arrives without a decision here.</para>
///
/// <para><see cref="ShowsCheckNow"/> (LIC-25, owner UAT 2026-09-07) is whether the page shows its ONE
/// "Check now" — the refresh icon beside the status chip. It force-validates the STORED key, so it is
/// offered exactly on the five keyed panels and never on the key box or the trial panel, where the
/// machine has nothing to check and an inert icon would invite a dead click. Keyed on the KIND, so the
/// activate-form override over a live trial hides it too, and the unmapped fallback shows none.</para>
/// </summary>
public readonly record struct LicensePanelPlan(
    LicensePanelKind Kind,
    bool OfferKeyFreeStart,
    bool ReturnToTryout,
    bool BuyIsPrimary,
    bool ShowsCheckNow)
{
    /// <param name="status">The cached licence status (<c>LicenseViewModel.Status</c>).</param>
    /// <param name="activateFormRequested">
    /// The page-local view hint set by "Activate now" on the tryout panel: the user asked for the key
    /// box without leaving the tryout. A view concern, but a LAYOUT one, so it resolves here with
    /// everything else rather than as an <c>if</c> ahead of the switch — which is how the shipped bug
    /// bypassed every per-status decision below.
    /// </param>
    /// <param name="tryoutEnded">
    /// <c>LicenseService.HasFirstRunGraceEnded()</c> — started on this device and run out. An ACTIVE
    /// tryout is not a separate input: <see cref="LicenseStatus.FirstRunGrace"/> already says so.
    /// </param>
    /// <param name="storedKeyExpired">
    /// <c>LicenseService.IsStoredKeyExpired()</c> — a persisted LS expiry in the past (a refunded key
    /// arrives this way, LIC-10; so does any expiring variant). Only meaningful for
    /// <see cref="LicensePanelKind.Invalid"/>.
    /// </param>
    public static LicensePanelPlan Resolve(
        LicenseStatus status,
        bool activateFormRequested,
        bool tryoutEnded,
        bool storedKeyExpired)
    {
        var kind = activateFormRequested
            ? LicensePanelKind.KeyEntry
            : status switch
            {
                LicenseStatus.Unlicensed       => LicensePanelKind.KeyEntry,
                LicenseStatus.FirstRunGrace    => LicensePanelKind.Tryout,
                LicenseStatus.Activated        => LicensePanelKind.Activated,
                LicenseStatus.OfflineGrace     => LicensePanelKind.OfflineGrace,
                LicenseStatus.GraceExpired     => LicensePanelKind.NeedsRevalidation,
                LicenseStatus.Invalid          => LicensePanelKind.Invalid,
                LicenseStatus.DisabledReadOnly => LicensePanelKind.Disabled,
                _                              => LicensePanelKind.KeyEntry,
            };

        // Unlicensed is the ONLY status with no stored key and no live window, so it is the only one
        // where starting the tryout does what the control says. `!activateFormRequested` because the
        // user asking for the key box is not asking to be sold the tryout again.
        var offerKeyFreeStart = status == LicenseStatus.Unlicensed && !tryoutEnded && !activateFormRequested;

        // The override's only exit. Gated on FirstRunGrace rather than on the flag alone: the flag is
        // only reachable from the tryout panel today AND is cleared on any status change, but if that
        // ever stops holding, an exit labelled "keep trying" must not appear to someone with no
        // tryout to return to.
        var returnToTryout = activateFormRequested && status == LicenseStatus.FirstRunGrace;

        // Buy leads on exactly two screens, both the moment the user has felt the value and the free
        // path is gone: the key box once the free trial has been used up on this device, and the
        // Invalid panel for an EXPIRED stored key. A generic invalid key keeps "Try this key"
        // primary; a live trial and every other panel keep Activate.
        //
        // The trial-ended half is gated on Unlicensed rather than merely on KeyEntry: under the
        // LIC-14 boundary skew (Status still FirstRunGrace while TryoutEnded has already flipped,
        // because the two are separate clock reads) an override-open key form would otherwise pair
        // the "free trial has ended on this device" intro with the "Back to my free trial" exit, on
        // one screen. Unreachable under the 3650-day scaffold; LIVE since LIC-21 PR A made the
        // window 7 days (Kimi diff round, 2026-09-04). Until LIC-21 (2026-09-06) this slot
        // promoted the Lemon Squeezy trial KEY instead; that key no longer exists — the local free
        // trial is the only try path — so Buy is what follows it.
        var buyIsPrimary =
            (kind == LicensePanelKind.KeyEntry && tryoutEnded && status == LicenseStatus.Unlicensed)
            || (kind == LicensePanelKind.Invalid && storedKeyExpired);

        // The refresh icon is the one "Check now" on the page (LIC-25). It shows where a key is
        // STORED — the five keyed panels — and nowhere else: the key box and the trial panel have
        // nothing to check, and the icon on them would be a control that reads as an action and does
        // nothing. Kind, not status, so the override (KeyEntry over a live trial) and the unmapped
        // fallback both hide it.
        var showsCheckNow = kind is LicensePanelKind.Activated
            or LicensePanelKind.OfflineGrace
            or LicensePanelKind.NeedsRevalidation
            or LicensePanelKind.Invalid
            or LicensePanelKind.Disabled;

        return new LicensePanelPlan(kind, offerKeyFreeStart, returnToTryout, buyIsPrimary, showsCheckNow);
    }
}
