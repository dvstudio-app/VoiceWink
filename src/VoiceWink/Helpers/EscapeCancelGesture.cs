namespace VoiceWink.Helpers;

/// <summary>What the hook does with one Escape key-down (HKY-11).</summary>
internal enum EscapeDownVerdict
{
    /// <summary>Let the key through to the foreground app.</summary>
    PassThrough,
    /// <summary>Swallow the key; it belongs to a press that already cancelled.</summary>
    SuppressOnly,
    /// <summary>Swallow the key and cancel the recording.</summary>
    CancelAndSuppress,
}

/// <summary>
/// HKY-11: the Escape decisions behind cancelling a recording — a double tap while recording, and the
/// swallowed press of a held-hotkey cancel. Pure and hook-thread-only: no locks, no I/O.
/// </summary>
internal sealed class EscapeCancelGesture
{
    /// <summary>Second tap must land within this many ms of the first.</summary>
    public const long DoubleTapWindowMs = 1500;

    /// <summary>A gap this long between downs means the key-up was lost (the hotkey hook's rule).</summary>
    public const long LostKeyUpGapMs = 2000;

    private bool _armed;
    private long _armedAt;
    private long _armedGeneration;
    private bool _upSinceLastDown = true;
    private long _lastDownAt;
    private bool _swallowing;

    /// <summary>The live word published by <c>HotkeyService.SetRecordingLive</c>: generation in the high bits, live in bit 0.</summary>
    public static long LiveWord(long generation, bool live) => (generation << 1) | (live ? 1L : 0L);

    /// <summary>Classify one Escape key-down at <paramref name="t"/> ms against the live word;
    /// <paramref name="heldHotkey"/> = a fresh press cancels at once (repeats still never count).</summary>
    public EscapeDownVerdict OnEscapeDown(long t, long liveWord, bool heldHotkey = false)
    {
        var gap = t - _lastDownAt;
        var lostUp = !_upSinceLastDown && gap >= LostKeyUpGapMs;
        var isRepeat = !_upSinceLastDown && !lostUp;
        _lastDownAt = t;
        _upSinceLastDown = false;

        // A swallowed press stays swallowed through its repeats, even after the recording ended.
        if (_swallowing)
        {
            if (isRepeat) return EscapeDownVerdict.SuppressOnly;
            _swallowing = false;
        }

        if ((liveWord & 1) == 0)
        {
            _armed = false;
            return EscapeDownVerdict.PassThrough;
        }
        if (isRepeat) return EscapeDownVerdict.PassThrough;

        var generation = liveWord >> 1;
        if (heldHotkey || (_armed && _armedGeneration == generation && t - _armedAt <= DoubleTapWindowMs))
        {
            _armed = false;
            _swallowing = true;
            return EscapeDownVerdict.CancelAndSuppress;
        }

        _armed = true;
        _armedAt = t;
        _armedGeneration = generation;
        return EscapeDownVerdict.PassThrough;
    }

    /// <summary>Escape key-up. True = swallow it, because its press was swallowed.</summary>
    public bool OnEscapeUp()
    {
        _upSinceLastDown = true;
        if (!_swallowing) return false;
        _swallowing = false;
        return true;
    }
}
