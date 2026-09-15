using System;
using Microsoft.Extensions.DependencyInjection;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// Centralizes the DI registration + eager resolution of every
/// <see cref="IMaintenanceStatusSource"/> adapter so the wiring is
/// unit-testable without launching WinUI.
///
/// <para><b>Why this exists.</b> <see cref="IMaintenanceStatusSource"/>
/// adapters self-register with <see cref="IMaintenanceGate"/> in their
/// constructor. .NET DI singletons are lazy by default — without an
/// eager <c>GetRequiredService&lt;T&gt;()</c> call somewhere during
/// startup, the source is never constructed and the gate never sees it.
/// A test against just the source class can't catch that mistake (the
/// source's own unit tests construct it directly, bypassing DI). This
/// helper makes the production wiring callable from a test against a
/// fresh <see cref="IServiceCollection"/>, so a regression that deletes
/// either the registration or the eager resolve breaks the test.</para>
///
/// <para>Per the App-side caller in <c>App.xaml.cs</c>: each new adapter
/// gets two lines — one in <see cref="Register"/> and one in
/// <see cref="Resolve"/>. Once two or more adapters exist, consider
/// switching to bulk <c>IEnumerable&lt;IMaintenanceStatusSource&gt;</c>
/// injection instead.</para>
/// </summary>
internal static class MaintenanceSourcesWiring
{
    /// <summary>
    /// Register every maintenance status source in DI. Each <c>Func&lt;bool&gt;</c> probe is bound
    /// to the production signal that indicates the subsystem is busy; in production these resolve
    /// the live singletons lazily (<c>AudioTranscribePage.IsTranscribing</c>,
    /// <c>MainViewModel.RecordingState</c>, <c>ModelDownloadManager.IsDownloading</c>,
    /// <c>ClipboardService.IsRestorePending</c>); in tests they point to local mutable bools.
    /// </summary>
    public static void Register(
        IServiceCollection services,
        Func<bool> audioTranscribeProbe,
        Func<bool> recorderProbe,
        Func<bool> modelDownloadProbe,
        Func<bool> pasteRestoreProbe,
        Func<bool> imageGenerationProbe)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(audioTranscribeProbe);
        ArgumentNullException.ThrowIfNull(recorderProbe);
        ArgumentNullException.ThrowIfNull(modelDownloadProbe);
        ArgumentNullException.ThrowIfNull(pasteRestoreProbe);
        ArgumentNullException.ThrowIfNull(imageGenerationProbe);

        services.AddSingleton(sp => new AudioTranscribeMaintenanceSource(
            sp.GetRequiredService<IMaintenanceGate>(), audioTranscribeProbe));
        services.AddSingleton(sp => new RecorderMaintenanceSource(
            sp.GetRequiredService<IMaintenanceGate>(), recorderProbe));
        services.AddSingleton(sp => new ModelDownloadMaintenanceSource(
            sp.GetRequiredService<IMaintenanceGate>(), modelDownloadProbe));
        services.AddSingleton(sp => new PasteRestoreMaintenanceSource(
            sp.GetRequiredService<IMaintenanceGate>(), pasteRestoreProbe));
        services.AddSingleton(sp => new ImageGenerationJobMaintenanceSource(
            sp.GetRequiredService<IMaintenanceGate>(), imageGenerationProbe));

        // Not a status SOURCE — a once-per-launch background pass, composed here because this is
        // the startup-composition seam for maintenance and routing it through the same place is
        // what keeps App.xaml.cs free of another provider lookup (AGENTS.md forbids new
        // App.Services usages; an earlier static Start(IServiceProvider) was refused at review as
        // the same violation relocated).
        //
        // The manager is passed as a FACTORY: its own registration creates the models directory, so
        // resolving it eagerly would move that failure onto the launch thread, outside the pass's
        // fail-soft handler.
        services.AddSingleton(sp => new StartupMaintenance(
            sp.GetRequiredService<IMaintenanceGate>(),
            () => sp.GetRequiredService<Transcription.ModelDownloadManager>()));
    }

    /// <summary>
    /// Eagerly resolve every registered source so each ctor runs and
    /// self-registers with <see cref="IMaintenanceGate"/>. Without this
    /// call, the singletons stay un-constructed and the gate sees nothing.
    ///
    /// <para><b>Returns the <see cref="StartupMaintenance"/> handle</b> rather than starting it,
    /// so <c>App</c> can trigger the background pass off a call it ALREADY makes — no new
    /// <c>App.Services</c> lookup, which <c>AGENTS.md</c> forbids. Resolving and starting are kept
    /// separate because the pass takes a cleanup lease: starting it in here made this seam's own
    /// "all probes false ⇒ gate clean" assertions race it.</para>
    /// </summary>
    public static StartupMaintenance Resolve(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _ = provider.GetRequiredService<AudioTranscribeMaintenanceSource>();
        _ = provider.GetRequiredService<RecorderMaintenanceSource>();
        _ = provider.GetRequiredService<ModelDownloadMaintenanceSource>();
        _ = provider.GetRequiredService<PasteRestoreMaintenanceSource>();
        _ = provider.GetRequiredService<ImageGenerationJobMaintenanceSource>();

        // Constructed, NOT started — see the summary. The caller decides when work begins.
        return provider.GetRequiredService<StartupMaintenance>();
    }
}
