using System;
using System.Threading;

namespace VoiceWink.Helpers;

/// <summary>UI-5d: cancels a picker's wait for the main window's dialog slot once its request has gone stale.</summary>
internal sealed class PickerWaitCancellation
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private bool _isRedoPicker;
    private bool _redoIsImage;
    private bool _redoOnHistory;

    /// <summary>The token a Retry or redo-choice host waits with; one picker at a time (the ViewModel's picker claim).</summary>
    public CancellationToken Begin() => Start(isRedoPicker: false, redoIsImage: false, redoOnHistory: false);

    /// <summary>The token a redo picker's host waits with, recording what a History delete can make stale.</summary>
    public CancellationToken BeginRedoPicker(bool isImageGeneration, bool onHistory) =>
        Start(isRedoPicker: true, isImageGeneration, redoOnHistory: onHistory);

    /// <summary>The token the "Run again" chooser waits with: a History delete covering its paired redo makes it stale.</summary>
    public CancellationToken BeginRedoChoice(bool pairedIsImageGeneration) =>
        Start(isRedoPicker: false, pairedIsImageGeneration, redoOnHistory: true);

    /// <summary>The picker's request ended; a later trip leaves its token alone.</summary>
    public void End(CancellationToken token)
    {
        lock (_gate)
        {
            if (_current != null && _current.Token == token)
                _current = null;
        }
    }

    /// <summary>The redo picker's dialog has answered; a Retry or choice wait is left alone.</summary>
    public void EndRedoPicker()
    {
        lock (_gate)
        {
            if (_isRedoPicker)
                _current = null;
        }
    }

    /// <summary>A new recording: whichever picker is waiting or open, its request is stale. True when one was.</summary>
    public bool TripAll() => Trip(_ => true);

    /// <summary>A History delete: a redo picker or chooser on History content the delete covers is stale. True when one was.</summary>
    public bool TripRedoPicker(Func<bool, bool> deleteCoversImageFlag) =>
        Trip(_ => _redoOnHistory && deleteCoversImageFlag(_redoIsImage));

    private CancellationToken Start(bool isRedoPicker, bool redoIsImage, bool redoOnHistory)
    {
        lock (_gate)
        {
            _current = new CancellationTokenSource();
            _isRedoPicker = isRedoPicker;
            _redoIsImage = redoIsImage;
            _redoOnHistory = redoOnHistory;
            return _current.Token;
        }
    }

    private bool Trip(Func<CancellationTokenSource, bool> applies)
    {
        CancellationTokenSource? tripped;
        lock (_gate)
        {
            if (_current == null || !applies(_current))
                return false;
            tripped = _current;
            _current = null;
        }
        // Outside the lock: Cancel runs the waiter's callbacks synchronously.
        tripped.Cancel();
        return true;
    }
}
