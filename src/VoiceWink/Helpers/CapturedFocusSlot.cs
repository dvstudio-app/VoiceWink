namespace VoiceWink.Helpers;

/// <summary>Owns the currently borrowed UIA focus element so cancel paths release only their own capture.</summary>
internal enum CapturedFocusOwner { None, Recording, Redo }

/// <summary>
/// Single owner of the UIA focus element captured at recording-start (or at
/// redo-time) and later borrowed by the paste path. Extracted from
/// <c>MainViewModel</c> fields so the pending-capture ownership rules are unit
/// testable without real COM — all bridge operations are constructor-injected.
///
/// <para><b>Why a pending Task at all:</b> the recording-start capture used to
/// run synchronously on the UI thread (10–500 ms bounded wait) BEFORE the
/// MiniRecorder pill became visible — the single largest hotkey→pill latency
/// contributor. <see cref="BeginForRecording"/> now enqueues the capture on the
/// UIA bridge's MTA worker immediately (so capture timing is unchanged) and the
/// UI thread moves on; <see cref="ResolveAsync"/> materializes the element at
/// paste time, seconds later, when the task is complete in all but
/// wedged-target scenarios.</para>
///
/// <para><b>Threading:</b> all members must be called on the UI thread — the
/// same single-threaded discipline the MainViewModel fields had. The awaited
/// section of <see cref="ResolveAsync"/> resumes on the captured context, and
/// every mutation is re-guarded by task-identity + owner checks so a
/// cancel/supersede that ran during the await can never be clobbered.</para>
///
/// <para><b>Ownership invariant:</b> after materialization, <see cref="Owner"/>
/// reflects capture SUCCESS (a null element demotes Recording ownership to
/// None), matching the pre-slot behavior that owner-sensitive readers such as
/// the redo capture rely on. While a capture is merely pending, Recording
/// ownership is provisional.</para>
/// </summary>
internal sealed class CapturedFocusSlot
{
    /// <summary>
    /// Upper bound on how long <see cref="ResolveAsync"/> waits for a pending
    /// capture. The async Begin path has NO bridge-side timeout (corrected
    /// 2026-07-30, PST-10 review — this doc previously claimed "the bridge call
    /// self-bounds at 500 ms", which is true only of the SYNC capture wrapper):
    /// the worker item completes whenever the target answers, and a wedged
    /// target measured ~17 s on WhatsApp (PST-8). This 1 s bound is therefore
    /// the ONLY cap the paste path pays for a pending capture; give-ups route
    /// the late RCW through EnqueueReleaseWhenComplete. It replaces a wait that
    /// previously happened on the recording-start UI thread ahead of the pill.
    /// </summary>
    internal static readonly TimeSpan PendingCaptureResolveTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<Task<UiaFocusBridge.IUIAutomationElement?>?> _beginCapture;
    private readonly Func<UiaFocusBridge.IUIAutomationElement?> _syncCapture;
    private readonly Action<UiaFocusBridge.IUIAutomationElement> _release;
    private readonly Action<Task<UiaFocusBridge.IUIAutomationElement?>> _releaseWhenComplete;

    private UiaFocusBridge.IUIAutomationElement? _materialized;
    private Task<UiaFocusBridge.IUIAutomationElement?>? _pendingTask;

    public CapturedFocusOwner Owner { get; private set; } = CapturedFocusOwner.None;

    /// <summary>
    /// The materialized element, for read sites that provably run after
    /// <see cref="ResolveAsync"/> (the paste paths resolve first). Null while a
    /// capture is still pending.
    /// </summary>
    public UiaFocusBridge.IUIAutomationElement? Materialized => _materialized;

    /// <summary>
    /// OBSERVATION-ONLY view of the pending recording capture (PILL-2: the
    /// editable-target indicator awaits it to probe the captured element's
    /// shape). Null when no capture is pending or the pending capture is not
    /// Recording-owned. Observers must NEVER release/route the task's element —
    /// ownership stays with this slot (materialize-at-paste or the release
    /// routing) — and must gate any use of the result behind their own
    /// staleness fences: a Release()/supersede after this read routes the RCW
    /// to a fire-and-forget release, and a subsequent probe of it merely fails
    /// (caught COM exception → null shape), never crashes.
    /// </summary>
    public Task<UiaFocusBridge.IUIAutomationElement?>? PendingRecordingCapture
        => Owner == CapturedFocusOwner.Recording ? _pendingTask : null;

    public CapturedFocusSlot()
        : this(
            UiaFocusBridge.BeginCaptureFocusedElement,
            UiaFocusBridge.CaptureFocusedElement,
            UiaFocusBridge.EnqueueRelease,
            UiaFocusBridge.EnqueueReleaseWhenComplete)
    {
    }

