namespace VoiceWink.Helpers;

/// <summary>
/// Which shape of language parameter an OpenAI transcription model expects.
///
/// <para>OpenAI's 2026-07-31 generation (<c>gpt-transcribe</c>, <c>gpt-live-transcribe</c>)
/// REPLACED the singular <c>language</c> field with a <c>languages</c> array, and documents
/// that both must never be sent together. Every earlier model — <c>whisper-1</c>,
/// <c>gpt-4o-transcribe</c>, <c>gpt-4o-mini-transcribe</c> — still takes the singular field.
/// Getting this wrong is silent: the request still succeeds, it just ignores the language the
/// user pinned, which is precisely the determinism <see cref="TranscriptionLanguageResolver"/>
/// exists to guarantee (PRM-5).</para>
///
/// <para>An explicit set, not a pattern. Unknown ids — a future model, a hand-edited
/// settings.json — fall to the LEGACY singular field on purpose: that is the status quo for
/// everything currently shipping, so an id we do not recognise keeps today's exact behaviour
/// instead of adopting a shape its endpoint may reject outright. Drift is a catalog question,
/// and the weekly <c>/vw-model-review</c> is what catches it.</para>
/// </summary>
internal static class OpenAITranscriptionParameters
{
    /// <summary>Models that take <c>languages[]</c> instead of <c>language</c>.
    /// <c>gpt-live-transcribe</c> is listed for completeness — VoiceWink does not route to it
    /// (it is realtime-sessions-only) — so that if it ever is routed, it cannot arrive with the
    /// wrong parameter shape by omission.</summary>
    private static readonly HashSet<string> LanguagesArrayModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-transcribe",
        "gpt-live-transcribe",
    };

    internal static bool UsesLanguagesArray(string? model)
        => !string.IsNullOrWhiteSpace(model) && LanguagesArrayModels.Contains(model);

    /// <summary>Models that accept the structured <c>keywords[]</c> biasing field.
    /// Deliberately the same set and deliberately MODEL-scoped, not provider-scoped:
    /// <c>gpt-4o-transcribe</c> is the model whose prompt echo on near-silent audio was actually
    /// observed by this project, and <c>keywords</c> is not documented for it.</summary>
    internal static bool SupportsKeywords(string? model) => UsesLanguagesArray(model);
}
