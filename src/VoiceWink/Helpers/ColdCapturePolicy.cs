using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// PST-13: should the recording-start UIA capture be re-taken because it hit a
/// Chromium accessibility tree that had not been built yet?
///
/// <para><b>The defect.</b> Chromium builds its UIA tree LAZILY, once per
/// process, when a UIA client first touches it. VoiceWink's recording-start
/// capture IS that first touch on a freshly launched Chromium/Electron target,
/// so it comes back holding the target's top-level Pane instead of the
/// composer's Edit. Seconds later the tree is warm, the focused element is the
/// real Edit, and <see cref="PasteDiagnostics"/>'s identity-verified restore
/// correctly refuses to paste into an element whose identity no longer matches
/// — outcome <c>FocusMovedInTarget</c>, amber pill, manual Ctrl+V. Measured on
/// the owner's machine 2026-08-19: 8 of 13 non-pastes carried this exact shape
/// (<c>controlType=50033 text=false value=false readOnly=true focusable=false</c>)
/// and every one of them was the FIRST paste into that target process, while 61
/// of 62 pastes that captured an Edit succeeded.</para>
///
/// <para><b>Why this is fixed at the CAPTURE and not at the guard.</b> Waiving
/// the identity guard would give up PST-4's "paste where intended, or nowhere"
/// in exactly the case where no identity information exists, so a mid-recording
/// tab switch inside one window would paste into the wrong tab — the field bug
/// PST-4 was built for. Re-capturing instead RESTORES the evidence the guard
/// needs, leaving the guarantee intact.</para>
/// </summary>
internal static class ColdCapturePolicy
{
    /// <summary>
    /// True when the captured element bears the cold-tree signature and a
    /// re-capture is warranted.
    ///
    /// <para><b>Both terms are load-bearing, and the focusability one is the
    /// one that makes this safe.</b> Not-editable-shaped alone would also match
    /// a Button, a focusable ARIA Group, or a focusable Pane — all of which the
    /// user may have focused DELIBERATELY, and
    /// <see cref="NoEditableFocusGate.IsEditableShapedForCapturedRescue"/>'s own
    /// doc says the broad predicate is "acceptable only when the user
    /// deliberately focused the element at recording start". Replacing such a
    /// capture would override the user. An element that is not
    /// KEYBOARD-FOCUSABLE could not have been focused by the user at all, so it
    /// is evidence of an unhydrated tree rather than of intent.</para>
    ///
    /// <para><b>A null shape returns false.</b> That means the ControlType read
    /// itself failed — a wedged or dead target, a different failure mode with
    /// its own handling, and nothing in the measured evidence asks for it.</para>
    ///
    /// <para><b>The pattern terms exist to EXCLUDE the partial-read fold, and
    /// that exclusion is what keeps the focusability argument honest.</b>
    /// <see cref="UiaElementShape.FromReads"/> folds ANY secondary read failure
    /// to the maximally-non-editable combination — which is non-focusable. So a
    /// predicate resting on <c>!IsKeyboardFocusable</c> alone cannot tell "this
    /// element genuinely cannot take the caret" from "we could not read whether
    /// it can", and the whole safety case for replacing the capture is the
    /// former (Codex diff round). An earlier revision admitted the fold
    /// deliberately; that was wrong. <b>The shape it endangers is a deliberately
    /// focused contenteditable Group or Document</b> whose secondary reads
    /// happened to fail and folded to non-focusable — that capture would have
    /// been silently re-aimed. It is NOT a ControlType-<c>Edit</c>, which the
    /// <see cref="NoEditableFocusGate.IsEditableShapedForCapturedRescue"/>
    /// short-circuit already protects with or without these terms (Kimi
    /// verification round — the earlier wording named the wrong shape). The fold
    /// sets <c>HasValuePattern: true</c>, so requiring BOTH patterns absent
    /// excludes it.</para>
    ///
    /// <para>What remains is a POSITIVE match on the one shape actually measured
    /// in the field — a bare container exposing no text and no value pattern and
    /// unable to take focus — rather than a negative "anything not editable".
    /// Narrower than the evidence strictly requires is the right direction here:
    /// a cold shape this misses simply keeps today's clipboard behaviour, and the
    /// existing <c>Paste target editability</c> and paste-failure log lines still
    /// record it, so a variant is discoverable without widening the trigger on
    /// speculation.</para>
    /// </summary>
    public static bool ShouldReCapture(UiaElementShape? capturedShape)
    {
        if (capturedShape is not { } shape)
            return false;

        // The editable-shaped term is not redundant against the two pattern terms: it is
        // what excludes a pattern-less, non-focusable element that still reports ControlType
        // Edit, which IsEditableShapedForCapturedRescue short-circuits to editable.
        return !NoEditableFocusGate.IsEditableShapedForCapturedRescue(shape)
               && !shape.IsKeyboardFocusable
               && !shape.HasTextPattern
               && !shape.HasValuePattern;
    }

