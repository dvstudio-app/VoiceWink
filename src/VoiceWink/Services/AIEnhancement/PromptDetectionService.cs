using System.Text.RegularExpressions;
using Serilog;
using VoiceWink.Models;

namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// Detects trigger words in transcribed text to auto-select AI prompts.
/// </summary>
public sealed class PromptDetectionService
{
    private static ILogger Logger => Log.ForContext<PromptDetectionService>();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    public sealed class DetectionResult
    {
        public bool ShouldOverridePrompt { get; init; }
        public CustomPrompt? OverridePrompt { get; init; }
        public string ProcessedText { get; init; } = string.Empty;
        public string? DetectedTriggerWord { get; init; }
    }

    /// <summary>
    /// Analyze transcribed text for trigger words. If found, returns the matching prompt
    /// and the text with the trigger word stripped.
    /// PRM-4 Option B: the bias-prompt ECHO defence lives in the pipeline
    /// (<see cref="VoiceWink.Helpers.TriggerEchoGate"/> + the generative-transport gate
    /// in MainViewModel), NOT here — a token-union guard in this method over-suppressed
    /// real requests (Codex). This method keeps only the local empty-remainder guard:
    /// a transcript that is ONLY a trigger phrase has nothing to process, so it never
    /// overrides — and once ANY prompt's trigger strips to empty, scanning STOPS (a
    /// shorter overlapping trigger of this or another prompt must not resurrect it as
    /// apparent content).
    /// </summary>
    public DetectionResult Analyze(string text, IReadOnlyList<CustomPrompt> prompts)
    {
        // GLOBAL trigger-only pre-scan (Codex diff review High): if ANY prompt's
        // trigger consumes the ENTIRE transcript, it is a trigger-only utterance —
        // refuse before per-prompt precedence, so a SHORTER trigger in an earlier
        // prompt can't win and leave a longer trigger's tail as apparent content
        // (prompt-1 "hey" vs prompt-2 "hey assistant", transcript "hey assistant").
        // Connector-aware for IMAGE prompts (Codex round 5): "generate an image of"
        // is trigger-only too — without this, an earlier prompt's shorter trigger
        // could resurrect "an image of" as content.
        foreach (var prompt in prompts)
        {
            foreach (var trigger in prompt.TriggerWords)
            {
                var t = trigger?.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                var stripped = TryStripTriggerWord(text, t, stripLeadingConnector: prompt.IsImageGeneration);
                if (stripped != null && string.IsNullOrWhiteSpace(stripped))
                {
                    Logger.Information("Trigger consumes the whole transcript — not switching prompts: userTerm={TriggerWord}", Helpers.LogValueSanitizer.SingleLine(t));
                    return new DetectionResult { ShouldOverridePrompt = false, ProcessedText = text };
                }
            }
        }

        foreach (var prompt in prompts)
        {
            if (prompt.TriggerWords.Count == 0) continue;

            // Most-specific-first by CANONICAL shape, not raw characters (Codex
            // round 4): raw length let a whitespace-padded short trigger outrank a
            // genuinely longer one, leaving part of the longer trigger as content.
            // Token count first (more words = more specific), canonical length as
            // the tiebreak.
            var sorted = prompt.TriggerWords
                .Select(w => w.Trim())
                .Where(w => w.Length > 0)
                .Select(w => (Trigger: w, Tokens: VoiceWink.Helpers.TriggerEchoGate.TokenizeSpans(w)))
                .Where(t => t.Tokens.Count > 0)
                .OrderByDescending(t => t.Tokens.Count)
                .ThenByDescending(t => t.Tokens.Sum(s => s.Token.Length))
                .Select(t => t.Trigger)
                .ToList();

            foreach (var trigger in sorted)
            {
                // Image prompts also shed ONE leading connector word ("generate an
                // image OF a fox" → description "a fox") — dictation naturally
                // includes it, the image prompt shouldn't (owner request 2026-07-18).
                var stripped = TryStripTriggerWord(text, trigger,
                    stripLeadingConnector: prompt.IsImageGeneration,
                    leadingOnly: prompt.IsImageGeneration);
                if (stripped == null)
                    continue;

                // Empty remainder = a trigger-only utterance: nothing to process. STOP
                // scanning entirely (do not fall through to a shorter overlapping
                // trigger, in this or a later prompt, which would treat the longer
                // trigger's tail as content — Codex round-1/round-2).
                if (string.IsNullOrWhiteSpace(stripped))
                {
                    Logger.Information("Trigger matched with no remaining content — not switching prompts: userTerm={TriggerWord}", Helpers.LogValueSanitizer.SingleLine(trigger));
                    return new DetectionResult { ShouldOverridePrompt = false, ProcessedText = text };
                }

                // {TriggerWord}/{Prompt} carry user/transcript-derived text — both property
                // names are in LogRedactionEnricher.RedactedPropertyNames so the local file
                // keeps full detail while the Sentry sub-logger + breadcrumb scrub redact them.
                Logger.Information("Trigger detected, switching prompt: userTerm={TriggerWord} | {Prompt}",
                    Helpers.LogValueSanitizer.SingleLine(trigger), Helpers.LogValueSanitizer.SingleLine(prompt.Title));
                return new DetectionResult
                {
                    ShouldOverridePrompt = true,
                    OverridePrompt = prompt,
                    ProcessedText = stripped,
                    DetectedTriggerWord = trigger
                };
            }
        }

        return new DetectionResult
        {
            ShouldOverridePrompt = false,
            ProcessedText = text
        };
    }

