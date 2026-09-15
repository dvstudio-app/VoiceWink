using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>Why the language a model will actually recognise with differs from the one asked for.</summary>
internal enum LanguageConstraint
{
    /// <summary>No constraint — the model recognises with exactly what it was given.</summary>
    None,
    /// <summary>An English-only build. It can only produce English, whatever was requested.</summary>
    EnglishOnlyModel,
    /// <summary>The engine auto-detects and accepts no language input at all.</summary>
    EngineAutoDetectsOnly,
    /// <summary>
    /// The model does not recognise the requested language AT ALL — it is outside the set the
    /// provider publishes (see <see cref="ModelLanguageSupport"/>).
    ///
    /// <para>Distinct from <see cref="EngineAutoDetectsOnly"/>, which says only that a pin is
    /// ignored. Parakeet ignores a pinned Dutch and still transcribes Dutch correctly; it ignores a
    /// pinned Chinese and cannot transcribe Chinese at all. Collapsing the two would report the
    /// harmless case and the harmful one identically.</para>
    /// </summary>
    LanguageNotSupported,
}

/// <summary>
/// What language a transcription attempt will ACTUALLY recognise with, given what was requested and
/// which model is about to run (TRN-1 step 3, PR A).
///
/// <para><b>Why this needs to exist as one function.</b> PRM-5's
/// <see cref="TranscriptionLanguageResolver"/> answers "what did the user ask for" — prompt override,
/// then App Mode, then the global setting. That is a different question from "what will the model
/// do", and the two were being answered separately in two places that already disagreed:
/// <c>ModelManagementViewModel</c>'s preload coerced an <c>.en</c> model to <c>"en"</c>, while
/// <c>MainViewModel.EnsureModelLoadedAsync</c> did not — so selecting an English-only model
/// preloaded it as English and recording start then prepared it again with something else, paying a
/// 2–8 s rebuild for a language that model cannot produce anyway.</para>
///
/// <para><b>Requested and effective are both kept, never collapsed.</b> History must record what the
/// attempt actually did, but a RETRY must replay what the user asked for — otherwise retrying
/// Parakeet audio through Whisper or a cloud provider would silently inherit <c>auto</c> and lose a
/// Dutch override. Collapsing them loses one or the other.</para>
///
/// <para>Pure and keyed on the catalog descriptor, because this is a property of the MODEL. An
/// unknown model constrains nothing — we cannot make claims about a row we do not have.</para>
/// </summary>
internal static class EffectiveTranscriptionLanguage
{
    /// <summary>The language a model will recognise with, and why it differs if it does.</summary>
    internal readonly record struct Result(string Language, LanguageConstraint Constraint)
    {
        /// <summary>True when the model will not honour what was requested.</summary>
        internal bool Diverges => Constraint != LanguageConstraint.None;
    }

    /// <summary>Auto-detect, the app's spelling of "no pinned language".</summary>
    internal const string Auto = "auto";

    internal static Result For(TranscriptionModelInfo? model, string? requested)
    {
        var asked = string.IsNullOrWhiteSpace(requested) ? Auto : requested!;

        // Language tags are compared case-INSENSITIVELY throughout: the value is persisted settings
        // and can come back "EN" or "Auto" from an import or a hand edit. An ordinal comparison
        // still produced the right LANGUAGE but reported a spurious divergence, which would surface
        // a warning about nothing.
        bool Asked(string tag) => string.Equals(asked, tag, StringComparison.OrdinalIgnoreCase);

        // No catalog row: cloud models, or a name no runtime serves. Nothing can be asserted about
        // a model we cannot see, and inventing a constraint would be the guess this type replaces.
        if (model is null) return new(asked, LanguageConstraint.None);

        // The LANGUAGE is decided by the chain below; "can this model even recognise what was
        // asked" is then OVERLAID onto whatever that chain produced (change D, 2026-08-04).
        //
        // Overlay rather than an early return, because the two questions are independent and the
        // first version conflated them: a global membership check ahead of every branch returned
        // the PIN as the effective language for an English-only row, re-creating the silent
        // wrong-output bug that branch exists to prevent — caught by both diff reviewers. Overlaying
        // preserves the language by CONSTRUCTION, and generalises: a future model with a bounded
        // set gets the right answer without another engine-specific branch.
        var baseResult = ResolveLanguage(model, asked, Asked);
        return ModelLanguageSupport.CannotRecognise(model, asked)
            ? baseResult with { Constraint = LanguageConstraint.LanguageNotSupported }
            : baseResult;
    }

    /// <summary>What language this model will run with, and why it differs — ignoring whether the
    /// model can recognise the request at all, which <see cref="For"/> overlays.</summary>
    private static Result ResolveLanguage(
        TranscriptionModelInfo model, string asked, Func<string, bool> Asked)
    {
        // Engine capability outranks the model's own name: an auto-detect-only engine ignores a
        // pinned language whatever the file is called.
        if (model.Runtime == LocalRuntimeKind.Parakeet)
            return new(Auto, Asked(Auto) ? LanguageConstraint.None : LanguageConstraint.EngineAutoDetectsOnly);

        // The `.en` builds are English-only WHISPER weights — asking for anything else cannot be
        // honoured, and preloading them with a non-English language costs a rebuild for nothing.
        //
        // Scoped to Whisper deliberately. `.en` is whisper.cpp's naming convention, not a universal
        // one, so applying it to every runtime would English-coerce a future engine's model purely
        // because of how it happens to be named — the same infer-from-the-name mistake that routing
        // on file layout was.
        //
        // The naming test itself is EnglishOnlyModelNaming's, not a second copy here. This site
        // previously repeated `EndsWith(".en")`, and the duplication is exactly what let the two
        // answers drift: both were right for `ggml-small.en` and both were wrong for
        // `ggml-small.en-q8_0`, whose `.en` is a segment rather than a suffix. Here that meant no
        // English coercion at all — the model would run with the user's language, which
        // English-only weights cannot honour (TRN-6, 2026-08-03).
        //
        // UNREACHABLE from the shipped catalogue since 2026-08-03 — no row is English-only — and
        // deliberately kept. `For` takes a descriptor, so a caller can still reach it; more to the
        // point, deleting it would make a restored `.en` row silently run with the user's language
        // again, which is the bug the comment above records fixing.
        if (model.Runtime == LocalRuntimeKind.Whisper
            && EnglishOnlyModelNaming.IsEnglishOnly(model.Name))
        {
            return new("en", Asked("en") || Asked(Auto)
                ? LanguageConstraint.None
                : LanguageConstraint.EnglishOnlyModel);
        }

        return new(asked, LanguageConstraint.None);
    }

    /// <summary>Convenience for the call sites that hold a name rather than a descriptor.</summary>
    internal static Result ForModelName(string? modelName, string? requested)
        => For(FindLocal(modelName), requested);

    private static TranscriptionModelInfo? FindLocal(string? modelName)
    {
        // Either Parakeet bundle spelling resolves to the active catalog row (TRN-29 flip) —
        // an upgrader's un-rewritten selection must keep producing EngineAutoDetectsOnly
        // rather than silently passing a pin the engine never honours.
        modelName = ParakeetCatalog.CanonicalName(modelName);
        return string.IsNullOrWhiteSpace(modelName)
            ? null
            : PredefinedModels.Models.FirstOrDefault(
                m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
    }
}
