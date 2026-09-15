using System;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// <see cref="IMaintenanceStatusSource"/> for the deferred clipboard-restore helper. Blocks
/// destructive maintenance (Velopack apply-and-restart, Reset-all-data) while a paste's deferred
/// clipboard restore is still pending, so the user's original clipboard contents aren't lost to a
/// mid-restore process swap.
///
/// <para>The window is short (a paste's restore-delay, ~2s), so this is the lowest-impact source —
/// but it completes the gate's view so <c>IUpdateService.ApplyAndRestartAsync</c> never interrupts
/// a restore in flight. The signal is injected as a <see cref="Func{Bool}"/> probe (resolved lazily
/// against <c>ClipboardService.IsRestorePending</c>) so this source is unit-testable in isolation.
/// Mirrors <see cref="AudioTranscribeMaintenanceSource"/>.</para>
/// </summary>
internal sealed class PasteRestoreMaintenanceSource : IMaintenanceStatusSource, IDisposable
{
    private readonly Func<bool> _isRestorePendingProbe;
    private readonly IDisposable _registration;

    public PasteRestoreMaintenanceSource(IMaintenanceGate gate, Func<bool> isRestorePendingProbe)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _isRestorePendingProbe = isRestorePendingProbe ?? throw new ArgumentNullException(nameof(isRestorePendingProbe));
        _registration = gate.Register(this);
    }

    public string Name => "Clipboard restore pending";

    public bool IsBlocking(out string? detail)
    {
        detail = null;
        return _isRestorePendingProbe();
    }

    public void Dispose() => _registration.Dispose();
}
