namespace VoiceWink.Helpers;

/// <summary>Which UIA source produced a text readback (PST-6). Sources are never
/// mixed across a before/after pair — comparing a Value read against a
/// TextPattern read would manufacture phantom changes.</summary>
internal enum PasteReadbackSource
{
    /// <summary>ValuePattern value on an element whose ValuePattern is available
    /// AND writable. A read-only Value is NEVER a content source — live probe
    /// evidence (Claude Desktop, 2026-07-29): Chromium Documents expose a
    /// read-only ValuePattern carrying the page URL, not the document text.</summary>
    WritableValue,
    /// <summary>TextPattern document-range text — the only real content source on
    /// Chromium Group/Document composers (same live probe).</summary>
    TextPattern
}

/// <summary>
/// One bounded text readback of the LIVE focused element (PST-6 insertion
/// verification). <see cref="CapHit"/> means the TextPattern read filled the
/// <see cref="PasteInsertionVerification.ReadbackCapCodeUnits"/> window — an
/// append beyond the window is then invisible, so a capped baseline can never
/// prove non-insertion. <see cref="RuntimeId"/> binds the pair to one element.
/// </summary>
internal readonly record struct PasteTextReadback(
    PasteReadbackSource Source,
    string Text,
    int[]? RuntimeId,
    bool CapHit);

/// <summary>Verdict of one before/after readback comparison.</summary>
internal enum PasteInsertionVerdict
{
    Inserted,
    NotInserted,
    /// <summary>Cannot be proven either way — callers FAIL OPEN, keeping today's
    /// success semantics, except where proof was required.</summary>
    Unknown
}

/// <summary>
/// Terminal delivery outcome of the paste delivery check (PST-6, reduced by PST-16).
/// <para>There is deliberately no <c>NotDelivered</c>. The readback source can lag behind the
/// target, so the check can prove that the payload ARRIVED but never that it is absent —
/// claiming non-delivery is what used to authorise a second insertion, and what would
/// otherwise make the pill tell the user to paste a copy they already have.</para>
/// </summary>
internal enum PasteDeliveryOutcome
{
    Delivered,
    /// <summary>Delivery could not be confirmed — never presented as success: no Enter,
    /// no deferred clipboard restore, and the text is re-asserted on the clipboard.</summary>
    DeliveryUncertain
}

/// <summary>
/// Pure decision core for PST-6 post-paste insertion verification. All rules
/// are fail-open: a `NotInserted` verdict requires two readable, identity-bound,
/// uncapped, source-matched reads that are EQUAL and do not contain the pasted
/// head. Round-2 rule: equal-but-contains-head proves NO mutation happened, so
/// it is <see cref="PasteInsertionVerdict.Unknown"/>, never Inserted (an old
/// occurrence must not vouch for a new paste).
/// </summary>
internal static class PasteInsertionVerification
{
    /// <summary>Head length (normalized code units) for the containment rule.</summary>
    public const int HeadLength = 24;

    /// <summary>TextPattern readback window. A read that fills the window sets
    /// <see cref="PasteTextReadback.CapHit"/> and disables NotInserted verdicts.</summary>
    public const int ReadbackCapCodeUnits = 16384;

