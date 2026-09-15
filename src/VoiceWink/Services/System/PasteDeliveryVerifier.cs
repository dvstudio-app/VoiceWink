using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// Per-attempt context for the paste delivery check (PST-6, reduced by PST-16). The
/// clipboard callback is a LEASE-HELD closure supplied by <see cref="ClipboardService"/>
/// for exactly this attempt — the verifier never resolves or calls the service itself
/// (its write lease is non-reentrant; a public-entry call from in here would deadlock).
/// </summary>
internal sealed record PasteDeliveryContext(
    string PastedText,
    PasteTextReadback? Before,
    Func<string, bool> RewriteClipboardUnderLease,
    /// <summary>
    /// True when this attempt's pre-restore safety block was RELAXED by a PST-6
    /// live-shape fallback (Codex diff review round 4). The relaxation was granted
    /// on the promise that delivery would be PROVEN, so on this path an
    /// unverifiable outcome may not fall back to a success claim: rung-1
    /// <see cref="PasteInsertionVerdict.Unknown"/> — and a fault before any proof —
    /// become <see cref="PasteDeliveryOutcome.DeliveryUncertain"/> (clipboard
    /// re-asserted, no Enter, no deferred restore) instead of Delivered.
    /// False everywhere else, where fail-open preserves pre-PST-6 behaviour.
    /// </summary>
    bool ProofRequired = false);

internal sealed record PasteDeliveryReport(
    PasteDeliveryOutcome Outcome,
    bool ClipboardAsserted,
    string Detail);

/// <summary>
/// PST-6 delivery check: after the caller's Ctrl+V dispatch it verifies insertion via
/// identity-bound readbacks and reports honestly.
///
/// <para><b>PST-16 removed the escalation ladder this class used to coordinate.</b> Rungs 2
/// (refocus + repaste) and 3 (bounded Unicode type-out) are gone, and with them the idea
/// that an unchanged readback PROVES non-delivery. The readback source — Chromium's
/// accessibility tree above all — publishes late under load, so a paste that landed can be
/// invisible for longer than any window worth waiting. The ladder raced that lag and lost:
/// measured 2026-08-30, a 22-char paste into a Chromium composer was repasted 1.1 s after it
/// had already arrived, and the attempt then reported "not delivered". Across ~14 days of
/// field logs the escalation dispatched 11 extra insertions and never once recorded a proven
/// rescue.</para>
///
/// <para><b>The asymmetry that replaces it.</b> This class may prove DELIVERY — it saw the
/// payload arrive — but it can never prove ABSENCE, because absence is indistinguishable from
/// a tree that has not caught up. So there is no longer a <c>NotDelivered</c> outcome: an
/// unconfirmed paste is reported as uncertain and the text is left on the clipboard. Anything
/// that tells the user the text "didn't appear" would hand them the duplicate the app used to
/// insert itself (Codex plan review, 2026-08-30).</para>
///
/// <list type="bullet">
/// <item><b>Fail open on Unknown:</b> an Unknown verdict with no proof requirement keeps
/// today's success semantics — verification must never create new false failures.</item>
/// <item><b>Never success once delivery is unconfirmed:</b> after three stable NotInserted
/// reads, no later path — including a thrown exception — may claim Delivered.</item>
/// <item><b>Clipboard postcondition:</b> every non-Delivered outcome re-asserts the dictated
/// text on the clipboard; a failed rewrite flips
/// <see cref="PasteDeliveryReport.ClipboardAsserted"/> so the caller presents the red
/// clipboard-failure pairing instead of promising a Ctrl+V that cannot work.</item>
/// <item><b>Privacy:</b> the diagnostic detail is flags-only — verdict tokens, source,
/// reasons; never content, never lengths.</item>
/// </list>
/// Constructor seams are the attempt-independent probe/clock so tests drive the whole check at
/// ms scale with fakes; production wiring is the parameterless ctor (DI singleton, injected
/// into <see cref="ClipboardService"/>).
/// </summary>
internal sealed class PasteDeliveryVerifier
{
    private static ILogger Logger => Log.ForContext<PasteDeliveryVerifier>();

