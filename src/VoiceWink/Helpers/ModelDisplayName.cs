using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// The ONE answer to "what do we call this model in front of a user" (UI-2, 2026-08-06).
///
/// <para>Owner rule, stated generally at UAT 2026-08-05 §91.4: <i>"we should never use technical file
/// names for the models … You should always use the human-friendly name for the models everywhere.
/// Except for in the logs where it matters."</i></para>
///
/// <para><b>Why this type exists rather than a fourth copy.</b> Four near-identical resolvers had
/// grown — <c>AudioTranscribePage</c>, <c>AppModePage</c>, <c>HistoryPage</c> and a delegate in
/// <c>OnboardingPage</c> — and the onboarding step still interpolated raw catalogue ids in two places
/// because the helper was somewhere else. Consolidating is what makes the rule enforceable; a fifth
/// copy is how the next leak happens.</para>
///
/// <para><b>Both lookups are case-insensitive, and that is a fixed defect, not politeness.</b> The
/// local-runtime seam resolves names case-insensitively, so a settings value of <c>ggml-Base</c> loads
/// and transcribes — while a helper comparing with <c>==</c> rendered the bare id for a model the app
/// was actively running. Cloud goes through <see cref="CloudModels.Canonicalize"/>, which is
/// OrdinalIgnoreCase and returns catalogue casing, so the <c>==</c> after it is exact by
/// construction.</para>
///
/// <para><b>Lookup order is immaterial and deliberately left as it reads.</b> The two catalogues are
/// disjoint (local rows are <c>ggml-*</c>/<c>parakeet-*</c> stems, cloud rows are provider model ids),
/// so no name can match both. The old helpers disagreed on order — local-first in two, cloud-first in
/// History — and neither was wrong; unifying the order here is not a behaviour change.</para>
/// </summary>
internal static class ModelDisplayName
{
    /// <summary>Rendered when the value is blank — empty or whitespace. Deliberately distinct from
    /// <see cref="Unrecognised"/>: "there is nothing here" and "that is not a model we know" are
    /// different statements, and a blank gap exactly where a model name belongs is worse than saying
    /// so.</summary>
    internal const string Unnamed = "(unnamed model)";

    /// <summary>Rendered by <see cref="Resolve"/> for a name NO catalogue claims — a retired id, a
    /// hand-edited one, or one an imported backup carried in.
    ///
    /// <para>Deliberately says "unrecognised" and not "no longer available": the two cases are
    /// indistinguishable here (retired-name machinery was removed once no user selection pointed at
    /// one), so claiming it used to exist would be a guess dressed as history.</para></summary>
    internal const string Unrecognised = "(unrecognised model)";

    /// <summary>Catalogue display name for a local or cloud model; fixed copy for anything else —
    /// <see cref="Unnamed"/> when the value is blank, <see cref="Unrecognised"/> when it is a name no
    /// catalogue claims.
    ///
    /// <para><b>An unknown name renders GENERIC copy, never the id</b>, and since the owner's
    /// 2026-08-06 decision that holds on EVERY surface including history — the rule was "always use
    /// the human-friendly name for the models everywhere, except in the logs". The id survives in
    /// <c>TranscriptionRecord.ModelName</c> and in the logs, which is where it is actually useful.</para>
    ///
    /// <para>Neither entry point echoes its input, so nothing here needs sanitizing: an id cannot leak
    /// by construction rather than by scrubbing. That is a stronger guarantee than the scrubbing it
    /// replaced, and it is why <c>UntrustedTextSanitizer</c> no longer appears in this file.</para>
    ///
    /// <para>Two reviewers disagreed about this on the way here — one arguing a history row is a
    /// record that must keep its provenance, the other that no user-facing surface may leak an id. An
    /// intermediate version split the behaviour by entry point. The owner settled it toward one rule,
    /// which is why this type is simpler than the discussion that produced it.</para></summary>
    internal static string Resolve(string? modelName)
    {
        // Either Parakeet bundle spelling renders as the active row's display name (TRN-29
        // flip): History rows are immutable SQLite, so a pre-flip record naming the sherpa-era
        // bundle must keep rendering "Parakeet" rather than the unrecognised-model copy.
        modelName = ParakeetCatalog.CanonicalName(modelName);

        var local = PredefinedModels.Models
            .FirstOrDefault(m => ModelDiskReconciliation.IsSameModel(m.Name, modelName));
        if (local != null) return local.DisplayName;

        var cloud = FindCloud(modelName);
        if (cloud != null) return cloud.DisplayName;

        // Nothing renderable is a DIFFERENT statement from "not a model we know", so the two keep
        // separate copy: one says the value is blank, the other that it is unrecognised.
        return string.IsNullOrWhiteSpace(modelName) ? Unnamed : Unrecognised;
    }

    /// <summary>History's rendering: a cloud model shows as <c>Provider/Name</c> with the provider
    /// stripped out of the display name, so a dense metadata line does not repeat it. Local and
    /// unknown names behave exactly like <see cref="Resolve"/>.
    ///
    /// <para><b>This is now formatting ONLY — it no longer carries a separate unknown-name policy.</b>
    /// It used to render an unknown id here, on the argument that a history row is a RECORD whose id
    /// is the only surviving evidence of what produced a transcript. <b>OWNER DECISION 2026-08-06:
    /// generic copy everywhere.</b> The rule as stated was "always use the human-friendly name for the
    /// models EVERYWHERE. Except for in the logs where it matters" — and history is part of
    /// everywhere. The provenance objection is answered rather than overruled: the id is still held in
    /// <c>TranscriptionRecord.ModelName</c> and in the logs, and this line truncated at 40 characters
    /// anyway, so it was never a complete provenance view.</para>
    ///
    /// <para>Consequence worth knowing: NEITHER entry point echoes its input now, so
    /// <see cref="ModelDisplayName"/> no longer sanitizes at all — an id cannot leak by construction.
    /// <c>UntrustedTextSanitizer</c> is still used by <c>UnknownTranscriptionModelException</c>.</para></summary>
    internal static string ResolveForHistory(string? modelName)
    {
        var cloud = FindCloud(modelName);
        if (cloud != null)
        {
            // PREFIX-only strip, deliberately not Replace(): Replace removes EVERY occurrence, so a
            // future catalogue row carrying the provider token mid-name would render mangled. Today
            // all cloud DisplayNames happen to start with "{Provider} ", so the two agree — this
            // makes that a non-issue rather than an invariant a test has to keep watching (Kimi diff
            // review offered either; removing the dependency beats pinning it).
            var provider = $"{cloud.Provider} ";
            var name = cloud.DisplayName.StartsWith(provider, StringComparison.Ordinal)
                ? cloud.DisplayName[provider.Length..]
                : cloud.DisplayName;
            return $"{cloud.Provider}/{name}";
        }

        return Resolve(modelName);
    }

    private static TranscriptionModelInfo? FindCloud(string? modelName)
    {
        var canonical = CloudModels.Canonicalize(modelName);
        return CloudModels.Models.FirstOrDefault(m => m.Name == canonical);
    }
}