    /// <summary>
    /// Is the pipeline still inside the RECORDING SESSION, i.e. may a cold-tree upgrade
    /// still commit?
    ///
    /// <para><b>Extracted so this exact question has a case table.</b> The first revision
    /// asked it inline as <c>state == Recording</c>, which is wrong: the editability probe
    /// that triggers the upgrade is launched before the warm/cold path split, and on the
    /// COLD audio path the pipeline sits in <see cref="RecordingState.Starting"/> through
    /// the entire pre-flight — App Mode detect, a local model ensure-load that can be a
    /// full download, device resolution, capture start — reaching
    /// <see cref="RecordingState.Recording"/> only afterwards. So the gate closed on the
    /// first attempt for every cold-path recording and the fix was a path-dependent no-op,
    /// invisible because the warm path flips between the two states microseconds apart and
    /// won the race (Kimi diff round, blocker).</para>
    ///
    /// <para><b>The bound is the paste path, not the audio.</b> What the swap must not
    /// outlive is the moment the paste BORROWS the element, and its only
    /// <c>ResolveAsync</c> sites run at <see cref="RecordingState.Transcribing"/> /
    /// <see cref="RecordingState.Enhancing"/>. Both are excluded here, which is what makes
    /// admitting <see cref="RecordingState.Starting"/> free of safety cost.</para>
    /// </summary>
    public static bool IsUpgradableSessionState(RecordingState state)
        => state is RecordingState.Starting or RecordingState.Recording;

    /// <summary>
    /// How long after the recording-start target capture an upgrade may still commit.
    ///
    /// <para><b>This bound exists to make the re-aim window STATED rather than
    /// incidental</b> (Codex diff round + owner decision, 2026-08-19). The upgrade takes
    /// whatever editable element holds focus a moment later, verifying only the top-level
    /// window — so a focus move to a DIFFERENT field inside that same window redirects the
    /// dictation there. Without a deadline the exposure was bounded only by however long
    /// the bridge happened to take, which is not a property anyone can reason about.</para>
    ///
    /// <para><b>800 ms is chosen against measurement, not taste.</b> On the owner's logs
    /// the editability verdict that triggers an upgrade landed a median ~30 ms after
    /// recording start (10 of 12 under 90 ms; outliers at 756 ms and 1044 ms), so the first
    /// attempt normally commits ~130–210 ms in. A deliberate human refocus needs visual
    /// reaction plus a click — ~200 ms before anything moves — so inside this bound a
    /// deliberate switch is not a physically plausible sequence. The cost is the slow-probe
    /// tail: on that sample the bound skips the 2 outliers and keeps the other 10.</para>
    ///
    /// <para>Anchored on the target-capture instant (<c>PasteTargetSnapshot.CapturedAtUtc</c>)
    /// rather than a fresh field: it is the same instant the identity fence already keys on,
    /// so the deadline and the fence can never disagree about which recording they mean.</para>
    /// </summary>
    public static readonly TimeSpan UpgradeDeadline = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// May an upgrade still commit, given when the target was captured and the time now?
    ///
    /// <para><b>A negative elapsed is REFUSED, not treated as "very fresh".</b> Clocks move
    /// backwards (NTP correction, DST-adjacent adjustments, a VM resume), and an age that
    /// reads negative is evidence the arithmetic is not trustworthy rather than evidence the
    /// capture just happened — so it fails closed, matching
    /// <c>HoistedResolutionPolicy</c>'s existing treatment of a negative age in this repo.</para>
    /// </summary>
    public static bool IsWithinUpgradeDeadline(DateTime capturedAtUtc, DateTime nowUtc)
    {
        var elapsed = nowUtc - capturedAtUtc;
        return elapsed >= TimeSpan.Zero && elapsed <= UpgradeDeadline;
    }
}

/// <summary>
/// PST-13: why one upgrade attempt ended as it did. Exists so the field log can
/// tell a REFUTED hypothesis (<see cref="CandidateStillCold"/> — the re-capture
/// returned another cold element, so re-capturing is not the answer and the
/// paste-time fallback is) apart from a retryable miss
/// (<see cref="SlotInFlight"/>, <see cref="CaptureFailed"/>) apart from a
/// deliberate refusal (<see cref="GateClosed"/>, <see cref="SlotRefused"/>).
/// A single undifferentiated "declined" would leave the log unable to answer the
/// one question this change exists to settle.
/// </summary>
internal enum ColdCaptureUpgradeOutcome
{
    /// <summary>The slot now holds a freshly captured, editable-shaped element.</summary>
    Upgraded,

    /// <summary>
    /// Re-capture succeeded and the replacement was PROVEN non-editable — retry. This is
    /// the refutation signal: it means re-capturing is not the answer. An unreadable
    /// probe must never land here (see <see cref="CaptureFailed"/>).
    /// </summary>
    CandidateStillCold,

    /// <summary>
    /// Nothing could be determined this attempt — retry. Covers both a bridge that
    /// returned no element and a shape probe that could not answer (busy worker, unready
    /// probe, timeout, dead element). Kept distinct from
    /// <see cref="CandidateStillCold"/> because "we did not find out" and "we found out
    /// it is still cold" point at opposite follow-up decisions.
    /// </summary>
    CaptureFailed,

    /// <summary>The initial capture had not landed yet — retry.</summary>
    SlotInFlight,

    /// <summary>The slot refused for an ownership reason that will not change — stop.</summary>
    SlotRefused,

    /// <summary>Generation, capture identity, pipeline state or foreground moved on — stop.</summary>
    GateClosed,

    /// <summary>
    /// Past <see cref="ColdCapturePolicy.UpgradeDeadline"/> — stop. Kept distinct from
    /// <see cref="GateClosed"/> because they say different things about the same log: this
    /// one means the upgrade was still WANTED and arrived too late (a slow accessibility
    /// probe), so a run of these is the signal that the deadline is tuned too tight, not
    /// that the target moved on.
    /// </summary>
    DeadlineExpired
}