    /// <summary>
    /// Whitespace-normalize for comparison: every whitespace RUN collapses to a
    /// single space, leading/trailing trimmed. Editors legitimately rewrite
    /// newlines/nbsp on paste; a transform must read as "changed", and the
    /// containment probe must not fail on whitespace shape alone.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new global::System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Verdict over one before/after readback pair. Every missing or
    /// mismatched signal is <see cref="PasteInsertionVerdict.Unknown"/>:
    /// null reads, source mismatch, missing runtime ID on either side (round-2
    /// rule: identity must be provable, not assumed), identity change between
    /// reads, empty normalized head, or a capped baseline (an append beyond the
    /// window is invisible).
    /// </summary>
    /// <summary>
    /// Payload-SPECIFIC verdict for paths that must PROVE our text arrived, not
    /// merely that something changed (Codex diff review round 5). `Decide`'s
    /// "any same-element mutation = Inserted" is right for the ordinary path — it
    /// is generous on purpose so verification never invents failures — but on a
    /// relaxed-block path it is exploitable by any unrelated change: an incoming
    /// chat message rendering into the composer's document, or an external process
    /// replacing the clipboard so the repaste inserts SOMEONE ELSE'S text. Both
    /// would read as proof. Here a change counts only when the payload's normalized
    /// head actually appears (and did not already), so an unrelated mutation
    /// degrades to <see cref="PasteInsertionVerdict.Unknown"/> — which callers on
    /// this path map to uncertain, never success.
    /// </summary>
    public static PasteInsertionVerdict DecidePayloadSpecific(
        PasteTextReadback? before, PasteTextReadback? after, string pastedText)
    {
        var verdict = Decide(before, after, pastedText);
        if (verdict != PasteInsertionVerdict.Inserted)
            return verdict;

        // Inserted ⇒ both reads exist, share a source, and are identity-matched.
        var nb = Normalize(before!.Value.Text);
        var na = Normalize(after!.Value.Text);
        var payload = Normalize(pastedText);
        if (payload.Length == 0)
            return PasteInsertionVerdict.Unknown;

        // FULL exact-insertion proof, not a prefix/head count (Codex diff review
        // round 6): counting head occurrences accepted content that merely STARTS
        // with our text — dictating "yes" would be proven by "yes — approved"
        // arriving from an unrelated writer or a replaced clipboard. Removing one
        // complete payload occurrence must reproduce the baseline exactly.
        // Deliberately strict: this runs only where a failure was already proven or
        // a safety block was relaxed, and its negative result is an honest
        // "uncertain" warning — never a lost paste.
        // BOTH reads must be uncapped (Codex diff review round 7): a truncated AFTER
        // read can hide the suffix of wrong content that merely begins with our
        // payload, leaving a window that looks exactly like "baseline + payload" and
        // passes the removal proof.
        return !before.Value.CapHit && !after!.Value.CapHit
            && ArrivalReproducesBaseline(na, payload, nb)
            ? PasteInsertionVerdict.Inserted
            : PasteInsertionVerdict.Unknown;
    }

    /// <summary>
    /// Did our payload ARRIVE — by insertion, or by replacing a selection?
    /// <para><see cref="RemovalReproducesBaseline"/> alone answers only the first, because it
    /// requires the remainder to reproduce the WHOLE baseline. A paste over selected text is an
    /// ordinary paste (dictating with text selected replaces it), and there the baseline's middle
    /// is gone by design, so the removal proof fails on a paste that plainly worked (Codex diff
    /// round 1 blocker, PST-15). That mattered the moment PST-15 made a failed proof DECLINE: the
    /// user saw "paste from clipboard with Ctrl+V" over text that had already landed, and doing
    /// as told produced the second copy the whole change exists to prevent.</para>
    /// <para>The replacement arm accepts <c>after = prefix + payload + suffix</c> only when the
    /// baseline both STARTS with that prefix and ENDS with that suffix — i.e. the payload stands
    /// exactly where a contiguous run of the baseline used to be. It is still an exact-shape
    /// proof, not a containment test, so round 6's counterexample stays rejected: an unrelated
    /// writer's "yes — approved" over an empty baseline leaves the suffix " — approved", which no
    /// empty baseline ends with. The length guard rejects a prefix/suffix pair that could not have
    /// come from this baseline at all.</para>
    /// </summary>
    public static bool ArrivalReproducesBaseline(string after, string payload, string before)
    {
        if (RemovalReproducesBaseline(after, payload, before))
            return true;

        if (payload.Length == 0 || after.Length < payload.Length)
            return false;

        var index = 0;
        while ((index = after.IndexOf(payload, index, StringComparison.Ordinal)) >= 0)
        {
            var prefix = after[..index];
            var suffix = after[(index + payload.Length)..];
            if (prefix.Length + suffix.Length <= before.Length
                && before.StartsWith(prefix, StringComparison.Ordinal)
                && before.EndsWith(suffix, StringComparison.Ordinal))
                return true;

            index++;
        }

        return false;
    }

