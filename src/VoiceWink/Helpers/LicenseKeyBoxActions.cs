namespace VoiceWink.Helpers;

/// <summary>The buttons the License page's key box can carry, in the abstract — the page maps each to its control.</summary>
public enum LicenseKeyBoxAction
{
    Activate,
    StartTrial,
    ReturnToTryout,
    Buy,
}

/// <summary>
/// The ORDER of the key box's buttons, as one pure decision over the <see cref="LicensePanelPlan"/>
/// (LIC-25, owner rule 2026-09-07): wherever a trial is offered the order is
/// <b>Activate → Start free trial → Buy a license</b> — the free step before the paid one — and Buy
/// leads only where the free path is gone (<see cref="LicensePanelPlan.BuyIsPrimary"/>), which is the
/// weighting the plan already decides. The wizard orders its two cards the same way (the trial card
/// above the key card); this makes the page structurally agree with it instead of by hand-placed
/// `Children.Add` calls, which is how the key box shipped as Activate | Buy | Start free trial.
/// </summary>
public static class LicenseKeyBoxActions
{
    /// <summary>
    /// Which buttons, in which order. Exactly one <see cref="LicenseKeyBoxAction.Activate"/> and one
    /// <see cref="LicenseKeyBoxAction.Buy"/> always; the free-path button (start OR return, never
    /// both — <see cref="LicensePanelPlan.Resolve"/> makes them exclusive) sits between them.
    /// </summary>
    public static LicenseKeyBoxAction[] Order(LicensePanelPlan plan)
    {
        // The used-up-trial key box: no trial button exists to place, and Buy leads (LIC-21).
        if (plan.BuyIsPrimary)
            return new[] { LicenseKeyBoxAction.Buy, LicenseKeyBoxAction.Activate };
        if (plan.OfferKeyFreeStart)
            return new[] { LicenseKeyBoxAction.Activate, LicenseKeyBoxAction.StartTrial, LicenseKeyBoxAction.Buy };
        if (plan.ReturnToTryout)
            return new[] { LicenseKeyBoxAction.Activate, LicenseKeyBoxAction.ReturnToTryout, LicenseKeyBoxAction.Buy };
        return new[] { LicenseKeyBoxAction.Activate, LicenseKeyBoxAction.Buy };
    }
}
