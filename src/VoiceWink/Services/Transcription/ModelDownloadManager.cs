using System.Collections.Concurrent;
using Serilog;
using VoiceWink.Models;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// Downloads local transcription models from the catalog's URLs (DV Studio's own mirror at
/// <c>models.voicewink.app</c> since TRN-33) — single-file Whisper <c>.bin</c> models, and since
/// TRN-1 step 2 multi-file BUNDLES (the Parakeet ONNX encoder/decoder/joiner + tokens). The
/// manager itself is host-agnostic: the catalog rows own the URLs, this owns the transfer.
///
/// <para>The two layouts take different paths and different guarantees: a single file is an atomic
/// write (temp file → rename) with progress reporting, while a bundle stages into its own root,
/// hashes every file, writes its completion manifest LAST, and commits with one directory move.
/// <see cref="TranscriptionModelInfo.Files"/> is the discriminator, and <c>null</c> takes the
/// legacy body verbatim — which is the whole backward-compatibility story for the fourteen
/// <c>.bin</c> models already on users' disks.</para>
/// </summary>
public sealed class ModelDownloadManager
{
    private static ILogger Logger => Log.ForContext<ModelDownloadManager>();

    // Target 2x the model's size as required free space: one copy going to
    // .download, one rename slot. Prevents the race where a model just
    // barely fits on disk but a concurrent write starves the rename.
    private const double FreeSpaceSafetyMultiplier = 2.0;

    /// <summary>
    /// Free-space demand for the pre-download check (F30): 2x the REMAINING bytes, not the
    /// full model size — a mostly-complete <c>.download</c> partial resumes via a Range
    /// request and only writes the tail, and requiring 2x the full size again would refuse
    /// exactly the resume that needs the least space. No partial ⇒ remaining == full size,
    /// identical to the original check. Pure and internal so the formula is test-pinned.
    /// </summary>
    internal static long RequiredFreeBytes(long fileSizeBytes, long existingPartialBytes)
        => (long)(Math.Max(0, fileSizeBytes - existingPartialBytes) * FreeSpaceSafetyMultiplier);

    // Bounded retry + HTTP Range resume (large model files over flaky links). A stalled/dropped
    // transfer retries up to MaxDownloadAttempts, each attempt resuming from the bytes already in
    // the .download file via a Range request. Production uses the defaults; the internal test-seam
    // ctor overrides them so retry/resume tests run at ms scale instead of waiting real seconds.
    private const int DefaultMaxDownloadAttempts = 4;
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromSeconds(2);
    private readonly int _maxDownloadAttempts;
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _retryBackoff;

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _modelsDirectory;

    /// <summary>
    /// The catalog this manager answers install-state questions against (TRN-1 step 2).
    ///
    /// <para>Needed because <see cref="GetDownloadedModels"/> is handed a directory, not a
    /// descriptor, and a bundle's install state cannot be inferred from disk shape — the catalog is
    /// the authority on which files a bundle should contain. Injectable so bundle tests can supply a
    /// fake row: step 2 deliberately ships NO production bundle entry, so without this seam the
    /// bundle path would be untestable.</para>
    ///
    /// <para>Defaults to <see cref="PredefinedModels.Models"/>, so the DI registration in
    /// <c>App.xaml.cs</c> is untouched.</para>
    /// </summary>
    private readonly IReadOnlyList<TranscriptionModelInfo> _catalog;
    private readonly IReadOnlyList<TranscriptionModelInfo> _auxiliaryBundles;

    /// <summary>
    /// Staging lives in its own root rather than beside the models as <c>{name}.partial</c>.
    /// Two reasons: a model could legitimately be NAMED <c>{something}.partial</c> (the name guard
    /// accepts it), and a sibling staging directory would sit inside the namespace
    /// <see cref="GetDownloadedModels"/> enumerates. A dedicated root removes both problems at once.
    /// The name itself is reserved — see <see cref="Helpers.BundleRelativePathGuard.StagingRootName"/>.
    /// </summary>
    private string StagingRoot => Path.Combine(_modelsDirectory, Helpers.BundleRelativePathGuard.StagingRootName);

    // Serializes concurrent downloads of the same model. Without this, a user
    // clicking Download twice (or a retry racing with a still-in-flight
    // download) writes to the same temp file and corrupts it. Distinct models
    // download in parallel as before.
    //
    // Keyed case-INSENSITIVELY: Windows filenames are, so "ggml-small" and "GGML-SMALL" reach one
    // .bin and one .download partial. With the default ordinal comparer they took two different
    // locks and the serialization silently did nothing for that pair.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perModelLocks =
        new(ModelNameGuard.NameComparer);

    // Count of downloads actively transferring bytes (past all early-exit checks). Read by
    // ModelDownloadMaintenanceSource so the update/reset gate won't yank the binary out from
    // under a multi-GB download in progress.
    private int _activeDownloads;

    /// <summary>True while at least one model is actively downloading. Thread-safe.</summary>
    public bool IsDownloading => global::System.Threading.Volatile.Read(ref _activeDownloads) > 0;

    /// <summary>
    /// Production constructor. The models root is REQUIRED and supplied by the DI registration,
    /// which is the one place that resolves <c>AppPaths.EnsureModels()</c>.
    ///
    /// <para>There is deliberately no rootless overload. While one existed, isolation was opt-OUT:
    /// a test could construct this class in one argument and silently write into the user's live
    /// <c>%LOCALAPPDATA%/VoiceWink/Models</c> — which on this project is shared with the running app
    /// and with concurrent sessions. Deleting it makes that a compile error instead of a promise in
    /// a comment.</para>
    /// </summary>
    public ModelDownloadManager(IHttpClientFactory httpFactory, string modelsDirectory)
        : this(httpFactory, DefaultMaxDownloadAttempts, DefaultStallTimeout, DefaultRetryBackoff,
               modelsDirectory, catalog: null) { }

    /// <summary>
    /// Test seam: lets download retry/resume tests use a small attempt count and ms-scale timers
    /// instead of waiting real seconds. Production always uses the public ctor's defaults.
    ///
    /// <para><paramref name="modelsDirectory"/> is REQUIRED, and the public constructor above is the
    /// only caller that passes the real <c>%LOCALAPPDATA%/VoiceWink/Models</c> root. It was briefly
    /// optional, which made isolation opt-out: a future four-argument test would have silently gone
    /// back to writing into live app data, which on this project means colliding with the running
    /// app and with concurrent sessions. Making it mandatory turns that regression into a
    /// compile error.</para>
    ///
    /// <para>Verified when the tests were migrated: a full suite run leaves the real Models
    /// directory byte-identical, with no temp roots left behind.</para>
    /// </summary>
    internal ModelDownloadManager(IHttpClientFactory httpFactory, int maxDownloadAttempts,
        TimeSpan stallTimeout, TimeSpan retryBackoff, string modelsDirectory,
        IReadOnlyList<TranscriptionModelInfo>? catalog = null,
        IReadOnlyList<TranscriptionModelInfo>? auxiliaryBundles = null)
    {
        _httpFactory = httpFactory;
        _modelsDirectory = modelsDirectory;
        _maxDownloadAttempts = maxDownloadAttempts;
        _stallTimeout = stallTimeout;
        _retryBackoff = retryBackoff;
        _catalog = catalog ?? PredefinedModels.Models;
        // The G6 flip made the Parakeet catalog row config-conditional, and the OTHER era's
        // bundle became a catalog-less auxiliary the coordinator installs/deletes by descriptor.
        // GetDownloadedModels stays catalog-driven for everything unknown, but a KNOWN auxiliary
        // descriptor's verified install must still enumerate — the migration window's whole
        // design serves from it, and dropping it from the stems made the retry picker and App
        // Mode dropdown lose a model that was actively transcribing (Codex diff r1).
        _auxiliaryBundles = auxiliaryBundles ?? [Models.ParakeetCatalog.LegacyRow];
    }

    public string ModelsDirectory => _modelsDirectory;

    /// <summary>TRN-29 slice 4: does a staging directory for this model survive from an earlier
    /// (crashed or cancelled) install? Feeds the coordinator's <see cref="GgufTier.Staging"/>
    /// derivation and the delete tombstone's self-heal. FAIL CLOSED on an access failure (Codex
    /// diff r1): <c>Directory.Exists</c> collapses unreadable into absent — the same hazard
    /// <see cref="ProbeOccupant"/> documents — and "absent" here can clear a delete tombstone
    /// over a staging tree that still exists, or flip <c>GgufTier.Staging</c> to
    /// <c>Absent</c> and start a download over it. Unknown answers TRUE: "cannot rule staging
    /// out", which defers rather than destroys, and the next derivation re-probes.</summary>
    internal bool HasStagingFor(string modelName)
    {
        var staging = Path.Combine(StagingRoot, ValidateModelName(modelName));
        try
        {
            global::System.IO.File.GetAttributes(staging);
            return true; // something occupies the path (directory OR debris file) - not ruled out.
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // POSITIVE absence - the only answer that may say "no staging".
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unreadable = cannot rule out = fail closed.
        }
    }

    /// <summary>
    /// TRN-29 slice 4: remove a model's staging leftovers, containment-safe and serialized under
    /// the SAME per-model lock as its installs — the delete flow's staging sweep must not race a
    /// concurrent install of the same name, and the coordinator must never re-implement deletion
    /// primitives outside the class that owns kernel-verified containment (slice-4 plan round,
    /// challenge 4a). A locked or unverifiable tree throws exactly as the install path's sweep
    /// would; callers treat that as the partial-failure arm of their own flow.
    /// </summary>
    internal void CleanupStagingFor(string modelName)
    {
        var sanitizedName = ValidateModelName(modelName);
        var staging = Path.Combine(StagingRoot, sanitizedName);

        var modelLock = _perModelLocks.GetOrAdd(sanitizedName, _ => new SemaphoreSlim(1, 1));
        modelLock.Wait();
        try
        {
            EnsureContainedIfPresent(StagingRoot, "the staging root");
            EnsureContainedIfPresent(staging, "the staging directory");
            DeleteOccupantFailClosed(staging, "stale staging directory");
        }
        finally
        {
            modelLock.Release();
        }
    }

