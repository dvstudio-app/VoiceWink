using Serilog;
using VoiceWink.Models;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// The sherpa-onnx side of the local-runtime seam (TRN-1 step 3) — mirror of
/// <see cref="WhisperLocalRuntime"/>.
///
/// <para><b>Ownership is by declared engine, never by file shape.</b> <c>CanServe</c> filters on
/// <see cref="TranscriptionModelInfo.Runtime"/>, exactly as Whisper's does on its own value, so the
/// two partition the catalog and <c>LocalModelPreparer.TryResolve</c>'s two-claim throw is
/// unreachable by construction. Routing on <c>Files</c> would have been storage LAYOUT, which is a
/// different question and misroutes the first single-file non-Whisper model.</para>
///
/// <para><b>Ownership does not depend on health.</b> A disabled or unsupported build still CLAIMS
/// its rows and answers <see cref="PrepareOutcome.Unavailable"/>. Dropping the claim would make the
/// model <c>UnknownModel</c> — an honest-looking answer that is simply wrong about whose model it
/// is, and one that turns a precise "this build can't run it" into "no idea what that is".</para>
///
/// <para><b>Health is a SEPARATE question, answered by <see cref="IsAvailable"/>, and the download
/// UI is what has to ask it.</b> An earlier draft claimed that claiming-plus-<c>Unavailable</c> kept
/// the Models page from offering a 670 MB download on a build that cannot run the engine. It does
/// not: <c>Unavailable</c> arrives at PREPARE time, which is after the download has finished. Both
/// diff reviewers caught the comment asserting protection the code did not provide. The gate lives
/// in <c>ModelManagementViewModel.DownloadModelAsync</c> and reads <see cref="IsAvailable"/>.</para>
/// </summary>
public sealed class ParakeetLocalRuntime : ILocalTranscriptionRuntime
{
    private static ILogger Logger => Log.ForContext<ParakeetLocalRuntime>();

    private readonly ParakeetTranscriptionService _parakeet;
    private readonly Func<string, string?> _resolveBundleDir;
    private readonly IParakeetPcppBackend? _pcppBackend;

    /// <param name="resolveBundleDir">Returns the installed bundle directory, or null when it is not
    /// installed. Injected so tests never read — or install into — the real Models directory, the
    /// same seam <see cref="WhisperLocalRuntime"/> takes for its path resolver.</param>
    /// <param name="pcppBackend">TRN-29 slice 4: the parakeet.cpp seam, null in every ordinary
    /// build. When present, preparation consults it FIRST — a GGUF-only install must prepare as
    /// Loaded rather than demanding the sherpa bundle it deliberately no longer has.</param>
    public ParakeetLocalRuntime(ParakeetTranscriptionService parakeet, Func<string, string?> resolveBundleDir,
        IParakeetPcppBackend? pcppBackend = null)
    {
        _parakeet = parakeet;
        _resolveBundleDir = resolveBundleDir;
        _pcppBackend = pcppBackend;
    }

    public ITranscriptionService Service => _parakeet;

    /// <summary>Build lever AND CPU floor — see <see cref="ParakeetTranscriptionService.IsAvailable"/>.</summary>
    public bool IsAvailable => _parakeet.IsAvailable;

    public bool CanServe(string modelName) => Find(modelName) is not null;

    public string? Canonicalize(string modelName) => Find(modelName)?.Name;

    public string? DisplayName(string modelName) => Find(modelName)?.DisplayName;

