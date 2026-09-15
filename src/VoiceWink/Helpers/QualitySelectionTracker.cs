namespace VoiceWink.Helpers;

/// <summary>
/// What the user has ASKED for on an image dialog's Quality row, kept apart from what the row is
/// currently able to DISPLAY.
///
/// <para><b>Why the two cannot be one value (IMG-12, three review findings).</b> The row's offered
/// tiers are per-model, so the same ask renders differently on different models — a saved "maximum"
/// shows as Enhanced on a model publishing only <c>{low, medium}</c>. The first version kept the ask
/// nowhere but the ComboBox's tag, so every clamp overwrote it, and three separate defects followed:
/// an explicit Auto read as "nothing picked" and replaced by the saved tier (the user is charged for
/// a tier they turned off); a clamp that stuck after switching back to a capable model; and a Save
/// that persisted an intermediate clamp permanently once a third model hid the row. The ask lives
/// here instead, and the combo becomes a pure view of it.</para>
///
/// <para><b>Why a user edit needs an EVENT, not a comparison.</b> The second version inferred edits
/// by comparing the combo's tag against what we last displayed. That cannot see an edit that returns
/// to the displayed value — pick Standard, change your mind, pick Enhanced again, Save, and the
/// comparison reports "untouched" and persists the hidden Maximum instead of the Enhanced actually
/// chosen (Codex diff r2). No end-state comparison can distinguish those histories; only the
/// intermediate event can, which is what <see cref="NoteUserPick"/> records.</para>
///
/// <para><b>The suppression scope is what makes the event trustworthy.</b> Populating the combo
/// raises the same <c>SelectionChanged</c> the user does, so a bare handler would immediately record
/// our own clamp as the user's ask and restore the stickiness this type exists to remove. Callers
/// wrap every programmatic populate in <see cref="BeginPopulate"/> and ignore events while
/// <see cref="IsPopulating"/> is set.</para>
///
/// <para><b>Residual, stated rather than assumed:</b> this relies on WinUI raising
/// <c>SelectionChanged</c> synchronously from the populate. If a future runtime deferred it past the
/// scope, the handler would record the displayed value as the ask — i.e. a clamp would become sticky
/// again. That is the SAFE direction (a value at or below what was asked, never above), and it is
/// the same class of failure the pre-IMG-12 code had, so the fallback is not a regression.</para>
///
/// <para>Deliberately NOT thread-safe and deliberately WinUI-free: every caller is the UI thread, and
/// keeping the type free of framework types is what lets the edit sequences be unit-tested at all —
/// the dialogs themselves cannot be.</para>
/// </summary>
internal sealed class QualitySelectionTracker
{
    private int _populateDepth;

    /// <param name="initialRequest">
    /// The ask to start from: the redo context's previous quality, else the prompt's saved quality,
    /// else null for Auto. NOT clamped — clamping is a display concern.
    /// </param>
    internal QualitySelectionTracker(string? initialRequest) => Requested = initialRequest;

    /// <summary>
    /// The standing ask, unclamped. This is what a confirm PERSISTS, so a model that cannot serve it
    /// narrows the run without rewriting the preference.
    /// </summary>
    internal string? Requested { get; private set; }

    /// <summary>True while a programmatic populate is in progress; selection events are ours, not the user's.</summary>
    internal bool IsPopulating => _populateDepth > 0;

    /// <summary>
    /// Record a selection the USER made. Null is a legitimate ask — the Auto row — and is exactly the
    /// value the pre-IMG-12 chain could not distinguish from "nothing selected".
    /// </summary>
    internal void NoteUserPick(string? tag) => Requested = tag;

    /// <summary>
    /// Scope a programmatic populate. Re-entrant (a depth counter, not a bool) so a nested or
    /// re-entrant populate cannot clear the flag early and expose the tail of the outer one.
    /// Dispose in a <c>using</c> so an exception mid-populate cannot latch it on — a latched flag
    /// would silently ignore every real user pick for the life of the dialog.
    /// </summary>
    internal PopulateScope BeginPopulate() => new(this);

    internal readonly struct PopulateScope : global::System.IDisposable
    {
        private readonly QualitySelectionTracker _owner;

        internal PopulateScope(QualitySelectionTracker owner)
        {
            _owner = owner;
            owner._populateDepth++;
        }

        public void Dispose()
        {
            if (_owner._populateDepth > 0) _owner._populateDepth--;
        }
    }
}
