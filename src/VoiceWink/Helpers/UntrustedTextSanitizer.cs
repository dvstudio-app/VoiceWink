using System.Text;

namespace VoiceWink.Helpers;

/// <summary>
/// Bounded, display-safe rendering of a string VoiceWink did not author.
///
/// <para>Extracted from <see cref="MiniRecorderTargetLabel"/> (PILL-1), which needed it for
/// executable metadata. The second caller is the local-model seam: a <c>SelectedModelName</c> that
/// is not in the catalog came from a hand-edited or imported settings file, and settings import
/// validates that field only as a string — so it can carry control characters, bidi overrides,
/// zero-width joiners, or unbounded length. Today it reaches the user through exactly ONE model-name
/// path: <c>UnknownTranscriptionModelException</c>'s message.</para>
///
/// <para><b><see cref="ModelDisplayName"/> is no longer a caller, and that is deliberate.</b> UI-2
/// consolidated four page-level fallbacks into it, and the owner's 2026-08-06 ruling then made BOTH
/// its entry points render fixed copy for an unknown name — history included. Nothing there echoes
/// its input any more, so there is nothing to sanitize: the leak is closed by construction rather
/// than by scrubbing. Do not re-add a call there without re-opening that decision.</para>
///
/// <para>Both callers show the result to the user as a TRUST CUE — "this is where your dictation
/// goes", "this is the model that failed" — which is exactly the sort of string a bidi override can
/// visually spoof. One implementation rather than two, because a second copy is a second chance to
/// forget the Format-category characters (which are invisible, so a missing case looks fine).</para>
/// </summary>
internal static class UntrustedTextSanitizer
{
    /// <summary>
    /// Collapse everything unprintable to single spaces and cap the length.
    /// Returns null when nothing usable survives — callers render their own fallback rather than
    /// an empty label.
    /// </summary>
    /// <param name="value">Untrusted text.</param>
    /// <param name="maxLength">Hard cap; longer text is truncated and gains an ellipsis.</param>
    public static string? Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            // Control chars, ALL whitespace (incl. U+2028/U+2029/NBSP) and Unicode Format
            // characters (bidi controls, zero-width joiners) become plain spaces; runs collapse.
            var mapped = char.IsControl(c)
                || char.IsWhiteSpace(c)
                || global::System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                    == global::System.Globalization.UnicodeCategory.Format
                ? ' ' : c;
            if (mapped == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            sb.Append(mapped);
        }

        var clean = sb.ToString().Trim();
        if (clean.Length == 0)
            return null;
        if (clean.Length > maxLength)
            clean = clean[..maxLength].TrimEnd() + "…";
        return clean;
    }
}
