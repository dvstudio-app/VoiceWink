namespace VoiceWink.Services.AppMode;

/// <summary>
/// Foreground-window / process-name lookup abstraction. Introduced as a DI
/// seam so <see cref="AppModeManager"/> can be tested against a fake that
/// simulates the slow <c>Process.GetProcessById</c> behavior observed under
/// contention (a 2.25 s UI-thread block on 2026-05-21 was diagnosed there).
///
/// <para>Visibility is <c>public</c> because <see cref="AppModeManager"/> is
/// public with a public constructor that takes this interface — the
/// accessibility chain must hold.</para>
/// </summary>
public interface IActiveWindowService
{
    /// <summary>
    /// Get the process name of the currently focused window. Convenience
    /// wrapper: <c>GetForegroundWindow</c> → <c>GetWindowThreadProcessId</c>
    /// → <c>Process.GetProcessById(pid).ProcessName</c>. The last step has
    /// been observed taking multiple seconds under contention; UI-thread
    /// callers should prefer the PID-first split via
    /// <see cref="GetProcessNameById(int)"/>.
    /// </summary>
    string? GetActiveProcessName();

    /// <summary>
    /// Look up the process name for a previously-captured PID. This is the
    /// slow .NET-enumeration step extracted from
    /// <see cref="GetActiveProcessName"/>. Designed to be called from a
    /// thread-pool worker via
    /// <see cref="Helpers.BoundedComCall.RunBoundedAsync{T}"/> with a tight
    /// timeout so a wedged lookup can't block the UI dispatcher. Returns
    /// <c>null</c> on lookup failure (PID exited, access denied, etc.).
    /// </summary>
    string? GetProcessNameById(int pid);
}
