namespace VoiceWink.Helpers;

/// <summary>What a disk sweep should do to one model row's <c>IsDownloaded</c> flag.</summary>
internal enum ModelDiskAction
{
    /// <summary>Flag already matches disk, or the row is mid-download and must not be touched.</summary>
    None,
    /// <summary>The file appeared on disk (e.g. copied in manually) — flag it present.</summary>
    MarkDownloaded,
    /// <summary>The file is gone from disk — flag it missing.</summary>
    MarkMissing,
}

/// <summary>Outcome of the select-model choreography (test-visible; the command itself is void).</summary>
internal enum SelectModelResult
{
    /// <summary>Never downloaded — nothing to select.</summary>
    Ignored,
    /// <summary>
    /// The row claimed the file was present but it is gone: the flag is corrected and NOTHING is
    /// published — this is the stale-click refusal that UAT 15.16 asked for.
    /// </summary>
    SelfHealedMissing,
    /// <summary>
    /// The selection was published (settings + rows) but the model then failed to load. Logged, not
    /// reverted — see <c>SelectModelCoreAsync</c>'s remarks on why nothing is written after the load.
    /// </summary>
    LoadFailed,
    /// <summary>Published (settings + rows), then loaded successfully — the fully happy path.</summary>
    Selected,
}

/// <summary>Outcome of the delete-model choreography.</summary>
internal enum DeleteModelResult
{
    /// <summary>Not downloaded, or it is the active model (which can't be deleted).</summary>
    Ignored,
    /// <summary>File was already gone: the row's flag was corrected.</summary>
    SelfHealedMissing,
    /// <summary>File deleted and the flag cleared.</summary>
    Deleted,
    /// <summary>Delete threw (locked/ACL): the flag stays TRUE because the file may still exist.</summary>
    DeleteFailed,
    /// <summary>
    /// TRN-1 step 2, bundles only: the completion manifest was removed but the payload sweep failed.
    /// The model is NOT installed — the flag is cleared — and the leftovers are deleted by the next
    /// download, whose first act is to clear any non-installed destination.
    ///
    /// <para>Distinct from <see cref="DeleteFailed"/> precisely because the flag must move the other
    /// way. Collapsing them would strand a row claiming a deleted model is still present.</para>
    /// </summary>
    InvalidatedButCleanupFailed,
}

/// <summary>Whether a per-model command may proceed, must self-heal stale state first, or is a no-op.</summary>
internal enum ModelCommandGate
{
    /// <summary>Preconditions hold — run the command.</summary>
    Proceed,
    /// <summary>The row claims the file is present but it is gone: correct the flag, do NOT act.</summary>
    SelfHealMissing,
    /// <summary>Legitimately nothing to do (never downloaded, or the delete target is selected).</summary>
    Ignore,
}

/// <summary>
/// Pure decisions for keeping the Models page honest about what is actually on disk (UAT 15.16,
/// 2026-07-25: after deleting model files by hand, the page still showed them as present, selecting
/// them still "worked", and the delete button silently did nothing).
/// <para>Root cause: <c>ModelManagementViewModel</c> is a DI SINGLETON whose <c>LoadModels()</c> runs
/// only in its constructor, so the on-disk set was read once per process and never again.</para>
/// <para>These decisions live here, filesystem-free, because they were originally untestable through
/// the ViewModel: <c>ModelDownloadManager</c> is sealed and its constructor resolved the user's REAL
/// Models directory via <c>AppPaths.EnsureModels()</c> (Codex plan review round 2). That constraint
/// is GONE — the manager now REQUIRES a models root and every test supplies an isolated one —
/// but keeping the decisions pure is still worth it: they are exercised directly, without a
/// filesystem, and the reasons each rule exists stay attached to the rule. Name comparison is
/// ordinal-ignore-case, because Windows filenames are.</para>
/// </summary>
internal static class ModelDiskReconciliation
{
    /// <summary>Comparer for on-disk model-name sets (Windows paths are case-insensitive).
    /// Forwards to <see cref="Services.Transcription.ModelNameGuard.NameComparer"/> so "these two
    /// names are the same model" has exactly one definition — two independently-declared
    /// OrdinalIgnoreCase comparers agree today and diverge silently the day one is edited.
    /// <para>This does point a Helpers type at a Services one, against the usual direction. Taken
    /// deliberately: the question is name semantics, which is the guard's subject, and the
    /// alternative put the canonical answer in a disk-reconciliation helper that the download
    /// manager's lock would then have to reach into. One line, no behavioural coupling.</para>
    /// </summary>
    internal static StringComparer NameComparer => Services.Transcription.ModelNameGuard.NameComparer;

