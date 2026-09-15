using System;
using VoiceWink.Helpers;
using VoiceWink.Models.Enums;

namespace VoiceWink.Views;

/// <summary>
/// UI-thread scheduler seam so the controller's single deadline callback is deterministic in tests
/// (fake clock + manual fire) and DispatcherQueue-backed in production.
/// </summary>
internal interface IPillScheduler
{
    DateTimeOffset UtcNow { get; }
    /// <summary>Arm a one-shot callback after <paramref name="delay"/>, REPLACING any prior armed
    /// callback (at most one is ever pending). Null <paramref name="delay"/> disarms.</summary>
    void Arm(TimeSpan? delay, Action onFire);
}

/// <summary>
/// ERR-PERSIST controller refactor (2026-07-21): the SINGLE UI-thread owner of "which pill surface is
/// current and when it auto-dismisses". Replaces the cross-layer epoch/timer/kind machinery. Sources
/// (App forwarding VM/domain events) publish/withdraw typed candidate slots; the controller applies a
/// fixed supersession matrix, selects one current presentation by precedence, drives ONE deadline via
/// the scheduler, and renders through the attached <see cref="IMiniRecorderRenderer"/>. Handles gate
/// stale UI input; TimedVisible action pills pause/resume (never restart) behind higher surfaces.
///
/// All methods are UI-thread-affine (App marshals before calling). The class holds no locks.
/// </summary>
internal sealed class MiniRecorderPresentationController : IDisposable
{
    private sealed class Slot
    {
        public PillHandle Handle;
        public PillContent Content = null!;
        public PillLifetime Lifetime = null!;
        // TimedVisible bookkeeping (unused for other lifetimes):
        public TimeSpan Remaining;          // banked visible time not yet spent
        public bool Counting;               // currently counting down (is current + not suspended)
        public DateTimeOffset CountingSince; // when the current count began
        // Affordance-only: this slot was exposed as a RetireAffordance FALLBACK, not a genuine arm.
        // A fallback is a resurfaced leftover, not news — under an idle image job it is masked like
        // an UntilDismissed slot, so it can never cover "Generating image…" (owner bug 2026-07-24).
        public bool FromFallback;
    }

    private readonly IPillScheduler _scheduler;

    // Candidate slots — the published intent of each source. null = no candidate of that kind.
    private Slot? _message, _pipeline, _download, _imageJob, _affordance;

    private PillPresentation? _current;
    // The payload LAST DELIVERED to the renderer. Distinct from _current since the user-hide latch
    // (below) can withhold a selected presentation: the changed-check must compare what was actually
    // delivered, or a hide (or its later re-show) computes "unchanged" and is never rendered.
    private PillPresentation? _lastRendered;
    private long _handleSeq;
    private IMiniRecorderRenderer? _renderer;
    private bool _renderSuspended;

    // User-hide (right-click the pill, 2026-07-30): the CURRENT presentation is not DRAWN while its
    // handle stays selected, but NOTHING is withdrawn — the image job / pipeline / download keeps
    // running and keeps owning the surface, so a long generation's Stop stays reachable from the tray.
    // Handle-scoped so it self-releases: the moment selection resolves to a DIFFERENT handle (job
    // ended, new dictation, a new message) the pill is drawn again, which is what makes a hidden pill
    // unable to strand the user. In-memory only — a restart never comes up invisible.
    private PillHandle? _userHiddenHandle;
    // UI-thread-written mirror of "is something hidden" for the tray-menu builder, which may run off
    // the UI thread: a bool read is atomic, a PillHandle? (long + flag) would tear.
    private volatile bool _isUserHidden;

    public MiniRecorderPresentationController(IPillScheduler scheduler)
        => _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    /// <summary>The presentation currently selected (null = nothing to show). Read by App to route
    /// stop/action requests against the live content. This is the LOGICAL selection: a user-hidden pill
    /// is still current here (its owner still owns the surface) — <see cref="IsInteractiveCurrent"/> is
    /// the gate for "the user can see and press it".</summary>
    public PillPresentation? Current => _current;

    // ── Renderer attach / recreate suspension ────────────────────────────────────

    /// <summary>Attach the (freshly created) window and render the current presentation once.
    /// Clears render-suspension. Called on the deferred post-DPI turn after a recreate swap.</summary>
    public void Attach(IMiniRecorderRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _renderSuspended = false;
        Reconcile(forceRender: true);
    }

    /// <summary>Detach the current window (recreate swap / shutdown). State + lifetimes continue;
    /// no renders are delivered until <see cref="Attach"/>. TimedVisible pauses while detached.</summary>
    public void Detach()
    {
        _renderer = null;
        Reconcile(); // pauses any counting TimedVisible; delivers nothing (no renderer)
    }

