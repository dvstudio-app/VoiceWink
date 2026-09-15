using System.Runtime.InteropServices;

namespace VoiceWink.Helpers;

/// <summary>
/// The native seam under <see cref="PriorityWatchdog"/>. Exists so the watchdog's loop —
/// ordering, recovery, realtime handling, retry, failure paths — is unit-testable with a
/// fake instead of by starting a real priority-15 thread inside the test host.
///
/// <para><b>Every member reports failure explicitly.</b> An earlier design had
/// <c>ReassertEcoQoS()</c> and a <c>UnbiasedNowMs</c> property with no way to say "that
/// didn't work", which would have lost the EcoQoS opt-out silently and turned a failed
/// clock read into a plausible-looking number. Codex 2026-08-05 flagged it; hence the
/// uniform <c>Try…(out value, out int error)</c> shape.</para>
/// </summary>
internal interface IPriorityPlatform
{
    /// <summary>Reads this process's priority class. <c>false</c> leaves <paramref name="priorityClass"/> at 0.</summary>
    bool TryGetProcessPriorityClass(out uint priorityClass, out int error);

    /// <summary>Pins this process's priority class to <c>NORMAL_PRIORITY_CLASS</c>.</summary>
    bool TrySetProcessPriorityClassNormal(out int error);

    /// <summary>Sets the CALLING thread's priority (one of the <c>THREAD_PRIORITY_*</c> values).</summary>
    bool TrySetCurrentThreadPriority(int priority, out int error);

    /// <summary>Reads the CALLING thread's achieved priority, translating the error sentinel into <c>false</c>.</summary>
    bool TryGetCurrentThreadPriority(out int priority, out int error);

    /// <summary>Re-applies the Efficiency Mode (EcoQoS) opt-out. Idempotent.</summary>
    bool TryReassertEcoQoSOptOut(out int error);

    /// <summary>
    /// Milliseconds from a monotonic source that EXCLUDES system suspend time, so a
    /// delta spanning a sleep/resume is not inflated by the suspension.
    /// </summary>
    bool TryGetUnbiasedMs(out long milliseconds, out int error);
}

/// <summary>
/// Production <see cref="IPriorityPlatform"/> — thin, allocation-free P/Invoke wrappers.
/// Allocation-free matters: these run on a TIME_CRITICAL thread whose whole job is to
/// work when the process is starved, which is also when the GC may be stalled.
/// </summary>
internal sealed class Win32PriorityPlatform : IPriorityPlatform
{
    /// <summary>100 ns units per millisecond — <c>QueryUnbiasedInterruptTime</c>'s unit.</summary>
    internal const long HundredNsPerMs = 10_000;

    public bool TryGetProcessPriorityClass(out uint priorityClass, out int error)
    {
        priorityClass = NativeInterop.GetPriorityClass(NativeInterop.GetCurrentProcess());
        if (priorityClass == 0)
        {
            // 0 is not a legal priority class, so it is unambiguously the failure return.
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    public bool TrySetProcessPriorityClassNormal(out int error)
    {
        if (NativeInterop.SetPriorityClass(
                NativeInterop.GetCurrentProcess(), NativeInterop.NORMAL_PRIORITY_CLASS))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    public bool TrySetCurrentThreadPriority(int priority, out int error)
    {
        if (NativeInterop.SetThreadPriority(NativeInterop.GetCurrentThread(), priority))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    public bool TryGetCurrentThreadPriority(out int priority, out int error)
    {
        priority = NativeInterop.GetThreadPriority(NativeInterop.GetCurrentThread());
        if (priority == NativeInterop.THREAD_PRIORITY_ERROR_RETURN)
        {
            error = Marshal.GetLastWin32Error();
            priority = 0;
            return false;
        }

        error = 0;
        return true;
    }

    public bool TryReassertEcoQoSOptOut(out int error)
    {
        // ControlMask says "I am setting EXECUTION_SPEED"; StateMask = 0 says "off",
        // i.e. do not throttle this process. Same call ProcessPriorityTuner makes once
        // at startup — re-applied per tick because nothing else undoes a later
        // re-throttle by Windows.
        var state = new NativeInterop.PROCESS_POWER_THROTTLING_STATE
        {
            Version = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0,
        };

        var size = (uint)Marshal.SizeOf<NativeInterop.PROCESS_POWER_THROTTLING_STATE>();
        if (NativeInterop.SetProcessInformation(
                NativeInterop.GetCurrentProcess(),
                NativeInterop.ProcessInformationClass.ProcessPowerThrottling,
                ref state,
                size))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    public bool TryGetUnbiasedMs(out long milliseconds, out int error)
    {
        if (NativeInterop.QueryUnbiasedInterruptTime(out var hundredNs))
        {
            milliseconds = (long)(hundredNs / HundredNsPerMs);
            error = 0;
            return true;
        }

        milliseconds = 0;
        error = Marshal.GetLastWin32Error();
        return false;
    }
}