    internal CapturedFocusSlot(
        Func<Task<UiaFocusBridge.IUIAutomationElement?>?> beginCapture,
        Func<UiaFocusBridge.IUIAutomationElement?> syncCapture,
        Action<UiaFocusBridge.IUIAutomationElement> release,
        Action<Task<UiaFocusBridge.IUIAutomationElement?>> releaseWhenComplete)
    {
        _beginCapture = beginCapture;
        _syncCapture = syncCapture;
        _release = release;
        _releaseWhenComplete = releaseWhenComplete;
    }

    /// <summary>
    /// Release any current capture and start a new pending one for a recording
    /// session. Never blocks — the capture executes on the bridge's MTA worker.
    /// </summary>
    public void BeginForRecording()
    {
        Release();
        _pendingTask = _beginCapture();
        Owner = _pendingTask != null ? CapturedFocusOwner.Recording : CapturedFocusOwner.None;
    }

    /// <summary>
    /// Synchronous capture for the redo path (button click — not latency
    /// sensitive). Keeps a live Recording capture instead of replacing it,
    /// matching the pre-slot redo semantics.
    /// </summary>
    public void CaptureForRedo()
    {
        if (Owner == CapturedFocusOwner.Recording)
            return;

        Release();
        _materialized = _syncCapture();
        Owner = _materialized != null ? CapturedFocusOwner.Redo : CapturedFocusOwner.None;
    }

    /// <summary>
    /// Retag a REDO-owned capture as Recording-owned (TRN-17). Pure bookkeeping: no capture, no
    /// release, no COM call — only <see cref="Owner"/> moves.
    ///
    /// <para><b>Why the retry picker needs it.</b> The paste target and the focus element must be
    /// captured BEFORE the retry dialog opens, because showing it calls <c>RestoreMainWindow()</c>
    /// and <c>CaptureFreshPasteTarget</c> is <c>GetForegroundWindow()</c> — capture after the dialog
    /// and the retry pastes into VoiceWink. But the capture then has to survive a user CANCEL, which
    /// wants an owner-scoped <c>Release(Redo)</c> that no-ops if a real recording has since taken
    /// the slot; Recording ownership has no such release (a cancel would destroy the live
    /// recording's capture). So it is captured as Redo and adopted here on CONFIRM, where the run
    /// that consumes it is Recording-owned and its <c>finally</c> does the owner-scoped release.</para>
    ///
    /// <para>No-op unless the slot is Redo-owned: a Recording capture belongs to a live recording
    /// and an empty slot has nothing to retag, and in both cases the retry's own element is already
    /// gone or was never taken.</para>
    /// </summary>
    public void AdoptForRecording()
    {
        if (Owner == CapturedFocusOwner.Redo)
            Owner = CapturedFocusOwner.Recording;
    }

    /// <summary>
    /// PST-13: replace the Recording capture with a freshly re-captured element,
    /// because the original one hit a cold Chromium accessibility tree (see
    /// <see cref="ColdCapturePolicy"/>). Returns true when the swap happened.
    ///
    /// <para><b>It adopts a COMPLETED pending capture, exactly as
    /// <see cref="ResolveAsync"/> would.</b> This is the whole reason the method
    /// exists in this shape. The upgrade runs DURING recording, and at that
    /// point nothing has adopted the capture yet — <c>ResolveAsync</c> runs at
    /// paste time and the editability probe only OBSERVES
    /// <see cref="PendingRecordingCapture"/>. So the slot the caller finds is
    /// invariably <c>Owner=Recording</c>, pending task present and completed,
    /// <c>_materialized == null</c>. A predicate that required
    /// <c>_pendingTask == null</c> would refuse every single time and make the
    /// whole feature an inert no-op (Kimi plan round, blocker B1). "Not landed"
    /// means <see cref="Task.IsCompleted"/> false — not "not yet adopted".</para>
    ///
    /// <para><b>Why the caller must not await <c>ResolveAsync</c> first.</b> On
    /// a slow-but-healthy capture — a busy Chromium target, which is precisely
    /// this feature's case — that would pay the 1 s
    /// <see cref="PendingCaptureResolveTimeout"/> mid-recording, route the still
    /// live task to <c>releaseWhenComplete</c> and demote the owner, destroying a
    /// capture the paste path would otherwise have received. The adoption has to
    /// be slot-side and completion-gated.</para>
    ///
    /// <para><b>A refusal never releases <paramref name="candidate"/>.</b>
    /// Ownership of it stays with the caller on every path, accepted or refused;
    /// the caller routes a refused candidate to
    /// <see cref="UiaFocusBridge.EnqueueRelease"/>. A caller that assumes the
    /// slot cleans up after a refusal leaks an RCW.</para>
    ///
    /// <para><b>Threading:</b> UI thread, like every other member. The caller
    /// must additionally evaluate its own staleness gate and this call in ONE
    /// uninterrupted UI-thread turn — that is what makes the swap safe against
    /// the paste path, which borrows the element on the UI thread once the
    /// pipeline has left <c>Recording</c>.</para>
    /// </summary>
    public bool TryUpgradeRecordingCapture(UiaFocusBridge.IUIAutomationElement candidate)
    {
        if (Owner != CapturedFocusOwner.Recording)
            return false;

        if (_pendingTask is { } pending)
        {
            if (!pending.IsCompleted)
                return false; // still in flight — a later attempt may retry

            _pendingTask = null;
            _materialized = pending.Status == TaskStatus.RanToCompletion ? pending.Result : null;
            if (_materialized == null)
            {
                // Mirrors ResolveAsync's demotion: a capture that produced no
                // element leaves the slot unowned. Nothing to release — a
                // faulted/cancelled task never produced an RCW, and a
                // RanToCompletion null result is null.
                Owner = CapturedFocusOwner.None;
                return false;
            }
        }

        if (_materialized == null)
            return false;

        _release(_materialized);
        _materialized = candidate;
        return true;
    }