    /// <summary>Suspend render delivery the instant a recreate is accepted (before the multi-second
    /// enqueue-to-start gap). State keeps mutating; TimedVisible pauses; nothing renders until a
    /// later <see cref="Attach"/> (or <see cref="ResumeRendering"/> if the same window survives).</summary>
    public void SuspendRendering()
    {
        _renderSuspended = true;
        Reconcile();
    }

    /// <summary>Resume rendering onto the still-attached window (recreate abandoned/failed with the
    /// old window surviving). Renders the current presentation once.</summary>
    public void ResumeRendering()
    {
        _renderSuspended = false;
        Reconcile(forceRender: true);
    }

    private bool CanRender => _renderer != null && !_renderSuspended;

    // ── Publish / withdraw candidate slots (App forwards VM/domain events) ─────────
    // Each Publish stamps a NEW handle only on a genuine new instance (content-identity change),
    // and PRESERVES the handle on in-instance updates (download %, notice text, elapsed) so a
    // pending click on the same instance stays valid. Supersession clears are applied inline.

    public void PublishMessage(string text, MiniRecorderTone tone, PillMessageAction action = PillMessageAction.None)
    {
        var lifetime = tone == MiniRecorderTone.Error
            ? (PillLifetime)new PillLifetime.UntilDismissed()
            : new PillLifetime.UntilUtc(_scheduler.UtcNow + TimeSpan.FromSeconds(MiniRecorderTimings.AttentionSeconds));
        // A NEW message is always a new instance (two identical "Recording failed" emissions are
        // distinct — the first's pending click must be rejected).
        _message = new Slot { Handle = NextHandle(), Content = new PillContent.Message(text, tone, action), Lifetime = lifetime };
        _download = null; // supersession: a message clears the download
        Reconcile();
    }

    /// <summary>
    /// LNC-11: what a click on the message body captured under <paramref name="handle"/> should do.
    /// <see cref="PillMessageAction.None"/> unless the handle is the current, VISIBLE presentation
    /// (<see cref="IsInteractiveCurrent"/> — the same ABA/hidden gate every other pill press takes)
    /// AND that presentation is a message carrying an action. The window raises the click for every
    /// non-drag release on the background, so this is the ONE place a plain message, a stale handle
    /// or a hidden pill is turned away.
    /// </summary>
    public PillMessageAction MessageActionFor(PillHandle handle)
        => IsInteractiveCurrent(handle) ? _current!.MessageAction : PillMessageAction.None;

    public void ClearMessage()
    {
        _message = null;
        Reconcile();
    }

    public void PublishDownload(double fraction, string modelName)
    {
        // In-instance progress update preserves the handle; a fresh download begins a new instance.
        if (_download is { Content: PillContent.Download })
            _download.Content = new PillContent.Download(fraction, modelName);
        else
            _download = new Slot { Handle = NextHandle(), Content = new PillContent.Download(fraction, modelName), Lifetime = new PillLifetime.WhileOwned() };
        _message = null; // supersession: download progress clears a message
        Reconcile();
    }

    public void ClearDownload()
    {
        _download = null;
        Reconcile();
    }

    // A busy pipeline whose PILL is hidden (pre-paste focus/occlusion) but which still owns the surface
    // (masks image-job + affordance) until the real Idle transition. See SuppressPipeline / Reconcile.
    private bool _pipelineSuppressed;

    public void PublishPipeline(RecordingState state, DateTime? startedAtUtc, bool canSkipEnhancement)
    {
        // A fresh publish reveals the pipeline pill again (a new recording after a suppressed tail).
        _pipelineSuppressed = false;
        var content = new PillContent.Pipeline(state, startedAtUtc, canSkipEnhancement);
        // Preserve the handle across state changes that keep the SAME stop-button MEANING, but allocate
        // a NEW handle when the effective stop action changes (Codex diff review finding 5): a stop
        // press captured during Recording (Toggle) must NOT act if the pill has since become Transcribing
        // (Cancel) or Enhancing-skippable (Skip) by tap-time. The handle IS the pointer-down↔tap action
        // contract, so a semantic change invalidates a spanning press via IsCurrent.
        var prevPipeline = _pipeline?.Content as PillContent.Pipeline;
        // AUD-1: an unexpired pipeline notice survives EVERY republish (state changes included —
        // it is time-scoped, not state-scoped); expiry alone clears it (Reconcile).
        if (prevPipeline?.Notice != null && _pipelineNoticeUntil != null)
            content = content with { Notice = prevPipeline.Notice };
        if (prevPipeline != null && StopClass(prevPipeline) == StopClass(content))
            _pipeline!.Content = content;
        else
            _pipeline = new Slot { Handle = NextHandle(), Content = content, Lifetime = new PillLifetime.WhileOwned() };
        // Supersession: an ACTIVE pipeline clears a stale message so a persistent license/audio error
        // can't cover the next recording (+ hide its stop button). Leaving Starting clears download.
        _message = null;
        if (state != RecordingState.Starting)
            _download = null;
        Reconcile();
    }

