using Serilog;
using VoiceWink.Models;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>What <see cref="LocalModelStore.DownloadAsync"/> did.</summary>
internal enum LocalModelDownloadOutcome
{
    Installed,
    /// <summary>An update or a data erasure holds the exclusive maintenance lease: nothing was
    /// started, no partial file exists.</summary>
    RefusedDuringMaintenance,
    UnknownModel,
}

/// <summary>
/// One running download, owned by the store rather than the row that started it: the Enhancement
/// page rebuilds its rows (the enhancement toggle, a prompt switch, navigating away and back), and
/// a rebuilt row must find the transfer again — show its progress, and still be able to cancel it.
/// </summary>
internal sealed class LocalModelDownloadAttempt
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary><see cref="Environment.TickCount64"/> when the attempt started — the double-click
    /// guard's reference (a Cancel tap inside the double-click interval is the second half of the
    /// Download click).</summary>
    internal long StartedTicks { get; } = Environment.TickCount64;

    /// <summary>The last progress reported, 0..1.</summary>
    internal double Progress { get; private set; }

    /// <summary>Raised on the thread that started the download (the UI thread).</summary>
    internal event Action<double>? ProgressChanged;

    internal Task<LocalModelDownloadOutcome> Completion { get; set; } = Task.FromResult(LocalModelDownloadOutcome.UnknownModel);

    internal CancellationToken Token => _cts.Token;

    internal bool IsCancellationRequested => _cts.IsCancellationRequested;

    // Never disposed: a Cancel that races completion must not throw ObjectDisposedException, and
    // this source has no timer to release.
    internal void Cancel() => _cts.Cancel();

    internal void Report(double value)
    {
        Progress = value;
        ProgressChanged?.Invoke(value);
    }
}

/// <summary>
/// LAI-3: download, delete and installed-state of the <see cref="LocalModelCatalog"/> models, in
/// <c>%LOCALAPPDATA%\VoiceWink\Models\llm\</c>. A thin owner over a SECOND
/// <see cref="ModelDownloadManager"/> (its own root and catalog), so the download, SHA-256 and
/// exact-length verification, staging and atomic commit are the speech models' own protocol,
/// not a copy. Every file comes from the mirror only — the descriptors carry no fallback URL.
/// <para>A download is refused while an update-apply or a GDPR erasure holds the exclusive
/// maintenance lease (the check <c>ModelManagementViewModel.DownloadModelAsync</c> makes for
/// speech models), and <see cref="IsDownloading"/> feeds the maintenance gate so neither can
/// start under a running download.</para>
/// </summary>
public sealed class LocalModelStore
{
    private static ILogger Logger => Log.ForContext<LocalModelStore>();

    /// <summary>The folder under the speech models root.</summary>
    internal const string FolderName = "llm";

    private readonly ModelDownloadManager _manager;
    private readonly Func<bool> _isExclusiveMaintenanceActive;
    private readonly Dictionary<string, TranscriptionModelInfo> _descriptors;
    private readonly Dictionary<string, LocalModelDownloadAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly object _attemptsLock = new();

    internal LocalModelStore(IHttpClientFactory httpFactory, string root, Func<bool> isExclusiveMaintenanceActive)
        : this(root, isExclusiveMaintenanceActive,
               catalog => ModelDownloadManager.ForCatalog(httpFactory, root, catalog))
    {
    }

    /// <summary>Test seam: the manager factory (tests pass one built on the internal timing ctor).</summary>
    internal LocalModelStore(
        string root, Func<bool> isExclusiveMaintenanceActive,
        Func<IReadOnlyList<TranscriptionModelInfo>, ModelDownloadManager> managerFactory)
    {
        Root = root;
        _isExclusiveMaintenanceActive = isExclusiveMaintenanceActive;
        _descriptors = LocalModelCatalog.All.ToDictionary(e => e.Id, e => e.ToDownloadDescriptor(), StringComparer.Ordinal);
        _manager = managerFactory(_descriptors.Values.ToList());
    }

    internal string Root { get; }

    internal bool IsDownloading => _manager.IsDownloading;

    internal bool IsInstalled(string id)
        => _descriptors.TryGetValue(id, out var descriptor) && _manager.TryGetInstalledLocation(descriptor) is not null;

    /// <summary>The installed GGUF's path — what LAI-4 hands to <c>LlamaServerProcess</c> — or null.</summary>
    internal string? InstalledModelPath(string id)
    {
        var entry = LocalModelCatalog.Find(id);
        if (entry is null || !_descriptors.TryGetValue(id, out var descriptor))
        {
            return null;
        }
        return _manager.TryGetInstalledLocation(descriptor) is { } location
            ? Path.Combine(location.Path, entry.Model.FileName)
            : null;
    }

    /// <summary>The download running for <paramref name="id"/>, or null.</summary>
    internal LocalModelDownloadAttempt? ActiveDownload(string id)
    {
        lock (_attemptsLock)
        {
            return _attempts.TryGetValue(id, out var attempt) ? attempt : null;
        }
    }

    /// <summary>
    /// Starts a download of <paramref name="id"/>, or returns the one already running — a second
    /// start never queues a second transfer behind the first. Call it on the UI thread: progress is
    /// reported on the thread that started it. Failures are logged here, once, however many rows
    /// await <see cref="LocalModelDownloadAttempt.Completion"/>.
    /// </summary>
    internal LocalModelDownloadAttempt StartDownload(string id)
    {
        LocalModelDownloadAttempt attempt;
        lock (_attemptsLock)
        {
            if (_attempts.TryGetValue(id, out var running))
            {
                return running;
            }
            attempt = new LocalModelDownloadAttempt();
            _attempts[id] = attempt;
        }
        var progress = new Progress<double>(attempt.Report);
        attempt.Completion = RunAttemptAsync(id, attempt, progress);
        return attempt;
    }

    private async Task<LocalModelDownloadOutcome> RunAttemptAsync(string id, LocalModelDownloadAttempt attempt, IProgress<double> progress)
    {
        try
        {
            return await DownloadAsync(id, progress, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            Logger.Information("Local model download cancelled: {LocalModelId}", id);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Local model download failed: {LocalModelId}", id);
            throw;
        }
        finally
        {
            lock (_attemptsLock)
            {
                _attempts.Remove(id);
            }
        }
    }

    internal async Task<LocalModelDownloadOutcome> DownloadAsync(string id, IProgress<double>? progress, CancellationToken ct)
    {
        if (!_descriptors.TryGetValue(id, out var descriptor))
        {
            return LocalModelDownloadOutcome.UnknownModel;
        }
        if (_isExclusiveMaintenanceActive())
        {
            Logger.Information("Local model download refused for {LocalModelId}: an update or data erasure is running", id);
            return LocalModelDownloadOutcome.RefusedDuringMaintenance;
        }
        await _manager.DownloadModelAsync(descriptor, progress, ct).ConfigureAwait(false);
        Logger.Information("Local model installed: {LocalModelId}", id);
        return LocalModelDownloadOutcome.Installed;
    }

    /// <summary>True when the model is gone afterwards (deleted, or was never there).</summary>
    internal bool Delete(string id)
    {
        if (!_descriptors.TryGetValue(id, out var descriptor))
        {
            return false;
        }
        var outcome = _manager.DeleteModel(descriptor);
        Logger.Information("Local model delete {LocalModelId}: {Outcome}", id, outcome);
        return outcome != ModelDeleteOutcome.FailedBeforeInvalidation;
    }
}
