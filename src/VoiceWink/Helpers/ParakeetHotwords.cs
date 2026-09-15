using System.Security.Cryptography;
using System.Text;

namespace VoiceWink.Helpers;

/// <summary>
/// The encoded hotword list for one recognizer build. <see cref="FileContent"/> is what sherpa's
/// <c>HotwordsFile</c> receives verbatim; <see cref="ContentHash"/> identifies one encoded list so
/// a future caller can skip the ~3.1 s recognizer rebuild when the vocabulary has not changed —
/// nothing reads it today (the app wiring was reverted; see the class remarks).
/// </summary>
internal sealed record ParakeetHotwordsPlan(
    string FileContent,
    int EncodedTermCount,
    int DroppedTermCount,
    string ContentHash,
    IReadOnlyList<string> EncodedTerms);

/// <summary>
/// TRN-26: encode Dictionary/vocabulary terms into sherpa-onnx's hotwords-file format for the
/// Parakeet TDT bundle.
///
/// <para><b>MEASUREMENT INSTRUMENT — the app deliberately does NOT call this.</b> The full wiring
/// was built and then measured on owner ground truth (2026-08-23): biasing requires
/// <c>modified_beam_search</c>, and that decode alone (one inert term) regressed the corpus
/// 152→214 total errors (deletions 96→168), while the real 51-term run scored 196 with ~zero
/// term recall — so the feature was refused at the gate; the numbers and reopen conditions live
/// on the TRN-26 card and in <c>ParakeetTranscriptionService</c>'s remarks. The sole caller is
/// <c>tools/parakeet-long-audio</c>'s <c>--hotwords</c> flag, which is what produced the verdict
/// and what retests a future sherpa release in one command.</para>
///
/// <para><b>The recipe is the TRN-1 spike's, measured not designed</b>
/// (<c>docs/plans/2026-08-02-trn1-parakeet-spike/50-decision.md</c>, correction box (b)): the
/// bundle ships no <c>bpe.model</c>, so sherpa cannot segment words itself — but supplying the
/// pieces WITHOUT the <c>▁</c> word-start marker, segmented by greedy longest-match against the
/// bare piece inventory of <c>tokens.txt</c>, encoded a realistic 27-term Dictionary (Dutch,
/// umlauts, accents, hyphens, multi-word triggers) with zero errors.</para>
///
/// <para><b>A term either encodes COMPLETELY or is dropped — never partially.</b> That is the
/// spike's sharpest hazard, not a preference: a hand-built list whose pieces failed to encode
/// turned a whole transcript into <c>T 1 T 1 T</c>. Malformed biasing does not merely fail to
/// help; it can destroy the decode. Dropping is safe (the word simply gets no boost) and the
/// caller logs the count.</para>
///
/// <para>Pure: no I/O, no logging (the caller logs the counts), no native code — the token
/// inventory arrives as lines so tests exercise every branch with a synthetic vocabulary.</para>
/// </summary>
internal static class ParakeetHotwords
{
    /// <summary>SentencePiece's word-start marker (U+2581). Pieces are supplied to sherpa WITHOUT
    /// it — its presence in a hotword line changes how sherpa splits the phrase (the spike measured
    /// this the hard way).</summary>
    private const char WordStartMarker = '▁';