    /// <summary>The stop-button's effective action class for a pipeline pill — the semantics the
    /// pointer-down↔tap handle must protect (mirrors MiniRecorderStopRouting + MainViewModel.ClassifyStopTap):
    /// Toggle (Starting/Recording) vs Skip (Enhancing WITH skip capability) vs Cancel (Transcribing, or
    /// Enhancing without skip). A change between classes gets a new handle so a spanning press is rejected.</summary>
    private static int StopClass(PillContent.Pipeline p) => p.State switch
    {
        RecordingState.Starting or RecordingState.Recording => 0, // Toggle
        RecordingState.Enhancing when p.CanSkipEnhancement => 2,   // Skip enhancement
        _ => 1,                                                    // Cancel (Transcribing / non-skippable Enhancing)
    };

    public void ClearPipeline()
    {
        _pipeline = null;
        _pipelineSuppressed = false;
        _download = null; // download only lives during Starting; the pipeline leaving clears it
        _pipelineNoticeUntil = null; // the notice is pipeline-scoped; it dies with the pipeline
        Reconcile();
    }

    // AUD-1: expiry deadline for the transient pipeline-scoped notice (mic-fallback). Mirrors
    // _imageJobNoticeUntil — the controller owns the revert; renderers never time anything.
    private DateTimeOffset? _pipelineNoticeUntil;

    /// <summary>AUD-1: set/refresh the transient notice rendered INSIDE the live pipeline pill
    /// (in-instance update — handle and stop-button semantics both preserved).
    /// Returns false without side effects when no pipeline is active (the caller decides the
    /// fallback surface — App routes to a plain Warning message, same shape as the image-job
    /// notice forwarder).</summary>
    public bool SetPipelineNotice(string notice, DateTimeOffset until)
    {
        if (_pipeline is not { Content: PillContent.Pipeline p })
            return false;
        _pipeline.Content = p with { Notice = notice };
        _pipelineNoticeUntil = until;
        Reconcile();
        return true;
    }

    /// <summary>Hide the pipeline PILL (pre-paste focus/occlusion, cancel/abort) WITHOUT withdrawing the
    /// pipeline as the busy owner: it keeps masking the image-job + affordance slots until the real Idle
    /// transition (<see cref="ClearPipeline"/>). No-op when no pipeline is active — a hide with nothing
    /// to suppress falls through to whatever the lower slots select. Prevents a latent background image
    /// job from surfacing its Stop during the dictation's paste/history tail (Codex r3 #1).</summary>
    public void SuppressPipeline()
    {
        if (_pipeline is { Content: PillContent.Pipeline p } && p.State != RecordingState.Idle)
            _pipelineSuppressed = true;
        Reconcile();
    }

    public void PublishImageJob()
    {
        // A job pill already exists → PRESERVE its content, including any live notice: a repeated
        // running-state notification (a later phase of the SAME job) must not wipe an unexpired notice
        // (Codex diff review finding 6). Only a genuinely new job (after ClearImageJob nulled the slot)
        // initializes to the plain "Generating image…" pill.
        if (_imageJob is not { Content: PillContent.ImageJob })
        {
            _imageJob = new Slot { Handle = NextHandle(), Content = new PillContent.ImageJob(null), Lifetime = new PillLifetime.WhileOwned() };
            // REL-22 supersession, mirroring PublishPipeline's: a genuinely NEW job clears a PERSISTENT
            // message so a stale error can't cover "Generating image…" (+ hide its stop button). Needed
            // because REL-22 stopped masking persistent messages under an idle job — without this, an
            // undismissed "Transcription failed" would sit on top of a job the user started afterwards
            // from History Redo, which reaches ImageGenerationJobService WITHOUT running a recording
            // pipeline (HistoryPage → RequestModelPickerWithContext), so nothing else clears it.
            // Scoped two ways, each deliberate: only on NEW slot creation, because
            // OnImageJobServiceStateChanged republishes on every job state change and an unconditional
            // clear would wipe a failure reported DURING the job — the REL-22 case itself; and only
            // UntilDismissed, because a timed message expires on its own and was never masked anyway.
            if (_message is { Lifetime: PillLifetime.UntilDismissed })
                _message = null;
            // The AFFORDANCE needs the same temporal rule but must NOT be cleared — its retained
            // context is the user's recording. So it is stamped as predating this job and MASKED by
            // Reconcile instead, resurfacing when the job ends. `FromFallback` cannot serve here: it
            // answers "was this resurfaced by machinery?", not "is this older than the job?" — a
            // genuine arm stays FromFallback=false forever, so an armed retry would have covered
            // "Generating image…" and its Stop for the whole job (Codex diff r1 blocker 1). Reachable:
            // a dictation fails and arms the retry, then the user starts New Image / Iterate / History
            // Redo — DispatchImageRedoAsync reserves the job BEFORE ClearRedoState, and ClearRedoState
            // is redo-only, so the retry is still armed when the job pill appears.
            // ANY affordance present at job start predates it (PILL-6, owner UAT 2026-08-17 §23.1
            // Fail: "the retry amber warning stays, generating image only appears when the amber
            // warning is gone"). This stamp was PERSISTENT-only, on the reasoning that a TIMED
            // affordance "expires within seconds and cannot strand the job pill". It strands it for
            // its whole remaining budget — up to the full success budget — which is the 2026-07-24 masking bug
            // in timed form: an OLD pill covering a NEW job and hiding its Stop button.
            // The rule that a just-finished dictation's outcome shows over a running job (UAT
            // 15.5/15.6) is untouched, because that is the OPPOSITE ordering: there the arm lands
            // AFTER the job, and ArmAffordance clears this stamp precisely because news is newer
            // than the job. Set only on NEW slot creation, so it always answers "was this affordance
            // already on screen when THIS job began?".
            // The masked affordance is not lost BY THE CONTROLLER: Reconcile pauses its TimedVisible
            // budget, so ClearImageJob reselects it with its REMAINING time, never a fresh one
            // (§23.2). Whether the USER then sees it depends on what the job end arms: on the normal
            // completion path MainViewModel queues the completion via EnqueueUiTurn (a LATER
            // dispatcher turn) while ReleaseSlot -> StateChanged -> ClearImageJob runs inline, so the
            // affordance is reselected for one turn and then replaced by the job's own redo arm
            // (ArmAffordance always replaces). That displacement is REL-22's recorded residual --
            // "the retry was displaced, not queued" -- now reaching timed pills too; the retained
            // context survives in RedoCoordinator either way. A CANCEL does not resurface it
            // promptly either (Codex diff r1): PresentImageJobCancelled emits a timed Warning
            // MESSAGE, and a message outranks everything in Reconcile, so the affordance waits that
            // out first. Only a job that ends arming nothing at all leaves it on screen at once.
            _affordancePredatesImageJob = _affordance is not null;
        }
        Reconcile();
    }

