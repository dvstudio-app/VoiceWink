namespace VoiceWink.Helpers;

/// <summary>
/// The pure step arithmetic behind onboarding's optional legal step (ONB-2).
///
/// <para>Owner UAT 2026-08-05: with acceptance already recorded, the wizard still showed
/// <i>"Step 2 of 13 — Terms and Privacy"</i> carrying only "Already accepted on this device. You can
/// skip to the next step." — <i>"there is not so much point in showing this page still."</i></para>
///
/// <para><b>Skipped in NAVIGATION, never deleted.</b> An unhealthy legal bundle takes
/// <c>BuildLegalStep</c>'s fail-closed branch (reinstall-or-exit) and must stay reachable, so the
/// skip is conditional on acceptance being recorded AND the bundle being healthy. Deleting the step
/// would remove the only surface that reports a broken bundle.</para>
///
/// <para>Pure and separate because a WinUI page is not unit-constructible, and the counter
/// arithmetic is exactly the kind that goes wrong silently — "Step 3 of 13" straight after step 1 is
/// the failure the card predicts.</para>
/// </summary>
internal static class OnboardingStepFlow
{
    /// <summary>The legal step's index in the switch. Not a display number.</summary>
    internal const int LegalStepIndex = 1;

    /// <summary>
    /// Where a request to show <paramref name="requested"/> should actually land.
    ///
    /// <para>Direction matters: arriving at the legal step from Welcome means "go forward past it",
    /// while arriving from Theme via Back means "go back past it". Getting this wrong traps the user
    /// — a skip that always goes forward makes Back from step 2 bounce straight to step 2 again.</para>
    /// </summary>
    /// <param name="requested">The step the caller asked for.</param>
    /// <param name="current">The step being displayed now, used only to infer direction.</param>
    /// <param name="skipLegal">Whether the legal step is being omitted this run.</param>
    internal static int Resolve(int requested, int current, bool skipLegal)
    {
        if (!skipLegal || requested != LegalStepIndex) return requested;

        // Moving BACK into the skipped step (from Theme) ⇒ continue back to Welcome.
        // Everything else — forward from Welcome, or a re-show — continues forward.
        return current > LegalStepIndex ? LegalStepIndex - 1 : LegalStepIndex + 1;
    }

    /// <summary>
    /// Whether the legal step may be omitted, from the two facts that decide it.
    ///
    /// <para>Pure so the CONJUNCTION is pinned. Every other test here receives the final boolean, so
    /// none of them could catch <c>BundleHealthy</c> being dropped or the negation being inverted —
    /// high-value coverage for a legal gate (Codex diff review r2).</para>
    ///
    /// <para><c>bundleHealthy</c> is the safety half: <c>BuildLegalStep</c>'s fail-closed branch is
    /// the only surface that reports a broken EULA/Privacy bundle, so a build that cannot load its
    /// legal texts must still stop there.</para>
    /// </summary>
    internal static bool ShouldSkipLegal(bool bundleHealthy, bool requiresPrompt)
        => bundleHealthy && !requiresPrompt;

    /// <summary>
    /// A decision evaluated at most ONCE, that fails CLOSED if the probe throws.
    ///
    /// <para>Exists as a type because the freeze itself needed testing and could not be: a test that
    /// merely compares the two skip states passes against the buggy re-evaluating version too
    /// (Codex diff review r2). Here the probe's CALL COUNT is observable, so "decided once per run"
    /// is a property a test can actually assert.</para>
    ///
    /// <para>Why it must be frozen: re-evaluating per <c>ShowStep</c> moved the counter under a new
    /// user — "Step 1 of 13", legal at "Step 2 of 13", then Theme as "Step 2 of 12" the moment they
    /// accepted. A run's step count has to be a constant of that run.</para>
    /// </summary>
    internal sealed class FrozenDecision
    {
        private readonly Func<bool> _probe;
        private readonly Action<Exception>? _onError;
        private bool? _value;

        internal FrozenDecision(Func<bool> probe, Action<Exception>? onError = null)
        {
            _probe = probe;
            _onError = onError;
        }

        internal bool Value => _value ??= Evaluate();

        private bool Evaluate()
        {
            try
            {
                return _probe();
            }
            catch (Exception ex)
            {
                // Fail CLOSED. An exception must never be the reason a user skips a legal gate.
                _onError?.Invoke(ex);
                return false;
            }
        }
    }

    /// <summary>How many steps the counter should claim, with the legal step omitted or not.</summary>
    internal static int EffectiveTotal(int totalSteps, bool skipLegal)
        => skipLegal ? totalSteps - 1 : totalSteps;

    /// <summary>
    /// The 1-based number to print for <paramref name="step"/>.
    ///
    /// <para>Steps after the omitted one shift DOWN by one, so the sequence the user reads stays
    /// contiguous: 1, 2, 3… rather than 1, 3, 4… — which is precisely the defect the card warns the
    /// naive fix produces.</para>
    /// </summary>
    internal static int DisplayNumber(int step, bool skipLegal)
        => skipLegal && step > LegalStepIndex ? step : step + 1;
}