    // Connector words a LEADING image trigger naturally attracts ("generate an image
    // OF/SHOWING/ABOUT …"). Deliberately small and image-only: text prompts must
    // never lose words ("hey assistant, OF course…"). SHARED with the trigger-only
    // pre-scan and the image echo gate via TriggerEchoGate.LeadingImageConnectors
    // (Codex round 5 — a connector stripped here but not there re-appears as
    // phantom content).
    private static HashSet<string> LeadingConnectors
        => VoiceWink.Helpers.TriggerEchoGate.LeadingImageConnectors;

    private static string? TryStripTriggerWord(
        string text, string trigger, bool stripLeadingConnector = false, bool leadingOnly = false)
    {
        // IMAGE prompts match LEADING ONLY (owner UAT 2026-08-02). A dictation that merely
        // MENTIONED image generation and happened to end on the trigger phrase opened the image
        // dialog unasked — the user was talking about the feature, not invoking it.
        //
        // Why this is safe for images and would NOT be for the others: an image trigger is
        // inherently a preamble, because the subject has to follow it ("create an image OF a fox" —
        // the connector strip right below exists for exactly that shape). A trailing image trigger
        // means the description came first, which is the rarer phrasing. Conversational triggers
        // are the opposite: "what is the capital of France, hey assistant" is the NATURAL form and
        // the reason trailing is tried first, so they keep both positions.
        //
        // Deliberately not solved by length or ratio heuristics: "create an image of a highly
        // detailed cyberpunk street at night with…" is both long and entirely legitimate, so a
        // "trigger is a small fraction of the text" rule would break the primary use case while
        // only guessing at this one. Reversible in one line if trailing image triggers turn out to
        // matter — remove the leadingOnly argument at the call site.
        if (!leadingOnly)
        {
            // Try trailing first (more natural: "what is the capital of France hey assistant")
            var result = StripTrailing(text, trigger);
            if (result != null)
            {
                // Also strip leading if present in both positions
                var both = StripLeading(result, trigger, stripLeadingConnector);
                return both ?? result;
            }
        }

        // Try leading ("hey assistant what is the capital of France")
        var leading = StripLeading(text, trigger, stripLeadingConnector);
        if (leading != null)
        {
            if (leadingOnly) return leading;
            var both = StripTrailing(leading, trigger);
            return both ?? leading;
        }

        return null;
    }

    // Token-based matching (UAT 2026-07-18): the transcription freely inserts
    // punctuation INSIDE a spoken trigger phrase — Groq Whisper produced
    // "Hey, assistant." for the trigger "hey assistant", which a literal
    // StartsWith/EndsWith can never match. Matching on lowercased token sequences
    // (split only on whitespace/ASR punctuation — see TriggerEchoGate.TokenizeSpans)
    // makes inserted punctuation irrelevant; the remainder is sliced from the
    // ORIGINAL string and then gets the same boundary-punctuation trim + capitalize
    // as before. Word-boundary safety falls out of token equality: "assistant"
    // never equals "assist".
    private static string? StripLeading(string text, string trigger, bool stripLeadingConnector = false)
    {
        var textTokens = VoiceWink.Helpers.TriggerEchoGate.TokenizeSpans(text);
        var trigTokens = VoiceWink.Helpers.TriggerEchoGate.TokenizeSpans(trigger);
        if (trigTokens.Count == 0 || textTokens.Count < trigTokens.Count)
            return null;
        for (var i = 0; i < trigTokens.Count; i++)
            if (!string.Equals(textTokens[i].Token, trigTokens[i].Token, StringComparison.Ordinal))
                return null;

        // Image prompts: consume ONE connector word right after the trigger. Done on
        // the token index (before slicing) so "generate an image of" alone strips to
        // EMPTY and the trigger-only guard refuses instead of firing with "Of".
        var next = trigTokens.Count;
        if (stripLeadingConnector && next < textTokens.Count && LeadingConnectors.Contains(textTokens[next].Token))
            next++;

        // Slice at the NEXT token's start, not the matched token's end (Codex round
        // 4): the tokenizer recognizes more separators than the legacy regex below
        // (em-dash, quotes, brackets), and slicing at End left them on the remainder
        // ("Hey—assistant—do it" → "—do it"). Span boundaries drop every separator
        // BETWEEN trigger and remainder by construction.
        var remaining = next < textTokens.Count
            ? text[textTokens[next].Start..]
            : "";

        // Strip leading punctuation and whitespace
        remaining = Regex.Replace(remaining, @"^[,\.!\?;:\s]+", "", RegexOptions.None, RegexTimeout).Trim();

        // Capitalize first letter
        if (remaining.Length > 0)
            remaining = char.ToUpper(remaining[0]) + remaining[1..];

        return remaining;
    }

    private static string? StripTrailing(string text, string trigger)
    {
        var textTokens = VoiceWink.Helpers.TriggerEchoGate.TokenizeSpans(text);
        var trigTokens = VoiceWink.Helpers.TriggerEchoGate.TokenizeSpans(trigger);
        if (trigTokens.Count == 0 || textTokens.Count < trigTokens.Count)
            return null;
        var offset = textTokens.Count - trigTokens.Count;
        for (var i = 0; i < trigTokens.Count; i++)
            if (!string.Equals(textTokens[offset + i].Token, trigTokens[i].Token, StringComparison.Ordinal))
                return null;

        // Slice at the PREVIOUS token's end (Codex round 4, mirror of StripLeading):
        // drops every recognized separator between remainder and trigger, not just
        // the legacy regex subset.
        var remaining = offset > 0 ? text[..textTokens[offset - 1].End] : "";
        remaining = Regex.Replace(remaining, @"[,\.!\?;:\s]+$", "", RegexOptions.None, RegexTimeout).Trim();

        if (remaining.Length > 0)
            remaining = char.ToUpper(remaining[0]) + remaining[1..];

        return remaining;
    }
}
