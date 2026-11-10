using VoiceWink.ViewModels;

namespace VoiceWink.Helpers;

/// <summary>
/// REL-20: holds at most ONE successfully delivered recording so it can be transcribed again
/// with another model. Ledger-tracked while held; emptied at the next recording start, on an
/// explicit dismiss and by the quit drain. DI singleton.
/// </summary>
public sealed class LastRecordingSlot
{
    private readonly RetainedWavLedger _ledger;
    private readonly Action<string, string> _dispose;
    private readonly object _lock = new();
    private MainViewModel.TranscriptionRetryContext? _held;
    private MainViewModel.RedoContext? _pairedRedo;

    /// <param name="dispose">Disposes a WAV no longer held: (path, reason).</param>
    public LastRecordingSlot(RetainedWavLedger ledger, Action<string, string> dispose)
    {
        _ledger = ledger;
        _dispose = dispose;
    }

    public MainViewModel.TranscriptionRetryContext? Current { get { lock (_lock) return _held; } }

    /// <summary>The redo the same run armed, or null; the chooser opens only for this redo.</summary>
    public MainViewModel.RedoContext? PairedRedo { get { lock (_lock) return _pairedRedo; } }

    public void Retain(MainViewModel.TranscriptionRetryContext context, MainViewModel.RedoContext? pairedRedo)
    {
        MainViewModel.TranscriptionRetryContext? previous;
        lock (_lock)
        {
            previous = _held;
            _held = context;
            _pairedRedo = pairedRedo;
        }
        _ledger.Track(context.WavPath, lastRecording: true);
        // Exactly one: recording start normally empties the slot first.
        if (previous != null && !SamePath(previous, context)) _dispose(previous.WavPath, "superseded");
    }

    /// <summary>Hands the held recording to a re-run iff the slot still holds <paramref name="held"/>.
    /// The WAV stays tracked; the run's own end-of-life disposition decides its fate.</summary>
    public bool TryTake(MainViewModel.TranscriptionRetryContext held)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_held, held)) return false;
            _held = null;
            _pairedRedo = null;
            return true;
        }
    }

    /// <summary>Pairs a redo armed after the run ended (an image job's) iff the slot still holds that WAV.</summary>
    public bool PairIfHeld(string wavPath, MainViewModel.RedoContext redo)
    {
        lock (_lock)
        {
            if (_held == null || !string.Equals(_held.WavPath, wavPath, StringComparison.OrdinalIgnoreCase)) return false;
            _pairedRedo = redo;
            return true;
        }
    }

    public void Discard(string reason)
    {
        MainViewModel.TranscriptionRetryContext? previous;
        lock (_lock)
        {
            previous = _held;
            _held = null;
            _pairedRedo = null;
        }
        if (previous != null) _dispose(previous.WavPath, reason);
    }

    private static bool SamePath(MainViewModel.TranscriptionRetryContext a, MainViewModel.TranscriptionRetryContext b)
        => string.Equals(a.WavPath, b.WavPath, StringComparison.OrdinalIgnoreCase);
}
