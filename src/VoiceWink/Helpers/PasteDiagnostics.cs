using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// Where a paste attempt ended. Exactly one summary line per attempt carries
/// this — success AND failure — so residual field failures are attributable
/// (the pre-iteration-2 summary only logged on success, leaving every failure
/// class indistinguishable in the logs).
/// </summary>
internal enum PasteAttemptOutcome
{
    Pasted,
    ClipboardSetFailed,
    TargetGone,
    TargetElevated,
    ForegroundAcquireFailed,
    ForegroundNotSettled,
    LostForegroundPostUia,
    NoEditableFocused,
    /// <summary>PST-4: the recording-start captured element still EXISTS but focus
    /// verifiably moved to a different element in the same target window and could
    /// not be restored (Windows Terminal tab switch — one top-level window for all
    /// tabs, so hwnd/pid/tid checks all pass). Content kept on clipboard.</summary>
    FocusMovedInTarget,
    SendInputFailed,
    /// <summary>IMG-BG: the caller's proceed gate tripped mid-sequence — a recording
    /// started while the background image job's paste was between its clipboard write
    /// and Ctrl+V. The image stays on the clipboard; forcing focus + injecting Ctrl+V
    /// during the user's live dictation would be a wrong-window paste by construction.</summary>
    AbortedByRecordingStart,
    /// <summary>PST-6, reduced by PST-16: Ctrl+V was dispatched but delivery could not be
    /// CONFIRMED — never presented as success (no Enter, no deferred clipboard restore).
    /// Text kept on clipboard (asserted postcondition).
    /// <para>There is no separate "not delivered" member: the readback source lags, so the
    /// check can prove arrival but never absence. Every unconfirmed paste lands here.</para>
    /// </summary>
    PasteDeliveryUncertain,
    /// <summary>UI-7: a picker dialog was on screen when the recording started, so
    /// VoiceWink itself held the foreground and there was no honest target. NOTHING
    /// WAS ATTEMPTED — unlike every other member here, this outcome is decided by the
    /// caller BEFORE the paste path runs, and the text is put on the clipboard by that
    /// caller. It exists so the deliberate copy-only case cannot be confused with a
    /// paste that was tried and declined.</summary>
    NoTargetPickerOpen,
    /// <summary>UI-7: no paste target was captured at all — the TRN-17 retry cancel path
    /// and the targetless-capture edge both deliberately zero the handle. Like
    /// <see cref="NoTargetPickerOpen"/> nothing is attempted; the text is copied.
    ///
    /// <para>Both paths ALREADY zeroed the handle and their comments already claimed a
    /// clipboard fallback — but nothing implemented it, and a zero handle passes every
    /// gate in <c>ClipboardService</c>, so the text was Ctrl+V'd into whatever held the
    /// foreground. This outcome is that claim made true.</para></summary>
    NoTarget
}

/// <summary>
/// Result of a text-paste attempt. <see cref="UserMessage"/> is the pill/status
/// text for failures that have a more actionable story than a generic
/// "Paste failed" (elevated target, target closed); null falls back to the
/// caller's generic message.
/// </summary>
internal readonly record struct PasteResult(bool Succeeded, PasteAttemptOutcome Outcome, string? UserMessage)
{
    public static PasteResult Success() => new(true, PasteAttemptOutcome.Pasted, null);
    public static PasteResult Fail(PasteAttemptOutcome outcome, string? userMessage = null)
        => new(false, outcome, userMessage);
}

/// <summary>
/// Maps a paste attempt to its pill presentation. The paste step runs only AFTER the clipboard
/// was successfully set, so every decline outcome (focus moved, no text box, elevated target,
/// window closed, foreground/SendInput refusals) leaves the content safely on the clipboard —
/// those are amber WARNINGS with "paste from clipboard" guidance, not errors (owner 2026-07-09:
/// declined auto-pastes are a normal Windows occurrence). The one exception is
/// <see cref="PasteAttemptOutcome.ClipboardSetFailed"/>: the content never reached the
/// clipboard, so it stays a red ERROR and must never carry "paste from clipboard" copy —
/// message and tone are therefore chosen TOGETHER here (<see cref="Present"/>), never
/// independently at call sites.
/// </summary>
internal static class PasteResultPresentation
{
    /// <summary>Fallback for declines whose outcome carries no cause-specific message.</summary>
    public const string GenericDeclineMessage = "Couldn't auto-paste — paste from clipboard with Ctrl+V";

    /// <summary>Fallback for a clipboard set failure — the content is NOT on the clipboard.
    /// Deliberately parallel to the image sibling ("Couldn't copy the image to the clipboard")
    /// and reused by the redo copy path, which had its own third phrasing ("Failed to copy
    /// text") until the 2026-07-25 copy review — one situation, one sentence shape.</summary>
    public const string ClipboardFailedMessage = "Couldn't copy the text to the clipboard";

    // Cause-first decline copy, shared by the text and image paste paths (ClipboardService).
    // Invariants: cause first, then the one calm action; never the word "failed"/"error"
    // (declines are normal Windows behavior, not malfunctions); ≤ 55 chars — the pill's
    // truncation cap (MiniRecorderWindow / TruncateForMiniRecorder).
    public const string WindowClosedMessage = "App window closed — paste from clipboard with Ctrl+V";
    public const string ElevatedMessage = "App runs as admin — paste from clipboard with Ctrl+V";
    public const string NotInFrontMessage = "App not in front — paste from clipboard with Ctrl+V";
    public const string NoTextBoxMessage = "No text box focused — paste from clipboard with Ctrl+V";
    public const string FocusMovedMessage = "Focus moved — paste from clipboard with Ctrl+V";
    public const string KeystrokeBlockedMessage = "Keystroke blocked — paste from clipboard with Ctrl+V";
    public const string RecordingStartedMessage = "Recording started — paste from clipboard with Ctrl+V";
    // PST-6 delivery decline (the check asserted the clipboard before this shows).
    // PST-16: the ONE delivery-decline string. It must never assert that the text is absent —
    // the readback lags, so a paste that landed can look missing, and "it didn't appear" would
    // make the user supply the duplicate the app used to insert itself. Check first, then act.
    public const string DeliveryUncertainMessage = "Couldn't confirm the paste — check before Ctrl+V";
    // UI-7. Same shape as the declines above even though nothing was attempted: the user does not
    // care about that distinction, they care where their words are and what to do next.
    public const string PickerOpenMessage = "Dialog was open — paste from clipboard with Ctrl+V";
    public const string NoTargetMessage = "Nowhere to paste — paste from clipboard with Ctrl+V";

    public static MiniRecorderTone ToneFor(PasteResult result) =>
        result.Succeeded ? MiniRecorderTone.Success
        : result.Outcome == PasteAttemptOutcome.ClipboardSetFailed ? MiniRecorderTone.Error
        : MiniRecorderTone.Warning;

    /// <summary>
    /// Message + tone for a FAILED paste result (callers handle success themselves).
    /// Keeping the pairing in one place guarantees a ClipboardSetFailed can never be
    /// presented with clipboard-available copy.
    /// </summary>
    public static (string Message, MiniRecorderTone Tone) Present(PasteResult result)
    {
        var tone = ToneFor(result);

        // ClipboardSetFailed IGNORES any caller-supplied message. Trusting call sites here
        // failed once already: ClipboardService passed its own "Couldn't access the clipboard —
        // nothing was pasted", which silently overrode the shared constant and left the redo
        // copy-only path reading differently for the identical failure — and that wording is
        // itself false where no paste was ever intended (Codex diff review rounds 1–2,
        // 2026-07-25). One situation, one sentence, enforced structurally rather than by
        // convention. Every other outcome is a post-clipboard decline, whose cause-specific
        // message is exactly what the caller should supply.
        if (result.Outcome == PasteAttemptOutcome.ClipboardSetFailed)
            return (ClipboardFailedMessage, tone);

        return (result.UserMessage ?? GenericDeclineMessage, tone);
    }
}

/// <summary>
/// Identity snapshot of the paste target captured at recording start (and at
/// redo-capture). A sealed record CLASS deliberately: the class name is
/// enriched asynchronously after the pill is visible (GetClassName has been
/// observed blocking under target contention — it must never run on the
/// recording-start hot path), and reference assignment lets that enrichment be
/// published with a race-free Interlocked.CompareExchange.
/// </summary>
internal sealed record PasteTargetSnapshot(
    IntPtr Hwnd,
    uint Pid,
    uint ThreadId,
    string? ClassName,
    DateTime CapturedAtUtc);

internal enum PasteTargetValidity
{
    /// <summary>No snapshot for this window — gate self-disarms (legacy behavior).</summary>
    NotApplicable,
    Valid,
    /// <summary>Window no longer exists.</summary>
    Gone,
    /// <summary>Window exists but its owner changed — the HWND value was recycled
    /// by another window. Activating it would paste into the wrong app.</summary>
    Recycled
}

/// <summary>
/// Pure paste-pipeline decision helpers. All Win32 lookups are injected so
/// every rule is unit-testable without real windows, tokens, or key state
/// (repo rule: tests never touch the real clipboard or inject input).
/// </summary>
internal static class PasteTargetValidation
{
    /// <summary>
    /// Liveness + identity gate for a captured target. Identity = PID and
    /// thread id must still match the capture; the class name participates
    /// only when the async enrichment landed (null/empty disarms that layer).
    /// Best-effort by design: a same-process, same-thread, same-class HWND
    /// reuse would still pass — the realistic hazards (window closed; HWND
    /// recycled by a different process/thread) are caught.
    /// </summary>
    public static PasteTargetValidity Validate(
        PasteTargetSnapshot? snapshot,
        IntPtr targetWindow,
        Func<IntPtr, bool> isWindow,
        Func<IntPtr, (uint Pid, uint ThreadId)> pidTidOf,
        Func<IntPtr, string> classOf)
    {
        if (snapshot == null || targetWindow == IntPtr.Zero || snapshot.Hwnd != targetWindow)
            return PasteTargetValidity.NotApplicable;

        if (!isWindow(targetWindow))
            return PasteTargetValidity.Gone;

        var (pid, tid) = pidTidOf(targetWindow);
        if (pid == 0)
            return PasteTargetValidity.Gone;
        if (pid != snapshot.Pid || tid != snapshot.ThreadId)
            return PasteTargetValidity.Recycled;

        if (!string.IsNullOrEmpty(snapshot.ClassName) && classOf(targetWindow) != snapshot.ClassName)
            return PasteTargetValidity.Recycled;

        return PasteTargetValidity.Valid;
    }
}