    /// <summary>Set/refresh the transient job-scoped notice on the live job pill (in-instance update,
    /// preserves the handle AND the batch progress). <paramref name="until"/> = when it reverts to
    /// the progress-bearing (or plain) job pill.</summary>
    public void SetImageJobNotice(string notice, DateTimeOffset until)
    {
        if (_imageJob == null)
            _imageJob = new Slot { Handle = NextHandle(), Lifetime = new PillLifetime.WhileOwned() };
        _imageJob.Content = new PillContent.ImageJob(notice, (_imageJob.Content as PillContent.ImageJob)?.Progress);
        _imageJobNoticeUntil = until;
        Reconcile();
    }

    /// <summary>IMG-3: set/refresh the batch progress on the LIVE job pill (in-instance update,
    /// preserves the handle and an unexpired notice — notice keeps render precedence and its
    /// expiry reverts to THIS progress). No-op when no job slot exists: slot creation stays with
    /// <see cref="PublishImageJob"/> (the job-state event), never with a progress straggler.</summary>
    public void SetImageJobProgress(string? progress)
    {
        if (_imageJob is not { Content: PillContent.ImageJob existing })
            return;
        _imageJob.Content = existing with { Progress = progress };
        Reconcile();
    }

    public void ClearImageJob()
    {
        _imageJob = null;
        _imageJobNoticeUntil = null;
        _affordancePredatesImageJob = false; // no job left to be older than
        Reconcile();
    }

    private DateTimeOffset? _imageJobNoticeUntil;

    /// <summary>REL-22: was the CURRENT affordance already armed when the live job slot was created?
    /// Set by <see cref="PublishImageJob"/>, cleared by a genuine <see cref="ArmAffordance"/> (news is
    /// by definition newer than the job) and by <see cref="ClearImageJob"/>. Read only in
    /// <see cref="Reconcile"/>'s idle-job mask. It is deliberately NOT part of the Slot: a slot is
    /// replaced on every arm, and this is a relation BETWEEN two slots.</summary>
    private bool _affordancePredatesImageJob;

    // ── Affordance (redo/retry) two-context protocol ──────────────────────────────

    // Per-kind remaining visible budget of the latest retained GENUINE timed arm (null = none this
    // episode: never armed timed, retired, or withdrawn; zero = spent by on-screen expiry). A
    // fallback exposure draws from here and is WITHHELD when null or ≤ 0 — the never-restart rule
    // extended across slot replacements. Without it, every image-redo retirement re-granted the
    // "No speech detected — …" retry pill a fresh full budget and masked the job pill
    // (owner bug 2026-07-24).
    private TimeSpan? _redoBank, _retryBank;

    // Per-kind fallback ELIGIBILITY: only a genuine arm makes a kind exposable as a fallback;
    // dismiss (×), on-screen expiry, retirement, and withdrawal all tombstone it until the next
    // genuine same-kind arm. Required for persistent (Error) fallbacks too — they bypass the bank,
    // and without the tombstone a DISMISSED error pill was resurrected by the next redo retirement
    // (Codex diff review, blocking finding).
    private bool _redoEligible, _retryEligible;

