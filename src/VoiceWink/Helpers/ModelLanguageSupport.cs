using VoiceWink.Models;
using VoiceWink.ViewModels;

namespace VoiceWink.Helpers;

/// <summary>
/// Which languages a LOCAL transcription model can actually recognise, and the one place that turns
/// that into a sentence a user reads.
///
/// <para><b>Why this exists.</b> The language picker populates from a single flat array
/// (<see cref="ModelManagementViewModel.SupportedLanguages"/>, 99 Whisper codes plus
/// <c>auto</c>) that is model-INDEPENDENT — every model offered every language. For Whisper that
/// was accurate; for Parakeet it is not, and the failure is silent: a user who pins Chinese and
/// selects Parakeet gets auto-detect against an engine with no Chinese, and the only record is a
/// log line.</para>
///
/// <para><b>Keyed on the MODEL, not the runtime.</b> Language coverage is a property of the WEIGHTS
/// — Parakeet v2 and v3 do not cover the same languages — so a runtime-wide table would silently
/// hand a future Parakeet model v3's languages (Codex, diff review). That is the opposite of the
/// TRN-1 seam's rule, and deliberately so: <c>CanServe</c> routes by ENGINE because that is what
/// executes the file, while what a model can RECOGNISE is decided by what it was trained on.</para>
///
/// <para>Keyed by exact catalogue name — identity lookup, the same shape as <see cref="ModelRatings"/>
/// — never by parsing the name's shape, which is the wrong-guess classifier TRN-1 exists to end.</para>
///
/// <para><b>Local models only — cloud returns <c>null</c>, meaning "no claim".</b> Not an empty
/// set: empty reads as "supports nothing" and would block every language. The distinction is
/// load-bearing and is what keeps cloud behaviour identical to before this type existed. Cloud
/// models are deliberately unmodelled because the provider validates the request itself and
/// <c>ProviderApiException</c> already surfaces a rejection, whereas sourcing an authoritative
/// per-model ISO SET for ten cloud models is a different order of work from sourcing a count — and
/// TRN-8's standing rule is that an unsourced user-visible claim is worse than none.</para>
///
/// <para><b>What "supported" means here: the provider lists it.</b> NOT that it transcribes well.
/// Per-language quality is a WER question ([[TRN-4]]) that neither NVIDIA nor OpenAI publishes, and
/// claiming a quality tier nobody measured is the sourcing failure TRN-3 spent fifteen review
/// rounds removing from the ratings table.</para>
/// </summary>
internal static class ModelLanguageSupport
{
    /// <summary>
    /// NVIDIA Parakeet TDT 0.6B v3's 25 languages, transcribed from the model card at
    /// <c>huggingface.co/nvidia/parakeet-tdt-0.6b-v3</c> (read 2026-08-03) — provider authority, not
    /// an aggregator. The same card states the model "automatically detects the language of the
    /// audio and transcribes it without requiring additional prompting", which is the independent
    /// basis for <see cref="LanguageConstraint.EngineAutoDetectsOnly"/>.
    ///
    /// <para>A literal, not a derivation: it is a factual claim about someone else's model, so it
    /// must be checkable against the source line by line. <c>ModelLanguageSupportTests</c> pins the
    /// exact set so a dropped or invented code fails the build.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> ParakeetCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it",
            "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk",
        };

    /// <summary>
    /// Model name → the languages that model recognises. Exact-name identity, one entry per set of
    /// weights, so a second Parakeet build gets its own row rather than inheriting v3's.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlySet<string>> ByModel =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // BOTH Parakeet bundle names, one set of weights: the sherpa-era int8 bundle and
            // the TRN-29 GGUF are the same v3 model (same NVIDIA model card, same 25 languages),
            // so this is still one entry per set of weights — spelled twice because persisted
            // selections and the config-conditional catalog can each carry either name.
            [Models.ParakeetCatalog.SherpaName] = ParakeetCodes,
            [Models.ParakeetCatalog.GgufName] = ParakeetCodes,
        };

    /// <summary>
    /// The codes a model can recognise, or <c>null</c> when nothing is claimed.
    ///
    /// <para><b>Only Parakeet claims a set. Whisper does NOT, and that is deliberate</b> — both diff
    /// reviewers rejected the first version, which derived Whisper's coverage from the app's own
    /// language PICKER. That is a proxy, not a source: whisper.cpp accepts codes the picker omits,
    /// so an imported or hand-edited pin outside the 99 would be declared "not supported" on the
    /// authority of our dropdown. A negative claim about someone else's engine needs a real source,
    /// and for Whisper we do not have one — so no claim is the honest answer, and it costs nothing,
    /// because every language the picker CAN offer is one Whisper handles.</para>
    ///
    /// <para><c>null</c> is also what cloud and unknown models return — "we do not model this" —
    /// and never an empty set, which would read as "supports nothing" and constrain everything.</para>
    ///
    /// <para>Case-INSENSITIVE, and that is not incidental: persisted settings come back <c>"EN"</c>
    /// or <c>"Auto"</c> from an import or a hand edit, which is why
    /// <see cref="EffectiveTranscriptionLanguage"/> compares tags the same way. A case-sensitive set
    /// here would report an imported <c>"NL"</c> as unsupported for a language that IS in range —
    /// the exact spurious-warning bug that comment records fixing.</para>
    /// </summary>
    internal static IReadOnlySet<string>? SupportedCodesFor(TranscriptionModelInfo? model) =>
        model is not null && ByModel.TryGetValue(model.Name, out var codes)
            ? codes
            : null;   // Whisper, cloud, anything unlisted — no sourced bounded set

    /// <summary>
    /// Positive evidence that a model CAN produce this language — the gate on any advice to
    /// download it.
    ///
    /// <para><b>Deliberately not <c>!CannotRecognise</c>.</b> That reads "not known to be
    /// unsupported", which is TRUE for unknown coverage — and Whisper's coverage is unknown by
    /// design (<see cref="SupportedCodesFor"/> returns null). So the negation would emit "Download
    /// Whisper Small to use made-up." for any language string a hand-edited or imported settings
    /// file carries, since import validates the field as a string with no language semantics. Advice
    /// that fails OPEN is worse than no advice: it sends the user to download 264 MB for nothing
    /// (Codex diff review — the first version shipped exactly that, under a comment claiming it
    /// could not).</para>
    ///
    /// <para><b>The picker list is evidence in ONE direction only.</b>
    /// <see cref="ModelManagementViewModel.SupportedLanguages"/> is Whisper's own published set, so
    /// MEMBERSHIP is positive evidence for a Whisper model. Non-membership is NOT evidence of
    /// absence — whisper.cpp accepts codes the picker omits — which is exactly why
    /// <see cref="SupportedCodesFor"/> refuses to derive a negative claim from it. Reading it one
    /// way and not the other is the whole distinction, not an inconsistency.</para>
    ///
    /// <para>The fallback is scoped to <see cref="LocalRuntimeKind.Whisper"/> because the list is
    /// WHISPER's. Anything else with no declared set — cloud, a future engine — yields no positive
    /// evidence and therefore no advice.</para>
    ///
    /// <para><b>Runtime is not enough — the weights decide.</b> An English-only <c>.en</c> build has
    /// the Whisper runtime and would otherwise inherit all 99 codes, so the note could say "Download
    /// Whisper Small (English) to use Japanese" (Codex diff review r2). No such row ships today, but
    /// this helper advertises a contract to future callers, and `EnglishOnlyModelNaming` exists
    /// precisely because that distinction is invisible in the runtime and silent when missed.</para>
    /// </summary>
    internal static bool CanRecognise(TranscriptionModelInfo? model, string? language)
    {
        if (model is null || string.IsNullOrWhiteSpace(language)) return false;
        if (string.Equals(language, EffectiveTranscriptionLanguage.Auto, StringComparison.OrdinalIgnoreCase))
            return false;

        var declared = SupportedCodesFor(model);
        if (declared is not null) return declared.Contains(language);

        if (model.Runtime != LocalRuntimeKind.Whisper) return false;

        return EnglishOnlyModelNaming.IsEnglishOnly(model.Name)
            ? string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            : ModelManagementViewModel.SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the model is known NOT to recognise this language.</summary>
    internal static bool CannotRecognise(TranscriptionModelInfo? model, string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        if (string.Equals(language, EffectiveTranscriptionLanguage.Auto, StringComparison.OrdinalIgnoreCase))
            return false;

        var supported = SupportedCodesFor(model);
        return supported is not null && !supported.Contains(language);
    }

    /// <summary>
    /// The sentence shown under a language picker, or <c>null</c> when there is nothing to say.
    ///
    /// <para><b>Composed, never hand-written per model.</b> Both halves come from data that is
    /// already sourced and already rendered elsewhere: the coverage phrase is
    /// <see cref="ModelRatings.LanguagesFor"/> — the same string the Models page shows beside the
    /// model — and the model's own display name. The first draft of this feature hardcoded
    /// "Parakeet supports 25 European languages" into the copy, which would have been a SECOND
    /// hand-maintained copy of a sourced user-visible claim, and a second place to forget when the
    /// next auto-detect engine arrives. That drift is what TRN-3 and TRN-8 spent their review
    /// rounds eliminating.</para>
    ///
    /// <para>Pure, so the wording is unit-testable without a page — the convention
    /// <c>ModelsPage.ComposeActiveModelSubtitle</c> already follows.</para>
    /// </summary>
    /// <param name="recommendation">
    /// The model the surrounding screen is offering to download, when there is one. Used ONLY to add
    /// an action clause naming it — the note still JUDGES <paramref name="model"/>, which is the
    /// two-path resolution ONB-5 was explicitly told to preserve (the note reads the PERSISTED
    /// selection because "Skip download for now" keeps it, while the panel above names the
    /// recommendation; against the recommendation the note was once "both dead and wrong").
    ///
    /// <para>The clause requires POSITIVE evidence — <see cref="CanRecognise"/>, never
    /// <c>!CannotRecognise</c>, which is true for unknown coverage and would promise a download that
    /// fixes nothing. A caller cannot make it advise a model that has not been shown to produce the
    /// language.</para>
    ///
    /// <para><b>The two guards differ in reachability, and conflating them has now been wrong in
    /// BOTH directions</b> (Codex diff review r1 then r2 — first the doc claimed the recommendation
    /// was machine-chosen, then the correction over-swung and called both guards defensive).
    ///
    /// <list type="bullet">
    /// <item><b>Same-model: DEFENSIVE.</b> Onboarding resolves through
    /// <c>RecommendedLocalModelFor(parakeetAvailable, selectedLanguage)</c>, and
    /// <c>DefaultLocalModel.Resolve</c> is language-aware — a Parakeet-unsupported language already
    /// recommends Whisper, so the shipping caller cannot present the complained-about model. It
    /// holds for a future caller choosing on other grounds.</item>
    /// <item><b>Positive evidence: LIVE.</b> An imported settings file can carry any language string
    /// (import validates no language semantics), and the resolver happily recommends Whisper for
    /// one — so <see cref="CanRecognise"/> is what stops a real user being told to download 264 MB
    /// to gain a language nobody claims. This one runs in production.</item>
    /// </list></para>
    /// </param>
    internal static string? ComposeLanguageNote(
        TranscriptionModelInfo? model, string? requestedLanguage,
        TranscriptionModelInfo? recommendation = null)
    {
        if (model is null) return null;

        var result = EffectiveTranscriptionLanguage.For(model, requestedLanguage);
        if (!result.Diverges) return null;

        var name = model.DisplayName;
        var coverage = ModelRatings.LanguagesFor(model.Name);
        var language = ModelManagementViewModel.GetLanguageDisplayName(requestedLanguage ?? "");

        // What the model will ACTUALLY do instead — read off the resolved effective language, never
        // assumed. The first version ended every LanguageNotSupported sentence with "it will detect
        // the language automatically", which is true only because Parakeet's fallback happens to be
        // auto; on any engine that keeps the pin, the note would have described behaviour that does
        // not occur (Kimi, diff review).
        var isAuto = string.Equals(result.Language, EffectiveTranscriptionLanguage.Auto,
                                   StringComparison.OrdinalIgnoreCase);
        var effective = ModelManagementViewModel.GetLanguageDisplayName(result.Language);

        // The clause is DROPPED when the effective language is the very language we just said is
        // unsupported: "does not support Chinese — it will use Chinese" is a contradiction, not a
        // consequence. Unreachable today (only Parakeet has a set, and Parakeet always coerces to
        // auto) but reachable the moment a pin-KEEPING engine gets one — which is exactly the
        // generalisation the overlay design advertises, so it should not ship a tautology waiting
        // to happen (Kimi, final review).
        var instead =
            isAuto ? "it will detect the language automatically"
            : string.Equals(effective, language, StringComparison.Ordinal) ? null
            : $"it will use {effective}";

        // ONB-5: what the user can DO about it, when the screen is offering something that would
        // actually help. Not composed for EngineAutoDetectsOnly — there the language is IN range and
        // nothing needs downloading, so "download X" would be advice against a non-problem.
        var fix = recommendation is not null
                  && !ModelDiskReconciliation.IsSameModel(recommendation.Name, model.Name)
                  && CanRecognise(recommendation, requestedLanguage)
            ? $" Download {recommendation.DisplayName} to use {language}."
            : "";

        return result.Constraint switch
        {
            // The actionable one: the model cannot produce this language at all.
            //
            // The coverage phrase is used AS PUBLISHED — no ToLowerInvariant, which turned
            // "25 European languages" into "25 european languages" and lowercased a proper
            // adjective in user-visible copy (Kimi). It reads correctly mid-sentence either way.
            // ONB-5: the AUTO-DETECT clause is suppressed here, and ONLY here. "does not support
            // Japanese — it will detect the language automatically" reads as reassurance in the one
            // position where nothing reassuring is true: auto-detect cannot produce a language the
            // weights do not contain, so the sentence argued against the very download that fixes
            // it (owner UAT §91.3: "the amber warning doesn't make sense here").
            //
            // A CONCRETE effective language is still stated, because that IS a real consequence the
            // user needs — "not Japanese — it will use English" tells them what they will actually
            // get. Only the auto case is dropped, which keeps the generalisation the constraint
            // model exists for: a pin-KEEPING engine with a coverage set still describes itself.
            //
            // This changes the MODELS PAGE too, not just onboarding — ONB-5 was written about the
            // wizard, but the misleading clause is a property of the SENTENCE, not the screen, and
            // there is one composer by design. What survives there reads "Parakeet … supports 25
            // European languages, not Chinese." — complete, and no longer implying the pin will be
            // honoured. UAT 90.1 asserted the old clause and is updated with this change.
            LanguageConstraint.LanguageNotSupported when coverage is not null =>
                isAuto || instead is null
                    ? $"{name} supports {coverage}, not {language}.{fix}"
                    : $"{name} supports {coverage} and not {language} — {instead}.{fix}",
            LanguageConstraint.LanguageNotSupported =>
                isAuto || instead is null
                    ? $"{name} does not support {language}.{fix}"
                    : $"{name} does not support {language} — {instead}.{fix}",

            // In range, but the engine takes no language argument. Harmless for output, and the
            // user still believes a setting applies that does not.
            LanguageConstraint.EngineAutoDetectsOnly =>
                $"{name} detects the language itself — your selected language is not used.",

            // Unreachable from the shipped catalogue (no row is English-only since 2026-08-03) and
            // deliberately given the same shape as the others rather than special-cased: a restored
            // `.en` row would otherwise be the one constraint with no words.
            LanguageConstraint.EnglishOnlyModel =>
                $"{name} transcribes English only — {language} will be transcribed as English.",

            _ => null,
        };
    }
}
