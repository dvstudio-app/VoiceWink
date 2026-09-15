namespace VoiceWink.Models;

/// <summary>
/// Vocabulary hints for a transcription request (PRM-3). Immutable by construction:
/// <see cref="FromTerms"/> takes a DEFENSIVE COPY, so a caller mutating its source
/// list after the fact cannot change an in-flight request (Codex final check —
/// a positional record over a caller-owned list is not actually immutable).
/// Each client composes its own provider-correct format from <see cref="Terms"/>:
/// Deepgram emits repeated keyterm/keywords query params, ElevenLabs emits repeated
/// keyterms multipart fields, OpenAI <c>gpt-transcribe</c> emits <c>keywords[]</c>.
/// <b>The Whisper family composes nothing — it ignores hints entirely since TRN-11</b>
/// (joining them into the initial prompt was measured to destroy transcripts on quiet
/// audio). <see cref="BuildWhisperProjection"/> and the token budget below are
/// consequently reachable only from tests; they are retained rather than deleted
/// because removing them reaches into <c>WhisperPromptTokenizer</c> and its
/// golden-pinned rank files, which is a separate change.
/// Term ORDER is meaningful — clients drop overflow deterministically from
/// the tail; HintTermComposition orders trigger phrases first, then Dictionary
/// words by insertion (Id).
/// </summary>
public sealed class TranscriptionHints
{
    private readonly IReadOnlyList<string> _terms;

    // ReadOnlyCollection wrapper, not the raw array: an array returned as
    // IReadOnlyList is castable back to string[]/IList and mutable mid-request
    // (Codex diff review). The wrapper throws on any mutation attempt.
    private TranscriptionHints(string[] terms, Services.Transcription.WhisperVocab vocab)
    {
        _terms = global::System.Array.AsReadOnly(terms);
        WhisperVocab = vocab;
    }

    private TranscriptionHints(
        IReadOnlyList<string> terms,
        Services.Transcription.WhisperVocab vocab,
        KeywordProjection? keywordProjection)
    {
        _terms = terms;
        WhisperVocab = vocab;
        KeywordProjection = keywordProjection;
    }

    public IReadOnlyList<string> Terms => _terms;

    /// <summary>
    /// The OpenAI <c>keywords[]</c> payload for THIS request, or null to send none.
    ///
    /// <para>A CARRIER, not a computation — and that distinction is the design. The pipeline
    /// decides once, before the NET-1 connect retry, whether keywords are eligible (model
    /// supports them, user toggle on) and attaches the result here; the client then sends exactly
    /// what it was handed and decides nothing. Two consequences fall out for free:</para>
    ///
    /// <para>A retry replays the same instance, so a toggle flipped mid-flight cannot make
    /// attempt 2 send something attempt 1 did not — and the echo gate, which matches against this
    /// same object's <c>IncludedTerms</c>, cannot disagree with the wire.</para>
    ///
    /// <para>And the file-transcription path (<c>AudioTranscribePage</c>), which has no echo gate
    /// at all, never attaches one — so it cannot send keywords. That suppression is structural:
    /// there is no flag there to forget to set.</para>
    /// </summary>
    public KeywordProjection? KeywordProjection { get; }

    /// <summary>Same terms and vocabulary, carrying <paramref name="projection"/>. Returns a new
    /// instance — these hints are immutable and shared with an in-flight request.</summary>
    public TranscriptionHints WithKeywordProjection(KeywordProjection? projection)
        => new(_terms, WhisperVocab, projection);

    /// <summary>
    /// The tokenizer vocabulary the Whisper-family projection budgets with —
    /// STAMPED at construction from the resolved transcriber's actual model
    /// identity (Codex round 5), so the pipeline's echo set and the client's wire
    /// prompt read ONE value and can never diverge. Irrelevant to Deepgram /
    /// ElevenLabs, which consume <see cref="Terms"/> directly.
    /// </summary>
    public Services.Transcription.WhisperVocab WhisperVocab { get; }