    /// <summary>
    /// Encode vocabulary terms against a bundle's <c>tokens.txt</c>. Returns null when nothing
    /// encodes — the caller then builds the recognizer exactly as it does today (greedy decode,
    /// no hotwords file), which is what keeps the no-vocabulary path byte-identical.
    /// </summary>
    internal static ParakeetHotwordsPlan? Encode(
        IReadOnlyList<string>? terms, IEnumerable<string> tokensFileLines)
    {
        if (terms is null || terms.Count == 0) return null;

        var inventory = BuildBareInventory(tokensFileLines);
        if (inventory.Count == 0) return null;

        var maxPieceLength = inventory.Max(p => p.Length);

        var lines = new List<string>();
        // The unique ACCEPTED source terms, one per emitted line — the only honest basis for any
        // per-term metric downstream: a raw-input-side count would tally "meridian" and "MERIDIAN"
        // as two while the file biases one phrase (Codex verification round).
        var encodedTerms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;
        foreach (var raw in terms)
        {
            var term = raw?.Trim() ?? string.Empty;
            if (term.Length == 0) continue;               // blank input is nothing, not a drop

            // A hotwords file is LINE-oriented; a term carrying a line break would smuggle a
            // second (partial) hotword line in — the exact malformed-list shape the spike saw
            // corrupt a decode. Vocabulary storage permits these characters, so reject here.
            if (term.Contains('\r') || term.Contains('\n')) { dropped++; continue; }

            var pieces = TryEncodeTerm(term, inventory, maxPieceLength);
            if (pieces is null) { dropped++; continue; }  // whole term, never a partial line

            // Dedup on the ENCODED line, not the raw term: spellings that normalize to one
            // phrase ("ab cd" / "ab  cd" / "AB CD") are one bias, and counting them separately
            // over-reported what was actually biased in the run's own record (self-review F1).
            var line = string.Join(' ', pieces);
            if (!seen.Add(line)) continue;

            lines.Add(line);
            encodedTerms.Add(term);
        }

        if (lines.Count == 0) return null;

        var content = string.Join('\n', lines) + "\n";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new ParakeetHotwordsPlan(content, lines.Count, dropped, hash, encodedTerms);
    }

    /// <summary>
    /// The bare piece inventory: every <c>tokens.txt</c> piece that exists LITERALLY without the
    /// word-start marker — the continuation pieces (3,756 of the v3 bundle's 8,193).
    ///
    /// <para><b>▁-carrying pieces are EXCLUDED, not stripped — measured live, not read off the
    /// spike.</b> sherpa's <c>EncodeBase</c> looks each supplied piece up verbatim in
    /// <c>tokens.txt</c>: a word-initial piece like <c>▁par</c> has no bare <c>par</c> entry, so a
    /// segmentation that used it produced <c>Cannot find ID for token par</c> and sherpa SKIPPED
    /// the whole line — the term silently lost its boost (the first implementation stripped the
    /// marker into a union inventory and 20 of the owner's 51 terms were skipped exactly this
    /// way). Continuation pieces alone spell any word the model can write, which is what the
    /// spike's "3756 bare vocabulary pieces" were.</para>
    ///
    /// <para>Control tokens (<c>&lt;unk&gt;</c>, <c>&lt;|pnc|&gt;</c>, Canary's ride-along
    /// language ids…) are excluded — they are decoder controls, not text a spoken word could
    /// segment into, and matching one would bias toward emitting it.</para>
    /// </summary>
    private static HashSet<string> BuildBareInventory(IEnumerable<string> tokensFileLines)
    {
        var inventory = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in tokensFileLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Format: "<piece> <id>". The piece never contains a space (SentencePiece uses ▁),
            // so the first whitespace split is the whole piece.
            var end = line.IndexOf(' ');
            var piece = end < 0 ? line : line[..end];
            if (piece.Length == 0) continue;
            if (piece.StartsWith('<') && piece.EndsWith('>')) continue;
            if (piece[0] == WordStartMarker) continue;   // word-initial-only: sherpa cannot look it up bare

            // A piece carrying whitespace or an interior marker would smuggle exactly the
            // malformed-line shape the encoder exists to make impossible. The shipped tokens.txt
            // contains no such piece — but the complete-or-dropped guarantee must hold by
            // construction, not by inventory content (self-review F3).
            if (piece.Any(c => char.IsWhiteSpace(c) || c == WordStartMarker)) continue;

            inventory.Add(piece);
        }

        return inventory;
    }

    /// <summary>
    /// Greedy longest-match segmentation of one term. Words are matched independently (the
    /// marker-free form sherpa expects carries no word-boundary information, so per-word
    /// segmentation is the phrase form the spike validated). Null when ANY word fails.
    /// </summary>
    private static List<string>? TryEncodeTerm(
        string term, HashSet<string> inventory, int maxPieceLength)
    {
        var pieces = new List<string>();
        foreach (var word in term.ToLowerInvariant()
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = 0;
            while (at < word.Length)
            {
                var take = Math.Min(maxPieceLength, word.Length - at);
                while (take > 0 && !inventory.Contains(word.Substring(at, take))) take--;
                if (take == 0) return null;               // this word cannot encode — drop the term

                pieces.Add(word.Substring(at, take));
                at += take;
            }
        }

        return pieces.Count > 0 ? pieces : null;
    }
}
