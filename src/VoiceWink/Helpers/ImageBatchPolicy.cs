namespace VoiceWink.Helpers;

/// <summary>
/// Pure decisions for multi-version image generation (IMG-3): count bounds, the
/// per-item progress/terminal copy, and which terminal work a finished batch run
/// selects. App-authored strings only — provider error text stays with the
/// SEC-1/PRM-6 pill surfaces in <c>MainViewModel</c> (<c>ComposeBatchImageFailureStatus</c>).
/// Every string here fits the 55-code-unit app-copy pill budget at max values.
/// </summary>
internal static class ImageBatchPolicy
{
    /// <summary>UI ceiling for the Versions picker — a cost guard, not a provider limit
    /// (every version is a separately billed BYOK call).</summary>
    internal const int MaxVersions = 4;

    /// <summary>Clamp a requested count into [1, <see cref="MaxVersions"/>] — corrupt or
    /// out-of-range input degrades to a single image, never to zero or a runaway batch.</summary>
    internal static int ClampCount(int requested)
        => requested < 1 ? 1 : requested > MaxVersions ? MaxVersions : requested;

    /// <summary>The Versions picker's pre-select: the redo chain's count when present
    /// (dialog-authoritative on confirm, like References), else 1. A FRESH dialog always
    /// starts at 1 — the count is a per-run spend decision, never persisted on the
    /// PROMPT or in settings (owner decision 4), so it can never become a sticky default.
    /// Since 2026-07-29 it IS recorded on the history ROW
    /// (<c>TranscriptionRecord.ImageVersionCount</c>) — that is not the same thing: the
    /// row states what produced it, exactly like the aspect/size/quality it already
    /// stores, which is what lets History's Redo repeat the request faithfully. Rows
    /// predating that column read null and seed 1.</summary>
    internal static int SeedCount(int? previousImageCount)
        => ClampCount(previousImageCount ?? 1);

    /// <summary>Batch launch progress (IMG-4 parallel: all versions generate at once, so
    /// the launch announces the whole batch). Null at a single-image run so N=1 renders
    /// exactly today's pill (no new text surface).</summary>
    internal static string? BatchStartText(int total)
        => total <= 1 ? null : $"Generating {total} images…";

    /// <summary>Generation-settled progress (IMG-4): counts PROVIDER SUCCESSES as they
    /// land, not committed rows — a settled version's commit follows within the same
    /// pump turn (IMG-4b incremental commits), but a persist-side stop can leave later
    /// settlements uncommitted, so settled generations remain the honest live signal.
    /// Persist-side terminal copy deliberately says "saved"
    /// (<see cref="HistoryOffStatus"/>/<see cref="SaveFailedStatus"/>)
    /// so the two vocabularies cannot contradict. Null at N=1.</summary>
    internal static string? BatchProgressText(int settled, int total)
        => total <= 1 ? null : $"{settled} of {total} images generated…";

    /// <summary>Only a single-image run pastes; a batch is a comparison workflow whose
    /// output surface is History (owner decision 2 — pasting an arbitrary version would
    /// contradict choosing afterwards).</summary>
    internal static bool ShouldPaste(int total) => total == 1;

    /// <summary>Batch success copy. Null at N=1 — the caller keeps today's paste-derived
    /// presentation ("Done"/"Image on clipboard"/decline).</summary>
    internal static string? SuccessStatus(int total)
        => total <= 1 ? null : $"{total} images generated";

    /// <summary>Cancel copy: zero completions keep today's exact text; a partial batch
    /// names what survived (those versions are committed and stay in History).</summary>
    internal static string CancelStatus(int completed, int total)
        => completed <= 0
            ? "Image generation cancelled"
            : $"Cancelled — {completed} of {total} images generated";

    /// <summary>Honest stop when History turned off mid-batch: the user's privacy
    /// instruction is honored (remaining items have no output destination), never
    /// overridden by a captured earlier value. Says "saved", not "generated" (IMG-4):
    /// progress counts provider successes, this counts committed rows — with parallel
    /// generation "4 of 4 images generated…" can honestly precede "History off — 0 of
    /// 4 saved".</summary>
    internal static string HistoryOffStatus(int completed, int total)
        => $"History off — {completed} of {total} saved";

    /// <summary>Stop for a provably-failed local save (PNG or row) — the provider
    /// delivered, the disk/DB did not. "saved" for the same IMG-4 vocabulary split as
    /// <see cref="HistoryOffStatus"/>.</summary>
    internal static string SaveFailedStatus(int completed, int total)
        => $"Couldn't save image — {completed} of {total} saved";