    private static TranscriptionModelInfo? Find(string? modelName)
    {
        // Either Parakeet bundle spelling claims THIS build's single Parakeet row (TRN-29 flip):
        // an upgrader's persisted selection says the sherpa-era name forever unless re-selected,
        // and a kill-switch rebuild meets GGUF-spelled ones — both must route here rather than
        // throw as unknown. The aliases live INSIDE this runtime's ownership check, so Whisper
        // never claims either and LocalModelPreparer's two-runtimes-one-name throw stays
        // unreachable. Canonicalize() consequently converges re-selections onto the active name.
        modelName = ParakeetCatalog.CanonicalName(modelName);
        return string.IsNullOrWhiteSpace(modelName)
            ? null
            : PredefinedModels.Models.FirstOrDefault(
                m => m.Runtime == LocalRuntimeKind.Parakeet
                     && string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PrepareOutcome> PrepareAsync(string modelName, string? language, CancellationToken ct)
    {
        var model = Find(modelName);
        if (model is null) return PrepareOutcome.UnknownModel;

        // Checked BEFORE the install check: an unavailable engine must not send the caller down the
        // download path for a model it could never run.
        if (!_parakeet.IsAvailable)
        {
            Logger.Information(
                "Parakeet is unavailable on this build or machine; '{Model}' cannot be prepared", model.Name);
            return PrepareOutcome.Unavailable;
        }

        // TRN-29 slice 4: with the pcpp backend registered, IT resolves what serves — after the
        // IsAvailable gate (the disable_parakeet lever kills the whole engine, both backends)
        // and BEFORE the sherpa bundle check, because a GGUF-only install returning
        // NotDownloaded here would send the caller to download a bundle the transition already
        // migrated away from (the plan round's challenge 1). ServeSherpa falls through into the
        // unchanged legacy path below; the backend also warms the resident server on ServePcpp
        // so the decode at recording stop finds it ready.
        if (_pcppBackend is not null)
        {
            switch (await _pcppBackend.PrepareAsync(ct).ConfigureAwait(false))
            {
                case PcppPrepareOutcome.ServePcpp:
                    return PrepareOutcome.Loaded;
                case PcppPrepareOutcome.NotDownloaded:
                    return PrepareOutcome.NotDownloaded;
                case PcppPrepareOutcome.Unavailable:
                    Logger.Warning(
                        "The parakeet.cpp backend cannot serve and no legacy bundle remains; reporting unavailable");
                    return PrepareOutcome.Unavailable;
                    // PcppPrepareOutcome.ServeSherpa: fall through.
            }
        }

        // Which directory the sherpa recognizer loads from depends on WHO is asking. With the
        // pcpp backend registered (post-flip), the catalog row is the GGUF bundle, so resolving
        // by the row name would hand sherpa the GGUF directory — the backend's sherpa-location
        // accessor is the truth there, and null after it just said ServeSherpa means an
        // interleaved delete won the race: NotDownloaded is self-repairing (the download flow
        // fetches the active catalog row, which is the migration target). Backend-null builds
        // (the kill-switch rebuild, and every pre-flip binary) keep the row-name path, where the
        // row IS the sherpa bundle — byte-unchanged.
        string? bundleDir;
        if (_pcppBackend is not null)
        {
            bundleDir = _pcppBackend.SherpaBundleDir();
            if (bundleDir is null)
            {
                Logger.Warning(
                    "Parakeet: the transition said serve-sherpa but the legacy bundle is gone " +
                    "(interleaved delete); reporting not-downloaded");
                return PrepareOutcome.NotDownloaded;
            }
        }
        else
        {
            bundleDir = _resolveBundleDir(model.Name);
            if (bundleDir is null) return PrepareOutcome.NotDownloaded;
        }

        // `language` is accepted and DISCARDED: Parakeet v3 auto-detects and takes no language
        // input. That is surfaced honestly rather than silently — EffectiveTranscriptionLanguage
        // reports EngineAutoDetectsOnly for this runtime, so the pipeline records what the engine
        // actually did instead of a pin it never honoured.
        try
        {
            await _parakeet.LoadModelAsync(bundleDir, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            // The native library did not resolve. IsAvailable cannot see this — it answers about the
            // build lever and the CPU, and a present-but-unloadable DLL passes both (antivirus
            // quarantine of onnxruntime.dll is the realistic cause; a broken publish payload is the
            // other). Reported as Unavailable rather than thrown, because it IS the same user-facing
            // fact — this machine cannot run the engine — and every caller already handles that
            // member without offering a download.
            Logger.Error(ex, "The Parakeet native library did not load; reporting the engine unavailable");
            return PrepareOutcome.Unavailable;
        }

        return PrepareOutcome.Loaded;
    }

    /// <summary>
    /// Is this exception "the native stack could not be loaded" rather than a genuine failure?
    ///
    /// <para>Wider than the obvious two, because the load can fail one frame removed from where it
    /// is triggered: a static initializer that P/Invokes surfaces as
    /// <see cref="TypeInitializationException"/> wrapping the real cause, so the INNER exception is
    /// unwrapped rather than the outer type matched. Deliberately still a CLOSED list — a blanket
    /// catch here would swallow real defects into a quiet "engine unavailable", which is the
    /// failure mode this project has spent several rounds removing from other subsystems.</para>
    ///
    /// <para><see cref="global::System.Runtime.InteropServices.SEHException"/> is included on
    /// evidence, not principle: REL-15 is this project's own case of a native dependency faulting
    /// on user machines no dev machine reproduced. Note the limit — an SEH fault is only catchable
    /// at all when the CLR converts it, and the illegal-instruction class this cannot catch is what
    /// <see cref="ParakeetNativeProbe"/> exists to pre-empt.</para>
    /// </summary>
    private static bool IsNativeLoadFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is DllNotFoundException or BadImageFormatException
                or global::System.Runtime.InteropServices.SEHException
                or EntryPointNotFoundException)
            {
                return true;
            }

            if (e is not TypeInitializationException) break;
        }

        return false;
    }
}
