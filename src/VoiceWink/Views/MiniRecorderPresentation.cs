using System;
using VoiceWink.Models.Enums;

namespace VoiceWink.Views;

// ERR-PERSIST controller refactor (2026-07-21): the SINGLE presentation model owned by
// MiniRecorderPresentationController. Replaces the cross-layer epoch/timer/kind machinery
// (PresentationEpochGuard, per-layer dismiss timers, MiniRecorderStateSnapshot/DecideRehydration,
// ShouldResurfaceAfterMessageClear), per the reviewed controller plan (2026-07-21).
//
// These types are public because they appear on MiniRecorderWindow's public IMiniRecorderRenderer
// surface (Render + the handle-carrying events). VoiceWink is an app assembly, not a shipped library,
// so public here is an accessibility formality, not an API-surface commitment.

/// <summary>
/// How long a pill stays on screen. EXPLICIT — never derived from tone (that conflation is what the
/// refactor removes). A pill's lifetime is fixed when it is published.
/// </summary>
public abstract record PillLifetime
{
    private PillLifetime() { }

    /// <summary>A live source owns it (recording pipeline, download, running image job): no ×, no
    /// expiry. It ends only when the owner withdraws the candidate.</summary>
    public sealed record WhileOwned : PillLifetime;

    /// <summary>Persists until the user dismisses it (corner ×) or a higher surface supersedes it.
    /// Error messages and Error-toned redo/retry actions. Shows the ×.</summary>
    public sealed record UntilDismissed : PillLifetime;

    /// <summary>Auto-dismiss after <paramref name="Duration"/> of VISIBLE time; the remaining
    /// duration PAUSES while masked by a higher surface (or while render is suspended/detached) and
    /// RESUMES — never restarts — when the pill becomes current again. Success/Warning action pills.</summary>
    public sealed record TimedVisible(TimeSpan Duration) : PillLifetime;

    /// <summary>Auto-dismiss at a wall-clock deadline regardless of masking. Warning messages
    /// (deadline = emit + the warning timing).</summary>
    public sealed record UntilUtc(DateTimeOffset DeadlineUtc) : PillLifetime;
}

/// <summary>The redo/retry affordance kind (an action pill's identity).</summary>
public enum AffordanceKind { Redo, Retry }

/// <summary>
/// The typed content of a pill. The content TYPE is the discriminator (no separate kind enum / no
/// optional-field bag), so illegal combinations (e.g. a download with a dismiss × and a redo button)
/// are unrepresentable. The renderer switches on the concrete type; <see cref="PillPresentation"/>
/// derives its affordances from content + lifetime.
/// </summary>
public abstract record PillContent
{
    private PillContent() { }

    /// <summary>Transient message pill (Error = persistent+×, Warning = timed).
    /// <paramref name="Action"/> is what a click on the body does (LNC-11) — <c>None</c> for every
    /// message but the license gate's refusal, which opens the License page.</summary>
    public sealed record Message(string Text, MiniRecorderTone Tone, PillMessageAction Action = PillMessageAction.None) : PillContent;

    /// <summary>A recording-pipeline state (Starting/Recording/Transcribing/Enhancing). Owns the
    /// pill while live; carries the stop-button routing inputs — <paramref name="State"/> plus
    /// <paramref name="CanSkipEnhancement"/>, which distinguishes an Enhancing whose stop SKIPS
    /// (main pipeline, raw transcript still pastes) from one whose stop FULL-CANCELS, and so
    /// drives the controller's stop-class handle rotation.
    /// <paramref name="Notice"/> is a transient pipeline-scoped message (AUD-1 mic-fallback)
    /// rendered as the status line INSIDE the pipeline pill — the stop button stays reachable,
    /// unlike a Message pill which would cover it (the same reasoning as ImageJob.Notice);
    /// the controller owns its expiry.</summary>
    public sealed record Pipeline(
        RecordingState State,
        DateTime? StartedAtUtc,
        bool CanSkipEnhancement,
        string? Notice = null) : PillContent;

    /// <summary>Model-download progress (owned by the live download).</summary>
    public sealed record Download(double Fraction, string ModelName) : PillContent;

