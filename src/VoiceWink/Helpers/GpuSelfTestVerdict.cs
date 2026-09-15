using System.Globalization;
using System.Text;

namespace VoiceWink.Helpers;

/// <summary>What one GPU self-test decode established. <see cref="Unknown"/> is the outcome of a
/// decode that did not RUN to a judgeable result — transport failure, cancellation, a clip with
/// no expected words — and it never pins anything. <see cref="Inconclusive"/> (TRN-64) is the
/// one timeout that DOES pin: Whisper's self-test did not reach a verdict within its budget on a
/// GPU-engaged process, so the decode the user is waiting on cannot be allowed to run there
/// either — it refuses for the session and the engine starts on the CPU next time. Parakeet
/// never persists it (its verdict forms off the hot path; an inconclusive warm-up leaves the
/// child on Auto, today's rule). Public only so xUnit theory rows can take it as a parameter;
/// nothing outside this assembly consumes it.</summary>
public enum GpuSelfTestOutcome
{
    Unknown,
    Pass,
    Fail,
    Inconclusive,
    /// <summary>TRN-64 PR 2: the GPU decodes correctly but too slowly — the faster of two timed
    /// clip decodes still took more than <see cref="GpuSpeedFloor.Multiplier"/>× the audio's own
    /// duration. Pins the engine to the CPU like <see cref="Fail"/>; unlike Fail, a Whisper decode
    /// already in flight proceeds (its text is right) and the pin applies from the next start.</summary>
    Slower,
}

/// <summary>The judgement and the numbers it was made from — the log line prints these, never the
/// decoded words.</summary>
internal readonly record struct GpuSelfTestJudgement(
    GpuSelfTestOutcome Outcome,
    double Recall,
    int MatchedWords,
    int ExpectedWords);

/// <summary>
/// TRN-50: the expected-output check behind the GPU self-test — does a decode of the golden clip
/// contain enough of the words we know it says? Pure, so the whole case table runs without a
/// model or a GPU.
///
/// <para><b>Recall, deliberately not word error rate.</b> The measured failure (owner's ARM64
/// laptop, 2026-09-03: an Adreno Vulkan driver that loads cleanly and decodes real speech to
/// NOTHING) scores 0 here; garbage text has near-zero overlap with a scripted sentence; and a
/// healthy engine scored 1.00 on the synthesised pangram probes on both compute paths and
/// ≥ 0.89 on the shipped clip — the owner's own recording, where every engine but Whisper small
/// mishears "brown" the same way on GPU and CPU alike, so the miss is the recording, never a
/// device (measured 2026-09-03; the rules row carries the table). Recall lets an engine that ADDS words
/// (a hallucinated filler, a repeated token) still pass — extra words are not the failure this
/// test exists for — while a missing half fails it. A multiset match, so a phrase with a repeated
/// word needs both copies.</para>
///
/// <para><b>The threshold has a stated margin, not a derivation.</b> Half the words: three words
/// short of the shipped clip's measured 8-of-9 baseline before a healthy driver reads as broken,
/// and four words present before a broken one reads as healthy. Move it only with a new
/// measurement on both sides.</para>
///
/// <para><b>Normalisation is the minimum that keeps two correct decodes from disagreeing:</b>
/// case, punctuation, and number words versus digits (Whisper writes "1 2 3" where Parakeet
/// writes "one two three"). Nothing language-specific beyond that; the clip is English.</para>
/// </summary>
internal static class GpuSelfTestVerdict
{
    /// <summary>Recall at or above which a decode passes. See the type remarks for the margin.</summary>
    internal const double PassRecall = 0.5;

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.Ordinal)
    {
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
        ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10",
    };

    /// <summary>Judge a decode against the clip's expected words.</summary>
    /// <param name="decoded">The engine's text, or null/empty for a decode that returned nothing.</param>
    /// <param name="expectedWords">The clip's words as authored (any case, punctuation tolerated).</param>
    internal static GpuSelfTestJudgement Judge(string? decoded, IReadOnlyList<string> expectedWords)
    {
        var expected = Normalize(string.Join(' ', expectedWords));
        if (expected.Count == 0)
        {
            // A clip with no words cannot judge anything — and must never manufacture a FAIL.
            return new GpuSelfTestJudgement(GpuSelfTestOutcome.Unknown, 0, 0, 0);
        }

        var got = Normalize(decoded ?? string.Empty);
        var pool = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in got)
        {
            pool[token] = pool.TryGetValue(token, out var n) ? n + 1 : 1;
        }

        var matched = 0;
        foreach (var token in expected)
        {
            if (pool.TryGetValue(token, out var n) && n > 0)
            {
                pool[token] = n - 1;
                matched++;
            }
        }

        var recall = matched / (double)expected.Count;
        var outcome = matched > 0 && recall >= PassRecall ? GpuSelfTestOutcome.Pass : GpuSelfTestOutcome.Fail;
        return new GpuSelfTestJudgement(outcome, recall, matched, expected.Count);
    }

    /// <summary>Lower-case, letters/digits only, whitespace-split, number words mapped to digits.
    /// Internal for the case table.</summary>
    internal static List<string> Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var tokens = new List<string>();
        foreach (var raw in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            tokens.Add(NumberWords.TryGetValue(raw, out var digit) ? digit : raw);
        }
        return tokens;
    }

    /// <summary>The one-line form the log and the advisory print: counts and a ratio, never words.</summary>
    internal static string Describe(GpuSelfTestJudgement j)
        => string.Create(CultureInfo.InvariantCulture, $"recall {j.Recall:F2} ({j.MatchedWords} of {j.ExpectedWords} words)");

    /// <summary>May a Whisper decode of the ENGLISH golden clip be judged at all? Only when the
    /// processor decodes as auto (the clip detects as English) or English. A processor pinned to
    /// any other language is built <c>WithLanguage</c> at load and decodes the clip AS that
    /// language by construction, so its low recall proves the pin, never a broken GPU — judging it
    /// would persist a GPU-confirmed FAIL on a healthy machine, and the re-arm would fail
    /// identically (Kimi diff r1 Blocker). Null/blank read as auto, which is how the service
    /// normalises them. Parakeet's transport sends no language and never consults this.</summary>
    internal static bool LanguageAllowsJudgement(string? language)
        => string.IsNullOrWhiteSpace(language)
           || language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
           || language.Trim().Equals("en", StringComparison.OrdinalIgnoreCase);
}
