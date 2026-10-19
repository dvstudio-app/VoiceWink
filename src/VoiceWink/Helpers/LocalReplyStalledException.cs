namespace VoiceWink.Helpers;

/// <summary>Where a local model's streamed reply went silent.</summary>
internal enum LocalReplyPhase
{
    /// <summary>No text arrived within the first-text wait.</summary>
    FirstText,
    /// <summary>Text had arrived, then none within the stall window.</summary>
    BetweenChunks,
}

/// <summary>
/// LAI-15: a local model's streamed reply went silent past its window. A <see cref="TimeoutException"/>
/// with the deadline's own message, so every catch that already handles "Enhancement timed out" — the
/// raw-transcript paste, the <c>[Enhancement failed]</c> marker, redo, the pill — handles this the same
/// way (owner 2026-10-16: the pill copy stays until the memory lines show a level worth naming).
/// </summary>
internal sealed class LocalReplyStalledException : TimeoutException
{
    internal const string StalledMessage = "Enhancement timed out";

    internal LocalReplyStalledException(LocalReplyPhase phase, long atMs)
        : base(StalledMessage)
        => (Phase, AtMs) = (phase, atMs);

    internal LocalReplyPhase Phase { get; }

    /// <summary>When the clock ran out, in ms since the request was sent — the same base as the
    /// watch-only line, so the two can be compared.</summary>
    internal long AtMs { get; }
}

/// <summary>
/// LAI-15: how far one local reply got — written by the stream reader, read by the timing line and the
/// memory-state line. One instance per call. Counts and durations only.
/// </summary>
internal sealed class LocalReplyProgress
{
    internal int TextChunks { get; set; }
    internal long? FirstTextMs { get; set; }
    internal long? LongestGapMs { get; set; }
    /// <summary>The built-in engine's child serving the call; null for Local server.</summary>
    internal uint? ChildPid { get; set; }

    /// <summary>Watch-only: the first clock that ran out on this reply, and when (ms since the send).
    /// Set from a timer thread, read after the call — a reference, so the read can never tear.</summary>
    internal LocalReplyWouldStall? WouldHaveStalled => _wouldHaveStalled;

    private LocalReplyWouldStall? _wouldHaveStalled;

    /// <summary>Called once, on the timer thread, when a watch-only clock runs out — so the memory
    /// state is read at that moment, not after the reply.</summary>
    internal Action<LocalReplyPhase, long>? OnWouldHaveStalled { get; set; }

    internal void MarkWouldHaveStalled(LocalReplyPhase phase, long atMs)
    {
        if (Interlocked.CompareExchange(ref _wouldHaveStalled, new LocalReplyWouldStall(phase, atMs), null) is null)
            OnWouldHaveStalled?.Invoke(phase, atMs);
    }
}

/// <summary>A watch-only clock that ran out: where, and when (ms since the send).</summary>
internal sealed record LocalReplyWouldStall(LocalReplyPhase Phase, long AtMs)
{
}