    public static PasteInsertionVerdict Decide(
        PasteTextReadback? before, PasteTextReadback? after, string pastedText)
    {
        if (before is not { } b || after is not { } a)
            return PasteInsertionVerdict.Unknown;
        if (b.Source != a.Source)
            return PasteInsertionVerdict.Unknown;
        if (!NoEditableFocusGate.RuntimeIdsEqual(b.RuntimeId, a.RuntimeId))
            return PasteInsertionVerdict.Unknown; // missing on either side is also non-equal

        var head = Head(pastedText);
        if (head.Length == 0)
            return PasteInsertionVerdict.Unknown;

        var nb = Normalize(b.Text);
        var na = Normalize(a.Text);

        if (!string.Equals(na, nb, StringComparison.Ordinal))
            return PasteInsertionVerdict.Inserted;

        // Equal — nothing changed. Contains-head proves NOTHING about this paste
        // (round-2: an old occurrence, or an identical selection replacement).
        if (na.Contains(head, StringComparison.Ordinal))
            return PasteInsertionVerdict.Unknown;

        return b.CapHit ? PasteInsertionVerdict.Unknown : PasteInsertionVerdict.NotInserted;
    }

    /// <summary>
    /// Is this baseline strong enough to VERIFY a paste whose safety block was
    /// relaxed by a PST-6 live-shape fallback (Codex diff review round 4)?
    /// Non-null is not enough: a CAPPED baseline can never prove non-insertion, and
    /// a missing or mismatched runtime ID makes every later comparison
    /// <see cref="PasteInsertionVerdict.Unknown"/>. Both states would let a swallowed
    /// paste ride the fail-open path back to "Delivered" — the precise combination
    /// (relaxed block + unverifiable delivery) that must never ship together.
    /// </summary>
    public static bool IsUsableFallbackBaseline(PasteTextReadback? baseline, int[]? verifiedRuntimeId)
        => baseline is { } b
           && !b.CapHit
           && NoEditableFocusGate.RuntimeIdsEqual(b.RuntimeId, verifiedRuntimeId);

    /// <summary>First <see cref="HeadLength"/> normalized code units of the payload.</summary>
    public static string Head(string pastedText)
    {
        var normalized = Normalize(pastedText);
        return normalized.Length <= HeadLength ? normalized : normalized[..HeadLength];
    }

    /// <summary>
    /// Round-4 exact-insertion proof: does removing ONE complete occurrence of
    /// <paramref name="payload"/> from <paramref name="after"/> reproduce
    /// <paramref name="before"/>? Every occurrence is tried (bounded — a
    /// non-empty payload has at most len/payloadLen non-overlapping matches).
    /// This is what makes "baseline `bc`, short dispatch typed only `a`, after
    /// `abc`" honest: removing `abc` yields "", not `bc` — no proof, no Delivered.
    /// <para>All three inputs are expected pre-normalized, and the REMAINDER is
    /// normalized again before comparison. Length arithmetic must NOT be used as
    /// a precondition here: normalization collapses and trims whitespace, so an
    /// insertion adjacent to a trimmed space (baseline "hello " → "hello", after
    /// "hello world") legitimately yields a remainder that is longer than the
    /// baseline by exactly the whitespace normalization removed. Requiring
    /// after.Length == before.Length + payload.Length rejected almost every real
    /// end-of-field insertion, which would have reported successful pastes as
    /// unproven.</para>
    /// </summary>
    public static bool RemovalReproducesBaseline(string after, string payload, string before)
    {
        if (payload.Length == 0 || after.Length < payload.Length)
            return false;

        var index = 0;
        while ((index = after.IndexOf(payload, index, StringComparison.Ordinal)) >= 0)
        {
            var remainder = Normalize(after.Remove(index, payload.Length));
            if (string.Equals(remainder, before, StringComparison.Ordinal))
                return true;
            index++;
        }
        return false;
    }

}