    /// <summary>
    /// Build hints from raw terms; null/whitespace entries are dropped. Returns null
    /// when nothing usable remains, so callers can pass the result straight through
    /// an optional parameter. <paramref name="vocab"/> stamps the Whisper tokenizer
    /// vocabulary (see <see cref="WhisperVocab"/>).
    /// </summary>
    public static TranscriptionHints? FromTerms(
        IEnumerable<string>? terms,
        Services.Transcription.WhisperVocab vocab = Services.Transcription.WhisperVocab.Multilingual)
    {
        if (terms == null)
            return null;

        var copy = terms.Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t.Trim())
                        .ToArray();
        return copy.Length == 0 ? null : new TranscriptionHints(copy, vocab);
    }

    // Whisper-family initial prompt is bounded — Groq documents a 224-token maximum
    // for the transcription `prompt` field and whisper.cpp's prompt context is
    // similarly capped, KEEPING THE LAST tokens on overflow — so an oversized prompt
    // loses its HEAD first, which under triggers-first ordering would silently cut
    // the trigger phrases AND desynchronize IncludedTerms from what the model saw
    // (the echo gate's soundness premise). Since 2026-07-18 the PRIMARY budget
    // counts REAL tokens via the actual Whisper tokenizer
    // (WhisperPromptTokenizer, golden-pinned against reference tiktoken output):
    // 220 tokens < 224, with typical vocabulary at ~3–5 bytes/token — the owner's
    // full 31-term set is 110 tokens and fits whole, where the old 1-byte-per-token
    // proxy fit 16. The BYTE budget remains as the deterministic FALLBACK whenever
    // exact counting is unavailable (rank file missing/corrupt) or not provably
    // reference-identical (astral letter/digit in a term): 220 bytes ⇒ ≤220 tokens
    // for ANY vocabulary (Codex round 4 — the safety primitive gets a guarantee on
    // BOTH paths, never an estimate). Triggers-first ordering (HintTermComposition)
    // means action phrases always fit and tail Dictionary words drop first, reported
    // per recording by MainViewModel's included/total log line. An oversized single
    // term is SKIPPED (continue), never truncated mid-term. Deepgram/ElevenLabs use
    // `Terms` directly with their own budgets — these caps are Whisper-family only.
    internal const int WhisperPromptTokenBudget = 220;
    internal const int WhisperPromptByteBudget = 220;

    /// <summary>
    /// Comma-joined form for the Whisper-family initial prompt (back-compat shim —
    /// callers that only need the string). Prefer <see cref="BuildWhisperProjection"/>
    /// when the exact included terms are also needed. Uses the stamped
    /// <see cref="WhisperVocab"/>.
    /// </summary>
    public string ToWhisperPrompt() => BuildWhisperProjection().Prompt;

    /// <summary>
    /// The Whisper-family prompt AND the exact ordered terms it contains, as ONE
    /// immutable projection (PRM-4 Option B). Pure and deterministic for a given
    /// (terms, stamped vocab, tokenizer availability) — the transport sends
    /// <c>Prompt</c> and the echo gate consumes <c>IncludedTerms</c>, so the two can
    /// never disagree even when a term was skipped for size or contains commas. Both
    /// call sites read the ONE stamped <see cref="WhisperVocab"/> (Codex round 5 —
    /// separately-derived variants could diverge on the missing-model fallback load).
    /// </summary>
    public WhisperPromptProjection BuildWhisperProjection()
    {
        var vocab = WhisperVocab;
        // Exact-token path. Whole-candidate recount per term (never sum-of-parts):
        // BPE merges can cross a ", " boundary when a term ends in symbols, so only
        // the exact final string's count proves the budget. Any term that can't be
        // counted exactly (astral letter/digit, or ranks unavailable — TryCountTokens
        // returns null) aborts to the byte path for the WHOLE composition, keeping
        // the projection deterministic and identical across call sites.
        var builder = new global::System.Text.StringBuilder();
        var included = new List<string>(_terms.Count);
        var exactPathValid = true;
        foreach (var term in _terms)
        {
            var candidate = builder.Length == 0 ? term : $"{builder}, {term}";
            var tokens = Services.Transcription.WhisperPromptTokenizer.TryCountTokens(candidate, vocab);
            if (tokens == null)
            {
                exactPathValid = false;
                break;
            }
            // continue (not break): a single oversized term is skipped so smaller
            // later terms still make it into the prompt.
            if (tokens > WhisperPromptTokenBudget)
                continue;
            builder.Clear();
            builder.Append(candidate);
            included.Add(term);
        }
        if (exactPathValid)
            return new WhisperPromptProjection(builder.ToString(), included);

        // Byte-budget fallback: guaranteed ≤ WhisperPromptByteBudget tokens for any
        // vocabulary (1 byte/token worst case).
        builder.Clear();
        included.Clear();
        var usedBytes = 0;
        foreach (var term in _terms)
        {
            var separatorBytes = builder.Length == 0 ? 0 : 2; // ", "
            var termBytes = global::System.Text.Encoding.UTF8.GetByteCount(term);
            if (usedBytes + separatorBytes + termBytes > WhisperPromptByteBudget)
                continue;
            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append(term);
            usedBytes += separatorBytes + termBytes;
            included.Add(term);
        }
        return new WhisperPromptProjection(builder.ToString(), included);
    }

    // OpenAI `keywords[]` bounds. NONE of these are vendor-documented — OpenAI publishes no count
    // or length cap — so they are ours, defensive, and deliberately conservative.
    //
    // The FORBIDDEN CHARACTERS are the exception: OpenAI documents that a keyword containing
    // '<', '>', CR or LF makes it "reject the entire request". Vocabulary storage permits those
    // characters today, so one such Dictionary word would break EVERY transcription for that user
    // until they found and removed it. A rejected term is dropped, never stripped — ElevenLabs'
    // recorded rule applies identically here: a mutated term is corrupted spelling data, and
    // sending a silently-altered spelling to a recognizer is worse than sending nothing.
    internal const int MaxKeywords = 100;
    internal const int KeywordAggregateByteBudget = 4096;
    private static readonly char[] ForbiddenKeywordChars = ['<', '>', '\r', '\n'];

    /// <summary>
    /// The terms that will ride an OpenAI <c>keywords[]</c> request, as ONE immutable projection.
    ///
    /// <para>Built ONCE per request by the pipeline — before the NET-1 connect retry — and used as
    /// BOTH the wire payload and the echo gate's match set. That is the whole point: the toggle
    /// that governs it is mutable and the operation can be replayed, so two separate reads of a
    /// pure projection would not be one source of truth. Same object, both consumers, both
    /// attempts (PRM-4 Option B, extended to this transport).</para>
    ///
    /// <para>Order is preserved from <c>Terms</c> (triggers first, then Dictionary words), and
    /// overflow drops from the tail. An oversized single term is SKIPPED rather than ending the
    /// scan, so smaller later terms still make it — same rule as the Whisper projection.</para>
    /// </summary>
    public KeywordProjection BuildKeywordProjection(
        int maxKeywords = MaxKeywords,
        int aggregateByteBudget = KeywordAggregateByteBudget)
    {
        var included = new List<string>(global::System.Math.Min(_terms.Count, maxKeywords));
        var rejected = 0;
        var dropped = 0;
        var usedBytes = 0;

        foreach (var term in _terms)
        {
            if (included.Count >= maxKeywords)
            {
                dropped++;
                continue;
            }

            if (term.IndexOfAny(ForbiddenKeywordChars) >= 0)
            {
                rejected++;
                continue;
            }

            var termBytes = global::System.Text.Encoding.UTF8.GetByteCount(term);
            if (usedBytes + termBytes > aggregateByteBudget)
            {
                dropped++;
                continue;
            }

            usedBytes += termBytes;
            included.Add(term);
        }

        return new KeywordProjection(included, rejected, dropped);
    }
}