    /// <summary>
    /// Validate a model name before composing it into a path — it comes back unchanged or an
    /// exception is thrown. Named for what it does: the previous <c>SanitizeModelName</c> called
    /// <c>Path.GetFileName</c> and kept whatever came back, so <c>..\ggml-small</c> became
    /// <c>ggml-small</c> and silently aliased a real model, while <c>ggml-small.</c>, <c>COM1</c>
    /// and the 8.3 form <c>GGML-L~1</c> passed unexamined. Nothing here repairs a name any more,
    /// and the name of the method should not suggest otherwise.
    /// See <see cref="ModelNameGuard"/> for why each rejected case matters.
    /// </summary>
    private static string ValidateModelName(string name)
    {
        var validated = ModelNameGuard.Validate(name);

        // The staging root's name is reserved HERE rather than in the download path alone, because
        // every descriptor-driven operation composes a path from this method — lookup, enumeration
        // and DELETE included. Rejecting it only on download left a descriptor named ".staging" able
        // to aim a recursive delete at the shared staging root. ModelNameGuard cannot catch it: the
        // name's stem is the empty string, so its reserved-device check never fires.
        if (string.Equals(validated, Helpers.BundleRelativePathGuard.StagingRootName,
                          StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid model name: '{Helpers.BundleRelativePathGuard.StagingRootName}' is reserved for staging.");
        }

        return validated;
    }

    /// <summary>What actually occupies a path — distinguishing genuine ABSENCE from an
    /// access/IO failure, which <see cref="File.Exists"/> and <see cref="Directory.Exists"/>
    /// both collapse into the same <c>false</c>.
    ///
    /// <para>That collapse is not a theoretical concern here: it is the identical ambiguity that
    /// made the manifest delete unsafe. A destructive caller reading "unreadable" as "absent"
    /// reports success for work it never did.</para>
    /// </summary>
    private enum OccupantKind { Absent, RegularFile, Directory, Unreadable }

    private static OccupantKind ProbeOccupant(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                ? OccupantKind.Directory
                : OccupantKind.RegularFile;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return OccupantKind.Absent;
        }
        catch
        {
            return OccupantKind.Unreadable;
        }
    }

    /// <summary>
    /// Download a model to the local models directory with progress reporting.
    ///
    /// <para>Dispatches on <see cref="TranscriptionModelInfo.IsBundle"/>. A single-file model takes
    /// <see cref="DownloadSingleFileAsync"/>, whose body is unchanged from before TRN-1 step 2 —
    /// that is the backward-compatibility guarantee for the fourteen Whisper <c>.bin</c> models
    /// already on users' disks.</para>
    ///
    /// <para>Returns a <see cref="ModelLocation"/> rather than a bare path: the result is a FILE for
    /// one kind and a DIRECTORY for the other, and a caller that cannot tell which will eventually
    /// call <c>File.Delete</c> on a directory.</para>
    /// </summary>
    public async Task<ModelLocation> DownloadModelAsync(
        TranscriptionModelInfo model,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ValidateDescriptor(model);

        return model.IsBundle
            ? new ModelLocation(ModelLocationKind.Bundle,
                await DownloadBundleAsync(model, progress, ct).ConfigureAwait(false))
            : new ModelLocation(ModelLocationKind.SingleFile,
                await DownloadSingleFileAsync(model, progress, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Catalog-shape validation, run before any filesystem work so a malformed entry fails the whole
    /// install rather than part of it. Bundle rules are stricter than the legacy ones on purpose:
    /// a bundle without per-file hashes cannot be installed transactionally in any meaningful sense.
    /// </summary>
    private static void ValidateDescriptor(TranscriptionModelInfo model)
    {
        var sanitizedName = ValidateModelName(model.Name);

        // (The reserved staging name is refused by ValidateModelName above, so it is rejected by
        // EVERY descriptor-driven operation rather than by download alone.)

        if (model.Files is null)
        {
            if (string.IsNullOrEmpty(model.DownloadUrl))
                throw new ArgumentException("Model has no download URL", nameof(model));
            return;
        }

        if (model.Files.Count == 0)
            throw new ArgumentException($"Model '{model.Name}' declares an empty file bundle", nameof(model));

        // A bundle installs to `{models}/{name}`, while a legacy model lives at `{models}/{name}.bin`
        // — so a BUNDLE named "ggml-small.bin" occupies exactly the path of the LEGACY model
        // "ggml-small". They take different per-model locks (the names differ), so nothing
        // serializes them, and the bundle's debris sweep would recursively delete a valid installed
        // model out from under its owner. Refused rather than reconciled: no bundle needs the
        // extension, and the alternative is a shared namespace with two spellings for one path.
        if (sanitizedName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Model '{model.Name}' is a bundle and must not end in '.bin' — that path belongs to the single-file layout",
                nameof(model));
        }

        // Carrying both shapes would leave "which one wins" to whichever branch ran first.
        // FallbackUrl joined the single-file fields with TRN-33: a bundle-level fallback would be
        // silently ignored (the bundle path reads only ModelFile.FallbackUrl), which is exactly
        // the authoring trap this guard exists to refuse.
        if (!string.IsNullOrEmpty(model.DownloadUrl) || !string.IsNullOrEmpty(model.Sha256Hash)
            || !string.IsNullOrEmpty(model.FallbackUrl))
            throw new ArgumentException(
                $"Model '{model.Name}' mixes bundle and single-file fields", nameof(model));

        foreach (var file in model.Files)
        {
            if (file is null)
                throw new ArgumentException($"Model '{model.Name}' has a null bundle file", nameof(model));
            if (string.IsNullOrWhiteSpace(file.Url))
                throw new ArgumentException($"Model '{model.Name}' has a bundle file with no URL", nameof(model));
            if (file.FileSizeBytes <= 0)
                throw new ArgumentException($"Model '{model.Name}' has a bundle file with a non-positive size", nameof(model));
            if (!IsSha256(file.Sha256Hash))
                throw new ArgumentException($"Model '{model.Name}' has a bundle file with a malformed SHA-256", nameof(model));
        }

        Helpers.BundleRelativePathGuard.ValidateAll(model.Files.Select(f => f.RelativePath).ToList());

        // Overflow would turn a free-space demand into a negative number, which compares as "plenty
        // of room" — the failure direction that matters for a precheck.
        long total = 0;
        foreach (var file in model.Files)
        {
            if (total > long.MaxValue - file.FileSizeBytes)
                throw new ArgumentException($"Model '{model.Name}' has an implausible aggregate size", nameof(model));
            total += file.FileSizeBytes;
        }

        // The top-level size is documented as the aggregate, and it is what the Models page renders.
        // Enforced UNCONDITIONALLY: exempting zero made the rule optional in exactly the case a
        // catalog author is most likely to hit — leaving the field off entirely — and then the page
        // would render "0 bytes" for a multi-gigabyte bundle.
        if (model.FileSizeBytes != total)
        {
            throw new ArgumentException(
                $"Model '{model.Name}' declares {model.FileSizeBytes} bytes but its files sum to {total}",
                nameof(model));
        }
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private async Task<string> DownloadSingleFileAsync(
        TranscriptionModelInfo model,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var sanitizedName = ValidateModelName(model.Name);
        var fileName = $"{sanitizedName}.bin";
        var finalPath = Path.Combine(_modelsDirectory, fileName);

        if (File.Exists(finalPath))
        {
            Logger.Information("Model already exists: {Path}", finalPath);
            progress?.Report(1.0);
            return finalPath;
        }

        var tempPath = finalPath + ".download";

        // Ensure directory exists (may have been deleted after constructor ran)
        Directory.CreateDirectory(_modelsDirectory);

        // Serialize concurrent downloads of the same model: prevents temp-file
        // corruption when the same download is triggered twice (e.g., retry
        // racing with an in-flight download). Per-model semaphore keeps
        // distinct models parallel.
        var modelLock = _perModelLocks.GetOrAdd(sanitizedName, _ => new SemaphoreSlim(1, 1));
        await modelLock.WaitAsync(ct).ConfigureAwait(false);

        var counted = false;
        try
        {
            // A concurrent waiter may have already completed the download — recheck
            // under the lock before doing any I/O.
            if (File.Exists(finalPath))
            {
                Logger.Information("Model finished by a concurrent download: {Path}", finalPath);
                progress?.Report(1.0);
                return finalPath;
            }

            // Disk-free-space pre-check: allocating 2x headroom avoids the mid-download
            // IOException and the temp+final rename both fitting. Based on the REMAINING
            // bytes, not the full model size (F30): a mostly-complete .download partial
            // resumes via a Range request and only writes the tail — requiring 2x the full
            // size again would spuriously refuse exactly the resume that needs the least
            // space. No partial ⇒ remaining == FileSizeBytes, identical to the old check.
            // If FileSizeBytes is unknown (hint only), skip — can't check what we don't know.
            if (model.FileSizeBytes > 0)
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(_modelsDirectory)
                                              ?? throw new InvalidOperationException("Models directory has no drive root"));
                    var existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                    var required = RequiredFreeBytes(model.FileSizeBytes, existing);
                    if (drive.AvailableFreeSpace < required)
                    {
                        throw new IOException(
                            $"Not enough disk space to download '{model.DisplayName}'. " +
                            $"Needed: {required / 1_000_000} MB (2x the remaining bytes for safety). " +
                            $"Available: {drive.AvailableFreeSpace / 1_000_000} MB.");
                    }
                }
                catch (Exception ex) when (ex is not IOException)
                {
                    // DriveInfo can throw on network-path ambiguity or permission issues —
                    // don't fail the download over a pre-check that couldn't even measure.
                    Logger.Warning(ex, "Could not check disk free space before download — proceeding anyway");
                }
            }

            // Mark "actively downloading" only now — past the already-exists / disk-space
            // early exits — so IsDownloading reflects a real in-flight transfer, not a no-op call.
            global::System.Threading.Interlocked.Increment(ref _activeDownloads);
            counted = true;

            Logger.Information("Downloading model {Name} from {Url}", model.Name, model.DownloadUrl);

            // Bounded retry with HTTP Range resume per SOURCE — the mirror first, then the
            // pinned-upstream fallback (TRN-33; see DownloadAndVerifyFromSourcesAsync). Only
            // transient failures retry within a source; the fallback engages when a source's
            // budget is spent, it failed non-transiently, or its completed bytes missed the pin.
            // Verification stays inside the semaphore so a concurrent Download sees either no
            // temp file (pre-verify) or the final .bin (post-rename), never an in-flight check.
            await DownloadAndVerifyFromSourcesAsync(model, model.Sha256Hash, tempPath, progress, ct)
                .ConfigureAwait(false);

            // Atomic rename.
            File.Move(tempPath, finalPath, overwrite: true);
            Logger.Information("Model downloaded: {Path}", finalPath);
            progress?.Report(1.0);

            return finalPath;
        }
        catch (Exception ex)
        {
            // Keep the .download partial on transient network failures so a later attempt — this
            // call's retry loop is exhausted, or the user re-clicks Download — can RESUME from it
            // via a Range request. Delete it for everything else (bad args, permissions, an
            // unexpected fault, or a SHA-256 mismatch, which throws the non-transient
            // InvalidOperationException) so a poisoned partial can never survive.
            if (!IsTransientDownloadError(ex, ct))
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
            // Rethrow with the original type intact — the caller (ViewModel) wraps for UI display;
            // types like UnauthorizedAccessException / CryptographicException carry real signal.
            throw;
        }
        finally
        {
            if (counted) global::System.Threading.Interlocked.Decrement(ref _activeDownloads);
            modelLock.Release();
        }
    }