/// <summary>
/// Sequencing policy for the foreground-acquisition escalation ladder. The
/// initial attach+SetForegroundWindow attempt (step 0) is today's behavior;
/// escalation only runs on paths that previously bailed outright, so the
/// added worst-case latency (~750 ms across all steps) is paid only where the
/// paste used to fail. SwitchToThisWindow is last because it is the bluntest
/// instrument (Alt-Tab-equivalent) — and callers must re-validate the target
/// before it (never Alt-Tab to a recycled handle).
/// </summary>
internal static class ForegroundEscalation
{
    public enum Step
    {
        RetryAttach,
        InputNudge,
        SwitchToThisWindow,
        GiveUp
    }

    /// <summary>Delay before the RetryAttach step — transient input-state churn
    /// (the observed 2026-07-05 failure class) usually clears within this.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Settle budget per escalation step — shorter than the initial
    /// 50+250 ms settle so the whole ladder stays bounded.</summary>
    public static readonly TimeSpan StepSettleBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>Escalation step for the given retry attempt (0-based, AFTER the
    /// initial step-0 attempt failed).</summary>
    public static Step NextStep(int attemptIndex) => attemptIndex switch
    {
        0 => Step.RetryAttach,
        1 => Step.InputNudge,
        2 => Step.SwitchToThisWindow,
        _ => Step.GiveUp
    };
}

/// <summary>
/// Physically-held-modifier policy for synthetic input. Probes the eight
/// side-specific modifier VKs and, when the wait budget expires with keys
/// still down, plans KEYUPs for exactly the held sides — prepended to the
/// synthetic chord in the same SendInput call so the target never observes
/// Ctrl+V merged with a user-held modifier (AltGr on the user's Belgian
/// AZERTY hotkey being the canonical case).
/// </summary>
internal static class ModifierReleasePlan
{
    public static readonly ushort[] SideSpecificModifierVks =
    [
        NativeInterop.VK_LCONTROL, NativeInterop.VK_RCONTROL,
        NativeInterop.VK_LMENU, NativeInterop.VK_RMENU,
        NativeInterop.VK_LSHIFT, NativeInterop.VK_RSHIFT,
        NativeInterop.VK_LWIN, NativeInterop.VK_RWIN,
    ];

    /// <summary>Poll interval while waiting for physical release.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Maximum time to wait for the user to release all modifiers.</summary>
    public static readonly TimeSpan WaitBudget = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The VKs eligible for force-release when still reported down at the wait
    /// deadline: only modifiers that serve as VoiceWink hotkeys. Their keyups are
    /// the ones our own low-level hook has historically suppressed, making them
    /// the phantom-stuck-state candidates; genuinely-held OTHER modifiers (a user
    /// typing with Shift down) are never force-released — the chord proceeds with
    /// them held, exactly today's behavior, and the summary logs it.
    /// "RightAlt" maps to the AltGr pair (RMENU + phantom LCONTROL) because
    /// AltGr layouts synthesize a LeftControl press alongside RightAlt.
    /// </summary>
    public static ushort[] PhantomProneVks(IEnumerable<string?> configuredHotkeyNames)
    {
        var vks = new HashSet<ushort>();
        foreach (var name in configuredHotkeyNames)
        {
            switch (name)
            {
                case "RightAlt":
                    vks.Add(NativeInterop.VK_RMENU);
                    vks.Add(NativeInterop.VK_LCONTROL); // AltGr phantom companion
                    break;
                case "LeftAlt": vks.Add(NativeInterop.VK_LMENU); break;
                case "LeftControl": vks.Add(NativeInterop.VK_LCONTROL); break;
                case "RightControl": vks.Add(NativeInterop.VK_RCONTROL); break;
                case "LeftShift": vks.Add(NativeInterop.VK_LSHIFT); break;
                case "RightShift": vks.Add(NativeInterop.VK_RSHIFT); break;
            }
        }
        return vks.ToArray();
    }

    /// <summary>The side-specific modifiers currently reported down by the probe.</summary>
    public static ushort[] HeldModifiers(Func<ushort, bool> isKeyPhysicallyDown)
    {
        List<ushort>? held = null;
        foreach (var vk in SideSpecificModifierVks)
        {
            if (isKeyPhysicallyDown(vk))
                (held ??= new List<ushort>()).Add(vk);
        }
        return held?.ToArray() ?? [];
    }

    /// <summary>
    /// Wait (bounded) for all side-specific modifiers to be released; returns the
    /// ones STILL held at the deadline — the caller injects KEYUPs for exactly
    /// those. Delay is injected so tests drive the loop with a virtual clock.
    /// </summary>
    public static async Task<ushort[]> WaitForReleaseAsync(
        Func<ushort, bool> isKeyPhysicallyDown,
        Func<TimeSpan, Task> delay,
        TimeSpan? budget = null)
    {
        var deadline = budget ?? WaitBudget;
        var waited = TimeSpan.Zero;

        var held = HeldModifiers(isKeyPhysicallyDown);
        while (held.Length > 0 && waited < deadline)
        {
            await delay(PollInterval).ConfigureAwait(false);
            waited += PollInterval;
            held = HeldModifiers(isKeyPhysicallyDown);
        }
        return held;
    }
}

/// <summary>
/// UIPI decision: a non-elevated process cannot inject input into an elevated
/// target — Windows drops it with NO error signal. Detect and fail fast with
/// an honest message instead. Unknown elevation (query failed) fails OPEN:
/// proceeding matches today's behavior, and a wrong skip would be worse.
/// </summary>
internal static class ElevationGate
{
    public static bool ShouldSkipElevatedTarget(bool? targetElevated, bool selfElevated)
        => targetElevated == true && !selfElevated;
}

/// <summary>
/// Two-stage gate for the "Ctrl+V has no editable to land in" loss class
/// (PST-2). Field-proven discriminator: every logged Chromium-family paste
/// LOSS (2026-06-17 22:22:51; 2026-07-06 16:15:29) had keyboard focus on the
/// TOP-LEVEL <c>Chrome_WidgetWin_*</c> frame itself; every success focused a
/// <c>Chrome_RenderWidgetHostHWND</c> child. But browser-chrome text fields
/// (omnibox, find box, DevTools search) ALSO keep HWND focus on the top-level
/// frame — so the cheap HWND shape only TRIGGERS a bounded UIA confirmation,
/// and the paste is blocked only when the live focused element's ControlType
/// is confirmed to not be Edit. Every missing/failed signal fails OPEN
/// (paste proceeds as today) — a false block on a working target would be
/// worse than the pre-existing silent loss.
/// <para>PST-2b: Stage 1 must be evaluated on the PRE-restore focus shape.
/// The pipeline's own UIA focus restore (SetFocus on ANY page element,
/// editable or not) converts top-level focus into renderer-child focus —
/// manufacturing the success shape and erasing the trigger (field-proven
/// 2026-07-06 18:20–18:21: three pastes vanished with outcome=Pasted because
/// the gate only looked post-restore). The post-restore gate remains as a
/// drift net only.</para>
/// </summary>
internal static class NoEditableFocusGate
{
    /// <summary>UIA_ControlTypePropertyId — property read from the live focused element.</summary>
    public const int UiaControlTypePropertyId = 30003;

    /// <summary>UIA_EditControlTypeId — browser-chrome text fields (omnibox, find box)
    /// report this; they accept Ctrl+V and must never be blocked.</summary>
    public const int UiaEditControlTypeId = 50004;

    /// <summary>UIA_IsKeyboardFocusablePropertyId (PST-3 shape probe).</summary>
    public const int UiaIsKeyboardFocusablePropertyId = 30009;

    /// <summary>UIA_IsTextPatternAvailablePropertyId (PST-3 shape probe).</summary>
    public const int UiaIsTextPatternAvailablePropertyId = 30040;

    /// <summary>UIA_IsValuePatternAvailablePropertyId (PST-3 shape probe).</summary>
    public const int UiaIsValuePatternAvailablePropertyId = 30043;

    /// <summary>UIA_ValueIsReadOnlyPropertyId (PST-3 shape probe) — the flag that
    /// separates a Chromium contenteditable (writable / no ValuePattern) from a
    /// read-only page document (probe evidence 2026-07-08).</summary>
    public const int UiaValueIsReadOnlyPropertyId = 30046;

    /// <summary>UIA_RuntimeIdPropertyId (PST-3 identity binding) — the element
    /// identity token that ties the rescue-aware drift net's broad predicate to
    /// the EXACT element the rescue verified (Codex plan round 2: without it,
    /// focus drifting to a different broad-shaped non-editable element between
    /// verification and drift net would false-allow).</summary>
    public const int UiaRuntimeIdPropertyId = 30000;

    /// <summary>UIA_ValueValuePropertyId (PST-6 readback) — a content source ONLY
    /// when the ValuePattern is available AND writable; a read-only Value on
    /// Chromium Documents carries the page URL (live probe 2026-07-29).</summary>
    public const int UiaValueValuePropertyId = 30045;

    /// <summary>UIA_IsPasswordPropertyId (PST-6 readback) — password fields are
    /// never read; the verification returns Unknown without touching content.</summary>
    public const int UiaIsPasswordPropertyId = 30019;

    /// <summary>UIA_TextPatternId (PST-6 readback) — the pattern whose document
    /// range is the real content source on Chromium Group/Document composers.</summary>
    public const int UiaTextPatternId = 10014;

    /// <summary>The exact top-level window class of a WinUI 3 / Windows App SDK desktop host.</summary>
    private const string WinUiTopLevelClass = "WinUIDesktopWin32WindowClass";

    /// <summary>The one app with measured evidence that our UIA restore breaks its paste
    /// (PST-8). Deliberately an app pin, not a family rule — see
    /// <see cref="IsOpaqueHybridCandidate"/>.</summary>
    private const string WhatsAppProcessName = "WhatsApp.Root";