    /// <summary>First readback after the caller's dispatch (matches the pre-existing
    /// post-paste diagnostics moment this verifier absorbed).</summary>
    internal static readonly TimeSpan AfterReadDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Additional settle before the stability re-read — only a SECOND
    /// identical NotInserted read may reach the third (slow editors must not be
    /// declared unconfirmed on one look).</summary>
    internal static readonly TimeSpan StableReReadDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// PST-15: a THIRD settle before delivery is called unconfirmed. Chromium publishes
    /// accessibility updates asynchronously, so an unchanged readback is evidence about the
    /// TREE, not about the text — and that lag routinely outruns the two reads above.
    /// Measured on the owner's logs (snapshot 2026-08-23 15:52): 13 of 57 pastes needed the
    /// SECOND read that day against 2 of 53 the day before.
    /// <para>Since PST-16 this window costs latency only, never correctness: nothing is
    /// inserted when it expires. It buys a cleaner verdict — a paste confirmed at the third
    /// read reports Delivered instead of uncertain — so widening it trades pill accuracy
    /// against how long the pill takes to settle, and nothing else.</para>
    /// </summary>
    internal static readonly TimeSpan LateSettleDelay = TimeSpan.FromMilliseconds(600);

    private readonly Func<PasteTextReadback?> _readback;
    private readonly Func<TimeSpan, Task> _delay;

    public PasteDeliveryVerifier()
        : this(UiaFocusBridge.TryGetFocusedElementTextReadback, Task.Delay)
    {
    }

    internal PasteDeliveryVerifier(
        Func<PasteTextReadback?> readback,
        Func<TimeSpan, Task> delay)
    {
        _readback = readback;
        _delay = delay;
    }

