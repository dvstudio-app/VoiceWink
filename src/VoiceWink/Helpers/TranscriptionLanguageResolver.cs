using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>Which source supplied a recording's speech-recognition language (PRM-5).</summary>
internal enum LanguageSource
{
    /// <summary>No override anywhere — the global Settings language, captured at recording start.</summary>
    Global,
    /// <summary>A retry reuses the language its original attempt was recognised with.</summary>
    Retry,
    /// <summary>The effective pre-recognition prompt carries a language override.</summary>
    Prompt,
    /// <summary>The active App Mode config carries a language override.</summary>
    AppMode,
}

/// <summary>
/// PRM-5: the ONE place that decides which language a recording is recognised with.
/// <para>Precedence — retry-captured → the effective prompt's override → App-Mode override → the
/// global Settings language. The result is ALWAYS a concrete language, never null: resolving at
/// recording start and returning null for "global" would let a mid-recording Settings edit change
/// the language the audio is transcribed with, and — worse — a retry would store null and re-read
/// Settings later, so changing the global language after a failure would make Retry recognise the
/// SAME WAV differently (Codex diff review, 2026-07-25).</para>
/// <para><b>The prompt is chosen BEFORE its override is read</b> (Codex plan review, 2026-07-25).
/// Pass the prompt that <see cref="PromptRunSnapshot.Resolve"/> picks with
/// <c>triggerFired: false</c> — hotkey → App-Mode-linked → globally active. A higher-priority
/// prompt with NO override must fall through to App-Mode/global; it must never borrow the override
/// of a lower-priority prompt that will not actually run.</para>
/// <para>Trigger-selected prompts are deliberately excluded from that resolution: a spoken trigger
/// word is matched in the transcript, so by then the audio has already been recognised.</para>
/// </summary>
internal static class TranscriptionLanguageResolver
{
    /// <summary>
    /// Resolve the CONCRETE language this recording will be recognised with. Never returns null or
    /// blank: <paramref name="globalLanguage"/> is the floor, and a blank global degrades to
    /// <c>"auto"</c> (what the settings default already is).
    /// <para>Blank is treated as "not set" at every level — both the App-Mode combo and the prompt
    /// dialog persist <c>""</c> for their "Use default" item.</para>
    /// </summary>
    internal static (string Language, LanguageSource Source) Resolve(
        string? retryLanguage,
        CustomPrompt? effectivePrePrompt,
        string? appModeLanguage,
        string? globalLanguage)
    {
        if (!string.IsNullOrWhiteSpace(retryLanguage))
            return (retryLanguage!, LanguageSource.Retry);

        // Read the override off THAT prompt only — never scan other candidates.
        var promptLanguage = effectivePrePrompt?.LanguageOverride;
        if (!string.IsNullOrWhiteSpace(promptLanguage))
            return (promptLanguage!, LanguageSource.Prompt);

        if (!string.IsNullOrWhiteSpace(appModeLanguage))
            return (appModeLanguage!, LanguageSource.AppMode);

        return (string.IsNullOrWhiteSpace(globalLanguage) ? AutoLanguage : globalLanguage!,
                LanguageSource.Global);
    }

    /// <summary>The whisper/cloud "detect it" sentinel, and the global setting's own default.</summary>
    internal const string AutoLanguage = "auto";
}
