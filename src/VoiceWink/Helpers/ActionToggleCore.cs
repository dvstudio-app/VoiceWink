namespace VoiceWink.Helpers;

/// <summary>Which colour family a toggle button is currently wearing.</summary>
internal enum TogglePalette
{
    /// <summary>Idle — the primary action (accent).</summary>
    Primary,

    /// <summary>Running — the cancel action (danger), so it cannot read as "start again".</summary>
    Cancel,
}

/// <summary>
/// The whole decision content of a Download ⇄ Cancel button (UI-3), with no WinUI in it.
///
/// <para><b>Why this exists as a separate type.</b> The first version put the state directly on the
/// WinUI wrapper, and its tests could only assert SHAPE — method names, field mutability — which
/// Codex correctly called out as testing structure rather than behaviour: they would all have passed
/// with empty method bodies, and the captured-brush regression they claimed to guard would have gone
/// straight through. A pure core can be driven end to end (primary → cancel → hover → back) in the
/// unit-test host, which is the only way that guard becomes real.</para>
///
/// <para><b>Both labels are bound at construction and the transitions take no arguments.</b> The
/// earlier API accepted arbitrary text on each transition, so <c>ShowCancel(primaryLabel)</c>
/// compiled — the label could drift from the state while the doc claimed it could not. Now the pair
/// is fixed once and a caller cannot express the mismatch.</para>
/// </summary>
internal sealed class ActionToggleCore
{
    private readonly string _primaryLabel;
    private readonly string _cancelLabel;

    internal ActionToggleCore(string primaryLabel, string cancelLabel)
    {
        if (string.IsNullOrWhiteSpace(primaryLabel))
            throw new ArgumentException("A primary label is required.", nameof(primaryLabel));
        if (string.IsNullOrWhiteSpace(cancelLabel))
            throw new ArgumentException("A cancel label is required.", nameof(cancelLabel));

        _primaryLabel = primaryLabel;
        _cancelLabel = cancelLabel;
    }

    /// <summary>
    /// How long after showing the cancel face a tap is IGNORED.
    ///
    /// <para>The button morphs to Cancel under the cursor, so the second click of a habitual
    /// double-click lands on it and aborts a download the user meant to start. The old layout
    /// absorbed that click on a disabled button; the toggle removes that accident-absorber, and
    /// telling the user afterwards is not the same as not doing it (Codex diff review r2, after
    /// Kimi flagged the hazard).</para>
    ///
    /// <para><b>The window is the USER'S double-click interval, not a constant.</b> An earlier
    /// version hardcoded 300 ms to match <c>HotkeyService</c>'s tap/hold boundary, which Codex
    /// correctly rejected: Windows lets the user configure a longer double-click time (Mouse control
    /// panel, up to ~900 ms), and a keyboard gesture threshold is not the right authority for a
    /// MOUSE accessibility setting. Someone on a slow double-click setting would still cancel their
    /// own download. <c>NativeInterop.GetDoubleClickTime()</c> is the system's own answer.</para>
    ///
    /// <para>300 ms remains only as the fallback for an implausible zero/negative return, so the
    /// guard degrades to "some protection" rather than none.</para>
    /// </summary>
    internal const int FallbackArmingMilliseconds = 300;

    /// <summary>True while the button is showing its cancel face — the ONE thing callers branch on.</summary>
    public bool IsCancelling { get; private set; }

    /// <summary>
    /// Whether a tap arriving <paramref name="sinceShown"/> after the cancel face appeared should
    /// actually cancel, given the system's <paramref name="doubleClickInterval"/>.
    ///
    /// <para>Pure — both the elapsed time and the interval are supplied — so the boundary is
    /// testable without a clock and without the Mouse control panel.</para>
    ///
    /// <para><b>A NEGATIVE elapsed time is treated as unarmed.</b> Callers measure with a monotonic
    /// clock precisely so that cannot happen, and this stays defensive because the alternative
    /// failure is a cancel the user did not ask for.</para>
    /// </summary>
    internal static bool CancelTapIsArmed(TimeSpan sinceShown, TimeSpan doubleClickInterval)
        => sinceShown >= TimeSpan.Zero && sinceShown >= doubleClickInterval;

    /// <summary>The system double-click interval, or the fallback when it reports nothing usable.</summary>
    internal static TimeSpan ArmingWindow(int systemDoubleClickMilliseconds)
        => TimeSpan.FromMilliseconds(
            systemDoubleClickMilliseconds > 0 ? systemDoubleClickMilliseconds : FallbackArmingMilliseconds);

    /// <summary>The label for the CURRENT state. Never supplied by a caller, so it cannot drift.</summary>
    public string Label => IsCancelling ? _cancelLabel : _primaryLabel;

    /// <summary>The colour family for the CURRENT state.</summary>
    public TogglePalette Palette => IsCancelling ? TogglePalette.Cancel : TogglePalette.Primary;

    public void ShowPrimary() => IsCancelling = false;

    public void ShowCancel() => IsCancelling = true;

    /// <summary>
    /// Which brush the element should carry right now, given whether the pointer is over it.
    ///
    /// <para>This is the captured-closure bug expressed as a function: the answer must be derived
    /// from the CURRENT <see cref="Palette"/> at the moment the pointer moves, never from a value
    /// sampled when the handlers were attached. A hover that restores the previous state's colour is
    /// exactly what the old <c>CreateAccentButton</c> shape produced after a swap.</para>
    /// </summary>
    public (TogglePalette Palette, bool Hovered) BackgroundFor(bool pointerOver) => (Palette, pointerOver);
}