    /// <summary>Background image-generation job (owned by the live job). <paramref name="Notice"/>
    /// is a transient job-scoped message rendered INSIDE the job pill (keeps the cancel button
    /// reachable); <paramref name="Progress"/> is the batch position ("Generating image 2 of 4…",
    /// IMG-3 — null for a single-image job). Render precedence: notice &gt; progress &gt; the plain
    /// "Generating image…" default.</summary>
    public sealed record ImageJob(string? Notice, string? Progress = null) : PillContent;

    /// <summary>A redo/retry affordance pill. Its retained CONTEXT lives in RedoCoordinator/VM; this
    /// is only the visible surface.</summary>
    public sealed record Affordance(AffordanceKind Kind, string Text, MiniRecorderTone Tone) : PillContent;
}

/// <summary>
/// Opaque identity of a specific presentation INSTANCE. Allocated by the controller on each explicit
/// lifecycle op (message emit, affordance arm, source begin, or a change of action/dismiss
/// semantics) — NEVER derived from content equality, so two identical "Recording failed" emissions
/// are distinct instances and the first's pending click is rejected. Replaces every epoch used for
/// UI-dismissal validation.
/// </summary>
public readonly record struct PillHandle(long Value)
{
    public static readonly PillHandle None = new(0);
    public bool IsNone => Value == 0;
}

/// <summary>
/// A fully-resolved pill to render (or the absence of one). Immutable; the controller produces it and
/// the renderer consumes it. Affordances are DERIVED, not free-set:
/// <list type="bullet">
/// <item><see cref="CanDismiss"/> ⇔ lifetime is <see cref="PillLifetime.UntilDismissed"/> (shows ×).</item>
/// <item>The stop button shows for a cancellable live source (Pipeline/ImageJob/Download).</item>
/// <item>The action button shows for an <see cref="PillContent.Affordance"/>.</item>
/// <item><see cref="MessageAction"/> is the message's own click action (LNC-11) — <c>None</c> for
/// every non-message content and for a plain message.</item>
/// </list>
/// </summary>
public sealed record PillPresentation(PillHandle Handle, PillContent Content, PillLifetime Lifetime)
{
    public bool CanDismiss => Lifetime is PillLifetime.UntilDismissed;
    public bool ShowsActionButton => Content is PillContent.Affordance;
    public bool ShowsStopButton => Content is PillContent.Pipeline or PillContent.ImageJob or PillContent.Download;
    public PillMessageAction MessageAction => Content is PillContent.Message m ? m.Action : PillMessageAction.None;
}

/// <summary>
/// The window as seen by the controller: a pure renderer with ONE entry point. The controller is the
/// only caller; App owns the window's create/warm/recreate/close lifecycle separately.
/// </summary>
public interface IMiniRecorderRenderer
{
    /// <summary>Render exactly this presentation, or hide when null. Idempotent; owns the
    /// Show-before-update ordering and stops incompatible mechanical activity. Must run on / marshal
    /// to the UI thread and preserve the inline FIFO render/hide ordering (zombie-pill №2).</summary>
    void Render(PillPresentation? presentation);

    /// <summary>Corner × tapped. Carries the handle captured at pointer-DOWN (ABA guard).</summary>
    event Action<PillHandle> DismissRequested;

    /// <summary>Redo/retry action button tapped. Handle captured at pointer-DOWN.</summary>
    event Action<PillHandle> ActionRequested;

    /// <summary>Stop button tapped (pipeline/download/image-job pills). Handle captured at
    /// pointer-DOWN; the controller routes it against the current content.</summary>
    event Action<PillHandle> StopRequested;

    /// <summary>Pill right-clicked to hide it while its owner keeps running (2026-07-30). Handle
    /// captured at pointer-DOWN (ABA guard) and consumed once per press.</summary>
    event Action<PillHandle> HideRequested;

    /// <summary>The body of a message pill was clicked — a left press on the background that was
    /// released WITHOUT crossing the drag threshold (LNC-11). Handle captured at pointer-DOWN (ABA
    /// guard); the controller answers what the message's action is for that handle, so a click on
    /// a plain message, a stale handle or a hidden pill resolves to <see cref="PillMessageAction.None"/>.</summary>
    event Action<PillHandle> MessageActionRequested;
}
