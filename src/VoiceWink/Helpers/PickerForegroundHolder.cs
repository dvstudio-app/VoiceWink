using System;

namespace VoiceWink.Helpers;

/// <summary>
/// UI-7: the window that had the foreground immediately BEFORE a picker dialog took it, held for
/// exactly as long as that dialog is open, and used for ONE thing — handing the foreground back
/// when the dialog closes.
///
/// <para><b>What it is deliberately NOT used for.</b> An earlier revision also handed this handle to
/// a recording starting under the open dialog, as its paste target. Codex's diff round killed that:
/// <c>MainViewModel.AdoptPasteTarget</c> forbids targeting a STORED handle — Windows recycles
/// handles, and building an identity snapshot around an old one validates whatever now owns it, so
/// a private dictation reaches another application. TRN-17's cancel path tried it and was rejected
/// in verification; this holder tried it again behind an identity re-check, and the check turned out
/// to be discarded four lines later when <c>StartRecordingAsync</c> re-probed the bare handle to
/// build its snapshot. **Do not reintroduce a paste target from this holder.** A recording that
/// starts under an open picker degrades to the clipboard instead (owner decision, 2026-08-16) —
/// the same answer TRN-17 already gives when it finds VoiceWink in front.
///
/// That is what makes the remaining use safe rather than a smaller version of the same mistake:
/// activating a window cannot route text. A stale handle here means the user is looking at the
/// wrong window, which they can see and fix; a stale handle as a paste target means their words
/// are somewhere they cannot see at all.</para>
///
/// <para><b>Never holds VoiceWink.</b> The pre-dialog foreground can legitimately be our own window
/// — press the retry hotkey while browsing History and it is — and restoring to it would be a
/// no-op that only muddies the intent. <see cref="CapturePreDialog"/> stores nothing in that case.</para>
///
/// <para>Locked rather than documented as UI-thread-only: the capture and release sites are App's
/// three dialog handlers, and "they are all on the UI thread" is a claim about callers this type
/// cannot enforce. Uncontended in practice.</para>
/// </summary>
internal sealed class PickerForegroundHolder
{
    private readonly object _gate = new();
    private IntPtr _held;

    /// <summary>True when a pre-dialog window is being held. Diagnostics and tests only.</summary>
    internal bool HasTarget
    {
        get { lock (_gate) return _held != IntPtr.Zero; }
    }

    /// <summary>Record the foreground as it stands right now, immediately before the dialog takes
    /// it. Stores nothing for a zero handle or for our own window.
    ///
    /// <para>Overwrites any previous capture. The three picker sites are serialised by
    /// <c>_enhancementPickerActive</c>, so an overwrite means an unreleased earlier dialog, and
    /// last-writer-wins keeps the CURRENT dialog's window — the one the user is looking past.</para></summary>
    internal void CapturePreDialog(IntPtr hwnd, bool foregroundIsOurOwnProcess)
    {
        lock (_gate)
        {
            _held = foregroundIsOurOwnProcess ? IntPtr.Zero : hwnd;
        }
    }

    /// <summary>Yield the held window so the closing dialog can hand the foreground back, and
    /// clear.
    ///
    /// <para>This IS the release — it always clears, and it runs in every picker handler's
    /// <c>finally</c>, so nothing survives the dialog that captured it on any exit path. There is
    /// deliberately no separate <c>Release()</c>: a second way to clear is a second thing to forget
    /// to call.</para>
    ///
    /// <para>No liveness or identity check, and that is a decision rather than an omission. The
    /// caller activates the result, and activation cannot route text — the worst case is a recycled
    /// handle whose new owner gets activated, which is visible to the user and costs them a click.
    /// A check here would buy a nicer failure for a case the user can already see, at the cost of
    /// implying this handle is trustworthy for anything else. It is not.</para></summary>
    internal bool TryTakeForRestore(out IntPtr hwnd)
    {
        lock (_gate)
        {
            hwnd = _held;
            _held = IntPtr.Zero;
            return hwnd != IntPtr.Zero;
        }
    }
}