    /// <summary>Stop when the row-commit VERDICT is unknown (probe query failed):
    /// deliberately distinct from <see cref="SaveFailedStatus"/> — the image may well be
    /// in History, and the count reports only CONFIRMED commits. Warning tone at the
    /// call site (uncertainty, not a confirmed failure).</summary>
    internal static string AmbiguousSaveStatus(int completed, int total)
        => $"Couldn't verify save — {completed} of {total} confirmed";

    /// <summary>
    /// The terminal work a finished batch selects. Side-effect selection ONLY — the
    /// complete presentation (status/tone/ids/redo context) is composed AFTER those
    /// side-effects ran (<c>MainViewModel.ComposeBatchCompletion</c>), so the marker
    /// row's id can feed the composition without circularity.
    /// </summary>
    internal enum BatchTerminalAction
    {
        /// <summary>N=1 success: today's paste tail, then present its derived status.</summary>
        PasteThenPresent,

        /// <summary>N&gt;1 success: no paste — present the batch success copy.</summary>
        PresentBatchSuccess,

        /// <summary>N=1 provider failure: write the failed-marker row (its own commit
        /// admission handles a quit race), then present. IMG-4c scoped this to N=1 —
        /// a batch writes each failed version's row INLINE in the runner, so a terminal
        /// marker step there would add a duplicate row for the first failure.</summary>
        PersistFailedMarkerThenPresent,

        /// <summary>N&gt;1 provider failure (IMG-4c): every failed version's row was
        /// already written inline by the runner — present directly from the runner's
        /// first-failure metadata, with no terminal marker step.</summary>
        PresentBatchFailure,

        /// <summary>Partial stop (persist failure / ambiguity / History off / user cancel
        /// with completions): present with the last committed state — no marker row (the
        /// local writer is the failing component, History is off, or nothing failed).</summary>
        PresentPartialStop,

        /// <summary>Zero-completion cancel: today's bare cancel presenter (its
        /// user/quit-drain matrix is unchanged).</summary>
        BareCancelPresenter,

        /// <summary>Nothing presents: quit drain owns the surface (shutdown refusals and
        /// cancels during shutdown end hidden, exactly like today's commit-refused path).</summary>
        Silent,
    }

    /// <summary>
    /// Map a batch result onto its terminal action. Unknown/future stop reasons fall to
    /// <see cref="BatchTerminalAction.Silent"/> — an unmapped value must never reach a
    /// paste or a presentation (the MiniRecorderStopRouting default-arm rule).
    /// </summary>
    internal static BatchTerminalAction DecideBatchTerminal(
        ImageBatchRunner.BatchStop reason, int completed, int total, bool shutdownRequested)
        => reason switch
        {
            ImageBatchRunner.BatchStop.Completed => total == 1
                ? BatchTerminalAction.PasteThenPresent
                : BatchTerminalAction.PresentBatchSuccess,
            // IMG-4c: only N=1 still writes its marker at the terminal step; a batch's
            // failure rows were committed inline, one per failed version. The batch arm
            // must ALSO go Silent under the latch — N=1 gets that for free from the
            // marker step's own commit admission, but with no terminal step there is
            // nothing else to stop a pill presenting mid-quit.
            ImageBatchRunner.BatchStop.ProviderFailure => total == 1
                ? BatchTerminalAction.PersistFailedMarkerThenPresent
                : shutdownRequested ? BatchTerminalAction.Silent : BatchTerminalAction.PresentBatchFailure,
            // IMG-4: persist-side stops go SILENT under the shutdown latch — quit can
            // latch between a successful commit admission and a failed persist result,
            // and a pill must never present mid-quit (the quit drain owns the surface,
            // mirroring the Cancelled+shutdown arm below).
            ImageBatchRunner.BatchStop.PersistFailure => shutdownRequested
                ? BatchTerminalAction.Silent : BatchTerminalAction.PresentPartialStop,
            ImageBatchRunner.BatchStop.RowAmbiguous => shutdownRequested
                ? BatchTerminalAction.Silent : BatchTerminalAction.PresentPartialStop,
            ImageBatchRunner.BatchStop.HistoryDisabled => shutdownRequested
                ? BatchTerminalAction.Silent : BatchTerminalAction.PresentPartialStop,
            ImageBatchRunner.BatchStop.Cancelled => completed <= 0
                ? BatchTerminalAction.BareCancelPresenter
                : shutdownRequested ? BatchTerminalAction.Silent : BatchTerminalAction.PresentPartialStop,
            ImageBatchRunner.BatchStop.ShutdownRefused => BatchTerminalAction.Silent,
            _ => BatchTerminalAction.Silent,
        };
}