    /// <summary>
    /// Materialize the pending capture (idempotent). Returns the element the
    /// current owner holds — null when capture failed, timed out, or was
    /// released/superseded while awaiting.
    /// </summary>
    public async Task<UiaFocusBridge.IUIAutomationElement?> ResolveAsync()
    {
        var task = _pendingTask;
        if (task == null)
            return _materialized;

        var winner = await Task.WhenAny(task, Task.Delay(PendingCaptureResolveTimeout));
        if (winner != task)
        {
            // Timed out (wedged worker / pool). Route the eventual RCW to a
            // fire-and-forget release — but only if we still own the routing;
            // a Release()/supersede during the await already routed it.
            if (ReferenceEquals(_pendingTask, task))
            {
                _pendingTask = null;
                _releaseWhenComplete(task);
                if (Owner == CapturedFocusOwner.Recording)
                    Owner = CapturedFocusOwner.None;
            }
            return _materialized;
        }

        if (!ReferenceEquals(_pendingTask, task) || Owner != CapturedFocusOwner.Recording)
        {
            // Two ways here: (a) released/superseded while awaiting — whoever
            // detached the task (Release, BeginForRecording, CaptureForRedo)
            // already routed its element via releaseWhenComplete, so do NOT
            // release again; (b) a concurrent ResolveAsync already adopted
            // this same task (pending cleared, owner still Recording) — the
            // adopted element is exactly what _materialized now holds.
            return _materialized;
        }

        _pendingTask = null;
        _materialized = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
        if (_materialized == null)
            Owner = CapturedFocusOwner.None;
        return _materialized;
    }

    /// <summary>
    /// Resolve and DETACH the current capture for a background-job handoff (IMG-BG,
    /// 2026-07-16): awaits the pending capture under the <see cref="ResolveAsync"/> bound,
    /// then empties the slot (owner None, nothing pending or materialized) and returns the
    /// raw element WITHOUT releasing it. Ownership transfers entirely to the caller, which
    /// must route the element to <see cref="UiaFocusBridge.EnqueueRelease"/> when done.
    /// The background image job outlives the pipeline's return to Idle, and the next
    /// recording's <see cref="BeginForRecording"/> starts with an unconditional
    /// <see cref="Release"/> — after a handoff it finds an empty slot, so it can neither
    /// destroy the job's element nor double-release it (the pipeline's own owner-scoped
    /// release in its finally no-ops the same way).
    /// </summary>
    public async Task<UiaFocusBridge.IUIAutomationElement?> TakeForHandoffAsync()
    {
        await ResolveAsync();
        var element = _materialized;
        _materialized = null;
        _pendingTask = null; // ResolveAsync already adopted or routed it
        Owner = CapturedFocusOwner.None;
        return element;
    }

    /// <summary>
    /// Release the current capture (pending and/or materialized). With
    /// <paramref name="expectedOwner"/>, releases only when the ownership
    /// matches — cancel paths use this so they never release a capture that a
    /// newer session has since taken over.
    /// </summary>
    public void Release(CapturedFocusOwner? expectedOwner = null)
    {
        if (expectedOwner.HasValue && Owner != expectedOwner.Value)
            return;

        if (_pendingTask != null)
        {
            _releaseWhenComplete(_pendingTask);
            _pendingTask = null;
        }

        if (_materialized != null)
        {
            _release(_materialized);
            _materialized = null;
        }

        Owner = CapturedFocusOwner.None;
    }
}