    /// <summary>
    /// PST-8 — the COMPLETE decision: route + the paired native focus snapshot + app
    /// identity. Entirely native; it performs NO accessibility call, which is the point
    /// (see the note below the method).
    /// <para>Measured signature (owner machine, 2026-07-30): WhatsApp Desktop is a WinUI
    /// top-level window hosting an OFFSCREEN Chromium widget. The hidden
    /// <c>Chrome_WidgetWin_0</c> child holds Win32 keyboard focus while content composites
    /// into the WinUI surface, so the accessibility tree is empty and UIA's "focused
    /// element" degrades to a bare Pane. Restoring focus to that Pane moves Win32 focus to
    /// the WinUI bridge — away from the window that would have accepted the paste — and the
    /// keystroke lands nowhere. This is the inverse of the PST-3 rescue: here the restore
    /// IS the failure.</para>
    /// <para><b>Scope is an APP PIN, not a family rule</b> — see the app-identity condition
    /// below. An earlier revision made this a WinUI+offscreen-Chromium FAMILY rule, arguing
    /// that a bare Pane proves there is no editable for the restore to target. That argument
    /// is UNSOUND (Codex diff review): a keyboard-focusable Pane can be a working focus
    /// PROXY for an internal editor, so a look-alike app that pastes correctly today via
    /// Pane restoration could match the fingerprint and regress. Only WhatsApp has
    /// behavioural evidence that the restore is harmful.</para>
    /// </summary>
    public static bool IsOpaqueHybridCandidate(
        bool routeConfident,
        PasteFocusRoute.Route route,
        string? targetClass,
        string? focusClass,
        bool focusBelongsToTarget,
        Func<string?> targetProcessName)
    {
        if (!routeConfident || route != PasteFocusRoute.Route.NormalUiaRestore)
            return false;
        // EXACT, non-empty class match. A negative test (“not Chromium”) would also accept
        // an EMPTY class, which ResolvePasteRoute still reports as Confident when
        // GetClassName fails (Codex plan round 1).
        if (!string.Equals(targetClass, WinUiTopLevelClass, StringComparison.Ordinal))
            return false;
        if (!IsChromiumTopLevelClass(focusClass))
            return false;
        // The focused HWND must provably belong to the target process, else "focus is on a
        // Chromium child" is an unverified pairing (Codex plan round 2).
        if (!focusBelongsToTarget)
            return false;

        // App identity, evaluated LAST so the process lookup only runs for a
        // WinUI+Chromium-focus target (rare), never on an ordinary paste.
        //
        // SCOPED TO THE ONE APP WITH BEHAVIOURAL EVIDENCE (Codex diff review): the earlier
        // "WinUI + offscreen-Chromium family" rule was justified by the claim that a bare
        // Pane means there is no editable for the restore to target. That does NOT follow —
        // a keyboard-focusable Pane can be a working FOCUS PROXY for an internal editor, so
        // another WinUI/Chromium app that pastes correctly TODAY via Pane restoration could
        // match the fingerprint and regress. Only WhatsApp has measured evidence that the
        // restore is harmful. Widening this is a one-condition change once another app is
        // behaviourally verified — the shape/route/focus conditions above already encode the
        // mechanism.
        return string.Equals(targetProcessName(), WhatsAppProcessName, StringComparison.OrdinalIgnoreCase);
    }

    // NOTE (owner decision after live UAT, 2026-07-30): this decision deliberately has NO
    // accessibility-probe half and NO live-focus identity guard. Both existed in an earlier
    // revision and were REMOVED, because requiring a UIA probe to confirm that UIA is
    // unusable is self-defeating: on the measured target the captured probe TIMED OUT at the
    // 500 ms bound and its straggler answered 17 SECONDS later, jamming the shared UIA worker
    // so the subsequent SetFocus fast-failed and PST-4 declined the paste. On the one target
    // this rule exists for, the confirming probe cannot be obtained at all.
    //
    // ACCEPTED RESIDUAL RISK (explicit owner decision): with no live identity check, a
    // mid-pipeline switch to another field inside WhatsApp can receive the text. That is
    // exactly what happens TODAY on this target (an unverified Ctrl+V lands wherever focus
    // is), so it is not a regression — but it is a real limitation, recorded here rather than
    // hidden. Re-add a guard only if a bounded, reliable signal for this target is found.
    /// <summary>
    /// Post-restore drift-net evaluation, extracted PURE with probe delegates so the
    /// net SELECTION is pinned at the layer the caller actually uses (Codex diff
    /// review round 3: a test against the predicate alone would still pass if the
    /// caller reverted to keying on the rescue flag — which is exactly the defect
    /// that shipped in round 2). The caller supplies the two bounded UIA probes and
    /// does the logging; every routing decision lives here.
    /// </summary>
    public static PostRestoreEvaluation EvaluatePostRestore(
        PreRestoreStage? stage,
        Func<UiaShapeWithIdentity?> probeShapeWithIdentity,
        Func<int?> probeControlType)
    {
        if (stage is { UsesIdentityBoundDriftNet: true } passed)
        {
            var probed = probeShapeWithIdentity();
            var identityMatch = RuntimeIdsEqual(probed?.RuntimeId, passed.VerifiedRuntimeId);
            return new PostRestoreEvaluation(
                Block: ShouldBlockPostRestoreShape(probed?.Shape, probed?.RuntimeId, passed.VerifiedRuntimeId),
                ProbeProduced: probed.HasValue,
                Detail: probed is { } p
                    ? $"shape controlType={p.Shape.ControlTypeId} identityMatch={identityMatch} (pass={passed.Kind})"
                    : $"shape probe failed (pass={passed.Kind})");
        }

        var controlType = probeControlType();
        return new PostRestoreEvaluation(
            Block: ShouldBlock(controlType.HasValue, controlType ?? 0),
            ProbeProduced: controlType.HasValue,
            Detail: $"controlType={controlType?.ToString() ?? "unread"}");
    }

    /// <summary>
    /// PST-6 password gate decision, extracted pure so the fail-closed rule is
    /// testable without COM. Content may be read ONLY when the provider
    /// EXPLICITLY answered "not a password" — i.e. a support-aware read
    /// (<c>GetCurrentPropertyValueEx(..., ignoreDefaultValue: true)</c>) that
    /// succeeded AND returned a real Boolean <c>false</c>.
    /// <para>Every other state returns false and skips the content read: an
    /// HRESULT failure, the NotSupported sentinel, any non-Boolean payload, or an
    /// explicit true. The plain (default-supplying) read is deliberately NOT a
    /// fallback here — <c>UIA_IsPasswordPropertyId</c> defaults to FALSE for
    /// unsupported properties, so falling back would turn "the provider never
    /// said" into "confirmed not a password" and read credentials from a field
    /// that simply does not implement the property (Codex diff review round 2).
    /// The cost of strictness is that verification silently does not run on such
    /// elements — a safe, observable degradation (no verdict line in the log),
    /// which is the correct trade against reading a password.</para>
    /// </summary>
    public static bool IsConfirmedNotPasswordFromSupportAwareRead(bool readSucceeded, object? value)
        => readSucceeded && value is bool isPassword && !isPassword;

    private const string ChromiumTopLevelClassPrefix = "Chrome_WidgetWin_";