    /// <summary>
    /// Verify an attempt whose Ctrl+V was already dispatched by the caller (with
    /// <see cref="PasteDeliveryContext.Before"/> read before it). Never throws; an
    /// internal fault degrades to fail-open unless delivery is already unconfirmed or
    /// proof was required.
    /// </summary>
    public async Task<PasteDeliveryReport> VerifyAsync(PasteDeliveryContext ctx)
    {
        // Latched: once three stable reads have failed to see the payload, no exception
        // path may claim success again. Still load-bearing after PST-16 removed the
        // escalation, because Finalize's clipboard rewrite runs AFTER that point and can
        // throw — without the latch the catch below would consult only ProofRequired and
        // fail OPEN to Delivered on a paste we could not confirm (Codex plan review).
        var deliveryUnconfirmed = false;
        try
        {
            return await RunVerificationAsync(ctx, () => deliveryUnconfirmed = true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A fault also cannot claim success when proof was REQUIRED (round 4) —
            // same rule as an Unknown read: the relaxed block was granted against a
            // promise of proof, and a fault produces none.
            var mustNotClaimSuccess = deliveryUnconfirmed || ctx.ProofRequired;
            Logger.Warning("Paste delivery verification faulted: {ExType} — {Disposition}",
                ex.GetType().Name, mustNotClaimSuccess ? "uncertain (no delivery proof)" : "failing open");
            if (!mustNotClaimSuccess)
                return new PasteDeliveryReport(PasteDeliveryOutcome.Delivered, true, "fault:fail-open");

            // Deliberately NOT routed through Finalize: the fault being handled may BE the
            // clipboard rewrite, and re-entering it would throw straight out of this catch,
            // breaking the contract that this method never throws. The postcondition is still
            // ATTEMPTED — the text belongs on the clipboard for a manual Ctrl+V — but a second
            // failure here just reports an un-asserted clipboard, which routes the caller to
            // the red clipboard-failure pairing instead of promising a paste that cannot work.
            var clipboardAsserted = false;
            try
            {
                clipboardAsserted = ctx.RewriteClipboardUnderLease(ctx.PastedText);
            }
            catch (Exception rewriteEx)
            {
                Logger.Warning("Paste delivery: clipboard re-assert also faulted after a verification fault: {ExType}",
                    rewriteEx.GetType().Name);
            }

            return new PasteDeliveryReport(
                PasteDeliveryOutcome.DeliveryUncertain,
                clipboardAsserted,
                deliveryUnconfirmed ? "fault:after-unconfirmed" : "fault:proof-required");
        }
    }

    private async Task<PasteDeliveryReport> RunVerificationAsync(
        PasteDeliveryContext ctx, Action markUnconfirmed)
    {
        var text = ctx.PastedText;

        await _delay(AfterReadDelay).ConfigureAwait(false);
        var after1 = _readback();
        var v1 = Verdict(ctx, ctx.Before, after1, text);
        if (v1 == PasteInsertionVerdict.Inserted)
            return Finalize(ctx, PasteDeliveryOutcome.Delivered, Detail("r1:inserted", after1));
        if (v1 == PasteInsertionVerdict.Unknown)
            return Finalize(ctx, UnknownAtRung1(ctx), Detail("r1:unknown", after1));

        await _delay(StableReReadDelay).ConfigureAwait(false);
        var after1B = _readback();
        var v1B = Verdict(ctx, ctx.Before, after1B, text);
        if (v1B == PasteInsertionVerdict.Inserted)
            return Finalize(ctx, PasteDeliveryOutcome.Delivered, Detail("r1:late-inserted", after1B));
        if (v1B == PasteInsertionVerdict.Unknown)
            return Finalize(ctx, UnknownAtRung1(ctx), Detail("r1:unstable-unknown", after1B));

        await _delay(LateSettleDelay).ConfigureAwait(false);
        var after1C = _readback();
        var v1C = Verdict(ctx, ctx.Before, after1C, text);
        if (v1C == PasteInsertionVerdict.Inserted)
            return Finalize(ctx, PasteDeliveryOutcome.Delivered, Detail("r1:very-late-inserted", after1C));
        if (v1C == PasteInsertionVerdict.Unknown)
            return Finalize(ctx, UnknownAtRung1(ctx), Detail("r1:late-unstable-unknown", after1C));

        // Three stable NotInserted reads. Before PST-16 this was treated as PROOF of
        // non-delivery and authorised a second insertion; it is not proof of anything
        // except that the readback has not shown us the payload yet. The text may have
        // landed and be invisible, so the honest report is uncertainty — and nothing is
        // inserted, by the app or (via the pill copy) by the user.
        markUnconfirmed();
        Logger.Information("Paste delivery: unconfirmed after three stable reads — reporting uncertain");
        return Finalize(ctx, PasteDeliveryOutcome.DeliveryUncertain, Detail("r1:unconfirmed", after1C));
    }

    /// <summary>
    /// Clipboard postcondition + the exactly-one verdict line. Every
    /// non-Delivered outcome RE-ASSERTS the dictated text on the clipboard
    /// unconditionally (round-2: sampling-based repair fails exactly on
    /// unstable/unavailable samples; an idempotent rewrite of the same text
    /// sidesteps sampling entirely) — a failed rewrite flips
    /// <see cref="PasteDeliveryReport.ClipboardAsserted"/> so the caller
    /// presents the red clipboard-failure pairing instead of promising a
    /// Ctrl+V that cannot work.
    /// </summary>
    private PasteDeliveryReport Finalize(PasteDeliveryContext ctx, PasteDeliveryOutcome outcome, string detail)
    {
        var clipboardAsserted = true;
        if (outcome != PasteDeliveryOutcome.Delivered)
            clipboardAsserted = ctx.RewriteClipboardUnderLease(ctx.PastedText);

        if (outcome == PasteDeliveryOutcome.Delivered)
            Logger.Information("Paste delivery verdict: outcome={Outcome} detail={Detail}", outcome, detail);
        else
            Logger.Warning("Paste delivery verdict: outcome={Outcome} detail={Detail} clipboardAsserted={Asserted}",
                outcome, detail, clipboardAsserted);

        return new PasteDeliveryReport(outcome, clipboardAsserted, detail);
    }

    /// <summary>
    /// What an unverifiable rung-1 outcome means. Normally FAIL OPEN (Delivered) —
    /// verification must never invent failures on targets it simply cannot read, so
    /// pre-PST-6 behaviour is preserved. But when the pre-restore safety block was
    /// RELAXED to get here (<see cref="PasteDeliveryContext.ProofRequired"/>), an
    /// unverifiable outcome is exactly the combination that block existed to prevent,
    /// so it degrades to uncertain instead of claiming success.
    /// </summary>
    private static PasteDeliveryOutcome UnknownAtRung1(PasteDeliveryContext ctx)
        => ctx.ProofRequired ? PasteDeliveryOutcome.DeliveryUncertain : PasteDeliveryOutcome.Delivered;

    /// <summary>
    /// Rung-1 verdict. Ordinary paths keep the generous "any same-element mutation
    /// counts" rule so verification never invents failures; a relaxed-block path
    /// demands the payload itself (Codex diff review round 5).
    /// </summary>
    private static PasteInsertionVerdict Verdict(
        PasteDeliveryContext ctx, PasteTextReadback? before, PasteTextReadback? after, string text)
        => ctx.ProofRequired
            ? PasteInsertionVerification.DecidePayloadSpecific(before, after, text)
            : PasteInsertionVerification.Decide(before, after, text);

    /// <summary>Flags-only detail: verdict token + readback source/support — never content or lengths.</summary>
    private static string Detail(string token, PasteTextReadback? readback)
        => readback is { } r
            ? $"{token} source={r.Source} idPresent={r.RuntimeId is { Length: > 0 }} capHit={r.CapHit}"
            : $"{token} readback=none";
}
