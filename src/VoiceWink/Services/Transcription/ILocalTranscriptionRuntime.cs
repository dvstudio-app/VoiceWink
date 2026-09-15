namespace VoiceWink.Services.Transcription;

/// <summary>
/// What happened when a local model was prepared for use.
///
/// <para>A typed outcome rather than an exception, because the cases have genuinely different
/// handling per caller: recording start downloads on <see cref="NotDownloaded"/> but aborts on
/// <see cref="UnknownModel"/> and on <see cref="Unavailable"/>, while preload is fail-soft for all
/// of them. An exception would force every caller through a catch block to tell them apart.</para>
/// </summary>
public enum PrepareOutcome
{
    /// <summary>The model is loaded and its service is ready to transcribe.</summary>
    Loaded,
    /// <summary>A known catalog model whose files are not on disk. The caller decides whether to
    /// download — that is UI work (progress, status text) and stays in the ViewModel.</summary>
    NotDownloaded,
    /// <summary>The name matches no local runtime's catalog. NEVER silently substituted.</summary>
    UnknownModel,

    /// <summary>
    /// A known model whose runtime owns it but cannot run right now — compiled out by a build lever,
    /// or unsupported on this CPU (TRN-1 step 3).
    ///
    /// <para><b>Distinct from <see cref="UnknownModel"/> because ownership must not depend on
    /// runtime health.</b> A disabled runtime that stopped claiming its rows would make them
    /// unknown, and the download UI would then offer a multi-gigabyte download for an engine that
    /// cannot run — a lie built out of a health check.</para>
    ///
    /// <para><b>Distinct from <see cref="NotDownloaded"/> because it must never trigger a
    /// download.</b> That is the concrete failure this member prevents: every existing caller's
    /// if-chain treats "not Loaded and not UnknownModel" as "fetch it", so without its own member an
    /// unavailable engine silently takes the download path.</para>
    /// </summary>
    Unavailable,
}

/// <summary>
/// One local inference stack. Two implementations since TRN-1 step 3: <see cref="WhisperLocalRuntime"/>
/// (whisper.cpp) and <see cref="ParakeetLocalRuntime"/> (sherpa-onnx).
///
/// <para>The interface exists because five call sites used to reach <c>WhisperTranscriptionService</c>
/// directly, which meant adding an engine would have handed an <c>.onnx</c> path to whisper.cpp and
/// failed inside native code. Its two engines must partition the catalog: overlapping claims THROW
/// in <see cref="LocalModelPreparer.TryResolve"/> rather than picking one.</para>
/// </summary>
public interface ILocalTranscriptionRuntime
{
    /// <summary>
    /// Does this runtime own <paramref name="modelName"/>?
    ///
    /// <para><b>Catalog membership, never file existence.</b> Answering from disk would let a stray
    /// <c>.onnx</c> route to whisper.cpp — the model catalog is what says which engine a name
    /// belongs to, and a file on disk says nothing about that. Case-insensitive, so an imported
    /// <c>"GGML-SMALL"</c> resolves rather than being rejected as unknown.</para>
    /// </summary>
    bool CanServe(string modelName);

    /// <summary>The catalog spelling of a name this runtime serves, for display and for keying.
    /// Returns null when <see cref="CanServe"/> is false.</summary>
    string? Canonicalize(string modelName);

    /// <summary>The catalog's human-readable name. Safe to show — it is app-authored, unlike the
    /// stored setting, which may be hand-edited.</summary>
    string? DisplayName(string modelName);

    /// <summary>The service that transcribes with this runtime.</summary>
    ITranscriptionService Service { get; }

    /// <summary>
    /// <b>Static install eligibility:</b> could this runtime run at all on this BUILD and this
    /// MACHINE — build lever, process bitness, CPU floor. Deliberately NOT "will the next
    /// transcription succeed": it does not touch the filesystem, does not load native code, and
    /// says nothing about a quarantined DLL, a corrupt model, or a wedged device. Those surface at
    /// prepare time as <see cref="PrepareOutcome.Unavailable"/>, and an implementation must not
    /// start answering them here — this is a cheap property read on UI paths and per list refresh,
    /// and its answer must be stable for the process lifetime.
    ///
    /// <para>Separate from <see cref="CanServe"/> on purpose: ownership must NOT depend on health
    /// (a runtime that stopped claiming its rows when disabled would make them
    /// <see cref="PrepareOutcome.UnknownModel"/>), so the two questions need two answers. This is
    /// the one that DOWNLOAD UI must ask — <see cref="PrepareOutcome.Unavailable"/> arrives only at
    /// prepare time, which is AFTER a multi-hundred-megabyte download has already completed.</para>
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Make <paramref name="modelName"/> ready to transcribe with <paramref name="language"/>.
    /// Must be cheap when the model is already loaded — this sits in the recording-start hot path,
    /// whose hotkey→pill latency is probe-pinned (PR #86).
    /// </summary>
    Task<PrepareOutcome> PrepareAsync(string modelName, string? language, CancellationToken ct);
}
