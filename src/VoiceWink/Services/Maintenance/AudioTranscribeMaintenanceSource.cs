using System;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// <see cref="IMaintenanceStatusSource"/> contribution for the
/// <c>AudioTranscribePage</c> file-transcription pipeline. Reports blocking
/// while a drop-zone transcription is in flight so that destructive
/// maintenance operations (Velopack apply-and-restart, Reset-all-data) wait
/// for the transcription to finish or be cancelled.
///
/// <para>The "am I transcribing?" signal lives as a private static field on
/// <c>AudioTranscribePage</c> (deliberately static so transcription survives
/// page recreation when the user navigates away — see the comment at the
/// field declaration). This source receives that signal via a
/// <see cref="Func{Bool}"/> probe injected at construction so the source is
/// unit-testable in isolation without dragging in the WinUI page type.</para>
///
/// <para>Lifetime: registered as a DI singleton; the constructor calls
/// <see cref="IMaintenanceGate.Register"/> and the returned handle is held
/// for the app lifetime. <see cref="Dispose"/> unregisters — only meaningful
/// for unit tests, since the singleton is never disposed in production.</para>
///
/// <para>This source is mandatory for the gate to be considered complete
/// enough for <c>IUpdateService.ApplyAndRestartAsync</c> to consume — an
/// in-flight file transcription would otherwise be silently killed when the
/// updater swaps the binary.</para>
/// </summary>
internal sealed class AudioTranscribeMaintenanceSource : IMaintenanceStatusSource, IDisposable
{
    private readonly Func<bool> _isTranscribingProbe;
    private readonly IDisposable _registration;

    public AudioTranscribeMaintenanceSource(IMaintenanceGate gate, Func<bool> isTranscribingProbe)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _isTranscribingProbe = isTranscribingProbe ?? throw new ArgumentNullException(nameof(isTranscribingProbe));
        _registration = gate.Register(this);
    }

    public string Name => "Audio file transcription";

    public bool IsBlocking(out string? detail)
    {
        detail = null;
        return _isTranscribingProbe();
    }

    public void Dispose() => _registration.Dispose();
}
