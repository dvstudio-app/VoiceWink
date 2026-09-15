using System;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// <see cref="IMaintenanceStatusSource"/> for the model-download pipeline. Blocks destructive
/// maintenance (Velopack apply-and-restart, Reset-all-data) while a Whisper model is actively
/// downloading — yanking the binary or wiping data mid-download would corrupt a multi-GB transfer.
///
/// <para>The "am I downloading?" signal is injected as a <see cref="Func{Bool}"/> probe (resolved
/// lazily against the live <c>ModelDownloadManager.IsDownloading</c> singleton) so this source is
/// unit-testable in isolation. Mirrors <see cref="AudioTranscribeMaintenanceSource"/>.</para>
/// </summary>
internal sealed class ModelDownloadMaintenanceSource : IMaintenanceStatusSource, IDisposable
{
    private readonly Func<bool> _isDownloadingProbe;
    private readonly IDisposable _registration;

    public ModelDownloadMaintenanceSource(IMaintenanceGate gate, Func<bool> isDownloadingProbe)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _isDownloadingProbe = isDownloadingProbe ?? throw new ArgumentNullException(nameof(isDownloadingProbe));
        _registration = gate.Register(this);
    }

    public string Name => "Model download in progress";

    public bool IsBlocking(out string? detail)
    {
        detail = null;
        return _isDownloadingProbe();
    }

    public void Dispose() => _registration.Dispose();
}