    /// <summary>
    /// PST-6: is this the Chromium top-level window-class family (Electron apps,
    /// Chrome/Edge/Brave, CEF hosts)? Scopes insertion verification to the target
    /// family whose accessibility values are known to reflect live document
    /// content (live probe evidence 2026-07-29) — a custom editor whose value
    /// lags could otherwise produce a false "didn't appear" verdict on a paste
    /// that actually landed.
    /// </summary>
    public static bool IsChromiumTopLevelClass(string? targetClass)
        => targetClass is not null
           && targetClass.StartsWith(ChromiumTopLevelClassPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Stage 1 (microseconds): should the bounded UIA confirmation run at all?
    /// True only for a CONFIDENTLY-normal route into a Chromium-family target
    /// whose keyboard focus sits on the top-level frame itself. Renderer-child
    /// focus (every logged success), non-Chromium targets, the WebView2 skip
    /// route, unconfident route resolution, and a missing focus handle all
    /// skip the probe — the paste proceeds untouched.
    /// </summary>
    public static bool ShouldProbe(
        bool routeConfident,
        PasteFocusRoute.Route route,
        string targetClass,
        IntPtr targetWindow,
        IntPtr focusHwnd,
        string focusClass)
    {
        if (!routeConfident || route != PasteFocusRoute.Route.NormalUiaRestore)
            return false;
        if (targetWindow == IntPtr.Zero || focusHwnd == IntPtr.Zero)
            return false;
        if (!targetClass.StartsWith(ChromiumTopLevelClassPrefix, StringComparison.Ordinal))
            return false;

        return focusHwnd == targetWindow
            || focusClass.StartsWith(ChromiumTopLevelClassPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stage 2: block only when the live focused element's ControlType was
    /// SUCCESSFULLY read and is not an Edit control. A failed/timed-out read
    /// fails open. This Edit-only rule is deliberately UNCHANGED by PST-3 —
    /// the broader shape predicate below applies ONLY on the captured-element
    /// rescue path, so the normal gate stages gain no new false-allow surface.
    /// </summary>
    public static bool ShouldBlock(bool probeSucceeded, int controlTypeId)
        => probeSucceeded && controlTypeId != UiaEditControlTypeId;

    /// <summary>
    /// PST-3 rescue predicate — used ONLY for (a) the recording-start CAPTURED
    /// element, (b) the rescue's fail-closed live verification, and (c) the
    /// rescue-aware post-restore drift net. Empirical basis (live UIA probes,
    /// owner machine 2026-07-08): Chromium contenteditables expose as Edit
    /// (writable ValuePattern) or as Group/Document-ish types with TextPattern
    /// and either no ValuePattern or a writable one; genuine page documents
    /// carry ValuePattern.IsReadOnly=true; video elements/panes carry no
    /// TextPattern. NOT used on the normal live gate stages — a focusable
    /// ARIA Group with TextPattern can be a non-editable region that ignores
    /// Ctrl+V, acceptable only when the user deliberately focused the element
    /// at recording start.
    /// </summary>
    public static bool IsEditableShapedForCapturedRescue(UiaElementShape shape)
        => shape.ControlTypeId == UiaEditControlTypeId
           || (shape.HasTextPattern
               && !(shape.HasValuePattern && shape.ValueIsReadOnly)
               && shape.IsKeyboardFocusable);

    /// <summary>
    /// PST-3 identity-bound drift net (post-restore Stage 2 after a successful
    /// rescue). The broad predicate may pass ONLY the exact element the rescue
    /// verified (runtime-ID match); a drifted-to or identity-unknown element
    /// falls back to the Edit-only rule, so a broad-shaped non-editable element
    /// that gained focus after verification BLOCKS. A null shape keeps the
    /// drift net's fail-open semantic (the rescue's own verification already
    /// ran fail-closed).
    /// </summary>
    public static bool ShouldBlockPostRestoreShape(
        UiaElementShape? shape, int[]? runtimeId, int[]? verifiedRuntimeId)
    {
        if (shape is not { } s)
            return false; // probe failed — fail open, as the control-type net does

        if (RuntimeIdsEqual(runtimeId, verifiedRuntimeId))
            return !IsEditableShapedForCapturedRescue(s);

        return s.ControlTypeId != UiaEditControlTypeId;
    }

    /// <summary>Match requires BOTH ids present and element-wise equal — null or
    /// empty on either side is "identity unknown", never a match.</summary>
    public static bool RuntimeIdsEqual(int[]? a, int[]? b)
        => a is { Length: > 0 } && b is { Length: > 0 } && a.AsSpan().SequenceEqual(b);

    /// <summary>
    /// PST-11: the rescue's admission test for one candidate — alive (typed probe, so an
    /// unreadable identity is unknown rather than dead) AND editable-shaped. Extracted only
    /// because two candidates now ask it; the predicate itself is unchanged and still
    /// single-sourced in <see cref="IsEditableShapedForCapturedRescue"/>.
    /// </summary>
    internal static bool IsAliveAndEditableShaped(CapturedElementProbe probe)
        => probe.IsAlive
           && probe.Shape is { } shape
           && IsEditableShapedForCapturedRescue(shape);

    /// <summary>
    /// PST-2b sequencing invariant, extracted so tests can pin it without
    /// touching UIA/SendInput: a pre-restore block MUST short-circuit the UIA
    /// focus restore (see the class remarks — the restore manufactures the
    /// success shape on non-editable content, so running it after a block
    /// would erase the evidence and let an immediate retry "succeed" into the
    /// void). <paramref name="skipUiaRestore"/> (the OOP-WebView2 route) runs
    /// neither the gate (which never acts on that route) nor the restore.
    /// <para>PST-3 captured-element rescue: when the gate blocks but the
    /// recording-start CAPTURED element probes editable-shaped (the user
    /// deliberately focused it before dictating — field case: Claude Desktop's
    /// contenteditable chat box, false-blocked 2026-07-08 20:29 because the
    /// re-foregrounded frame held focus on the read-only page document), the
    /// stage restores focus DIRECTLY to that element (never the fresh-recapture
    /// fallback — recapturing would focus the current non-editable element and
    /// manufacture the success shape, PST-2b's evidence-destruction scenario)
    /// and then requires an unconditional live shape verification. The
    /// verification FAILS CLOSED (null → still blocked): the baseline on this
    /// path was a block, so unknown must not fail open. Every rescue failure
    /// mode degrades to today's block.</para>
    /// <para>PST-11: the rescue may carry a SECOND candidate
    /// (<see cref="CapturedElementRescue.Fallback"/>) — the element the RECORDING pasted into,
    /// offered when the first candidate is not alive-and-editable. A redo re-captures at
    /// picker-OPEN time, by which point the dictation's send-Enter has blurred the composer, so
    /// the first candidate is a focusable <c>Group</c> and the rescue that exists for exactly this
    /// case cannot engage. Trying the retained element here — rather than choosing between them in
    /// the ViewModel — keeps the editable-shape predicate single-sourced and confines the extra
    /// bounded probe to the already-blocked path.</para>
    /// <para><b>Only the not-alive-or-not-editable arm advances.</b> A first candidate that IS
    /// alive and editable but whose restore FAILS still blocks without consulting the fallback:
    /// that is PST-4's wrong-tab signature, and trying another element there would paste into
    /// something the user did not focus. The live-shape fallback keeps reading the FIRST
    /// candidate's probe, because its dead-capture arm reasons about the element the paste path
    /// actually captured — a dead RETAINED element says nothing about where focus is now.</para>
    /// </summary>
    public static async Task<PreRestoreStage> RunPreRestoreStageAsync(
        bool skipUiaRestore,
        Func<Task<bool>> shouldBlockNoEditableFocused,
        Func<bool> restoreUiaFocus,
        CapturedElementRescue? rescue = null,
        IdentityVerifiedRestore? identityRestore = null,
        Func<UiaShapeWithIdentity?>? probeLiveShape = null,
        Func<UiaShapeWithIdentity?>? probePostRestoreIdentity = null)
    {
        if (skipUiaRestore)
            return PreRestoreStage.NotBlocked(uiaRestored: false);

        if (await shouldBlockNoEditableFocused().ConfigureAwait(false))
        {
            if (rescue == null)
                return PreRestoreStage.BlockedWith(PreRestoreBlock.NoEditableFocused, uiaRestored: false);

            var captured = rescue.ProbeCaptured();

            // PST-11: advance to the first candidate that is alive AND editable-shaped. With no
            // Fallback this is exactly the pre-PST-11 single evaluation, which is what keeps the
            // change additive for every non-redo paste.
            //
            // The SELECTED candidate's probe travels with its restore delegate, because the
            // identity check below compares the live element against the one we actually restored
            // to. Carrying only the delegate (the first version of this) silently kept the FRESH
            // id: a retained Tiptap-style Group would restore fine, verify as itself, fail the
            // equality arm against the wrong id, fail the Edit arm because it is a Group, and
            // block — rejecting exactly the contenteditable rescue this card exists for, in the
            // Chromium composers that are its whole subject (Codex diff round 1).
            var selectedProbe = captured;
            Func<bool> restoreDirect = rescue.RestoreDirect;
            if (!IsAliveAndEditableShaped(captured))
            {
                var fallback = rescue.Fallback;
                var fallbackCaptured = fallback?.ProbeCaptured();

                if (fallback == null || fallbackCaptured is not { } probed || !IsAliveAndEditableShaped(probed))
                    // The FIRST candidate's probe, deliberately — see the method remarks.
                    return DecideBlockedLiveShapeFallback(captured, probeLiveShape);

                selectedProbe = probed;
                restoreDirect = fallback.RestoreDirect;
            }

            // An ALIVE, editable-shaped captured element whose restore/verify fails
            // keeps today's block WITHOUT consulting the live-shape fallback — that
            // is PST-4's wrong-tab signature (the intended element exists but can't
            // regain focus; whatever the user focused since must not be pasted into).
            if (!restoreDirect())
                return PreRestoreStage.BlockedWith(PreRestoreBlock.NoEditableFocused, uiaRestored: false);

            var live = rescue.VerifyLiveShape();
            if (live is not { } verified)
                return PreRestoreStage.BlockedWith(PreRestoreBlock.NoEditableFocused, uiaRestored: true);

            // PST-4 (Codex PST-4 round 1, C5): equality-or-live-Edit-after-success.
            // SetFocus on a contenteditable CONTAINER (Tiptap/ProseMirror Group)
            // legitimately lands focus on a child Edit with a DIFFERENT runtime ID —
            // strict equality would block a real rescue. The Edit arm is safe because
            // the direct restore already succeeded to reach this line. A different-ID
            // broad-non-Edit element still blocks; no ancestry inference from
            // runtime-ID array prefixes.
            // PST-11: against the SELECTED candidate, which is `captured` unless a fallback was
            // adopted above. Anything else compares the live element to one we never restored to.
            var pass = (RuntimeIdsEqual(verified.RuntimeId, selectedProbe.RuntimeId)
                        && IsEditableShapedForCapturedRescue(verified.Shape))
                       || verified.Shape.ControlTypeId == UiaEditControlTypeId;
            if (!pass)
                return PreRestoreStage.BlockedWith(PreRestoreBlock.NoEditableFocused, uiaRestored: true);

            return new PreRestoreStage(PreRestoreBlock.None, UiaRestored: true, PreRestorePassKind.Rescue, verified.RuntimeId);
        }

        // Non-blocked path: PST-4 identity-verified restore when a captured element
        // exists; legacy restore (with its recapture fallback) otherwise.
        if (identityRestore == null)
            return PreRestoreStage.NotBlocked(uiaRestored: restoreUiaFocus());


        var restored = RunIdentityVerifiedRestore(identityRestore, probePostRestoreIdentity);
        // PST-7: an EffectiveRuntimeId (when the text path asked for one) rides on a
        // Kind == None stage. That deliberately does NOT change any existing routing —
        // EvaluatePostRestore selects the identity-bound drift net on
        // UsesIdentityBoundDriftNet (Kind != None) and RequiresDeliveryVerification is
        // likewise Kind-scoped, so a normal stage still takes the Edit-only probe
        // (reviewer-confirmed in code; pinned by PreRestoreStage tests).
        return restored.Block == PreRestoreBlock.None
            ? new PreRestoreStage(PreRestoreBlock.None, restored.UiaRestored,
                PreRestorePassKind.None, restored.EffectiveRuntimeId)
            : PreRestoreStage.BlockedWith(restored.Block, restored.UiaRestored);
    }

    /// <summary>
    /// PST-6 last-resort live-shape fallback on the BLOCKED path, consulted ONLY
    /// after the rescue could not engage. Exactly two evidence-backed pass cases
    /// (plan round 2, 2026-07-29):
    /// <list type="bullet">
    /// <item><b>DeadCaptureLiveShape</b> — the captured element is certified dead
    /// (stale RCW; Chromium re-rendered the composer between recording start and
    /// paste — the Claude Desktop 19:38:35 field case) and the LIVE focused
    /// element, probed in place with NO SetFocus and NO recapture (PST-2b's
    /// evidence stays intact), is editable-shaped with a readable runtime ID.</item>
    /// <item><b>SameElementShapeTransition</b> — the captured element is alive
    /// with identity but was NOT editable-shaped at capture (unhydrated Chromium
    /// a11y tree at first UIA touch), and the live focused element IS that same
    /// element (runtime-ID match) now reading editable-shaped. A DIFFERENT live
    /// element never passes — that is the PST-4 wrong-tab hazard.</item>
    /// </list>
    /// <see cref="CapturedProbeKind.ProbeUnavailable"/> proves nothing and never
    /// enables the fallback; a null/id-less/non-editable live probe fails CLOSED
    /// (the baseline here is a block). A pass carries the live element's runtime
    /// ID so the post-restore drift net applies the identity-bound broad
    /// predicate — an Edit-only drift net would re-block every pass (Chromium
    /// keeps Win32 focus on the top-level frame permanently).
    /// </summary>
    private static PreRestoreStage DecideBlockedLiveShapeFallback(
        CapturedElementProbe captured, Func<UiaShapeWithIdentity?>? probeLiveShape)
    {
        var blocked = PreRestoreStage.BlockedWith(PreRestoreBlock.NoEditableFocused, uiaRestored: false);
        if (probeLiveShape == null)
            return blocked;

        var deadCapture = captured.Kind == CapturedProbeKind.ElementUnavailable;
        var hydrationCandidate = captured.Kind == CapturedProbeKind.AliveWithIdentity;
        if (!deadCapture && !hydrationCandidate)
            return blocked;

        var live = probeLiveShape();
        if (live is not { } l
            || l.RuntimeId is not { Length: > 0 }
            || !IsEditableShapedForCapturedRescue(l.Shape))
            return blocked;

        if (deadCapture)
            return new PreRestoreStage(PreRestoreBlock.None, UiaRestored: false, PreRestorePassKind.DeadCaptureLiveShape, l.RuntimeId);

        return RuntimeIdsEqual(l.RuntimeId, captured.RuntimeId)
            ? new PreRestoreStage(PreRestoreBlock.None, UiaRestored: false, PreRestorePassKind.SameElementShapeTransition, l.RuntimeId)
            : blocked;
    }

    /// <summary>
    /// PST-4 identity-verified restore ("paste where intended, or nowhere").
    /// Field bug 2026-07-08: Windows Terminal is ONE top-level window for all
    /// tabs — recording in tab A, clicking to tab B, and pasting landed in B
    /// because the fresh-recapture fallback recaptured B's TermControl after
    /// SetFocus on hidden tab A's control failed. Rules:
    /// <list type="bullet">
    /// <item>Fresh recapture is legal ONLY on a certified-dead captured element
    /// (probe or SetFocus returns the stale signature) — the Chromium DOM
    /// re-render case it was built for.</item>
    /// <item>An ALIVE captured element must end up focused (identity match) or
    /// the paste blocks as <see cref="PreRestoreBlock.FocusMovedInTarget"/> —
    /// never redirected to whatever the user focused since.</item>
    /// <item>Identity-unknown rows fail toward today's behavior WITHOUT
    /// recapture: restore success proceeds fail-open; non-dead restore failure
    /// blocks (an alive-but-unfocusable target with unverifiable focus is the
    /// wrong-tab signature).</item>
    /// </list>
    /// </summary>
    public static IdentityVerifiedRestoreResult RunIdentityVerifiedRestore(
        IdentityVerifiedRestore restore,
        Func<UiaShapeWithIdentity?>? probePostRestoreIdentity = null)
    {
        var captured = restore.ProbeCaptured();
        switch (captured.Kind)
        {
            case CapturedProbeKind.ElementUnavailable:
                // Certified dead — legacy semantics: direct attempt, then recapture.
                var direct = restore.RestoreDirect();
                if (direct == RestoreFocusResult.Success)
                    return IdentityVerifiedRestoreResult.Unbound(PreRestoreBlock.None, true);
                return IdentityVerifiedRestoreResult.Unbound(PreRestoreBlock.None, restore.Recapture());

            case CapturedProbeKind.ProbeUnavailable:
            case CapturedProbeKind.AliveIdentityUnknown:
            {
                var result = restore.RestoreDirect();
                if (result == RestoreFocusResult.Success)
                    return IdentityVerifiedRestoreResult.Unbound(PreRestoreBlock.None, true); // fail open — can't verify, SetFocus vouches
                if (result == RestoreFocusResult.ElementUnavailable)
                    return IdentityVerifiedRestoreResult.Unbound(PreRestoreBlock.None, restore.Recapture()); // second dead signal → legacy
                return IdentityVerifiedRestoreResult.Unbound(PreRestoreBlock.FocusMovedInTarget, false);
            }

            case CapturedProbeKind.AliveWithIdentity:
            default:
            {
                var live = restore.ProbeLiveIdentity();
                if (live is { } l && RuntimeIdsEqual(l.RuntimeId, captured.RuntimeId))
                {
                    // Focus never left the captured element — proceed regardless of
                    // the (still attempted, non-gating) direct restore: Chromium needs
                    // the DOM-depth SetFocus, Win32 targets don't.
                    //
                    // PST-7: this arm deliberately does NOT recapture on any result,
                    // including ElementUnavailable — preserving today's behaviour exactly,
                    // because this function is SHARED with the image paste path (Codex plan
                    // round 2: adding a recapture here would change image behaviour even
                    // with probePostRestoreIdentity null).
                    var matchDirect = restore.RestoreDirect();
                    var restoredOk = matchDirect == RestoreFocusResult.Success;
                    return new IdentityVerifiedRestoreResult(
                        PreRestoreBlock.None, restoredOk,
                        // The identity is proven only by a probe taken AFTER this last
                        // focus-changing call — the pre-call match is a stale answer — and only
                        // when that call SETTLED (AllowsIdentityProof).
                        ProveEffectiveIdentity(probePostRestoreIdentity, captured.RuntimeId, matchDirect));
                }

                var result = restore.RestoreDirect();
                if (result == RestoreFocusResult.ElementUnavailable)
                    return IdentityVerifiedRestoreResult.Unbound(PreRestoreBlock.None, restore.Recapture()); // probe→restore race with a re-render

                var reProbe = restore.ProbeLiveIdentity();
                if (reProbe is { } l2 && RuntimeIdsEqual(l2.RuntimeId, captured.RuntimeId))
                    // This re-probe already ran after the restore, so it IS the post-restore
                    // proof — no extra probe needed on this arm. It is still only PROOF when
                    // the restore SETTLED: a timed-out Unavailable leaves a live SetFocus that
                    // can move focus after this read (Codex diff round 1, finding 2).
                    return new IdentityVerifiedRestoreResult(
                        PreRestoreBlock.None, result == RestoreFocusResult.Success,
                        probePostRestoreIdentity == null || !AllowsIdentityProof(result)
                            ? null
                            : l2.RuntimeId);

                var liveIdentityUnreadable = reProbe is not { } lr || lr.RuntimeId is not { Length: > 0 };
                if (result == RestoreFocusResult.Success && liveIdentityUnreadable)
                    return IdentityVerifiedRestoreResult.Unbound(PreRestoreBlock.None, true); // documented fail-open (plan C3)

                return IdentityVerifiedRestoreResult.Unbound(
                    PreRestoreBlock.FocusMovedInTarget, result == RestoreFocusResult.Success);
            }
        }
    }

    /// <summary>
    /// PST-7: prove which element holds focus AFTER the last focus-changing call. Accepts
    /// the expected element, or a CHILD EDIT — the restore legitimately lands focus on a
    /// child Edit of a contenteditable container, and both
    /// <c>UiaFocusBridge.VerifyPostSetFocusIdentity</c> and the PST-3 rescue already accept
    /// that transition, so a strict equality test here would refuse the very editor the
    /// restore correctly reached. The child arm additionally requires a SUCCESSFUL direct
    /// restore: a child Edit observed without one is not evidence the restore landed there.
    /// Null (no probe supplied, unsettled restore, unreadable, id-less, or a different
    /// element) ⇒ no identity to re-check later ⇒ the PST-7 guard is skipped and behaviour
    /// is today's.
    /// </summary>
    private static int[]? ProveEffectiveIdentity(
        Func<UiaShapeWithIdentity?>? probe, int[]? expectedRuntimeId, RestoreFocusResult direct)
    {
        // Gate BEFORE probing: an unsettled restore cannot be proven at any cost, and this
        // also keeps the no-probe (image) path free of extra UIA calls.
        if (probe == null || !AllowsIdentityProof(direct))
            return null;
        if (probe() is not { } live || live.RuntimeId is not { Length: > 0 })
            return null;
        if (RuntimeIdsEqual(live.RuntimeId, expectedRuntimeId))
            return live.RuntimeId;
        return direct == RestoreFocusResult.Success && live.Shape.ControlTypeId == UiaEditControlTypeId
            ? live.RuntimeId
            : null;
    }

    /// <summary>
    /// PST-7/PST-10: may a post-restore probe be trusted as PROOF of where focus ended up?
    /// Only when NO focus mutation with an unknown landing time can still be in flight on the
    /// restore worker, and the element was not certified dead.
    /// <list type="bullet">
    /// <item><see cref="RestoreFocusResult.Success"/> / <see cref="RestoreFocusResult.Failed"/>
    /// — the call ran to completion; the probe observes a settled state.</item>
    /// <item><see cref="RestoreFocusResult.Unavailable"/> — binds since PST-10: the value now
    /// GUARANTEES the restore performed no focus-changing call and none is pending on that
    /// worker (never-activated / abandoned-before-start / mutation-barred — see the enum doc
    /// for the admission-CAS chain). PST-7 round 1 refused this value because the old enum
    /// conflated it with the live-straggler timeout; the PST-10 split restores the guard on
    /// the provably-safe sources, which the round-1 commit named as the desired follow-up.</item>
    /// <item><see cref="RestoreFocusResult.ElementUnavailable"/> — the call completed, but UIA
    /// certified the element DEAD. A live probe returning its runtime ID is then contradictory
    /// evidence (or a recycled ID), and arming the guard on it risks a FALSE block, which is
    /// the failure mode PST-7 must not introduce.</item>
    /// <item><see cref="RestoreFocusResult.BusyPending"/> — an incumbent of UNKNOWN kind owns
    /// the worker; it may be a PREVIOUS attempt's pending SetFocus, which can land between our
    /// probe and the keystroke. Never binds.</item>
    /// <item><see cref="RestoreFocusResult.TimedOutPending"/> — our own SetFocus is
    /// committed/executing with an unknown landing time (a 500 ms bound answering ~17 s later
    /// is documented live behaviour — PST-8/WhatsApp). The probe runs on the dedicated probe
    /// worker (PST-2b), which does not serialize against the restore worker, so the in-flight
    /// SetFocus is genuinely unobservable. Binding would re-create the exact race PST-7
    /// closes while reporting "Proven". Never binds.</item>
    /// </list>
    /// </summary>
    private static bool AllowsIdentityProof(RestoreFocusResult result)
        => result is RestoreFocusResult.Success
            or RestoreFocusResult.Failed
            or RestoreFocusResult.Unavailable;
}

/// <summary>
/// Editability shape of a UIA element (PST-3): the ControlType plus the
/// pattern flags that discriminate Chromium contenteditables from read-only
/// page documents — ControlType alone cannot (both can read Document/Group).
/// Constructed via <see cref="FromReads"/> so partial read failures fold
/// conservatively.
/// </summary>
internal readonly record struct UiaElementShape(
    int ControlTypeId,
    bool HasTextPattern,
    bool HasValuePattern,
    bool ValueIsReadOnly,
    bool IsKeyboardFocusable)
{
    /// <summary>
    /// Fold raw per-property reads into a shape. ControlType read failure →
    /// null (probe failed; callers keep their existing fail-open/fail-closed
    /// contract). ANY secondary read failure → all four secondary fields
    /// forced to the maximally-NON-editable combination, so only
    /// ControlType==Edit can pass the rescue predicate — a partially-read
    /// shape must never be MORE permissive than today's Edit-only rule
    /// (Codex plan round 1: a failed HasValuePattern read defaulting to false
    /// would have made "TextPattern && no ValuePattern" editable-shaped).
    /// </summary>
    public static UiaElementShape? FromReads(
        int? controlTypeId,
        bool? hasTextPattern,
        bool? hasValuePattern,
        bool? valueIsReadOnly,
        bool? isKeyboardFocusable)
    {
        if (controlTypeId is not { } controlType)
            return null;

        if (hasTextPattern is not { } text
            || hasValuePattern is not { } value
            || valueIsReadOnly is not { } readOnly
            || isKeyboardFocusable is not { } focusable)
        {
            return new UiaElementShape(
                controlType,
                HasTextPattern: false,
                HasValuePattern: true,
                ValueIsReadOnly: true,
                IsKeyboardFocusable: false);
        }

        return new UiaElementShape(controlType, text, value, readOnly, focusable);
    }
}

/// <summary>
/// A live focused element's shape plus its UIA runtime ID (PST-3 identity
/// binding). <see cref="RuntimeId"/> null/empty = identity unknown — the
/// drift net then falls back to the Edit-only rule for that probe.
/// </summary>
internal readonly record struct UiaShapeWithIdentity(UiaElementShape Shape, int[]? RuntimeId);

/// <summary>
/// PST-4: typed status of the CAPTURED recording-start element probe. The
/// alive/dead question must NOT be inferred from a failed identity read — a
/// busy worker or transient COM failure says nothing about the element
/// (Codex PST-4 round 1). Only <see cref="ElementUnavailable"/> (the stale-RCW
/// signature) certifies death, and only death legalizes the fresh-recapture
/// fallback.
/// </summary>
internal enum CapturedProbeKind
{
    /// <summary>Property reads succeeded, runtime ID present.</summary>
    AliveWithIdentity,
    /// <summary>Element answered property reads but the runtime-ID read failed —
    /// alive, identity unknown.</summary>
    AliveIdentityUnknown,
    /// <summary>Reads failed with the stale-RCW signature (0x80040201 /
    /// InvalidComObjectException / disconnected proxy) — the element is DEAD
    /// (Chromium DOM re-render). Fresh recapture is legal here and only here.</summary>
    ElementUnavailable,
    /// <summary>Timeout / busy interlock / worker not ready / non-stale read
    /// failure — says NOTHING about the element. Never enables recapture.</summary>
    ProbeUnavailable
}

/// <summary>Typed result of the captured-element probe (single bounded
/// main-worker item: shape + runtime ID + status classification).</summary>
internal readonly record struct CapturedElementProbe(
    CapturedProbeKind Kind, UiaElementShape? Shape, int[]? RuntimeId)
{
    public bool IsAlive => Kind is CapturedProbeKind.AliveWithIdentity or CapturedProbeKind.AliveIdentityUnknown;
}

/// <summary>
/// PST-4: typed SetFocus outcome. <see cref="ElementUnavailable"/> is the
/// second dead signal (besides the probe's) — it routes to the legacy
/// recapture-allowed path; <see cref="Failed"/>/<see cref="Unavailable"/> do
/// NOT (an alive-but-unfocusable element is the wrong-tab signature).
/// </summary>
public enum RestoreFocusResult
{
    Success,
    /// <summary>SetFocus returned UIA_E_ELEMENTNOTAVAILABLE (0x80040201) or the
    /// RCW threw the stale signature — element dead; legacy recapture legal.</summary>
    ElementUnavailable,
    /// <summary>SetFocus ran and failed for a non-dead reason.</summary>
    Failed,
    /// <summary>
    /// The restore performed NO focus-changing call and none is pending on its worker —
    /// guaranteed, not assumed (PST-10 restored this value's original documented meaning
    /// after PST-7 found it false for the old timeout branch). Exactly three sources:
    /// worker never activated (nothing was ever queued), the queued item was ABANDONED
    /// before start (<see cref="BoundedCallGate"/> — the body provably never runs), or the
    /// mutation was BARRED while the item stalled in its pre-SetFocus reads (reads don't
    /// move focus; the commit gate is the last instruction before SetFocus, so a lost
    /// commit proves the mutation never runs). Each of the latter two implies the
    /// admission CAS was WON, which proves no PRIOR item on that worker can still mutate
    /// (busy is held from admission until an item's completion, and a mutation only runs
    /// between a won commit and completion). This chain is why
    /// <c>AllowsIdentityProof</c> may bind on this value.
    /// </summary>
    Unavailable,
    /// <summary>
    /// PST-10: the worker's admission CAS was lost — an incumbent operation of UNKNOWN
    /// kind owns the worker, and it may itself be a pending SetFocus from a PREVIOUS
    /// paste attempt (Codex PST-10 plan round 2: grouping this with
    /// <see cref="Unavailable"/> would have bound an identity across exactly the race
    /// PST-7 closes). Routes as generic failure everywhere; never bind-eligible, never
    /// legalizes recapture.
    /// </summary>
    BusyPending,
    /// <summary>
    /// PST-10: our own SetFocus was committed/executing when the bound expired — outcome
    /// unknown, focus possibly still changing (PST-8 measured a 500 ms bound answering
    /// ~17 s later). The straggler's eventual completion is logged by the bridge. Routes
    /// as generic failure everywhere; never bind-eligible, never legalizes recapture.
    /// </summary>
    TimedOutPending
}

/// <summary>
/// Narrow, testable classification of the stale-RCW COM signature (Codex PST-4
/// round 2: 0x80040201 / InvalidComObjectException / disconnected-proxy
/// COMException must never collapse into a generic false/unknown).
/// </summary>
internal static class UiaComClassification
{
    /// <summary>UIA_E_ELEMENTNOTAVAILABLE.</summary>
    public const int ElementNotAvailableHr = unchecked((int)0x80040201);
    /// <summary>RPC_E_DISCONNECTED — proxy to a dead remote object.</summary>
    public const int RpcDisconnectedHr = unchecked((int)0x80010108);
    /// <summary>RPC_S_SERVER_UNAVAILABLE (HRESULT-wrapped) — target process gone.</summary>
    public const int RpcServerUnavailableHr = unchecked((int)0x800706BA);

    public static bool IsStaleHr(int hr)
        => hr is ElementNotAvailableHr or RpcDisconnectedHr or RpcServerUnavailableHr;

    public static bool IsStaleException(Exception ex)
        => ex is global::System.Runtime.InteropServices.InvalidComObjectException
           || (ex is global::System.Runtime.InteropServices.COMException com && IsStaleHr(com.HResult));
}

/// <summary>
/// PST-3 rescue delegates for <see cref="NoEditableFocusGate.RunPreRestoreStageAsync"/>.
/// <see cref="ProbeCaptured"/> and <see cref="RestoreDirect"/> run on the
/// MAIN UIA worker (the captured RCW's home apartment); <see cref="VerifyLiveShape"/>
/// runs on the dedicated probe worker and returns the verified element's
/// runtime ID for the identity-bound drift net. <see cref="RestoreDirect"/>
/// MUST be the direct SetFocus path only — never the fresh-recapture fallback
/// (structurally enforced at the call site via a direct-only helper; pinned by
/// call-count tests). PST-4: the captured probe is TYPED so the rescue only
/// engages on an alive element.
/// </summary>
/// <param name="Fallback">
/// PST-11: a SECOND candidate, tried only when the first is not alive-and-editable — the element
/// the RECORDING pasted into, held by <c>RedoFocusRetention</c> across the redo picker. Null on
/// every non-redo paste, which makes the whole chain the pre-PST-11 single evaluation.
///
/// <para>It carries no <c>VerifyLiveShape</c> of its own **by design**: the verification probes
/// whichever element now holds focus, so it is a property of the attempt rather than of the
/// candidate, and giving each candidate one would invite two subtly different answers to a
/// question that has one.</para>
/// </param>
internal sealed record CapturedElementRescue(
    Func<CapturedElementProbe> ProbeCaptured,
    Func<bool> RestoreDirect,
    Func<UiaShapeWithIdentity?> VerifyLiveShape,
    RescueCandidate? Fallback = null);

/// <summary>
/// PST-11: one alternative element the rescue may try — exactly the two delegates a candidate
/// needs, on the same workers as <see cref="CapturedElementRescue"/>'s own pair.
/// </summary>
internal sealed record RescueCandidate(
    Func<CapturedElementProbe> ProbeCaptured,
    Func<bool> RestoreDirect);

/// <summary>
/// PST-4 delegates for the identity-verified restore on the NON-blocked path.
/// <see cref="ProbeCaptured"/> and <see cref="RestoreDirect"/> run on the main
/// UIA worker; <see cref="ProbeLiveIdentity"/> on the probe worker;
/// <see cref="Recapture"/> is the legacy fresh-recapture fallback — invoked
/// ONLY when the captured element is certified dead (pinned by call-count
/// tests).
/// </summary>
internal sealed record IdentityVerifiedRestore(
    Func<CapturedElementProbe> ProbeCaptured,
    Func<UiaShapeWithIdentity?> ProbeLiveIdentity,
    Func<RestoreFocusResult> RestoreDirect,
    Func<bool> Recapture);

/// <summary>How the pre-restore stage blocked, if it did (PST-4: callers map
/// each to its own <see cref="PasteAttemptOutcome"/> + pill message).</summary>
public enum PreRestoreBlock
{
    None,
    NoEditableFocused,
    FocusMovedInTarget
}

/// <summary>Result of <see cref="NoEditableFocusGate.EvaluatePostRestore"/>:
/// whether to block, whether a probe produced a reading (drives which log line the
/// caller emits), and the flags-only detail string for it.</summary>
internal readonly record struct PostRestoreEvaluation(bool Block, bool ProbeProduced, string Detail);

/// <summary>
/// PST-7: outcome of <see cref="NoEditableFocusGate.RunIdentityVerifiedRestore"/>.
/// <see cref="EffectiveRuntimeId"/> is non-null ONLY when a probe taken AFTER the last
/// focus-changing call proved which element holds focus — never a pre-operation identity.
/// Null means "nothing to re-check", which makes the late guard a no-op and preserves
/// today's behaviour exactly (the image path always gets null: it supplies no probe).
/// </summary>
internal readonly record struct IdentityVerifiedRestoreResult(
    PreRestoreBlock Block, bool UiaRestored, int[]? EffectiveRuntimeId)
{
    public static IdentityVerifiedRestoreResult Unbound(PreRestoreBlock block, bool uiaRestored)
        => new(block, uiaRestored, null);

    /// <summary>2-arity deconstruction so the PST-4 call sites and tests that only care about
    /// (block, restored) read unchanged — the identity is additive.</summary>
    public void Deconstruct(out PreRestoreBlock block, out bool uiaRestored)
    {
        block = Block;
        uiaRestored = UiaRestored;
    }
}

/// <summary>What the PST-7 late identity check concluded — one Information-level outcome per
/// guarded attempt, and the four values SUM to total attempts so UAT can compute real
/// coverage instead of inferring it (Codex plan round 2: a Debug-level branch would be
/// invisible under the production Information sink and would silently undercount).</summary>
internal enum PreSendIdentityOutcome
{
    /// <summary>Identity proven after all focus work and still the focused element — proceed.</summary>
    Proven,
    /// <summary>Identity proven earlier and provably DIFFERENT now — block, amber decline.</summary>
    Blocked,
    /// <summary>Identity was proven earlier but is unreadable now — proceed (fail open). This is
    /// the PST-7 coverage gap: the wrong-field window stays open on this branch.</summary>
    UnreadableGap,
    /// <summary>No identity was ever proven, so there is nothing to check — proceed. The other
    /// PST-7 coverage gap (dead captures, unknown probes, rescue's id-less child arm).</summary>
    NoPriorGap
}

/// <summary>How a blocked pre-restore evaluation was overturned, if it was (PST-6).</summary>
internal enum PreRestorePassKind
{
    /// <summary>Normal non-blocked path, or a block that stood.</summary>
    None,
    /// <summary>PST-3 captured-element rescue: focus restored to the recording-start
    /// editable and live-verified.</summary>
    Rescue,
    /// <summary>PST-6: captured element certified dead; the live focused element is
    /// editable-shaped and passes in place (no SetFocus, no recapture).</summary>
    DeadCaptureLiveShape,
    /// <summary>PST-6: the SAME captured element (runtime-ID match) transitioned to
    /// an editable shape after a11y hydration.</summary>
    SameElementShapeTransition
}

/// <summary>
/// Outcome of <see cref="NoEditableFocusGate.RunPreRestoreStageAsync"/>:
/// either the paste is blocked (<see cref="Block"/> says how; evidence
/// preserved) or the UIA restore ran and <see cref="UiaRestored"/> carries its
/// result. <see cref="Kind"/> says how a would-be block was overturned; every
/// non-<see cref="PreRestorePassKind.None"/> kind carries
/// <see cref="VerifiedRuntimeId"/> — the post-restore drift net switches to the
/// identity-bound broad predicate on that token, because targets like Claude
/// Desktop keep Win32 focus on the top-level frame permanently, so an Edit-only
/// drift net would re-block every pass. <see cref="UsedRescue"/> is preserved
/// as the PST-3/PST-4 test-pinned view (<c>Kind == Rescue</c>).
/// </summary>
internal readonly record struct PreRestoreStage(
    PreRestoreBlock Block, bool UiaRestored, PreRestorePassKind Kind, int[]? VerifiedRuntimeId)
{
    public bool Blocked => Block != PreRestoreBlock.None;

    public bool UsedRescue => Kind == PreRestorePassKind.Rescue;

    /// <summary>
    /// Does the post-restore drift net use the IDENTITY-BOUND broad predicate for
    /// this stage? True for EVERY overturned block, not just the PST-3 rescue
    /// (Codex diff review round 2): a `DeadCaptureLiveShape` /
    /// `SameElementShapeTransition` pass admits a Chromium Group composer, and the
    /// Edit-only rule would immediately re-block it downstream — silently undoing
    /// the pass and restoring the exact false "No text box focused" this wave
    /// exists to fix. Every non-None kind carries a
    /// <see cref="VerifiedRuntimeId"/>, which is what keeps the broad predicate
    /// bound to one element.
    /// </summary>
    public bool UsesIdentityBoundDriftNet => Kind != PreRestorePassKind.None;

    /// <summary>
    /// Do the PST-6 fallback kinds' relaxed semantics REQUIRE a working delivery
    /// verification? Yes — and this is a safety invariant, not an optimisation
    /// (Codex diff review round 3).
    /// <para>The fallbacks overturn a block that would otherwise have stood, and
    /// the entire justification for doing so is that a swallowed Ctrl+V will be
    /// CAUGHT by the post-paste readback. If that readback is unavailable (the
    /// strict password/support gate declining, an unreadable element), the
    /// promise cannot be kept: the paste would proceed unverified AND unblocked,
    /// report success, and fire Enter — strictly worse than the pre-PST-6 block,
    /// which at least told the user to press Ctrl+V. So a fallback pass without a
    /// baseline reverts to the original block.</para>
    /// <para>The PST-3 <see cref="PreRestorePassKind.Rescue"/> is deliberately
    /// EXCLUDED: it performs a real SetFocus plus its own fail-closed live
    /// verification, which is independent evidence that predates this wave, and
    /// its behavior must not regress when verification is unavailable.</para>
    /// </summary>
    public bool RequiresDeliveryVerification
        => Kind is PreRestorePassKind.DeadCaptureLiveShape or PreRestorePassKind.SameElementShapeTransition;

    public static PreRestoreStage NotBlocked(bool uiaRestored)
        => new(PreRestoreBlock.None, uiaRestored, PreRestorePassKind.None, VerifiedRuntimeId: null);

    public static PreRestoreStage BlockedWith(PreRestoreBlock block, bool uiaRestored)
        => new(block, uiaRestored, PreRestorePassKind.None, VerifiedRuntimeId: null);
}

/// <summary>
/// Formats the exactly-one-per-attempt paste summary line. Kept pure so tests
/// can pin the shape without a log sink.
/// </summary>
internal static class PasteSummary
{
    public static string Format(
        PasteAttemptOutcome outcome,
        int ageMs,
        string route,
        bool uiaTarget,
        bool uiaFocused,
        string escalation)
        => $"outcome={outcome} ageMs={ageMs} route=\"{route}\" uiaTarget={uiaTarget} uiaFocused={uiaFocused} escalation={escalation}";
}

/// <summary>
/// Which instant of the text-paste flow a clipboard probe sample was captured at
/// (REL-16 diagnostic — the 2026-07-24 Notepad++ empty-paste investigation: text verified
/// on the clipboard ~10 ms before Ctrl+V, caret never moved, send-Enter landed; every
/// traced mechanism ruled out). Phase order is canonical — samples must arrive in
/// declaration order within one attempt.
/// </summary>
internal enum ClipboardProbePhase
{
    /// <summary>Detailed sample right after the text was set (under the write lease).
    /// Its sequence number also best-effort-tags VoiceWink's own text write.</summary>
    AfterSet,

    /// <summary>Sequence-only sample inside <c>SendCtrlVModifierSafeAsync</c>, after the
    /// modifier wait and immediately before <c>SendInput</c> — the race boundary pays one
    /// open-free syscall, never a detailed sample.</summary>
    PreSend,

    /// <summary>Sequence-only sample immediately after <c>SendInput</c> returned (the Win32
    /// error is snapshotted first — see <see cref="CtrlVSendSequence"/>).</summary>
    PostSend,

    /// <summary>Detailed sample after the 200 ms post-paste delay, captured BEFORE the final
    /// foreground recheck that gates the send-Enter (the recheck stays the last gate).</summary>
    PreEnter,
}

/// <summary>
/// One clipboard identity sample: the system clipboard sequence number (bracketed
/// before/after on detailed samples — the reads are not atomic), the clipboard owner
/// (hwnd + pid; often absent — VoiceWink itself opens with a null owner window, so
/// ownership is a secondary signal, never proof), and CF_UNICODETEXT / CF_DIB
/// availability (availability includes synthesized formats — it does not identify the
/// exact stored format). All source calls are open-free Win32; no clipboard open, no
/// content read — deliberately, so a sample cannot perturb or stall the paste path,
/// and no transcript text/length ever reaches a log.
/// </summary>
internal readonly record struct ClipboardProbeSample(
    ClipboardProbePhase Phase,
    bool Captured,
    bool Detailed,
    uint SeqBefore,
    uint SeqAfter,
    IntPtr OwnerHwnd,
    uint OwnerPid,
    bool HasText,
    bool HasDib)
{
    /// <summary>Bracketing reads agree — the sample observed one clipboard generation.
    /// Seq-only samples are trivially stable (one read).</summary>
    public bool IsStable => Captured && SeqBefore == SeqAfter;

    /// <summary>Zero means <c>GetClipboardSequenceNumber</c> was unavailable (no window
    /// station access) — never a real generation to compare.</summary>
    public bool SeqAvailable => Captured && SeqBefore != 0;

    public static ClipboardProbeSample Failed(ClipboardProbePhase phase)
        => new(phase, false, false, 0, 0, IntPtr.Zero, 0, false, false);

    public static ClipboardProbeSample SeqOnly(ClipboardProbePhase phase, uint seq)
        => new(phase, true, false, seq, seq, IntPtr.Zero, 0, false, false);
}

/// <summary>
/// Attempt-scoped collector for one text paste's probe endpoints (REL-16). Knows its
/// EXPECTED phases — <see cref="ClipboardProbePhase.AfterSet"/> / <see cref="ClipboardProbePhase.PreSend"/> /
/// <see cref="ClipboardProbePhase.PostSend"/> always, plus <see cref="ClipboardProbePhase.PreEnter"/>
/// only after <see cref="ExpectPreEnter"/> (a successful paste with send-Enter enabled) — so a
/// truncated attempt (clipboard-set failure, gated exit) reads as indeterminate instead of a
/// vacuous "no mutation observed". Duplicate or out-of-order additions poison
/// <see cref="Integrity"/>. Emission is single-shot via the atomic <see cref="TryMarkEmitted"/>.
/// Pure data — no clipboard, no logging, no process work.
/// </summary>
internal sealed class ClipboardProbeAttempt
{
    private static readonly ClipboardProbePhase[] CorePhases =
        [ClipboardProbePhase.AfterSet, ClipboardProbePhase.PreSend, ClipboardProbePhase.PostSend];

    private readonly object _gate = new();
    private readonly Dictionary<ClipboardProbePhase, ClipboardProbeSample> _slots = new();
    private readonly List<ClipboardProbeSample> _order = new();
    private bool _duplicateOrOutOfOrder;
    private bool _expectPreEnter;
    private int _emitted;

    /// <summary>Require the <see cref="ClipboardProbePhase.PreEnter"/> endpoint too — called
    /// only when the paste succeeded AND send-Enter is enabled.</summary>
    public void ExpectPreEnter()
    {
        lock (_gate) _expectPreEnter = true;
    }

    public void Add(in ClipboardProbeSample sample)
    {
        lock (_gate)
        {
            if (_slots.ContainsKey(sample.Phase)
                || (_order.Count > 0 && sample.Phase <= _order[^1].Phase))
            {
                _duplicateOrOutOfOrder = true;
            }
            _slots[sample.Phase] = sample;
            _order.Add(sample);
        }
    }

    /// <summary>True exactly once — the winner performs the emission.</summary>
    public bool TryMarkEmitted()
        => global::System.Threading.Interlocked.Exchange(ref _emitted, 1) == 0;

    /// <summary>False when a duplicate or out-of-canonical-order endpoint arrived —
    /// the verdict must then be indeterminate.</summary>
    public bool Integrity
    {
        get { lock (_gate) return !_duplicateOrOutOfOrder; }
    }

    /// <summary>Endpoints in arrival order (canonical order when <see cref="Integrity"/> holds).</summary>
    public IReadOnlyList<ClipboardProbeSample> Ordered
    {
        get { lock (_gate) return _order.ToArray(); }
    }

    /// <summary>Expected phases that never arrived (any ⇒ indeterminate verdict).</summary>
    public IReadOnlyList<ClipboardProbePhase> MissingRequired
    {
        get
        {
            lock (_gate)
            {
                ClipboardProbePhase[] required = _expectPreEnter
                    ? [.. CorePhases, ClipboardProbePhase.PreEnter]
                    : CorePhases;
                return required.Where(p => !_slots.ContainsKey(p)).ToArray();
            }
        }
    }
}

/// <summary>
/// Formats one probe endpoint line (REL-16). Deliberately carries NO owner process
/// name — the name is PII-adjacent and rides a dedicated redacted log property
/// (<c>{OwnerProcess}</c>) appended by the emitter, so the Sentry breadcrumb path and
/// the rendered-line export scrubs both catch it. Kept pure so tests pin the shape.
/// </summary>
internal static class ClipboardProbeSummary
{
    public static string PhaseLabel(ClipboardProbePhase phase) => phase switch
    {
        ClipboardProbePhase.AfterSet => "after-set",
        ClipboardProbePhase.PreSend => "pre-send",
        ClipboardProbePhase.PostSend => "post-send",
        ClipboardProbePhase.PreEnter => "pre-enter",
        _ => phase.ToString(),
    };

    public static string Format(in ClipboardProbeSample s)
    {
        var label = PhaseLabel(s.Phase);
        if (!s.Captured)
            return $"Clipboard probe [{label}]: capture failed";

        var seq = !s.SeqAvailable
            ? "seq=unavailable"
            : s.IsStable
                ? $"seq={s.SeqBefore}"
                : $"seq={s.SeqBefore}->{s.SeqAfter} UNSTABLE";

        return s.Detailed
            ? $"Clipboard probe [{label}]: {seq} ownerHwnd=0x{s.OwnerHwnd:X} ownerPid={s.OwnerPid} text={s.HasText} dib={s.HasDib}"
            : $"Clipboard probe [{label}]: {seq}";
    }
}

/// <summary>
/// Cross-endpoint verdict for one attempt (REL-16). "no mutation observed" requires EVERY
/// expected endpoint present, captured, stable, and nonzero with all sequence numbers equal —
/// anything less (missing/failed/unstable/unavailable endpoint, duplicate/out-of-order
/// arrival) is INDETERMINATE, never a vacuous pass. A sequence change is reported as
/// "mutation observed", deliberately NOT attributed to a writer — correlation against the
/// best-effort tagged write lines (text after-set seq, the image writer's "Image set on
/// clipboard … seq=" line) is manual; other writers are untagged.
/// </summary>
internal static class ClipboardProbeReport
{
    public static string Describe(ClipboardProbeAttempt attempt)
    {
        if (!attempt.Integrity)
            return "indeterminate (duplicate or out-of-order endpoints)";

        var missing = attempt.MissingRequired;
        if (missing.Count > 0)
            return $"indeterminate (missing {string.Join(", ", missing.Select(ClipboardProbeSummary.PhaseLabel))})";

        var endpoints = attempt.Ordered;
        foreach (var s in endpoints)
        {
            if (!s.Captured)
                return $"indeterminate ({ClipboardProbeSummary.PhaseLabel(s.Phase)} capture failed)";
            if (!s.IsStable)
                return $"indeterminate ({ClipboardProbeSummary.PhaseLabel(s.Phase)} unstable)";
            if (!s.SeqAvailable)
                return $"indeterminate ({ClipboardProbeSummary.PhaseLabel(s.Phase)} sequence unavailable)";
        }

        // Report EVERY differing adjacent window, not just the first: an unrelated
        // clipboard-manager touch during the pre-send gates must not hide a second
        // mutation in the post-send..pre-enter window — the ~200 ms post-lease gap that
        // is the ONLY interval where a lease-serialized internal writer (the background
        // image job) can actually collide, i.e. the window this diagnostic exists to
        // observe (adversarial workflow review, 2026-07-24).
        List<string>? mutations = null;
        for (var i = 1; i < endpoints.Count; i++)
        {
            if (endpoints[i].SeqBefore != endpoints[i - 1].SeqBefore)
            {
                (mutations ??= []).Add(
                    $"{ClipboardProbeSummary.PhaseLabel(endpoints[i - 1].Phase)}(seq={endpoints[i - 1].SeqBefore})"
                    + $" -> {ClipboardProbeSummary.PhaseLabel(endpoints[i].Phase)}(seq={endpoints[i].SeqBefore})");
            }
        }
        if (mutations != null)
        {
            return $"mutation observed {string.Join(", ", mutations)}"
                + " — correlate against tagged clipboard-write seq lines (best-effort)";
        }

        return $"no mutation observed (seq={endpoints[0].SeqBefore} stable"
            + $" {ClipboardProbeSummary.PhaseLabel(endpoints[0].Phase)}..{ClipboardProbeSummary.PhaseLabel(endpoints[^1].Phase)})";
    }
}

/// <summary>
/// Pins the send → error-snapshot → post-send-sequence ordering (REL-16). The Win32 error
/// MUST be read on the line after <c>SendInput</c>, before any further P/Invoke — a probe
/// call in between could overwrite it (Codex plan review round 2). Pure and delegate-driven
/// so the ordering is unit-testable without touching real input APIs.
/// </summary>
internal static class CtrlVSendSequence
{
    public static (uint Sent, int LastError, uint PostSendSeq) Run(
        Func<uint> send, Func<int> lastError, Func<uint> postSendSeq)
    {
        var sent = send();
        var err = lastError();
        var seq = postSendSeq();
        return (sent, err, seq);
    }
}

/// <summary>Outcome of the post-paste Enter phase (REL-16 seam).</summary>
internal enum PostPasteEnterOutcome
{
    /// <summary>Paste failed or send-Enter disabled — nothing ran.</summary>
    NotRequested,

    /// <summary>The final foreground recheck failed after the delay — Enter withheld
    /// (the caller logs the existing warning).</summary>
    SkippedForeground,

    Pressed,
}

/// <summary>
/// Pins the post-paste Enter ordering (REL-16): delay → capture the pre-enter probe →
/// final foreground recheck (stays the LAST gate before Enter) → press Enter; the emit
/// callback ALWAYS runs (finally), even when Enter throws — paired with the caller's own
/// idempotent emit backstop around the paste itself. Pure and delegate-driven so the
/// ordering, gating, and emit-on-throw guarantees are unit-testable without real
/// keyboard/clipboard APIs.
/// </summary>
internal static class PostPasteEnterSequence
{
    public static async Task<PostPasteEnterOutcome> RunAsync(
        bool sendEnter,
        Func<Task> delay,
        Action capturePreEnter,
        Func<bool> foregroundOk,
        Func<Task> pressEnter,
        Action emit)
    {
        try
        {
            if (!sendEnter)
                return PostPasteEnterOutcome.NotRequested;

            await delay();
            capturePreEnter();
            if (!foregroundOk())
                return PostPasteEnterOutcome.SkippedForeground;

            await pressEnter();
            return PostPasteEnterOutcome.Pressed;
        }
        finally
        {
            emit();
        }
    }
}
