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
    private bool _onLastRecording;

    /// <summary>The token a Retry or redo-choice host waits with; one picker at a time (the ViewModel's picker claim).</summary>
    /// <param name="onLastRecording">UI-5e: the picker acts on the kept last recording, which the pill's × discards.</param>
    public CancellationToken Begin(bool onLastRecording = false) =>
        Start(isRedoPicker: false, redoIsImage: false, redoOnHistory: false, onLastRecording);

    /// <summary>The token a redo picker's host waits with, recording what a History delete can make stale.</summary>
    /// <param name="fromChooser">UI-5e: opened by the "Run again" chooser, so the pill's × makes it stale.</param>
    public CancellationToken BeginRedoPicker(bool isImageGeneration, bool onHistory, bool fromChooser = false) =>
        Start(isRedoPicker: true, isImageGeneration, redoOnHistory: onHistory, onLastRecording: fromChooser);

    /// <summary>The token the "Run again" chooser waits with: a History delete covering its paired redo, or the pill's ×, makes it stale.</summary>
    public CancellationToken BeginRedoChoice(bool pairedIsImageGeneration) =>
        Start(isRedoPicker: false, pairedIsImageGeneration, redoOnHistory: true, onLastRecording: true);

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

    /// <summary>A new recording: whichever picker is waiting, its request is stale. True when one was.</summary>
    public bool TripAll() => Trip(_ => true);

    /// <summary>A History delete: a redo picker or chooser on History content the delete covers is stale. True when one was.</summary>
    public bool TripRedoPicker(Func<bool, bool> deleteCoversImageFlag) =>
        Trip(_ => _redoOnHistory && deleteCoversImageFlag(_redoIsImage));

    /// <summary>UI-5e: the pill's × discarded the last recording; a picker on it is stale, a failure Retry or History picker is not. True when one was.</summary>
    public bool TripLastRecording() => Trip(_ => _onLastRecording);

    private CancellationToken Start(bool isRedoPicker, bool redoIsImage, bool redoOnHistory, bool onLastRecording)
    {
        lock (_gate)
        {
            _current = new CancellationTokenSource();
            _isRedoPicker = isRedoPicker;
            _redoIsImage = redoIsImage;
            _redoOnHistory = redoOnHistory;
            _onLastRecording = onLastRecording;
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