    /// <summary>
    /// Transactional multi-file install (TRN-1 step 2). Returns the bundle directory.
    ///
    /// <para><b>Sequencing, all of it load-bearing.</b> Under the same per-model lock the
    /// single-file path uses (so a legacy download and a bundle install of one name serialize):</para>
    /// <list type="number">
    /// <item>if the destination is fully INSTALLED — manifest present, matching the catalog plan,
    /// every payload at its recorded size — report 1.0 and return. The early-exit predicate is the
    /// FULL installed check, not merely "a manifest is there": a bundle with an intact marker and a
    /// hand-truncated payload would otherwise early-exit as success while
    /// <see cref="TryGetInstalledLocation"/> called it missing, and the debris branch below could
    /// never run — a permanently wedged model, recoverable only by Delete.</item>
    /// <item>otherwise, a destination we can POSITIVELY classify as wrong is DEBRIS regardless of
    /// manifest presence, and is deleted — that is what makes a damaged bundle self-heal on the
    /// next download. <b>"Otherwise" is not unconditional</b>, and this line used to say it was:
    /// a destination we cannot classify (<c>Indeterminate</c> — a lock, an ACL, an unresolvable
    /// path) or one written by a NEWER build (<c>NewerSchema</c>) is PRESERVED and the install is
    /// refused with <see cref="ModelInstallBlockedException"/>. Unknown is not corrupt, and
    /// deleting on unknown is how a transient sharing failure destroys a complete multi-gigabyte
    /// install.</item>
    /// <item>stage into the dedicated staging root, per-file, each with the same Range-resume and
    /// bounded retry the single-file path uses.</item>
    /// <item>verify every file's SHA-256.</item>
    /// <item>write the manifest LAST.</item>
    /// <item>check cancellation, then commit with an atomic same-volume
    /// <see cref="Directory.Move"/>. Progress reaches 1.0 only after the commit succeeds.</item>
    /// </list>
    ///
    /// <para>A crash at any point leaves either staging debris (no manifest at the destination, so
    /// not installed) or a complete directory. There is no intermediate state that reads as
    /// installed.</para>
    ///
    /// <para><b>Stale staging is discarded, not resumed.</b> A staging directory surviving a crash
    /// has unknown provenance, and the cheap validity check available here is existence and size,
    /// not hashes. Restarting costs one re-download; accepting it risks committing a bundle this run
    /// never verified.</para>
    /// </summary>
    private async Task<string> DownloadBundleAsync(
        TranscriptionModelInfo model,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var sanitizedName = ValidateModelName(model.Name);
        var files = model.Files!;
        var destination = Path.Combine(_modelsDirectory, sanitizedName);
        var staging = Path.Combine(StagingRoot, sanitizedName);

        var modelLock = _perModelLocks.GetOrAdd(sanitizedName, _ => new SemaphoreSlim(1, 1));
        await modelLock.WaitAsync(ct).ConfigureAwait(false);

        var counted = false;
        var containmentVerified = false;
        try
        {
            // Containment comes FIRST — before the install-state read, before enumeration, before
            // any write. IsBundleInstalled opens a manifest and stats payloads, so running it first
            // would let a junctioned destination answer "installed" from someone else's files.
            EnsureContainedIfPresent(StagingRoot, "the staging root");
            EnsureContainedIfPresent(staging, "the staging directory");
            EnsureContainedIfPresent(destination, "the bundle destination");
            containmentVerified = true;

            // Recheck under the lock using the FULL classification — the single-file path's
            // File.Exists({name}.bin) is simply the wrong question for a bundle.
            var destinationState = ClassifyBundle(model, destination);
            if (destinationState == BundleState.Installed)
            {
                Logger.Information("Model bundle already installed: {Path}", destination);
                progress?.Report(1.0);
                return destination;
            }

            // INDETERMINATE STOPS THE INSTALL. This is the data-loss boundary: we could not
            // determine what is at the destination, and "unknown" is not "corrupt". Proceeding
            // would delete it as debris — which, for a transient sharing or ACL failure over a
            // complete multi-gigabyte install, destroys it. Existing bytes are preserved and the
            // user is told to retry; the next attempt reclassifies.
            if (destinationState == BundleState.NewerSchema)
            {
                // A NEWER VoiceWink installed this model. Retrying cannot help, and the transient
                // message's "delete the folder and download it fresh" tail would destroy a
                // perfectly good install — the exact outcome this classification exists to prevent.
                Logger.Warning(
                    "Refusing to install '{Model}': its files were installed by a newer version of VoiceWink. Nothing was changed.",
                    model.Name);
                throw new ModelInstallBlockedException(
                    $"'{model.DisplayName}' was installed by a newer version of VoiceWink. Nothing was " +
                    "changed — update VoiceWink to use it, or pick a different model.",
                    isRetryable: false);
            }

            if (destinationState == BundleState.Indeterminate)
            {
                // Typed, and logged at WARNING by the caller: this is an environmental condition,
                // not a defect, and an Error here would forward a locked folder to Sentry.
                Logger.Warning(
                    "Refusing to install '{Model}': the existing files could not be classified. Nothing was changed.",
                    model.Name);
                throw new ModelInstallBlockedException(
                    $"Can't verify the existing files for '{model.DisplayName}'. Nothing was changed — " +
                    "close anything using the models folder, then try again. If it keeps happening, " +
                    "delete the model's folder yourself and download it fresh.",
                    isRetryable: true);
            }

            global::System.Threading.Interlocked.Increment(ref _activeDownloads);
            counted = true;

            // Debris is cleared BEFORE free space is measured. Ordering it the other way walked the
            // stale staging tree — following any interior reparse point, and crediting bytes that
            // are about to be discarded, so the check under-demanded exactly when it mattered.
            //
            // Reached only for Absent or Invalid, so this deletes something we positively KNOW is
            // not a valid install — including a regular FILE sitting where the bundle directory
            // belongs, which would otherwise fail the commit move forever.
            DeleteOccupantFailClosed(destination, "debris at the bundle destination");
            DeleteOccupantFailClosed(staging, "stale staging directory");

            // Nothing resumable survives the two deletes above, so the demand is the full size.
            var totalBytes = files.Sum(f => f.FileSizeBytes);
            EnsureFreeSpaceForBundle(model, totalBytes);

            Directory.CreateDirectory(staging);

            Logger.Information("Installing model bundle {Name} ({Count} files)", model.Name, files.Count);

            long completedBytes = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var target = Helpers.BundleRelativePathGuard.ResolveWithin(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                var fileStart = completedBytes;
                var fileProgress = progress is null ? null : new Progress<double>(p =>
                    progress.Report(Math.Clamp((fileStart + (p * file.FileSizeBytes)) / totalBytes, 0, 0.99)));

                await DownloadBundleFileAsync(model, file, target, fileProgress, ct).ConfigureAwait(false);
                await VerifyBundleFileHashAsync(model, file, target, ct).ConfigureAwait(false);

                // The declared size is not merely a download hint here — it is half the installed
                // predicate. Committing a correctly-hashed file whose DECLARED size is wrong would
                // produce an install that immediately reports itself missing and re-downloads
                // forever. Catch the catalog error at install time instead.
                var actualLength = new FileInfo(target).Length;
                if (actualLength != file.FileSizeBytes)
                {
                    throw new InvalidOperationException(
                        $"Model '{model.DisplayName}' declares {file.FileSizeBytes} bytes for a bundle file " +
                        $"but {actualLength} were written — the catalog entry is wrong.");
                }

                completedBytes += file.FileSizeBytes;
            }

            // The manifest is the LAST write into staging, so a crash before this point leaves a
            // staging tree that can never be mistaken for an install.
            var manifestPath = Path.Combine(staging, Helpers.BundleRelativePathGuard.ManifestFileName);
            await File.WriteAllTextAsync(manifestPath,
                ModelInstallManifest.For(sanitizedName, files).ToJson(), ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            Directory.Move(staging, destination);
            Logger.Information("Model bundle installed: {Path}", destination);
            progress?.Report(1.0);
            return destination;
        }
        catch
        {
            // Staging is ALWAYS discarded on failure, transient or not — which is the same rule the
            // entry path applies, and the two must agree or the comments start lying about the
            // behaviour. A bundle install therefore does NOT resume across calls; only the retry
            // loop WITHIN one call resumes, from the .download partials inside staging.
            //
            // That is a deliberate difference from the single-file path, which keeps its .download
            // across calls: there, one file's partial is self-describing and cheap to validate. A
            // staging TREE that survived a crash is not — the affordable check is existence and
            // size, not hashes — so trusting it risks committing a bundle this run never verified.
            // The cost is a re-download; the alternative is an unverifiable install.
            //
            // GUARDED on containmentVerified: if the gate above is what threw, the staging path has
            // NOT been proven to resolve inside the models directory, and cleaning up anyway is how
            // a junctioned .staging gets an external file deleted on the way out.
            if (containmentVerified)
            {
                try { DeleteOccupantFailClosed(staging, "failed bundle install"); } catch { }
            }
            throw;
        }
        finally
        {
            if (counted) global::System.Threading.Interlocked.Decrement(ref _activeDownloads);
            modelLock.Release();
        }
    }

    /// <summary>
    /// One bundle file, reusing the single-file streaming path so Range resume, the stall timeout,
    /// the transient-retry classification AND the TRN-33 mirror→upstream fallback are shared
    /// rather than reimplemented. Per-source SHA verification runs here at download time; the
    /// transactional whole-bundle verification still runs later, unchanged, as the install's own
    /// gate — a DELIBERATE second hash of the same bytes (for the 940 MB GGUF, a few seconds of
    /// read per install): the transaction's gate stays independent of the download's, so a
    /// future change to either cannot silently strip the other's coverage.
    /// </summary>
    private async Task DownloadBundleFileAsync(TranscriptionModelInfo model, ModelFile file,
        string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        var tempPath = targetPath + ".download";
        var fileDescriptor = new TranscriptionModelInfo
        {
            Name = model.Name,
            DisplayName = model.DisplayName,
            Provider = model.Provider,
            DownloadUrl = file.Url,
            FallbackUrl = file.FallbackUrl,
            FileSizeBytes = file.FileSizeBytes,
        };

        await DownloadAndVerifyFromSourcesAsync(fileDescriptor, file.Sha256Hash, tempPath, progress, ct)
            .ConfigureAwait(false);

        File.Move(tempPath, targetPath, overwrite: true);
    }

    /// <summary>
    /// TRN-33 fallback (owner decision, 2026-08-27): download one payload into
    /// <paramref name="tempPath"/>, trying the descriptor's
    /// <see cref="TranscriptionModelInfo.DownloadUrl"/> (the mirror) first and its pinned-upstream
    /// <see cref="TranscriptionModelInfo.FallbackUrl"/> second. Each source gets the full
    /// transient-retry budget with Range resume; the fallback is reached only when the mirror's
    /// budget is exhausted, it failed non-transiently, or its COMPLETED bytes miss
    /// <paramref name="expectedSha256"/> — a mirror serving wrong bytes is as broken as one
    /// serving none, and the pin gates BOTH sources equally. The switch is logged at Warning BY
    /// DESIGN: a silently absorbed mirror outage would never get fixed. The partial crosses the
    /// switch only for a TRANSIENT failure (both hosts serve byte-identical bytes, and the
    /// per-source sha gate plus the outer catch's delete self-heal a mixed result); a
    /// non-transient failure deletes it — that partial's provenance is a host that lied, not one
    /// that stuttered. Cancellation never falls back — the filter re-checks the token.
    ///
    /// <para><b>Only SOURCE failures fall back</b> (Codex round-2 B2): the switch filter
    /// allowlists <see cref="global::System.Net.Http.HttpRequestException"/> and
    /// <see cref="IModelSourceFailure"/>, so a LOCAL fault — a locked .download file, a full
    /// disk, a hash-read error — propagates without ever contacting the fallback host, which the
    /// shipped privacy policy says is reached only when the mirror cannot deliver the file
    /// correctly.</para>
    ///
    /// <para><b>The carried-partial weld gets ONE clean retry</b> (Codex round-2 B1): a transient
    /// mirror failure can leave a CORRUPT partial that a healthy upstream then Range-appends a
    /// valid suffix onto — the weld fails verification through no fault of the upstream, and
    /// without this arm the failure repeats on every call (mirror re-serves the corrupt prefix,
    /// upstream re-appends, checksum re-fails), making installs impossible against a healthy
    /// fallback. A <see cref="ModelChecksumMismatchException"/> on ANY source whose attempt began
    /// with partial bytes — carried from the previous host, a previous call, or local corruption —
    /// replays that source once from byte zero (per-source budget; Codex verification round).</para>
    ///
    /// <para>A null <paramref name="expectedSha256"/> skips the hash gate (the legacy single-file
    /// contract for rows without a pin); transport failures still fall back.</para>
    /// </summary>
    private async Task DownloadAndVerifyFromSourcesAsync(TranscriptionModelInfo descriptor,
        string? expectedSha256, string tempPath, IProgress<double>? progress, CancellationToken ct)
    {
        var sources = new List<string> { descriptor.DownloadUrl! };
        if (!string.IsNullOrEmpty(descriptor.FallbackUrl))
        {
            sources.Add(descriptor.FallbackUrl);
        }

        // Per-source clean-retry budget (Codex verification round): the checksum-after-partial
        // replay must be available on EVERY source, not just the fallback — a PRE-EXISTING
        // locally corrupt .download partial welds on the MIRROR's own attempt too, and without
        // the s==0 arm that local fault fell through to a Hugging Face request the privacy
        // policy does not disclose. One clean replay per source keeps it bounded.
        var cleanRetryUsedForSource = new bool[sources.Count];
        for (var s = 0; s < sources.Count; s++)
        {
            // Whether this source's attempt starts on pre-existing partial bytes (carried from
            // the previous host, a prior call, or an unrelated writer) — the precondition for
            // the clean-retry arm below.
            var startedWithPartial = File.Exists(tempPath) && new FileInfo(tempPath).Length > 0;
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await StreamModelToTempAsync(descriptor, sources[s], tempPath, progress, ct)
                            .ConfigureAwait(false);
                        break; // tempPath now holds this source's complete file
                    }
                    catch (Exception ex) when (attempt < _maxDownloadAttempts && IsTransientDownloadError(ex, ct))
                    {
                        Logger.Warning(ex,
                            "Model download attempt {Attempt}/{Max} from {Url} failed transiently; retrying (resuming from partial) after {Backoff}",
                            attempt, _maxDownloadAttempts, sources[s], _retryBackoff);
                        await Task.Delay(_retryBackoff, ct).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    await VerifyTempFileHashAsync(descriptor, expectedSha256, tempPath, ct).ConfigureAwait(false);
                }
                return;
            }
            catch (ModelChecksumMismatchException ex) when (startedWithPartial
                                                            && !cleanRetryUsedForSource[s]
                                                            && !ct.IsCancellationRequested)
            {
                // Codex round-2 B1 + verification round: an attempt that STARTED on partial
                // bytes may fail verification through no fault of the host — the partial's
                // provenance is a previous host, a previous call, or local corruption. One
                // replay of THIS source from byte zero settles whether the source or the weld
                // was bad; a second mismatch propagates (and, on a non-last source, falls back).
                // The delete is deliberately UNSWALLOWED: if the partial cannot be deleted the
                // fault is LOCAL (a lock), and the plain IOException escaping here must
                // propagate rather than let a retry loop re-type a local fault as a source
                // failure. File.Delete does not throw for a missing file.
                Logger.Warning(ex,
                    "TRN-33 fallback: an attempt that started on partial bytes failed verification against {Url}; retrying this source once from a clean start",
                    sources[s]);
                File.Delete(tempPath);
                cleanRetryUsedForSource[s] = true;
                s--; // the for-increment replays this source
            }
            catch (Exception ex) when (s < sources.Count - 1
                                       && !ct.IsCancellationRequested
                                       && (ex is global::System.Net.Http.HttpRequestException
                                              or IModelSourceFailure
                                              or InvalidDataException)) // sealed; see ModelDownloadFailures
            {
                Logger.Warning(ex,
                    "TRN-33 fallback: model download from the mirror ({Primary}) failed; retrying from the pinned upstream source {Fallback}",
                    sources[s], sources[s + 1]);
                // Keep the partial across the switch when the failure was TRANSIENT, so a flaky
                // link still converges: pre-fallback, the kept partial grew monotonically across
                // user retries, and an unconditional delete here reset the mirror's progress on
                // every call — an 874 MB model on a poor connection could then never finish
                // (self-review B1). Resuming another host's partial is safe because both hosts
                // serve byte-identical files (the uploader's fallback↔upstream parity check) and
                // the per-source sha gate rejects a mixed result, whose non-transient failure
                // then deletes the partial in the outer catch — self-healing in one extra call.
                // A NON-transient failure (404, wrong bytes) still deletes: that partial's
                // provenance is a host that lied, not one that stuttered.
                if (!IsTransientDownloadError(ex, ct))
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
        }

        // Unreachable while callers validate DownloadUrl before dispatch (the last source either
        // returned or let its exception propagate) — kept for totality over the source list.
        throw new InvalidOperationException($"No download source succeeded for model '{descriptor.DisplayName}'.");
    }

