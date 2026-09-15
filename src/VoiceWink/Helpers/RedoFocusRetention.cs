namespace VoiceWink.Helpers;

/// <summary>
/// The editable element a RECORDING pasted into, held for the redo that may follow (PST-11).
///
/// <para><b>The defect it closes.</b> A redo re-captures whatever has UIA focus at picker-OPEN time
/// and hands that to the paste path. By then focus has legitimately moved off the composer — the
/// dictation's send-Enter submitted the message, or the user clicked — so the redo supplies a
/// focusable <c>Group</c> where the recording had an <c>Edit</c>, and the PST-2 gate declines.
/// Measured in the owner's logs: <c>outcome=NoEditableFocused</c> with <c>escalation=none</c> and
/// the target already foreground, against <c>controlType=50004</c> on the dictation that worked
/// minutes earlier into the same window. The gate is right; the QUESTION is wrong — a redo should
/// put the improved text where the original text went.</para>
///
/// <para><b>It only ever ADDS a candidate.</b> The retained element is offered to
/// <c>PasteDiagnostics</c>'s captured-element rescue as a second candidate, on the path where the
/// gate has already blocked. Every failure mode — dead element, re-rendered composer, window
/// mismatch — degrades to exactly today's decline, because the rescue keeps its typed alive-probe
/// (PST-4) and its fail-closed live verification. Nothing here widens a gate.</para>
///
/// <para><b>Deliberately NOT a new owner kind on <see cref="CapturedFocusSlot"/></b> (Kimi plan
/// review): <see cref="CapturedFocusSlot.CaptureForRedo"/> releases any non-Recording occupant
/// before re-capturing, so the picker-open capture would destroy the very element this holds, and
/// <c>ClearRedoStatePublic</c> releases the <c>Redo</c> owner. A separate holder also keeps the
/// state out of the <c>MainViewModel</c> hub, per AGENTS.md.</para>
///
/// <para><b>No time bound, and that is reasoned rather than lazy.</b> A stale RCW cannot produce a
/// wrong paste — it can only fail the rescue's probe and land on today's decline — so the cost of
/// holding one is a single pinned cross-process proxy, and a timer would buy nothing but a second
/// way to be wrong about lifetime.</para>
///
/// <para><b>That cost is per DICTATION THAT PASTED, not per armed redo</b> (Kimi diff round 1
/// corrected an earlier claim of the latter). Retention happens at the paste site, where the
/// element is already materialized and free to take; whether a redo gets armed is not known until
/// later in the pipeline. So a plain dictation with no enhancement — which arms no redo — still
/// holds one proxy until the next recording start releases it. Gating on arming was considered and
/// not done: it would move the take into the pipeline's finally, where the paths that never
/// resolved an element (image, empty transcript) would each pay a bounded cross-process wait for an
/// element no redo can use. One idle proxy is the cheaper end of that trade, and it is bounded by
/// the next recording either way.</para>
///
/// <para><b>Threading:</b> UI-thread only, the same discipline <see cref="CapturedFocusSlot"/>
/// carries. The release is routed through the injected delegate (the bridge's MTA worker in
/// production) so a wedged target cannot block the UI thread on a cross-process <c>Release()</c>.</para>
/// </summary>
internal sealed class RedoFocusRetention
{
    private readonly Action<UiaFocusBridge.IUIAutomationElement> _release;

    private UiaFocusBridge.IUIAutomationElement? _element;
    private IntPtr _targetWindow;

    public RedoFocusRetention()
        : this(UiaFocusBridge.EnqueueRelease)
    {
    }

    internal RedoFocusRetention(Action<UiaFocusBridge.IUIAutomationElement> release)
        => _release = release;

    /// <summary>True while an element is held — for logging and tests, never a paste decision.</summary>
    internal bool HasElement => _element != null;

    /// <summary>
    /// Take ownership of the recording's element. Any element already held is released first, so a
    /// second recording can never strand the first one's proxy.
    /// </summary>
    internal void Retain(UiaFocusBridge.IUIAutomationElement? element, IntPtr targetWindow)
    {
        Release();

        // A zero window can never match at consumption (see ElementFor), so retaining against one
        // would pin a proxy nothing could ever use.
        if (element == null || targetWindow == IntPtr.Zero) return;

        _element = element;
        _targetWindow = targetWindow;
    }

    /// <summary>
    /// The retained element IF it belongs to <paramref name="resolvedTargetWindow"/>, else null.
    ///
    /// <para>The caller must pass the target the redo actually RESOLVED to, not the context's
    /// original — <c>ResolveRedoTargetWindow</c> prefers a freshly captured foreground and falls
    /// back to zero for a History redo, and offering an element for a window the paste is not aimed
    /// at is how a rescue moves focus in the wrong place. Both sides must be non-zero: zero is the
    /// clipboard-only answer, and two zeros comparing equal would turn "no target" into a match.</para>
    /// </summary>
    internal UiaFocusBridge.IUIAutomationElement? ElementFor(IntPtr resolvedTargetWindow)
        => _element != null
           && resolvedTargetWindow != IntPtr.Zero
           && resolvedTargetWindow == _targetWindow
            ? _element
            : null;

    /// <summary>
    /// Release the held element. Idempotent — every call site below is allowed to fire without
    /// knowing whether another already has.
    ///
    /// <para><b>The release SITES are the design</b>, and one of them is an anti-site: this must NOT
    /// be called from <c>ClearRedoState</c>, which runs at redo START, before the paste that needs
    /// the element. Wiring it there was the plan's Blocker — it would have shipped the whole feature
    /// as a silent no-op that no test could see (Kimi plan review). The real sites are the redo
    /// pipeline's finally, a new recording start, and the picker-cancel teardown.</para>
    /// </summary>
    internal void Release()
    {
        var element = _element;
        _element = null;
        _targetWindow = IntPtr.Zero;

        if (element != null) _release(element);
    }
}
