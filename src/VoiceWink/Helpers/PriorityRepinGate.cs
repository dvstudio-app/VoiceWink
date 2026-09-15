namespace VoiceWink.Helpers;

/// <summary>
/// What the REL-14 re-pin watchdog should do about an observed process priority class.
/// </summary>
internal enum PriorityRepinAction
{
    /// <summary>The class is fine (or deliberately elevated) — leave the process alone.</summary>
    Skip,

    /// <summary>Windows demoted us; pin the class back to Normal.</summary>
    Repin,

    /// <summary>
    /// The process is realtime-class. Shed the watchdog thread's own TIME_CRITICAL
    /// boost (it would mean priority 31 here) and do nothing else — a realtime-class
    /// process is not the starvation case the watchdog exists for.
    /// </summary>
    ShedBoost,

    /// <summary>The class could not be read. Record the failure; change nothing.</summary>
    Unknown,
}

/// <summary>
/// Pure decision helper for "what does this observed <c>GetPriorityClass</c> value mean?".
/// Split out of <see cref="ProcessPriorityTuner"/> so the decision can be pinned by a truth
/// table — the same reason <see cref="HookThreadBoostGate"/> exists.
///
/// <para><b>Why a switch and not a comparison.</b> The Win32 priority-class constants are
/// NOT monotonic: <c>NORMAL</c>(0x20) &lt; <c>IDLE</c>(0x40) &lt; <c>HIGH</c>(0x80) &lt;
/// <c>REALTIME</c>(0x100) &lt; <c>BELOW_NORMAL</c>(0x4000) &lt; <c>ABOVE_NORMAL</c>(0x8000).
/// So the natural-looking test "anything below NORMAL means Windows demoted us" is wrong in
/// both directions at once — it would re-pin a deliberately ABOVE_NORMAL process and ignore a
/// BELOW_NORMAL one. The managed <c>Process.PriorityClass</c> path this replaced hid that
/// behind an enum; reading raw DWORDs on the watchdog thread (no allocation, no dependency on
/// a possibly-stalled GC) exposes it. A constant-pin test catches a typo'd value but cannot
/// catch a wrong comparison, which is what this gate plus
/// <c>PriorityRepinGateTests</c> is for.</para>
///
/// <para>Kimi 2026-08-05 plan review caught the non-monotonicity trap and pointed at the
/// <see cref="HookThreadBoostGate"/> precedent.</para>
/// </summary>
internal static class PriorityRepinGate
{
    /// <summary>
    /// Maps a raw <c>GetPriorityClass</c> result to the watchdog's action.
    /// </summary>
    /// <param name="observedClass">
    /// The value <c>GetPriorityClass</c> returned, or <c>0</c> if the call failed
    /// (0 is not a legal priority class, which is what makes it usable as a sentinel).
    /// </param>
    public static PriorityRepinAction Decide(uint observedClass) => observedClass switch
    {
        0 => PriorityRepinAction.Unknown,

        // Checked before everything else by the caller: TIME_CRITICAL means 31 here.
        NativeInterop.REALTIME_PRIORITY_CLASS => PriorityRepinAction.ShedBoost,

        // The two classes Windows demotes us INTO. Idle is what the field incidents
        // recorded; BelowNormal is the same defect one step milder.
        NativeInterop.IDLE_PRIORITY_CLASS => PriorityRepinAction.Repin,
        NativeInterop.BELOW_NORMAL_PRIORITY_CLASS => PriorityRepinAction.Repin,

        // Healthy, or deliberately elevated by a developer / Task Manager. Never
        // bump a process DOWN to Normal — that carve-out predates this gate.
        NativeInterop.NORMAL_PRIORITY_CLASS => PriorityRepinAction.Skip,
        NativeInterop.ABOVE_NORMAL_PRIORITY_CLASS => PriorityRepinAction.Skip,
        NativeInterop.HIGH_PRIORITY_CLASS => PriorityRepinAction.Skip,

        // An unrecognized non-zero class. Skip, not Repin: a value we cannot name is
        // not evidence of a demotion, and writing the class on a guess is the more
        // destructive of the two mistakes.
        _ => PriorityRepinAction.Skip,
    };

    /// <summary>
    /// Human-readable name for a raw priority class, for the log line. Allocates, so it is
    /// only ever called from <see cref="PriorityDiagnosticChannel"/>'s drain — which runs on
    /// the normal-priority pump thread, never on the boosted watchdog thread.
    /// </summary>
    public static string Describe(uint priorityClass) => priorityClass switch
    {
        NativeInterop.IDLE_PRIORITY_CLASS => "Idle",
        NativeInterop.BELOW_NORMAL_PRIORITY_CLASS => "BelowNormal",
        NativeInterop.NORMAL_PRIORITY_CLASS => "Normal",
        NativeInterop.ABOVE_NORMAL_PRIORITY_CLASS => "AboveNormal",
        NativeInterop.HIGH_PRIORITY_CLASS => "High",
        NativeInterop.REALTIME_PRIORITY_CLASS => "RealTime",
        0 => "unreadable",
        _ => $"0x{priorityClass:X}",
    };
}