    /// <summary>
    /// Hash-gate one downloaded temp file against its catalog pin. Extracted so the single-file
    /// path and the bundle per-file path verify identically, per SOURCE, inside the TRN-33
    /// fallback loop — which is what lets a mirror serving wrong bytes fall back instead of
    /// failing the whole install.
    /// </summary>
    private static async Task VerifyTempFileHashAsync(TranscriptionModelInfo descriptor,
        string expectedSha256, string tempPath, CancellationToken ct)
    {
        string actualHash;
        // Read+hash in its own scope so the stream is closed before any delete attempt —
        // Windows blocks File.Delete on an open FileStream.
        using (var sha256 = global::System.Security.Cryptography.SHA256.Create())
        await using (var hashStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true))
        {
            var hashBytes = await sha256.ComputeHashAsync(hashStream, ct).ConfigureAwait(false);
            actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            // {ModelDisplayName}, not {Model}: a catalog DISPLAY name ("Whisper Small") is app-authored
            // text with a space, which the root ModelIdEnricher would mask under {Model}.
            Logger.Error("SHA-256 mismatch for model {ModelDisplayName}: expected {Expected}, got {Actual}",
                descriptor.DisplayName, expectedSha256, actualHash);
            // Typed (still an InvalidOperationException) so the source loop's clean-retry arm and
            // the fallback filter can tell wrong BYTES from every other invalid-operation shape.
            throw new ModelChecksumMismatchException(
                $"SHA-256 checksum mismatch for model '{descriptor.DisplayName}'. Download may be corrupted.");
        }
        Logger.Information("SHA-256 verified for model {ModelDisplayName}", descriptor.DisplayName);
    }

    private static async Task VerifyBundleFileHashAsync(TranscriptionModelInfo model, ModelFile file,
        string targetPath, CancellationToken ct)
    {
        string actualHash;
        using (var sha256 = global::System.Security.Cryptography.SHA256.Create())
        await using (var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true))
        {
            var hashBytes = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        if (!string.Equals(actualHash, file.Sha256Hash, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Error("SHA-256 mismatch for bundle file in {ModelDisplayName}: expected {Expected}, got {Actual}",
                model.DisplayName, file.Sha256Hash, actualHash);
            throw new InvalidOperationException(
                $"SHA-256 checksum mismatch for model '{model.DisplayName}'. Download may be corrupted.");
        }
    }

    /// <summary>
    /// Free-space demand for a bundle. Callers must have cleared staging first: a bundle install
    /// never resumes across calls, so there are no pre-existing bytes to credit — passing 0 as the
    /// partial keeps the shared <see cref="RequiredFreeBytes"/> formula honest instead of
    /// discounting bytes that are about to be deleted.
    /// </summary>
    private void EnsureFreeSpaceForBundle(TranscriptionModelInfo model, long totalBytes)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_modelsDirectory)
                                      ?? throw new InvalidOperationException("Models directory has no drive root"));
            var required = RequiredFreeBytes(totalBytes, existingPartialBytes: 0);
            if (drive.AvailableFreeSpace < required)
            {
                throw new IOException(
                    $"Not enough disk space to download '{model.DisplayName}'. " +
                    $"Needed: {required / 1_000_000} MB (2x the remaining bytes for safety). " +
                    $"Available: {drive.AvailableFreeSpace / 1_000_000} MB.");
            }
        }
        catch (Exception ex) when (ex is not IOException)
        {
            Logger.Warning(ex, "Could not check disk free space before download — proceeding anyway");
        }
    }

    /// <summary>
    /// Clear whatever occupies <paramref name="path"/> — a directory tree, a regular file, or
    /// nothing — refusing anything that does not kernel-resolve inside the models directory.
    ///
    /// <para><b>Containment is checked on the resolved path, not the leaf's attributes.</b> An
    /// attribute check answers "is THIS a junction" and misses an ANCESTOR junction: with
    /// <c>Models\.staging</c> itself a junction, <c>Models\.staging\model</c> is an ordinary
    /// directory living outside the app's data that a leaf check waves through. Both sides are
    /// kernel-resolved instead.</para>
    ///
    /// <para><b>A regular file here is debris too.</b> Checking only <c>Directory.Exists</c> left a
    /// file sitting at the destination or staging path untouched, and then <c>Directory.Move</c>
    /// failed against it on every retry — a permanent wedge that no amount of re-downloading could
    /// clear.</para>
    ///
    /// <para>Leftover staging outlives a crashed run, so the app did not necessarily create every
    /// directory it is about to delete. Residual, stated plainly: a same-user attacker with write
    /// access to the models directory is not defended against, and never was.</para>
    /// </summary>
    private void DeleteOccupantFailClosed(string path, string what)
    {
        switch (ProbeOccupant(path))
        {
            case OccupantKind.Absent:
                return;

            case OccupantKind.Unreadable:
                // NOT treated as absent. Reporting success for a path we could not even classify is
                // how a delete comes back "done" having done nothing.
                throw new IOException(
                    $"Refusing to delete {what}: '{path}' could not be classified (access or I/O failure).");

            case OccupantKind.RegularFile:
                // Kernel-verified, not merely lexical. A lexical check passes for
                // `<models>\.staging\name` even when `.staging` is a junction — File.Delete then
                // follows the link and destroys a file OUTSIDE the app's data.
                if (!Helpers.VerifiedFileAccess.IsVerifiedUnder(path, _modelsDirectory))
                {
                    throw new IOException(
                        $"Refusing to delete {what}: '{path}' does not resolve inside the models directory.");
                }
                File.Delete(path);
                return;

            default:
                if (!Helpers.VerifiedFileAccess.IsDirectoryVerifiedUnder(path, _modelsDirectory))
                {
                    throw new IOException(
                        $"Refusing to delete {what}: '{path}' does not resolve inside the models directory " +
                        "(a reparse point, or its real path could not be read).");
                }
                Directory.Delete(path, recursive: true);
                return;
        }
    }

    /// <summary>
    /// Containment gate for a path this class is about to WRITE to, ENUMERATE, or READ install state
    /// from. Genuine absence is fine — the caller creates it — but anything already there must
    /// kernel-resolve inside the models directory first, and a path that cannot be classified is
    /// refused rather than assumed empty.
    /// </summary>
    private void EnsureContainedIfPresent(string path, string what)
    {
        switch (ProbeOccupant(path))
        {
            case OccupantKind.Absent:
                return;

            case OccupantKind.Unreadable:
                throw new IOException(
                    $"Refusing to use {what}: '{path}' could not be classified (access or I/O failure).");

            case OccupantKind.RegularFile:
                if (!Helpers.VerifiedFileAccess.IsVerifiedUnder(path, _modelsDirectory))
                    throw new IOException($"Refusing to use {what}: '{path}' does not resolve inside the models directory.");
                return;

            default:
                if (!Helpers.VerifiedFileAccess.IsDirectoryVerifiedUnder(path, _modelsDirectory))
                {
                    throw new IOException(
                        $"Refusing to use {what}: '{path}' does not resolve inside the models directory " +
                        "(a reparse point, or its real path could not be read).");
                }
                return;
        }
    }

    /// <summary>
    /// One download attempt from <paramref name="url"/> into <paramref name="tempPath"/>, resuming
    /// from any bytes already there via an HTTP Range request. The URL arrives as a parameter
    /// (TRN-33) because one descriptor can carry two sources — mirror and pinned-upstream
    /// fallback — and the source loop above this decides which is being tried. Throws a transient
    /// error (IOException / stall OperationCanceledException / EndOfStreamException for a short
    /// transfer) if the stream drops or ends before the server-declared total, so the caller's
    /// retry loop resumes; returns only once the file is complete.
    /// </summary>
    private async Task StreamModelToTempAsync(TranscriptionModelInfo model, string url,
        string tempPath, IProgress<double>? progress, CancellationToken ct)
    {
        long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        var httpClient = _httpFactory.CreateClient("downloads");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new global::System.Net.Http.Headers.RangeHeaderValue(existing, null);

        // REMOTE interaction is typed as a SOURCE failure (IModelSourceFailure / the already
        // remote-typed HttpRequestException) so the fallback loop can tell it from a LOCAL fault —
        // the privacy boundary Codex round-2 B2 named: a locked .download file or a full disk must
        // never trigger a Hugging Face request the policy says only mirror failures cause. The
        // wrap preserves transience (ModelSourceIOException IS an IOException) and lets a
        // user-cancel OperationCanceledException pass untouched; a stall/timeout OCE (ct not
        // requested) is remote unresponsiveness and wraps.
        var response = await RemoteAsync(
            () => httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct),
            ct).ConfigureAwait(false);
        using var _ = response;

        // 416: our partial is at/beyond the server's file size (stale or oversized partial) —
        // discard it so the next attempt restarts cleanly from zero. The delete is UNSWALLOWED
        // (Codex verification round): a delete-locked partial would otherwise loop 416 →
        // silent delete failure → the same oversized Range again, exhaust the budget, and
        // surface as a SOURCE-typed failure that contacts the fallback for a purely LOCAL
        // fault. A locked file now throws plain IOException here — local, no fallback.
        if (response.StatusCode == global::System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(tempPath);
            throw new ModelSourceIOException("Server rejected the resume range; discarded the partial so the next attempt restarts.");
        }

        response.EnsureSuccessStatusCode();

        // 206 = server honored our Range → append. 200 (server ignored Range, or no partial) =
        // full body from the start → overwrite whatever partial exists.
        bool resuming = existing > 0 && response.StatusCode == global::System.Net.HttpStatusCode.PartialContent;

        // Defense-in-depth for flaky/proxied links (eSIM / GFW intermediaries can mangle partials):
        // before appending, confirm the 206 actually starts where our partial ends. A mismatched
        // Content-Range.From would corrupt the file on append — discard the partial and restart.
        if (resuming && response.Content.Headers.ContentRange?.From is { } from && from != existing)
        {
            // Unswallowed for the same reason as the 416 path above.
            File.Delete(tempPath);
            throw new ModelSourceIOException(
                $"Resume range mismatch: requested from {existing} but server returned from {from}; discarded the partial to restart.");
        }

        long startOffset = resuming ? existing : 0;

        // Server-authoritative total (Content-Range total on 206, Content-Length on 200), or -1 if
        // the server didn't declare one. ONLY this drives the completeness check — never the
        // approximate model.FileSizeBytes hint, which legitimately differs from the real file.
        long serverTotal =
            response.Content.Headers.ContentRange?.Length
            ?? (resuming
                ? (response.Content.Headers.ContentLength is { } remaining ? startOffset + remaining : -1)
                : (response.Content.Headers.ContentLength ?? -1));
        long progressTotal = serverTotal > 0 ? serverTotal : model.FileSizeBytes;

        long written = startOffset;
        await using (var contentStream = await RemoteAsync(
            () => response.Content.ReadAsStreamAsync(ct), ct).ConfigureAwait(false))
        await using (var fileStream = new FileStream(tempPath, resuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            // Stall timeout: cancel if no bytes arrive within the window (a silent network drop).
            // CancelAfter resets on each read, so an actively-progressing download never times out.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(_stallTimeout);

            var buffer = new byte[81920];
            int bytesRead;
            double lastReportedPercent = -1;

            // The read (remote) and the write (local) alternate in one loop, so the source-failure
            // wrap is per-call: only the READ half may classify as a source failure.
            while ((bytesRead = await RemoteAsync(
                () => contentStream.ReadAsync(buffer, stallCts.Token).AsTask(), ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), stallCts.Token).ConfigureAwait(false);
                written += bytesRead;
                stallCts.CancelAfter(_stallTimeout); // reset stall timer on progress

                if (progressTotal > 0)
                {
                    // Throttle: report only on integer-percent change (not thousands of callbacks).
                    var currentPercent = Math.Floor((double)written / progressTotal * 100);
                    if (currentPercent > lastReportedPercent)
                    {
                        lastReportedPercent = currentPercent;
                        progress?.Report(Math.Min(1.0, (double)written / progressTotal));
                    }
                }
            }

            await fileStream.FlushAsync(ct).ConfigureAwait(false);
        }

        // If the server declared a total and the stream ended short, the connection closed early —
        // signal a transient failure so the caller resumes the remainder instead of accepting a
        // truncated file. (When the server gave no length, SHA-256 verification is the safety net.)
        if (serverTotal > 0 && written < serverTotal)
            throw new ModelSourceEndOfStreamException(
                $"Model download ended early: {written}/{serverTotal} bytes for '{model.DisplayName}'.");

        // General empty-download guard (restored from the removed ValidateDownloadedModelSize):
        // a 200 with an empty body and no declared length + no SHA configured would otherwise
        // promote a zero-byte .bin. Non-transient — a truly empty resource won't fix itself.
        // Plain InvalidDataException (the type is sealed, so it cannot carry the source-failure
        // marker) — the fallback filter allowlists it by concrete type instead: this is the one
        // site that throws it, and a remote empty body IS a source failure.
        if (written <= 0)
            throw new InvalidDataException($"Downloaded model '{model.DisplayName}' is empty.");
    }

    /// <summary>
    /// Runs one REMOTE operation and re-types its transport faults as
    /// <see cref="ModelSourceIOException"/> (see that type for why). HttpRequestException passes
    /// through untouched — it is already remote-typed and the fallback filter allowlists it. A
    /// user-cancel OperationCanceledException passes through untouched; an OCE with the user's
    /// token NOT requested is a stall/timeout — remote unresponsiveness — and wraps.
    /// </summary>
    private static async Task<T> RemoteAsync<T>(Func<Task<T>> operation, CancellationToken userCt)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or global::System.Net.Sockets.SocketException or TimeoutException
            || (ex is OperationCanceledException && !userCt.IsCancellationRequested))
        {
            throw new ModelSourceIOException($"Remote transfer failure: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A download failure that should be retried (and resumed): a dropped/reset connection, a
    /// stall (the linked stall-CTS firing), a timeout, or a short transfer. Caller-requested
    /// cancellation is never transient — it surfaces so the download aborts. A PERMANENT HTTP
    /// status (a 4xx other than 408/429 — e.g. 404 model removed, 403 auth) is not transient
    /// either: retrying a Range request against it is pointless, and the caller must delete the
    /// partial rather than keep resuming a URL that will never succeed.
    /// </summary>
    private static bool IsTransientDownloadError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false; // the user cancelled — don't retry

        if (ex is HttpRequestException httpEx)
        {
            // No status → a connection-level failure (DNS/connect/reset): transient.
            if (httpEx.StatusCode is not { } status) return true;
            var code = (int)status;
            // Retry only the genuinely-transient statuses; permanent 4xx are not retried.
            return status is global::System.Net.HttpStatusCode.RequestTimeout       // 408
                          or global::System.Net.HttpStatusCode.TooManyRequests      // 429
                || code >= 500;                                                     // 5xx
        }

        return ex is IOException          // includes EndOfStreamException + socket-abort-wrapped body reads
            or global::System.Net.Sockets.SocketException
            or TimeoutException
            or OperationCanceledException; // a stall trips the linked CTS (ct not requested, guarded above)
    }

    /// <summary>
    /// The local path of a downloaded SINGLE-FILE model, or null.
    ///
    /// <para>Named for exactly what it answers. It was <c>GetModelPath</c>, which would have had to
    /// return null for an installed bundle — a method called "get model path" answering "not there"
    /// about a model that IS there is a contract that lies, and the next caller would build on the
    /// lie. The rename keeps the property that makes it valuable: <c>WhisperLocalRuntime</c> resolves
    /// through here, so a bundle path is structurally unable to reach whisper.cpp.</para>
    /// </summary>
    public string? GetSingleFileModelPath(string modelName)
    {
        var sanitizedName = ValidateModelName(modelName);
        var path = Path.Combine(_modelsDirectory, $"{sanitizedName}.bin");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Where a model is installed, or null when it is not — descriptor-driven, so the expected shape
    /// comes from the CATALOG and is never inferred from what happens to be on disk. That is what
    /// makes a stray <c>{bundle-name}.bin</c> unable to mark a bundle installed, and vice versa.
    /// </summary>
    internal ModelLocation? TryGetInstalledLocation(TranscriptionModelInfo model)
    {
        var sanitizedName = ValidateModelName(model.Name);

        if (!model.IsBundle)
        {
            var path = Path.Combine(_modelsDirectory, $"{sanitizedName}.bin");
            return File.Exists(path) ? new ModelLocation(ModelLocationKind.SingleFile, path) : null;
        }

        var dir = Path.Combine(_modelsDirectory, sanitizedName);
        return IsBundleInstalled(model, dir) ? new ModelLocation(ModelLocationKind.Bundle, dir) : null;
    }

    /// <summary>
    /// The full installed predicate for a bundle: a manifest of a schema this build knows, matching
    /// the catalog's plan, with every payload file present at its recorded size.
    ///
    /// <para><b>Sizes, not hashes.</b> Per-file SHA-256 is the install-time corruption gate; re-hashing
    /// gigabytes on every Models-page refresh is not acceptable, and a size check catches the
    /// truncation and hand-deletion cases that actually occur (UAT 15.16 exists because users delete
    /// model files by hand).</para>
    ///
    /// <para><b>The final manifest re-read is the linearization point.</b> Delete removes the manifest
    /// FIRST, so a reader that checked payloads and then re-confirms the marker cannot report
    /// "installed" for a bundle whose deletion had already begun. The snapshot may go stale the
    /// instant after it is taken — that is unavoidable and honest; what it must never do is be wrong
    /// at the moment it is taken.</para>
    /// </summary>
    private bool IsBundleInstalled(TranscriptionModelInfo model, string bundleDir)
        => ClassifyBundle(model, bundleDir) == BundleState.Installed;

    /// <summary>
    /// What is at a bundle destination, and — separately — whether a caller may DELETE it.
    ///
    /// <para><b>Every exit is bucketed deliberately.</b> The predicate this replaced returned a bare
    /// <c>false</c> for six different reasons, and <c>DownloadBundleAsync</c> deletes a
    /// not-installed destination as debris. Three of those six were indeterminate — an unreadable
    /// manifest, an unstattable payload, an unverifiable containment check — so a transient sharing
    /// or ACL failure on a COMPLETE 670 MB install read as corruption and destroyed it.</para>
    ///
    /// <list type="table">
    /// <item><term>Absent</term><description>nothing there — installable</description></item>
    /// <item><term>Installed</term><description>manifest valid ∧ matches the catalog plan ∧ every
    /// payload present at its recorded size</description></item>
    /// <item><term>Invalid</term><description>definitely wrong and definitely known: malformed
    /// manifest, plan mismatch, missing payload, wrong size, a relative path the guard rejects, or a
    /// regular FILE where the directory belongs — <b>the only replaceable non-absent state</b></description></item>
    /// <item><term>Indeterminate</term><description>an I/O or access failure anywhere —
    /// <b>never replaceable</b>, and TRANSIENT</description></item>
    /// <item><term>NewerSchema</term><description>a manifest schema this build does not know, i.e.
    /// written by a NEWER VoiceWink — <b>never replaceable</b>, and DURABLE. Split out of
    /// Indeterminate: this table used to list the two together, which is how one message told the
    /// user to retry and then delete a perfectly good install</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <see cref="Indeterminate"/> and <see cref="NewerSchema"/> both refuse the install and both
    /// preserve what is on disk, but they are NOT the same answer to the user. Indeterminate is
    /// TRANSIENT — a lock, an ACL, an unresolvable path — so "close what's holding it and try
    /// again" is real advice. NewerSchema is DURABLE: a newer VoiceWink wrote that manifest, so
    /// retrying can never succeed, and the stock "delete the folder and re-download" tail would
    /// destroy the very install this classification exists to preserve. Both diff reviewers found
    /// them collapsed into one message with <c>isRetryable: true</c>.
    /// </remarks>
    private enum BundleState { Absent, Installed, Invalid, Indeterminate, NewerSchema }

    private BundleState ClassifyBundle(TranscriptionModelInfo model, string bundleDir)
    {
        switch (ProbeOccupant(bundleDir))
        {
            case OccupantKind.Absent: return BundleState.Absent;
            // A file where the bundle directory belongs is debris we can name — and clearing it is
            // what stops Directory.Move failing against it forever.
            case OccupantKind.RegularFile: return BundleState.Invalid;
            case OccupantKind.Unreadable: return BundleState.Indeterminate;
        }

        // Containment belongs HERE, not only at the call sites. This predicate opens a manifest and
        // stats payload files, so a junctioned bundle directory could otherwise answer "installed"
        // out of somebody else's files — and it also backs TryGetInstalledLocation and the
        // Models-page enumeration, neither of which has a gate of its own.
        //
        // Unverifiable is INDETERMINATE, not invalid: we could not read the real path, which is not
        // evidence that what is there is wrong.
        if (!Helpers.VerifiedFileAccess.IsDirectoryVerifiedUnder(bundleDir, _modelsDirectory))
            return BundleState.Indeterminate;

        var manifestPath = Path.Combine(bundleDir, Helpers.BundleRelativePathGuard.ManifestFileName);
        var read = ModelInstallManifest.Read(manifestPath);
        switch (read.Status)
        {
            case ModelInstallManifest.ManifestReadStatus.Valid:
                break;
            // Absent marker over a payload, or a malformed one: debris we can name.
            case ModelInstallManifest.ManifestReadStatus.Absent:
            case ModelInstallManifest.ManifestReadStatus.Invalid:
                return BundleState.Invalid;
            // A NEWER build wrote this. Not corruption, and not transient either — separated from
            // Indeterminate so the user gets advice that can actually work.
            case ModelInstallManifest.ManifestReadStatus.UnknownSchemaVersion:
                return BundleState.NewerSchema;
            // Unreadable: a lock, an ACL, a transient failure. Not evidence of corruption.
            default:
                return BundleState.Indeterminate;
        }

        if (!read.Manifest!.MatchesPlan(model.Name, model.Files!)) return BundleState.Invalid;

        foreach (var file in model.Files!)
        {
            string payload;
            try
            {
                payload = Helpers.BundleRelativePathGuard.ResolveWithin(bundleDir, file.RelativePath);
            }
            catch (ArgumentException)
            {
                return BundleState.Invalid;     // the catalog's own path is unusable — knowable
            }

            switch (ProbeOccupant(payload))
            {
                case OccupantKind.Absent: return BundleState.Invalid;
                case OccupantKind.Directory: return BundleState.Invalid;
                // FileInfo.Length would throw or lie here; an unstattable payload is exactly the
                // transient-lock case that must not authorize a delete.
                case OccupantKind.Unreadable: return BundleState.Indeterminate;
            }

            long length;
            try { length = new FileInfo(payload).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return BundleState.Indeterminate;
            }

            if (length != file.FileSizeBytes) return BundleState.Invalid;
        }

        // Re-read as the linearization point: delete removes the manifest FIRST, so a reader that
        // checked payloads and then re-confirms the marker cannot report "installed" for a bundle
        // whose deletion had already begun.
        var confirm = ModelInstallManifest.Read(manifestPath);
        if (confirm.Status == ModelInstallManifest.ManifestReadStatus.Valid)
            return confirm.Manifest!.MatchesPlan(model.Name, model.Files!)
                ? BundleState.Installed
                : BundleState.Invalid;

        // The SAME schema split as the first read above. This arm previously collapsed every
        // non-replaceable confirmation into Indeterminate, so an UnknownSchemaVersion observed HERE
        // — a newer VoiceWink writing the marker between the two reads — got the retryable message
        // and its "delete the model's folder yourself" tail, which is exactly the destruction the
        // NewerSchema split exists to prevent. Splitting it in one read and not the other is how a
        // fix looks complete and is not.
        if (confirm.Status == ModelInstallManifest.ManifestReadStatus.UnknownSchemaVersion)
            return BundleState.NewerSchema;

        return confirm.MayReplace ? BundleState.Invalid : BundleState.Indeterminate;
    }

    /// <summary>
    /// List all downloaded model files.
    ///
    /// <para>Names the guard would reject are filtered out rather than returned. A hand-placed file
    /// such as <c>CON.bin</c> or <c>a..b.bin</c> yields a stem that every other entry point on this
    /// class now refuses, so listing it would advertise a model that cannot be resolved or deleted
    /// — and this list feeds the App Mode model picker directly.</para>
    /// </summary>
    public IReadOnlyList<string> GetDownloadedModels()
    {
        if (!Directory.Exists(_modelsDirectory)) return [];

        var stems = Directory.GetFiles(_modelsDirectory, "*.bin")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();

        var usable = stems.Where(ModelNameGuard.IsValid).ToList();

        // A filtered file stays on disk taking space while being invisible to the UI and refused by
        // DeleteModel, so it must not vanish silently — this line is the only thing that would
        // explain it in a support bundle. Names are sanitized: they are untrusted filesystem input.
        //
        // Computed BEFORE bundles join the list: this compares FILE stems against FILE stems, and
        // adding directory-backed names first would make the arithmetic report a negative count.
        if (usable.Count != stems.Count)
        {
            Logger.Warning("Ignoring {Count} model file(s) whose name cannot be used safely: {Names}",
                stems.Count - usable.Count,
                string.Join(", ", stems.Except(usable, ModelNameGuard.NameComparer)
                    .Select(n => Helpers.UntrustedTextSanitizer.Sanitize(n, 40) ?? "(empty)")));
        }

        // Installed bundles. Catalog-driven, because a directory's contents cannot say which model
        // it is meant to be — and because the check must be the same full predicate the rest of the
        // class uses, not "a directory with that name exists". The staging root can never appear:
        // it is not a catalog name, and is reserved so it can never become one.
        var installedBundles = _catalog
            // The reserved staging name is excluded here too. Every OTHER descriptor-driven
            // operation refuses it through ValidateModelName, and a row named ".staging" could never
            // actually match — but leaving enumeration as the one path that does not share the rule
            // is how the exception gets forgotten, and this one would probe the staging root.
            .Where(m => m.IsBundle
                        && ModelNameGuard.IsValid(m.Name)
                        && !string.Equals(m.Name, Helpers.BundleRelativePathGuard.StagingRootName,
                                          StringComparison.OrdinalIgnoreCase))
            .Where(m => IsBundleInstalled(m, Path.Combine(_modelsDirectory, m.Name)))
            .Select(m => m.Name)
            .Where(n => !usable.Contains(n, ModelNameGuard.NameComparer));

        usable.AddRange(installedBundles);

        // Known AUXILIARY bundles (the config-conditional Parakeet flip's other-era bundle):
        // same full installed predicate, same reservations, appended only when genuinely
        // installed and not already present. This is a closed, descriptor-driven list — never a
        // scan — so the catalog-driven rule above ("a directory's contents cannot say which
        // model it is meant to be") is preserved: these directories ARE claimed, by descriptors
        // the coordinator installs and deletes by.
        var auxiliaryInstalled = _auxiliaryBundles
            .Where(m => m.IsBundle
                        && ModelNameGuard.IsValid(m.Name)
                        && !string.Equals(m.Name, Helpers.BundleRelativePathGuard.StagingRootName,
                                          StringComparison.OrdinalIgnoreCase))
            .Where(m => IsBundleInstalled(m, Path.Combine(_modelsDirectory, m.Name)))
            .Select(m => m.Name)
            .Where(n => !usable.Contains(n, ModelNameGuard.NameComparer));

        usable.AddRange(auxiliaryInstalled);

        return usable;
    }

    /// <summary>
    /// Delete an installed model — the single owner of deletion for BOTH shapes (TRN-1 step 2).
    ///
    /// <para><b>Serialized on the same per-model lock the installs use.</b> Without that, a delete
    /// and a same-name install race as two recursive deleters over one tree: the installer's debris
    /// sweep removes the delete's target, the delete maps absent to success, and the install then
    /// commits — the user clicks Delete and the model ends up installed.</para>
    ///
    /// <para><b>Marker-first for bundles.</b> The manifest is removed before the payload sweep, so no
    /// reader can observe "installed" while a delete is in progress. That is what makes
    /// <see cref="IsBundleInstalled"/>'s final manifest re-read a real linearization point.</para>
    ///
    /// <para><b>Absent is success.</b> The single-file path relied on <c>File.Delete</c> being a
    /// no-op on a missing file — that is what lets <c>DeleteModelCoreAsync</c> skip a second existence
    /// check and stay race-free (UAT 15.16). <c>Directory.Delete</c> throws instead, so that
    /// property is restored here rather than lost.</para>
    ///
    /// <para><b>Synchronous; since TRN-37 the delete command wraps this call in <c>Task.Run</c>, so
    /// the wait is a pool thread's, not the UI thread's.</b> The bound stays load-bearing: the
    /// coordinator's <c>TryDeleteBoth</c> path can still hold the per-model lock for bounded joins
    /// (~20 s worst), and a caller that can contend with a live multi-GB install needs an async
    /// delete, not a longer timeout.</para>
    /// </summary>
    internal ModelDeleteOutcome DeleteModel(TranscriptionModelInfo model)
    {
        var sanitizedName = ValidateModelName(model.Name);

        var modelLock = _perModelLocks.GetOrAdd(sanitizedName, _ => new SemaphoreSlim(1, 1));
        modelLock.Wait();
        try
        {
            if (!model.IsBundle)
            {
                var path = Path.Combine(_modelsDirectory, $"{sanitizedName}.bin");
                if (!File.Exists(path)) return ModelDeleteOutcome.NotPresent;

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning(ex, "Model delete failed: {Name}", sanitizedName);
                    return ModelDeleteOutcome.FailedBeforeInvalidation;
                }

                Logger.Information("Model deleted: {Name}", sanitizedName);
                return ModelDeleteOutcome.Deleted;
            }

            var dir = Path.Combine(_modelsDirectory, sanitizedName);

            // Absent is a success; UNREADABLE is not. Directory.Exists collapses the two into one
            // false, which would report "nothing there" for a directory we simply could not open —
            // the same ambiguity that made the manifest delete unsafe.
            switch (ProbeOccupant(dir))
            {
                case OccupantKind.Absent:
                    return ModelDeleteOutcome.NotPresent;
                case OccupantKind.Unreadable:
                    Logger.Warning("Model bundle delete refused: '{Name}' could not be classified (access or I/O failure).", sanitizedName);
                    return ModelDeleteOutcome.FailedBeforeInvalidation;
            }

            var manifestPath = Path.Combine(dir, Helpers.BundleRelativePathGuard.ManifestFileName);

            // Containment is verified BEFORE invalidation, not after: refusing a junctioned bundle
            // must leave it fully intact, and removing the marker first would already have made it
            // read as uninstalled.
            try
            {
                EnsureContainedIfPresent(dir, "the model bundle being deleted");
            }
            catch (IOException ex)
            {
                Logger.Warning(ex, "Refusing to delete a model bundle that does not resolve inside the models directory: {Name}", sanitizedName);
                return ModelDeleteOutcome.FailedBeforeInvalidation;
            }

            // Phase 1 — invalidate. A failure here leaves the model installed, so the row must keep
            // saying so.
            //
            // Deleted UNCONDITIONALLY: File.Exists returns false for an ACCESS failure just as it
            // does for absence, so guarding on it could skip invalidation, let the sweep below fail,
            // and report InvalidatedButCleanupFailed — clearing the row's flag while the manifest
            // was still on disk saying "installed". File.Delete is a no-op on a genuinely absent
            // file and throws on a real failure, which is exactly the distinction needed here.
            try
            {
                File.Delete(manifestPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Model bundle delete could not invalidate the manifest: {Name}", sanitizedName);
                return ModelDeleteOutcome.FailedBeforeInvalidation;
            }

            // Phase 2 — sweep. Past this point the model is NOT installed whatever happens, so a
            // failure must not report it as present. The debris is cleared by the next install.
            try
            {
                DeleteOccupantFailClosed(dir, "the model bundle being deleted");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex,
                    "Model bundle marker removed but payload cleanup failed — the model is no longer installed and the leftovers will be cleared on the next download: {Name}",
                    sanitizedName);
                return ModelDeleteOutcome.InvalidatedButCleanupFailed;
            }

            Logger.Information("Model bundle deleted: {Name}", sanitizedName);
            return ModelDeleteOutcome.Deleted;
        }
        finally
        {
            modelLock.Release();
        }
    }

    /// <summary>What one retired-model sweep did. Bytes rather than a bare count because the whole
    /// point of the sweep is space, and a retired <c>large-v3</c> alone is ~3.1 GB.</summary>
    internal readonly record struct RetiredSweepResult(
        int FilesDeleted, long BytesFreed, IReadOnlyList<string> SkippedNames);

    /// <summary>
    /// Delete the on-disk files of models the catalogue no longer ships.
    ///
    /// <para><b>Why this exists beside <see cref="DeleteModel"/>:</b> that one takes a
    /// <c>TranscriptionModelInfo</c>, and a retired name has no descriptor — that is what retired
    /// means. The names come from <c>LocalModelMigration.SupersededNames</c>, the same map that
    /// decides a persisted selection must be rewritten, so "this name is retired" has exactly one
    /// definition.</para>
    ///
    /// <para><b>The safety argument rests on a precondition proved elsewhere:</b>
    /// <c>LocalModelMigrationTests.NoLegacyNameSurvivesInTheCatalog</c> pins that no superseded name
    /// is also a shipped row. If that ever broke, this method would delete a live model. It is a
    /// closed allowlist for the same reason — "delete what the catalogue does not know" is one
    /// catalogue edit away from deleting a file the user put there, and
    /// <see cref="GetDownloadedModels"/> deliberately warns about and KEEPS such names.</para>
    ///
    /// <para><b>Single-file only, by construction.</b> Deletion goes through
    /// <c>File.Delete</c> — never <see cref="DeleteOccupantFailClosed"/>, whose directory arm does
    /// <c>Directory.Delete(recursive: true)</c>. That is deliberate and it is what makes the
    /// directory rule structural rather than probabilistic: an earlier design probed first and
    /// deleted second, so an occupant that turned into a directory between the two probes would
    /// have been recursively deleted — the exact action this sweep is not authorised to take. With
    /// <c>File.Delete</c> a directory throws instead, and lands in the per-name skip. Containment
    /// is still kernel-verified; only the directory arm is given up, and it was never wanted here.
    /// (Every retired name is a single-file whisper.cpp <c>.bin</c>; the one bundle in the
    /// catalogue, Parakeet, is not retired and never was.)</para>
    ///
    /// <para><b>Every per-name failure class is a skip, not an abort.</b> Including
    /// <see cref="ArgumentException"/> from <see cref="ValidateModelName"/> — only reachable if
    /// someone adds a guard-invalid entry to the map, but letting it escape would abandon the
    /// remaining names, and "one bad name does not strand the rest" is the invariant.</para>
    ///
    /// <para><b>A name still in the live catalogue is REFUSED at runtime, not merely absent from
    /// the caller's list.</b> Codex's diff review made the distinction: the disjointness test proves
    /// the shipped allowlist omits live models, which is a property of the ARGUMENT, while this
    /// method would happily delete any well-formed name handed to it. Since the argument is what a
    /// future caller controls, the check that matters belongs here — a shipped model must be
    /// undeletable by this path however it is called.</para>
    /// </summary>
    internal RetiredSweepResult DeleteRetiredModelFiles(
        IEnumerable<(string Name, string Sha256)> retired)
    {
        var deleted = 0;
        long freed = 0;
        var skipped = new List<string>();

        foreach (var (rawName, expectedSha256) in retired)
        {
            try
            {
                var name = ValidateModelName(rawName);

                // Fail closed: retired means "no longer shipped", so a name the catalogue still
                // carries is by definition not retired, and deleting it would take a working model
                // out from under the user.
                if (_catalog.Any(m => ModelNameGuard.NameComparer.Equals(m.Name, name)))
                {
                    skipped.Add(name);
                    Logger.Warning(
                        "Retired-model sweep refused a name the catalogue still ships: {Name}", name);
                    continue;
                }

                var modelLock = _perModelLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
                modelLock.Wait();
                try
                {
                    // The final model and the HTTP-Range partial it streams through are the same
                    // orphan class; an abandoned large-v3 download leaves gigabytes in the partial
                    // with no .bin beside it.
                    var final = Path.Combine(_modelsDirectory, $"{name}.bin");

                    // The FINAL file must prove it is ours before it is deleted; the PARTIAL does
                    // not, and cannot. A `.download` is a prefix of a transfer, so it has no stable
                    // hash — and nothing but this app's downloader ever creates that name, so the
                    // ownership question the hash answers does not arise for it.
                    if (TryDeleteVerifiedArtifact(final, name, expectedSha256, skipped, out var finalBytes))
                    {
                        deleted++;
                        freed += finalBytes;
                    }
                    if (TryDeletePartial(final + ".download", name, skipped, out var partialBytes))
                    {
                        deleted++;
                        freed += partialBytes;
                    }
                }
                finally
                {
                    modelLock.Release();
                }
            }
            catch (Exception ex)
            {
                // Name-level failure (a guard-invalid map entry). Sanitized: the name is the only
                // thing we can say, and it reaches a log that rides into support bundles.
                skipped.Add(Helpers.UntrustedTextSanitizer.Sanitize(rawName, 40) ?? "(empty)");
                Logger.Warning("Retired-model sweep skipped a name it could not use: {ErrorType}", ex.GetType().Name);
            }
        }

        // Distinct: a name whose .bin AND .bin.download both fail (both locked, say) would
        // otherwise be listed twice, and the launch warning would read "Could not remove 2 retired
        // model file(s): x, x" — two files, but one name, reported as if it were two of each.
        return new RetiredSweepResult(
            deleted, freed, skipped.Distinct(ModelNameGuard.NameComparer).ToList());
    }

    /// <summary>
    /// Delete a retired model's FINAL file — only after proving the bytes are the artifact
    /// VoiceWink distributed under that name.
    ///
    /// <para><b>The digest is REQUIRED, not optional.</b> An earlier version took a nullable one and
    /// treated null as "skip hashing", which made unverified deletion of a final file reachable by
    /// any future caller that forgot to pass it — contradicting the whole point (Codex, round 5).
    /// Partials go through <see cref="TryDeletePartial"/> instead, so the two contracts cannot be
    /// confused for one another.</para>
    ///
    /// <para><b>Verification and deletion share ONE open handle.</b> Hashing through a handle that
    /// is then closed before deleting by path leaves a window in which the file can be replaced,
    /// and the per-model semaphore is process-LOCAL so it does not close it (Codex, round 5). The
    /// handle is opened with <c>FileShare.Delete</c> — which permits the delete while it is held —
    /// and kept open across both.</para>
    ///
    /// <para><b>Scope of that guarantee, stated precisely because a neighbouring comment was
    /// corrected twice for less:</b> the handle does not share WRITE, so the verified bytes cannot
    /// be modified underneath it — but <c>FileShare.Delete</c> also permits RENAME, so a process
    /// with write access to the models directory could move the verified file aside and plant a
    /// replacement at the path. That is not a threat worth closing here: the same access deletes
    /// these files outright (Kimi, final review).</para>
    ///
    /// <para><b>Recorded product decision: canonical bytes under the models directory are treated as
    /// the app's.</b> A user's own Hugging Face download of <c>ggml-small.bin</c> is byte-identical
    /// to what VoiceWink shipped, so it hashes as a match and IS deleted. That is intended — it is
    /// the same retired model the owner asked to clean up, re-downloadable from the same place, and
    /// the app has never been able to load it under a retired name. What the digest protects is
    /// DIFFERENT bytes at the same name: a fine-tune, or anything else the user put there.</para>
    /// </summary>
    private bool TryDeleteVerifiedArtifact(
        string path, string name, string expectedSha256, List<string> skipped, out long bytes)
    {
        bytes = 0;
        if (!PassesPreDeleteChecks(path, name, skipped)) return false;

        try
        {
            // FileShare.Delete lets the delete below proceed while this handle is open; holding it
            // is what binds "these bytes hashed correctly" to "these bytes were removed".
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 1024 * 1024, FileOptions.SequentialScan);

            using (var sha256 = global::System.Security.Cryptography.SHA256.Create())
            {
                var actual = Convert.ToHexString(sha256.ComputeHash(stream));
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(name);
                    // INFORMATION, not Warning: this is the design working. The file is somebody
                    // else's and keeping it is the correct outcome — but it is also a PERMANENT
                    // state, so a warning would fire on every launch forever for a decision the
                    // app made on purpose (Kimi, final review). Warnings stay for the states that
                    // might resolve: a lock, an ACL, an unreadable path.
                    Logger.Information(
                        "Retired-model sweep kept a file whose contents are not the artifact VoiceWink shipped: {Name}",
                        name);
                    return false;
                }
            }

            var length = stream.Length;
            File.Delete(path);
            bytes = length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(name);
            return false;
        }
    }

    /// <summary>
    /// Delete a retired model's <c>.download</c> PARTIAL.
    ///
    /// <para>No identity check, and none is possible: a partial is a prefix of a transfer, so it has
    /// no stable digest. It needs none — nothing but this app's downloader ever creates that name,
    /// so the ownership question the artifact hash answers does not arise.</para>
    /// </summary>
    private bool TryDeletePartial(string path, string name, List<string> skipped, out long bytes)
    {
        bytes = 0;
        if (!PassesPreDeleteChecks(path, name, skipped)) return false;

        try
        {
            var length = new FileInfo(path).Length;
            File.Delete(path);
            bytes = length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(name);
            return false;
        }
    }

    /// <summary>Occupant classification + containment, shared by both delete paths. False means
    /// "leave it alone"; genuine absence is silent, everything else records a skip.</summary>
    private bool PassesPreDeleteChecks(string path, string name, List<string> skipped)
    {
        switch (ProbeOccupant(path))
        {
            case OccupantKind.Absent:
                // The steady state on every launch after the first. Silent by design.
                return false;

            case OccupantKind.Directory:
                // Not a model file, and deliberately never recursively removed — the shared
                // DeleteOccupantFailClosed primitive would do exactly that.
                skipped.Add(name);
                return false;

            case OccupantKind.Unreadable:
                // Fail-closed. An access failure is not absence, and reporting "gone" for a path we
                // could not even classify is how a delete comes back done having done nothing.
                skipped.Add(name);
                return false;
        }

        // Kernel-verified containment, kept for consistency with DeleteOccupantFailClosed — but
        // claimed for no more than it does. Two review rounds each corrected a different
        // overstatement here: File.Delete removes a file-shaped reparse point rather than following
        // it and refuses a directory-shaped one, so this is not what stops an escape (Kimi); and
        // IsVerifiedUnder resolves the ROOT as well, so a redirected models directory is accepted,
        // not caught (Codex). What it buys is defense in depth and one shared rule across both
        // delete paths.
        if (!Helpers.VerifiedFileAccess.IsVerifiedUnder(path, _modelsDirectory))
        {
            skipped.Add(name);
            Logger.Warning("Retired-model sweep refused a path that does not resolve inside the models directory: {Name}", name);
            return false;
        }

        return true;
    }

}
