namespace VoiceWink.Helpers;

/// <summary>
/// Whether a whisper.cpp model name denotes English-only weights.
///
/// <para><b>No catalogue row is English-only since 2026-08-03</b> — the four <c>.en</c> builds were
/// dropped because each rendered identical accuracy stars, speed stars and download size to its
/// multilingual twin, so the Models page could not justify the choice it was offering. This rule
/// survives that removal for two live callers and one future one:</para>
/// <list type="bullet">
/// <item><c>EffectiveTranscriptionLanguage</c> — English-only weights cannot honour a pinned
/// language, and running them with one is wrong OUTPUT, not a missing dialog;</item>
/// <item><c>WhisperPromptTokenizer.VocabFor</c> — the <c>.en</c> family uses a different rank table
/// (gpt2 vs multilingual), and the two produce different token counts for the same text;</item>
/// <item>whoever restores an <c>.en</c> row. Deleting the rule would make that restoration silently
/// wrong in both of the above, which is why it is kept rather than removed with the rows.</item>
/// </list>
///
/// <para><b>This type used to be <c>EnglishOnlyModelWarning</c> and also owned the Models-page
/// warning</b> (<c>ShouldWarn</c> / <c>BuildMessage</c>). That warning is gone with the rows it
/// guarded: no selectable model can be English-only, so it protected an unreachable state. The
/// rename is deliberate — a type called <c>…Warning</c> that no longer warns is a lie in the
/// filename.</para>
/// </summary>
internal static class EnglishOnlyModelNaming
{
    /// <summary>
    /// <para><b>The <c>.en</c> marker is a SEGMENT, not a suffix</b> — and reading it as a suffix was
    /// a live latent bug caught at review before the q8 catalogue shipped (TRN-6, 2026-08-03). The
    /// old <c>EndsWith(".en")</c> was correct for every name that existed at the time
    /// (<c>ggml-small.en</c>), and silently wrong for the quantized English builds
    /// (<c>ggml-small.en-q8_0</c>), which end in the quantization tag. It mattered far beyond a
    /// missing dialog: <c>EffectiveTranscriptionLanguage</c> shared the same test, so an
    /// English-only model would have been run with whatever language the user had set — weights
    /// that cannot honour it, i.e. wrong output rather than a missing warning.</para>
    ///
    /// <para>This is the ONE definition; both callers delegate here rather than repeating the test,
    /// because two copies of a naming rule is how the two answers drifted apart in the first place.
    /// A trailing <c>.en</c> still matches (the plain builds); so does <c>.en</c> followed by
    /// <c>-</c> (any quantization or future tag). Deliberately NOT a bare <c>Contains(".en")</c>,
    /// which would match a hypothetical <c>ggml-v.enhanced</c>.</para>
    /// </summary>
    internal static bool IsEnglishOnly(string modelName)
        => !string.IsNullOrEmpty(modelName)
           && (modelName.EndsWith(".en", StringComparison.OrdinalIgnoreCase)
               || modelName.Contains(".en-", StringComparison.OrdinalIgnoreCase));
}
