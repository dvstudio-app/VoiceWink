namespace VoiceWink.Helpers;

/// <summary>
/// TRN-75: the ONE rule for the language a local Whisper processor is built with.
///
/// <para><b>The defect it fixes.</b> <c>BuildProcessor</c> called <c>WithLanguage</c> only for a
/// pinned language and left it unset on "auto". whisper.net 1.9.1 starts from
/// <c>whisper_full_default_params</c> and copies only the options it was given, and that default
/// language is <c>"en"</c> (its own <c>WithLanguage</c> doc: "Default value is "en""). So every
/// local Whisper decode on Auto was forced to English: field logs showed 42 of 42 decodes reporting
/// "detected language: en", and Chinese speech came out as English or as a placeholder.</para>
///
/// <para><b>Why <c>"auto"</c> and not <c>WithLanguageDetection()</c>.</b> Both make whisper.cpp
/// detect the language and THEN transcribe: <c>whisper_full</c> runs its auto-detect when the
/// language is null, empty or <c>"auto"</c>, and stops after detecting only when the separate
/// <c>detect_language</c> flag is set — which whisper.net 1.9.1 never writes (decompiled: the field
/// exists on <c>WhisperFullParams</c> and nothing assigns it). <c>WithLanguageDetection()</c> just
/// sets the language to <c>""</c>. The explicit <c>"auto"</c> is chosen for clarity: the value
/// names the behaviour in the code, the trace and a debugger, where <c>""</c> reads as "unset" —
/// the very state this type exists to rule out.</para>
/// </summary>
internal static class WhisperLanguageParameter
{
    /// <summary>The value that makes whisper.cpp detect the language, then transcribe in it.</summary>
    internal const string AutoDetect = "auto";

    /// <summary>The language the GPU self-test decodes in: the golden clip is English, so an
    /// auto processor's self-test decodes on a separate English processor (see
    /// <c>WarmUpDecodeAsync</c>).</summary>
    internal const string SelfTestLanguage = "en";

    /// <summary>Null, blank and "auto" in any casing — how the service normalises them.</summary>
    internal static bool IsAuto(string? language)
        => string.IsNullOrWhiteSpace(language)
           || language.Trim().Equals(AutoDetect, StringComparison.OrdinalIgnoreCase);

    /// <summary>What <c>WithLanguage</c> receives. Never null: leaving it unset means English.</summary>
    internal static string For(string? language)
        => IsAuto(language) ? AutoDetect : language!.Trim();
}
