using System;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// <see cref="IMaintenanceStatusSource"/> for the recording pipeline. Blocks destructive
/// maintenance (Velopack apply-and-restart, Reset-all-data) while a recording / transcription /
/// enhancement is in flight — i.e. while <c>MainViewModel.RecordingState</c> is not <c>Idle</c>.
///
/// <para>The "am I recording?" signal is injected as a <see cref="Func{Bool}"/> probe (resolved
/// lazily against the live <c>MainViewModel</c> singleton at <see cref="IsBlocking"/> time) so
/// this source stays unit-testable without the ViewModel or WinUI. Mirrors
/// <see cref="AudioTranscribeMaintenanceSource"/>.</para>
/// </summary>
internal sealed class RecorderMaintenanceSource : IMaintenanceStatusSource, IDisposable
{
    private readonly Func<bool> _isRecordingProbe;
    private readonly IDisposable _registration;

    public RecorderMaintenanceSource(IMaintenanceGate gate, Func<bool> isRecordingProbe)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _isRecordingProbe = isRecordingProbe ?? throw new ArgumentNullException(nameof(isRecordingProbe));
        _registration = gate.Register(this);
    }

    public string Name => "Recording in progress";

    public bool IsBlocking(out string? detail)
    {
        detail = null;
        return _isRecordingProbe();
    }

    public void Dispose() => _registration.Dispose();
}
