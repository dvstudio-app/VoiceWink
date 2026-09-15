namespace VoiceWink.Helpers;

/// <summary>
/// Which model and which language a RETRY attempt runs with (TRN-17).
///
/// <para><b>Why a helper for two null-coalescing chains.</b> The precedence is stated twice by
/// necessity — the retry picker pre-selects with it, and <c>StopAndTranscribeAsync</c> transcribes
/// with it — and the dialog showing one model while the pipeline runs another is a wrong-output
/// failure that no gate would catch. One expression, two callers, a case table.</para>
///
/// <para><b>Precedence: the user's per-run pick, then what the failed attempt captured, then the
/// global setting.</b> The pick outranks the capture deliberately — the user chose it against the
/// failure they can see, having been shown what the attempt would otherwise use, which is later and
/// better evidence than a value resolved at recording start. With a null pick the expression is
/// IDENTICAL to the pre-TRN-17 one, and that equivalence is what makes the context fields additive
/// rather than a behaviour change; <c>RetryAttemptResolutionTests</c> pins it.</para>
///
/// <para><b>What the middle term MEANS differs between the two</b> — App-Mode's model override for
/// <see cref="Model"/>, PRM-5's fully-resolved language for <see cref="Language"/> — which is why
/// they are two methods with their own parameter names rather than one generic call. See
/// <see cref="Language"/> for the asymmetry that follows from it.</para>
///
/// <para><b>Takes the three values, not the context record.</b> Keeps this off
/// <c>MainViewModel</c> (a 9-field record would have to be constructed for every test row to vary
/// one string) and makes the argument order visible at the call sites, which pass them by name.</para>
///
/// <para>The result is a REQUEST, not a promise: <see cref="EffectiveTranscriptionLanguage"/> still
/// clamps the language against whatever the chosen model can recognise, at the wire.</para>
///
/// <para>Pinned by <c>RetryAttemptResolutionTests</c>, whose load-bearing row is the null-pick
/// equivalence — a regression there is a silent change to every retry that never opened the
/// dialog.</para>
/// </summary>
internal static class RetryAttemptResolution
{
    /// <summary>The model this retry attempt prepares AND transcribes with — one value, resolved once.</summary>
    /// <param name="userPick">The retry dialog's per-run choice, or null when it was cancelled or never shown.</param>
    /// <param name="appModeOverride">The failed attempt's captured App-Mode model override, if any.</param>
    /// <param name="globalSelectedModel">The live <c>selectedModelName</c> setting, re-read at retry time.</param>
    internal static string Model(string? userPick, string? appModeOverride, string globalSelectedModel)
        => Coalesce(userPick, appModeOverride, globalSelectedModel);

    /// <summary>
    /// The language this retry attempt REQUESTS. Not necessarily what it recognises with — the model
    /// constrains it afterwards (Parakeet auto-detects; an English-only build coerces).
    ///
    /// <para><b>The middle term is NOT an App-Mode override, unlike <see cref="Model"/>'s</b>, and
    /// naming it one was wrong (Kimi plan review). <c>TranscriptionRetryContext.LanguageOverride</c>
    /// holds PRM-5's FULLY RESOLVED recording-start chain — prompt override, then App Mode, then the
    /// global setting as it stood when recording began.</para>
    ///
    /// <para>So the two levels are deliberately asymmetric: the MODEL re-reads the live global
    /// (that is the fix-your-provider scenario this card exists for), while the LANGUAGE replays
    /// what recording start resolved, because PRM-5 requires a retry to recognise the same audio the
    /// same way rather than picking up a Settings edit made since. The user's pick overrides either.</para>
    /// </summary>
    /// <param name="userPick">The retry dialog's per-run choice, or null.</param>
    /// <param name="capturedLanguage">PRM-5's language resolved at recording start, if any.</param>
    /// <param name="globalLanguage">
    /// The floor when the context carries none — a context persisted by an older build can.
    /// </param>
    internal static string Language(string? userPick, string? capturedLanguage, string globalLanguage)
        => Coalesce(userPick, capturedLanguage, globalLanguage);

    /// <summary>
    /// Shared precedence, and the blank-handling is ASYMMETRIC on purpose.
    ///
    /// <para>A blank USER PICK falls through. That level is new, so there is no behaviour to
    /// preserve, and falling through to what the attempt would have used anyway is the safe
    /// direction — the dialog cannot emit one (confirm is gated on a selection), but the context
    /// record is public and a future caller could.</para>
    ///
    /// <para>The other two levels use plain <c>??</c>, matching the pre-TRN-17 expressions
    /// EXACTLY. Blank-normalizing them would be an unrelated behaviour change smuggled into an
    /// additive diff: today a blank App-Mode override reaches <c>GetService("")</c> and throws
    /// <c>UnknownTranscriptionModelException</c>, and whether that should instead fall back to the
    /// global selection is its own decision with its own failure story — not this card's.</para>
    /// </summary>
    private static string Coalesce(string? userPick, string? captured, string global)
        => string.IsNullOrWhiteSpace(userPick) ? captured ?? global : userPick;
}
