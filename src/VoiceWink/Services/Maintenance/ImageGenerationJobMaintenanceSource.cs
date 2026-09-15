using System;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// <see cref="IMaintenanceStatusSource"/> for the background image-generation job (IMG-BG,
/// 2026-07-16). Blocks destructive maintenance (Velopack apply-and-restart, Reset-all-data)
/// while a job is reserved/generating/committing — the job writes History rows, the Images
/// folder, and reference copies, and it deliberately no longer rides
/// <c>MainViewModel.RecordingState</c> (whose <see cref="RecorderMaintenanceSource"/> stops
/// covering image work the moment the pipeline returns to Idle at dispatch).
///
/// <para>The "is a job running?" signal is injected as a <see cref="Func{Bool}"/> probe
/// (resolved lazily against the live <c>ImageGenerationJobService</c> singleton at
/// <see cref="IsBlocking"/> time — its <c>IsRunning</c> is lock-guarded, safe off-thread).
/// Mirrors <see cref="RecorderMaintenanceSource"/>.</para>
/// </summary>
internal sealed class ImageGenerationJobMaintenanceSource : IMaintenanceStatusSource, IDisposable
{
    private readonly Func<bool> _isJobRunningProbe;
    private readonly IDisposable _registration;

    public ImageGenerationJobMaintenanceSource(IMaintenanceGate gate, Func<bool> isJobRunningProbe)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _isJobRunningProbe = isJobRunningProbe ?? throw new ArgumentNullException(nameof(isJobRunningProbe));
        _registration = gate.Register(this);
    }

    public string Name => "Image generation in progress";

    public bool IsBlocking(out string? detail)
    {
        detail = null;
        return _isJobRunningProbe();
    }

    public void Dispose() => _registration.Dispose();
}