/// <summary>
/// The exact ordered terms an OpenAI <c>keywords[]</c> request carries, plus count-only drop
/// bookkeeping. <see cref="IncludedTerms"/> is simultaneously what goes on the wire and the only
/// set a generative recognizer could echo back, so the transport and the echo gate cannot
/// disagree about what was sent.
///
/// <para><see cref="RejectedCount"/> counts terms refused for containing a character OpenAI
/// rejects requests over; <see cref="DroppedCount"/> counts terms that did not fit the count cap
/// or byte budget. Both are counts on purpose — term VALUES are user vocabulary and never reach a
/// log line.</para>
/// </summary>
public sealed class KeywordProjection
{
    public IReadOnlyList<string> IncludedTerms { get; }
    public int RejectedCount { get; }
    public int DroppedCount { get; }

    public KeywordProjection(IReadOnlyList<string> includedTerms, int rejectedCount, int droppedCount)
    {
        // Defensive copy + read-only wrap, for the same reason WhisperPromptProjection does it:
        // an IReadOnlyList backed by an array is castable back to a mutable list and could be
        // changed after the gate captured it but before the request goes out.
        IncludedTerms = global::System.Array.AsReadOnly((includedTerms ?? global::System.Array.Empty<string>()).ToArray());
        RejectedCount = rejectedCount;
        DroppedCount = droppedCount;
    }

    public bool HasTerms => IncludedTerms.Count > 0;
}

/// <summary>
/// PRM-4: the exact Whisper-family bias prompt string plus the ordered terms actually
/// included in it. <see cref="IncludedTerms"/> is the ONLY set that can be echoed by a
/// generative recognizer, so the echo gate matches against it — never the full
/// composed hint list, which may have been head-truncated.
/// </summary>
public sealed class WhisperPromptProjection
{
    public string Prompt { get; }
    public IReadOnlyList<string> IncludedTerms { get; }

    public WhisperPromptProjection(string prompt, IReadOnlyList<string> includedTerms)
    {
        Prompt = prompt;
        // Defensive copy + read-only wrap: IncludedTerms must not be castable back to
        // a mutable list and changed independently of Prompt (Codex diff review).
        IncludedTerms = global::System.Array.AsReadOnly((includedTerms ?? global::System.Array.Empty<string>()).ToArray());
    }
}
