namespace VoiceWink.Helpers;

/// <summary>Which of setup's two paths a run takes (LNC-15, owner 2026-10-13). Public only so xUnit
/// theories can take it as a parameter.</summary>
public enum SetupPath
{
    /// <summary>Language and hotkey, plus the screens that are not defaults: terms, license, help improve, updates.</summary>
    Express,

    /// <summary>Express plus the speech-model and AI screens.</summary>
    Advanced,
}

/// <summary>
/// The pure step arithmetic behind setup's navigation: which steps a run shows, where a request to
/// show a step lands, and the "Step N of M" counter.
///
/// <para>ONB-2 (2026-08-05) built this for ONE optional step — the legal step, skipped once
/// acceptance is recorded. LNC-15 (2026-10-13) widened the same rule to a step LIST per path: a
/// request for a step the run does not show moves on in the direction of travel. That is what keeps
/// every page's <c>ShowStep(_step - 1)</c> Back button correct on both paths without each one
/// knowing which steps its path skips.</para>
///
/// <para><b>The legal step is skipped in NAVIGATION, never deleted.</b> An unhealthy legal bundle
/// takes <c>BuildLegalStep</c>'s fail-closed branch (reinstall-or-exit) and must stay reachable, so
/// the skip is conditional on acceptance being recorded AND the bundle being healthy.</para>
///
/// <para>Pure and separate because a WinUI page is not unit-constructible, and the counter
/// arithmetic is exactly the kind that goes wrong silently.</para>
/// </summary>
internal static class OnboardingStepFlow
{
    /// <summary>The Welcome step's index in the page's switch. It carries the path choice and no counter.</summary>
    internal const int WelcomeStepIndex = 0;

    /// <summary>The legal step's index in the switch. Not a display number.</summary>
    internal const int LegalStepIndex = 1;

    /// <summary>The Updates step's index in the switch (both paths since A42, 2026-10-19).</summary>
    internal const int UpdatesStepIndex = 12;

    /// <summary>The Done step's index in the switch.</summary>
    internal const int DoneStepIndex = 13;

    // Indices are the page's switch cases, which LNC-15 did not renumber: 2 (Theme), 8 (Clipboard)
    // and 9 (Startup) were removed from both paths (owner delegation, 2026-10-13). 12 (Updates) was on
    // Advanced only from 2026-10-13; since A42 (owner, 2026-10-19) Express offers the
    // automatic-update-check choice too, so both paths show it.
    private static readonly int[] AdvancedSteps = [0, 1, 3, 4, 5, 6, 7, 10, 11, 12, 13];
    private static readonly int[] ExpressSteps = [0, 1, 3, 7, 10, 11, 12, 13];

    /// <summary>The steps a run shows, in order: the path's list, minus the legal step when skipped.</summary>
    internal static IReadOnlyList<int> Steps(SetupPath path, bool skipLegal)
    {
        var steps = path == SetupPath.Express ? ExpressSteps : AdvancedSteps;
        return skipLegal ? steps.Where(s => s != LegalStepIndex).ToArray() : steps;
    }

    /// <summary>
    /// Where a request to show <paramref name="requested"/> should actually land.
    ///
    /// <para>Direction matters: arriving at a skipped step from an earlier one means "go forward past
    /// it", while arriving from a later one via Back means "go back past it". Getting this wrong traps
    /// the user — a skip that always goes forward makes Back bounce straight to the step it left.</para>
    /// </summary>
    /// <param name="requested">The step the caller asked for.</param>
    /// <param name="current">The step being displayed now, used only to infer direction.</param>
    /// <param name="steps">The run's steps, from <see cref="Steps"/>.</param>
    internal static int Resolve(int requested, int current, IReadOnlyList<int> steps)
    {
        if (steps.Contains(requested)) return requested;

        // Moving BACK into a skipped step ⇒ the nearest shown step before it (Welcome at worst).
        if (requested < current)
            return steps.Where(s => s < requested).DefaultIfEmpty(steps[0]).Max();

        // Forward, or a re-show ⇒ the nearest shown step after it (Done at worst).
        return steps.Where(s => s > requested).DefaultIfEmpty(steps[^1]).Min();
    }

    /// <summary>
    /// Whether the legal step may be omitted, from the two facts that decide it.
    ///
    /// <para>Pure so the CONJUNCTION is pinned. <c>bundleHealthy</c> is the safety half:
    /// <c>BuildLegalStep</c>'s fail-closed branch is the only surface that reports a broken
    /// EULA/Privacy bundle, so a build that cannot load its legal texts must still stop there.</para>
    /// </summary>
    internal static bool ShouldSkipLegal(bool bundleHealthy, bool requiresPrompt)
        => bundleHealthy && !requiresPrompt;

    /// <summary>
    /// A decision evaluated at most ONCE, that fails CLOSED if the probe throws.
    ///
    /// <para>Exists as a type because the freeze itself needed testing and could not be: a test that
    /// merely compares the two skip states passes against the buggy re-evaluating version too
    /// (Codex diff review r2). Here the probe's CALL COUNT is observable.</para>
    ///
    /// <para>Why the legal skip must be frozen: re-evaluating per <c>ShowStep</c> moved the counter
    /// under a new user — "Step 1 of 13", legal at "Step 2 of 13", then Theme as "Step 2 of 12" the
    /// moment they accepted. The PATH is deliberately not frozen (LNC-15): a user who goes Back to
    /// Welcome and picks the other path gets the other path, and that choice is the one moment the
    /// counter may change.</para>
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

    /// <summary>How many steps the counter claims: every shown step but Welcome, which is the path choice.</summary>
    internal static int Total(IReadOnlyList<int> steps) => steps.Count - 1;

    /// <summary>
    /// The 1-based number to print for <paramref name="step"/>, or 0 for Welcome (no counter).
    ///
    /// <para>The number is the step's POSITION in the run's list, so the sequence the user reads stays
    /// contiguous — 1, 2, 3… — whatever the path skips. Numbering by index would read "Step 3" straight
    /// after "Step 1", the defect ONB-2's card warned the naive fix produces.</para>
    /// </summary>
    internal static int DisplayNumber(int step, IReadOnlyList<int> steps)
    {
        var position = steps.ToList().IndexOf(step);
        return position < 0 ? 0 : position;
    }
}
