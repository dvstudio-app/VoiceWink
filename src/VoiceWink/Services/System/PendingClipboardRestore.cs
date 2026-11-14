namespace VoiceWink.Services.System;

/// <summary>PST-19: the newest scheduled clipboard restore, read and changed under ClipboardService's write lease.</summary>
internal sealed class PendingClipboardRestore
{
    private readonly object _gate = new();
    private long _lastId;
    private long _id;
    private ClipboardService.ClipboardSnapshot? _snapshot;
    private string? _pastedText;

    /// <summary>Record the restore a successful paste scheduled; it replaces any earlier one. Returns its id.</summary>
    public long Park(ClipboardService.ClipboardSnapshot snapshot, string pastedText)
    {
        lock (_gate)
        {
            _id = ++_lastId;
            _snapshot = snapshot;
            _pastedText = pastedText;
            return _id;
        }
    }

    /// <summary>The pending original while the fresh snapshot (null = capture failed) still holds its paste's text; else the fresh one, dropping the stale entry.</summary>
    public ClipboardService.ClipboardSnapshot? ChooseOriginal(ClipboardService.ClipboardSnapshot? fresh)
    {
        lock (_gate)
        {
            if (_snapshot is null) return fresh;
            if (fresh is null || string.Equals(fresh.UnicodeText, _pastedText, StringComparison.Ordinal))
                return _snapshot;
            // The pending paste's text is proven gone, so its restore would skip; a later failed capture must not borrow it.
            _snapshot = null;
            _pastedText = null;
            return fresh;
        }
    }

    /// <summary>Drop the pending restore: an in-process write emptied the clipboard it would compare against.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _snapshot = null;
            _pastedText = null;
        }
    }

    /// <summary>True, and empties the slot, only while <paramref name="id"/> is still the pending restore.</summary>
    public bool TryClaim(long id)
    {
        lock (_gate)
        {
            if (_snapshot is null || _id != id) return false;
            _snapshot = null;
            _pastedText = null;
            return true;
        }
    }
}