    /// <summary>
    /// Whether two model names denote the same model. Use at every identity boundary — above all
    /// the DESTRUCTIVE ones.
    ///
    /// <para>Exists because <c>==</c> was being used to answer this. Since the local-runtime seam
    /// resolves names case-insensitively, a settings value of <c>GGML-SMALL</c> loads and
    /// transcribes fine while the Models page compares it to the row's <c>ggml-small</c>, concludes
    /// the row is not the active model, and offers to delete the file the app is currently
    /// using.</para>
    /// </summary>
    /// <summary>Same MODEL, not same string: case-insensitive, and since the TRN-29 flip the two
    /// Parakeet bundle spellings compare equal (either maps onto the active catalog row via
    /// <see cref="Models.ParakeetCatalog.CanonicalName"/>) — a persisted selection, App Mode
    /// override, or captured retry name carries whichever spelling was current when written, and
    /// every consumer of THIS predicate asks an identity question (is this row the active model /
    /// the override / the preselect target). Disk-name PROJECTION deliberately does not go
    /// through here — it uses <see cref="NameComparer"/> directly (`InstalledCatalogModels`, the
    /// manager), where mapping the sherpa DIRECTORY onto the GGUF row would claim installed off
    /// the wrong bytes.</summary>
    internal static bool IsSameModel(string? a, string? b) =>
        NameComparer.Equals(Models.ParakeetCatalog.CanonicalName(a), Models.ParakeetCatalog.CanonicalName(b));

    /// <summary>
    /// Reconcile one row against the on-disk set. A row that is mid-download is ALWAYS left alone —
    /// the live download owns its flags, and a sweep landing mid-transfer must not report it missing.
    /// </summary>
    internal static ModelDiskAction Decide(bool isDownloaded, bool isDownloading, bool onDisk)
    {
        if (isDownloading) return ModelDiskAction.None;
        if (onDisk && !isDownloaded) return ModelDiskAction.MarkDownloaded;
        if (!onDisk && isDownloaded) return ModelDiskAction.MarkMissing;
        return ModelDiskAction.None;
    }

    /// <summary>
    /// Gate for "select this local model". Checked BEFORE any persistence: the pre-fix command
    /// persisted the selection and only then discovered the path was null, leaving settings pointing
    /// at a model that isn't there. A stale row self-heals instead, so the card offers Download.
    /// </summary>
    internal static ModelCommandGate GateSelect(bool isDownloaded, bool fileExists)
    {
        if (!isDownloaded) return ModelCommandGate.Ignore;
        return fileExists ? ModelCommandGate.Proceed : ModelCommandGate.SelfHealMissing;
    }

    /// <summary>
    /// Gate for "delete this local model". <paramref name="isSelected"/> keeps the existing rule that
    /// the active model can't be deleted. An already-missing file returns
    /// <see cref="ModelCommandGate.SelfHealMissing"/> rather than the old silent return — that silence
    /// IS the owner's "the delete button does nothing".
    /// </summary>
    internal static ModelCommandGate GateDelete(bool isDownloaded, bool isSelected, bool fileExists)
    {
        if (!isDownloaded || isSelected) return ModelCommandGate.Ignore;
        return fileExists ? ModelCommandGate.Proceed : ModelCommandGate.SelfHealMissing;
    }
}
