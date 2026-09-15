using Serilog;
using VoiceWink.Models;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// The whisper.cpp local runtime — one of TWO since TRN-1 step 3 added
/// <see cref="ParakeetLocalRuntime"/>. It was the only one when this adapter was written, and the
/// interface existed precisely so it would stop being the only one the call sites knew about.
///
/// <para>A thin adapter over the existing <see cref="WhisperTranscriptionService"/>: it answers
/// "is this model mine", resolves the file, and calls the unchanged <c>LoadModelAsync</c>. It adds
/// no loading behaviour of its own, so the recording hot path keeps that method's same-path
/// short-circuit.</para>
/// </summary>
public sealed class WhisperLocalRuntime : ILocalTranscriptionRuntime
{
    private static ILogger Logger => Log.ForContext<WhisperLocalRuntime>();

    private readonly WhisperTranscriptionService _whisper;
    private readonly Func<string, string?> _resolvePath;

    /// <remarks>
    /// Holds both collaborators and disposes NEITHER. <see cref="WhisperTranscriptionService"/> is a
    /// DI singleton whose <c>WhisperFactory</c> must never be disposed — the processor holds native
    /// pointers into it (CLAUDE.md WinUI constraint 5). A wrapper like this is precisely where an
    /// <c>IDisposable</c> gets added by reflex; do not add one.
    /// </remarks>
    public WhisperLocalRuntime(WhisperTranscriptionService whisper, ModelDownloadManager downloads)
        : this(whisper, downloads.GetSingleFileModelPath)
    {
    }

    /// <summary>
    /// Test seam: supply the path resolver directly.
    ///
    /// <para>Not a nicety — <see cref="ModelDownloadManager"/> resolves against the real
    /// <c>%LOCALAPPDATA%\VoiceWink\Models</c>, so a test calling <see cref="PrepareAsync"/> with the
    /// production resolver would inspect the developer's own models and, if one happened to be
    /// present, LOAD a multi-gigabyte native model into the test process. That makes the suite
    /// machine-dependent, slow and memory-heavy — and the reviewer safety overlay forbids touching
    /// that directory at all.</para>
    /// </summary>
    internal WhisperLocalRuntime(WhisperTranscriptionService whisper, Func<string, string?> resolvePath)
    {
        _whisper = whisper;
        _resolvePath = resolvePath;
    }

    public ITranscriptionService Service => _whisper;

    /// <summary>Always available: whisper.cpp ships in every build, behind no lever, and its native
    /// library has no CPU-feature floor this app checks. Constant rather than a probe so the shipped
    /// engine's behaviour is unchanged by the seam.</summary>
    public bool IsAvailable => true;

    /// <summary>Catalog membership, case-insensitively. NOT file existence — see the interface.</summary>
    public bool CanServe(string modelName) => Find(modelName) is not null;

    public string? Canonicalize(string modelName) => Find(modelName)?.Name;

    public string? DisplayName(string modelName) => Find(modelName)?.DisplayName;

    /// <summary>
    /// Catalog membership, narrowed to the rows this runtime actually OWNS.
    ///
    /// <para>Before TRN-1 step 3 this matched any name in <c>PredefinedModels</c>, which was
    /// correct only while whisper.cpp was the sole local engine. The moment a second one exists,
    /// broad membership makes Whisper claim its models too — and
    /// <c>LocalModelPreparer.TryResolve</c> THROWS when two runtimes claim one name, so the first
    /// such row breaks the very first prepare.</para>
    ///
    /// <para>Keyed on <see cref="TranscriptionModelInfo.Runtime"/> — the declared ENGINE — never on
    /// <c>Files</c>, which is storage layout and would misroute a future single-file non-Whisper
    /// model straight into whisper.cpp.</para>
    /// </summary>
    private static TranscriptionModelInfo? Find(string? modelName)
        => string.IsNullOrWhiteSpace(modelName)
            ? null
            : PredefinedModels.Models.FirstOrDefault(
                m => m.Runtime == LocalRuntimeKind.Whisper
                     && string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));

    public async Task<PrepareOutcome> PrepareAsync(string modelName, string? language, CancellationToken ct)
    {
        var model = Find(modelName);
        if (model is null)
            return PrepareOutcome.UnknownModel;

        // GetSingleFileModelPath returns null when the file is absent — that null IS the existence signal,
        // the same one ModelManagementViewModel's select gate uses. Downloading is the CALLER's
        // decision: it owns the progress UI and the settings write.
        var path = _resolvePath(model.Name);
        if (path is null)
            return PrepareOutcome.NotDownloaded;

        ct.ThrowIfCancellationRequested();

        // Unchanged call, deliberately: LoadModelAsync short-circuits when the same path + language
        // is already loaded, and the recording-start path depends on that staying free.
        // TRN-64: the catalog name rides along — the GPU self-test and its gate are keyed per
        // model, and this is the one caller that knows the name (Kimi r2 C3).
        await _whisper.LoadModelAsync(path, language, ct, modelName: model.Name).ConfigureAwait(false);
        Logger.Debug("Whisper runtime prepared {Model}", model.Name);
        return PrepareOutcome.Loaded;
    }
}