    private TimeSpan? GetBank(AffordanceKind kind) => kind == AffordanceKind.Redo ? _redoBank : _retryBank;

    private void SetBank(AffordanceKind kind, TimeSpan? value)
    {
        if (kind == AffordanceKind.Redo) _redoBank = value;
        else _retryBank = value;
    }

    private bool GetEligible(AffordanceKind kind) => kind == AffordanceKind.Redo ? _redoEligible : _retryEligible;

    private void SetEligible(AffordanceKind kind, bool value)
    {
        if (kind == AffordanceKind.Redo) _redoEligible = value;
        else _retryEligible = value;
    }

    /// <summary>The single bank-capture boundary: a genuine arm of <paramref name="incomingKind"/>
    /// replacing a live TimedVisible slot of the OTHER kind banks that kind's remaining visible time
    /// (clamped ≥ 0) — its context may still be retained, and a later fallback exposure resumes from
    /// exactly here. A retiring kind is never captured (its context is gone).</summary>
    private void CaptureOutgoingBank(AffordanceKind incomingKind)
    {
        if (_affordance is { Content: PillContent.Affordance old, Lifetime: PillLifetime.TimedVisible } outgoing
            && old.Kind != incomingKind)
        {
            var remaining = EffectiveRemaining(outgoing, _scheduler.UtcNow);
            SetBank(old.Kind, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
        }
    }

    /// <summary>Arm the visible redo/retry affordance — ALWAYS replaces the UI slot with a fresh
    /// instance (new handle, full TimedVisible budget: a genuine arm is a new event). The retained
    /// CONTEXT lives in RedoCoordinator/VM; this is only the surface. Supersession: arming clears
    /// message + download. A persistent (Error) arm invalidates any stale timed bank for the kind.</summary>
    public void ArmAffordance(AffordanceKind kind, string text, MiniRecorderTone tone)
    {
        CaptureOutgoingBank(kind);
        // REL-22: a genuine arm is NEWS, so by definition it is newer than any live job and must show
        // over it. Cleared here rather than in SetAffordanceSlot, which also serves fallback exposures.
        _affordancePredatesImageJob = false;
        // DeadlineFor is THE tone→lifetime seam: Success and Warning both 10 s, Error → null (no
        // timer → UntilDismissed). A null budget doubles as "clear any stale timed bank".
        var budget = MiniRecorderTimings.DeadlineFor(tone);
        SetBank(kind, budget);
        SetEligible(kind, true); // a genuine arm is what makes this kind exposable as a fallback
        SetAffordanceSlot(kind, text, tone, fromFallback: false, timedBudget: budget);
        // A genuine arm supersedes a message/download (the user acted; the terminal outcome is what
        // matters). NOTE: this clearing is what a fallback exposure must NOT do — see RetireAffordance.
        _message = null;
        _download = null;
        Reconcile();
    }

    /// <summary>Build the affordance UI slot (fresh handle). A null <paramref name="timedBudget"/>
    /// (= <see cref="MiniRecorderTimings.DeadlineFor"/> for an Error tone) means UntilDismissed;
    /// otherwise TimedVisible carries the budget in BOTH Duration and Remaining so the lifetime
    /// record always states the real budget. Does NOT apply the genuine-arm supersession, so it can
    /// serve both a real arm and a fallback exposure.</summary>
    private void SetAffordanceSlot(AffordanceKind kind, string text, MiniRecorderTone tone, bool fromFallback, TimeSpan? timedBudget)
    {
        var lifetime = timedBudget is null
            ? (PillLifetime)new PillLifetime.UntilDismissed()
            : new PillLifetime.TimedVisible(timedBudget.Value);
        _affordance = new Slot
        {
            Handle = NextHandle(),
            Content = new PillContent.Affordance(kind, text, tone),
            Lifetime = lifetime,
            Remaining = timedBudget ?? TimeSpan.Zero,
            Counting = false,
            FromFallback = fromFallback,
        };
    }

    /// <summary>Retiring <paramref name="kind"/> means its DOMAIN CONTEXT is gone (the VM
    /// availability flag dropped), in strict order: (1) that kind's bank clears unconditionally —
    /// there is nothing left to bound; (2) the ownership guard — a retirement for a non-current kind
    /// must never replace the current slot or mint a fallback handle (and a later domain clear can't
    /// resurrect an already-dismissed/expired fallback); (3) a <paramref name="fallback"/> is
    /// exposed only while its kind is ELIGIBLE — genuinely armed and not since dismissed, expired,
    /// retired, or withdrawn — and a timed one additionally draws strictly from ITS OWN bank (spent
    /// → withheld), so a fallback can never restart a budget nor resurrect a dismissed error pill.
    /// Exposing goes through <see cref="SetAffordanceSlot"/>,
    /// NOT ArmAffordance — a routine redo↔retry retirement must not clear an unrelated live
    /// message/download the way a genuine new arm does (Codex finding 3).</summary>
    public void RetireAffordance(AffordanceKind kind, (AffordanceKind Kind, string Text, MiniRecorderTone Tone)? fallback)
    {
        SetBank(kind, null);
        SetEligible(kind, false); // the retiring context is gone — tombstoned until a genuine re-arm
        if (_affordance is not { Content: PillContent.Affordance a } || a.Kind != kind)
            return; // already gone — never resurrect (bank/eligibility stay cleared: the context IS gone)
        if (fallback is { } f && GetEligible(f.Kind))
        {
            // A persistent fallback (DeadlineFor → null) needs eligibility alone; a timed one also
            // needs a live banked budget — withheld once spent, never restarted.
            var persistent = MiniRecorderTimings.DeadlineFor(f.Tone) is null;
            if (persistent)
                SetAffordanceSlot(f.Kind, f.Text, f.Tone, fromFallback: true, timedBudget: null);
            else if (GetBank(f.Kind) is { } banked && banked > TimeSpan.Zero)
                SetAffordanceSlot(f.Kind, f.Text, f.Tone, fromFallback: true, timedBudget: banked);
            else
                _affordance = null;
        }
        else
            _affordance = null; // no fallback, or the fallback kind is tombstoned (dismissed/expired/retired)
        Reconcile();
    }

    /// <summary>Withdraw the visible affordance regardless of kind (used when neither context
    /// survives). Never touches retained contexts. Clears both banks (defensive — no production
    /// caller today; a withdraw ends the episode for both kinds).</summary>
    public void WithdrawAffordance()
    {
        _affordance = null;
        _redoBank = null;
        _retryBank = null;
        _redoEligible = false;
        _retryEligible = false;
        Reconcile();
    }

    // ── UI-originated requests (handle-gated) ─────────────────────────────────────

    /// <summary>Corner × : consume the CURRENT presentation iff the handle still matches, it is
    /// dismissable, and it is on screen. For an affordance this withdraws only the visible pill; the
    /// retained context is untouched (App does not clear it). Returns true if applied.</summary>
    public bool Dismiss(PillHandle handle)
    {
        if (!IsInteractiveCurrent(handle) || _current is not { CanDismiss: true })
            return false;
        WithdrawCurrentSlot();
        Reconcile();
        return true;
    }

    /// <summary>Is this handle the logical current presentation AND not user-hidden — i.e. the pill the
    /// user can actually see and press? Gates every UI-originated action (stop / action / dismiss): a
    /// press captured just before a right-click hide must not execute against a parked pill.
    /// <para>Deliberately does NOT consider render suspension (<c>SuspendRendering</c>/<c>Detach</c>):
    /// those paths detach the window's event handlers, so no stale input can arrive from a suspended
    /// window in the first place, whereas a user-hidden window stays attached. <see cref="Current"/>
    /// remains the LOGICAL selection — a hidden pill's owner still owns the surface.</para></summary>
    public bool IsInteractiveCurrent(PillHandle handle)
        => _current is { } c && c.Handle == handle && !_isUserHidden;

    // ── User-hide latch (right-click hide / tray restore) ─────────────────────────

    /// <summary>True while a selected presentation is being withheld from the renderer. Read by the
    /// tray-menu builder to show "Show mini recorder" (may be read off the UI thread — see the
    /// volatile mirror).</summary>
    public bool IsUserHidden => _isUserHidden;

    /// <summary>Right-click hide: stop DRAWING the current presentation while it stays selected — its
    /// owner keeps running and keeps the surface. Handle-gated (the pointer-down ABA guard), so a press
    /// that spanned a pill swap is rejected, as is <see cref="PillHandle.None"/> and an already-hidden
    /// pill. Returns true if applied.</summary>
    public bool HideCurrent(PillHandle handle)
    {
        if (handle.IsNone || !IsInteractiveCurrent(handle))
            return false;
        _userHiddenHandle = handle;
        _isUserHidden = true;
        Reconcile();
        return true;
    }

    /// <summary>Tray "Show mini recorder": release the latch and draw whatever is currently selected.
    /// Returns false when nothing was hidden.</summary>
    public bool ShowHidden()
    {
        if (_userHiddenHandle is null)
            return false;
        ClearUserHidden();
        Reconcile();
        return true;
    }

    private void ClearUserHidden()
    {
        _userHiddenHandle = null;
        _isUserHidden = false;
    }

    private void WithdrawCurrentSlot()
    {
        switch (_current?.Content)
        {
            case PillContent.Message: _message = null; break;
            case PillContent.Affordance dismissed:
                // The user closed it: tombstone the kind so no later retirement can resurrect it as
                // a fallback (the retained VM context is untouched — a genuine re-arm restores it).
                _affordance = null;
                SetEligible(dismissed.Kind, false);
                break;
            // Pipeline/Download/ImageJob are WhileOwned (not dismissable) — never reach here.
        }
    }

    // ── Core: supersession is applied in the Publish/Clear methods; here we select + schedule ──

    private PillHandle NextHandle() => new(++_handleSeq);

    private void Reconcile(bool forceRender = false)
    {
        var now = _scheduler.UtcNow;

        // 1) Expire time-limited candidates.
        if (_message is { Lifetime: PillLifetime.UntilUtc u } && now >= u.DeadlineUtc)
            _message = null;
        if (_affordance is { Lifetime: PillLifetime.TimedVisible, Content: PillContent.Affordance expired }
            && EffectiveRemaining(_affordance, now) <= TimeSpan.Zero)
        {
            // Spent on screen: zero the bank AND tombstone the kind — an expired pill must not be
            // resurrected by a later fallback exposure.
            SetBank(expired.Kind, TimeSpan.Zero);
            SetEligible(expired.Kind, false);
            _affordance = null;
        }
        if (_imageJob != null && _imageJobNoticeUntil is { } nu && now >= nu)
        {
            _imageJobNoticeUntil = null;
            if (_imageJob.Content is PillContent.ImageJob { Notice: not null } expiredNotice)
                // Revert to the PROGRESS-bearing job pill (IMG-3), not the bare one —
                // an expiring notice must not erase "Generating image 2 of 4…".
                _imageJob.Content = expiredNotice with { Notice = null };
        }
        if (_pipeline != null && _pipelineNoticeUntil is { } pnu && now >= pnu)
        {
            // AUD-1: pipeline notice expiry — revert to the plain state-derived status line;
            // handle untouched (in-instance content update).
            _pipelineNoticeUntil = null;
            if (_pipeline.Content is PillContent.Pipeline { Notice: not null } expiredPipeNotice)
                _pipeline.Content = expiredPipeNotice with { Notice = null };
        }

        // 2) Select the current presentation by precedence. A GENUINE message or affordance shows OVER a
        //    running background image job, TIMED or PERSISTENT alike — the owner wants to SEE the
        //    dictation outcome, not have "Generating image…" swallow it (UAT 15.5/15.6, 2026-07-21;
        //    refines the IMG-BG "cancel-first" rule for the fresh-dictation case).
        //
        //    REL-22 (owner decision "A", 2026-08-11) EXTENDED that from timed to persistent, but ONLY
        //    for news that POSTDATES the job — see the two temporal guards below. Until then a
        //    persistent (UntilDismissed = Error) message or affordance was masked outright, on the
        //    reasoning that it "stays a candidate and resurfaces when the job ends, so the job's cancel
        //    stays reachable". The measured cost was a failed dictation with NO user-visible feedback at
        //    all: three transcription failures each landed inside an image-job window, so each retry pill
        //    was masked for the whole job, and each job then ended by arming its OWN Error redo
        //    affordance, which REPLACED the retry in the slot (ArmAffordance always replaces). The retry
        //    context survived in RedoCoordinator; the pill the user could have acted on never appeared.
        //    Since IMG-BG's premise is that recording never blocks on generation, dictating during a
        //    multi-minute job is the normal case, not an edge one. Cancel is NOT lost: the pill is one ×
        //    from dismissal and the tray carries a "Cancel image" item (App.SetupTrayIcon).
        //
        //    What did NOT change, and must not: a FALLBACK-exposed affordance (RetireAffordance) is a
        //    resurfaced leftover, not news, and stays masked — it was a fallback riding this branch that
        //    let a stale retry pill cover "Generating image…" (owner bug 2026-07-24).
        //
        //    FRESHNESS is what the two guards below enforce, because "persistent" says nothing about
        //    age and an OLD persistent pill covering a NEW job is the 2026-07-24 bug again. Neither slot
        //    could answer it alone: the message slot has no flag at all, and the affordance's
        //    FromFallback answers a different question (resurfaced by machinery, not older than the
        //    job). So PublishImageJob stamps both at job start — clearing a persistent message, masking
        //    a predating affordance via `_affordancePredatesImageJob` — and PublishPipeline keeps
        //    clearing `_message` at recording start. Both sites are needed: an image job does NOT always
        //    follow a dictation (History Redo starts one with no recording at all).
        var pipelineActive = _pipeline is { Content: PillContent.Pipeline p } && p.State != RecordingState.Idle;
        var idleJob = _imageJob != null && !pipelineActive;
        Slot? selected;
        if (_message is { } msg)
            selected = msg;                                   // fresh news always outranks an idle job
        else if (_download is { } dl && DownloadShowable(dl)) // download only while pipeline Starting
            selected = dl;
        else if (pipelineActive)
            // A SUPPRESSED active pipeline renders nothing (pre-paste hide) but still owns the surface —
            // it does NOT fall through to the image-job / affordance below, so a latent background image
            // job can't surface its Stop during the dictation's paste/history tail and cancel the wrong
            // operation (Codex r3 #1). Only the real Idle transition (ClearPipeline) withdraws it.
            selected = _pipelineSuppressed ? null : _pipeline;
        else if (_affordance is { } aff && !(idleJob && (aff.FromFallback || _affordancePredatesImageJob)))
            selected = aff;                                   // a genuine arm NEWER than the job shows over it
        else if (idleJob)
            selected = _imageJob;
        else
            selected = null;

        // 3) TimedVisible counting: exactly the selected affordance counts (when renderable); all
        //    others pause, banking remaining. Pausing while suspended/detached is intentional.
        UpdateTimedCounting(selected, now);

        var next = selected == null ? null : new PillPresentation(selected.Handle, selected.Content, selected.Lifetime);

        // 4) Arm the single deadline to the nearest pending transition of the CURRENT presentation.
        ArmNextTransition(next, now);

        // 5) Apply the user-hide latch. It withholds the PAYLOAD only: selection, supersession, the
        //    timed-counting bookkeeping above and the armed deadline all ran against `next`, so a
        //    hidden pill's owner keeps the surface and a hidden TimedVisible keeps burning its budget
        //    (deliberately unlike suspend/detach, which pause — a recreate is transient and its news
        //    unseen, whereas a deliberate hide means "done with this"; resuming minutes later would
        //    pop stale news back on screen). Selection moving to a DIFFERENT handle releases the
        //    latch, which is what makes a hidden pill unable to strand the user.
        var effective = next;
        if (_userHiddenHandle is { } hidden)
        {
            if (next is { } n && n.Handle == hidden)
                effective = null;
            else
                ClearUserHidden();
        }

        // 6) Render (gated). Only deliver when attached + not suspended. The changed-check compares the
        //    DELIVERED payload (_lastRendered), not the selection — they differ while hidden — and
        //    _lastRendered advances only after Render returns, so a throwing render is retried.
        _current = next;
        if (CanRender && (!PresentationEquals(_lastRendered, effective) || forceRender))
        {
            _renderer!.Render(effective);
            _lastRendered = effective;
        }
    }

    private bool DownloadShowable(Slot download)
        => _pipeline is { Content: PillContent.Pipeline { State: RecordingState.Starting } };

    private void UpdateTimedCounting(Slot? selected, DateTimeOffset now)
    {
        // Only the affordance slot is ever TimedVisible.
        if (_affordance is not { Lifetime: PillLifetime.TimedVisible } aff)
            return;
        var shouldCount = ReferenceEquals(selected, aff) && CanRender;
        if (shouldCount && !aff.Counting)
        {
            aff.Counting = true;
            aff.CountingSince = now;
        }
        else if (!shouldCount && aff.Counting)
        {
            aff.Remaining = EffectiveRemaining(aff, now);
            aff.Counting = false;
        }
    }

    private static TimeSpan EffectiveRemaining(Slot slot, DateTimeOffset now)
        => slot.Counting ? slot.Remaining - (now - slot.CountingSince) : slot.Remaining;

    private void ArmNextTransition(PillPresentation? current, DateTimeOffset now)
    {
        TimeSpan? delay = null;
        void Consider(TimeSpan? d) { if (d is { } v && (delay is null || v < delay)) delay = v < TimeSpan.Zero ? TimeSpan.Zero : v; }

        switch (current?.Content)
        {
            case PillContent.Message when current.Lifetime is PillLifetime.UntilUtc u:
                Consider(u.DeadlineUtc - now);
                break;
            case PillContent.Affordance when _affordance is { Lifetime: PillLifetime.TimedVisible } aff:
                Consider(EffectiveRemaining(aff, now));
                break;
            case PillContent.Pipeline:
                if (_pipelineNoticeUntil is { } pipeNoticeDue)
                    // AUD-1: the pipeline notice's revert — the pipeline pill's only deadline.
                    Consider(pipeNoticeDue - now);
                break;
            case PillContent.ImageJob when _imageJobNoticeUntil is { } nu:
                Consider(nu - now);
                break;
        }
        _scheduler.Arm(delay, OnScheduledTick);
    }

    private void OnScheduledTick()
    {
        // Re-evaluate against the clock — NEVER blindly act on the transition this callback was armed
        // for (a stopped/re-armed schedule may still fire). Reconcile re-selects, handles every
        // expiry, and re-arms; a stale tick is therefore harmless.
        Reconcile();
    }

    private static bool PresentationEquals(PillPresentation? a, PillPresentation? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        // Identity + content equality; lifetime object identity is not compared (a re-armed instance
        // carries a new handle). Record value-equality on Content covers text/tone/progress/notice.
        return a.Handle == b.Handle && a.Content == b.Content;
    }

    public void Dispose() => _scheduler.Arm(null, () => { });
}
