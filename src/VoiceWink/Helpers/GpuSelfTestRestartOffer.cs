namespace VoiceWink.Helpers;

/// <summary>
/// TRN-64: the restart dialog after a refused Whisper decode is offered ONCE per session (Codex r2
/// F4). A refused decode is expected to recur — the retry picker lets the user pick Whisper again,
/// and it refuses again — so an offer per refusal would re-open the dialog on every attempt. The
/// claim is released only when the offer could NOT be shown (no XamlRoot, a handler that threw),
/// so a later refusal may offer again; an offer the user answered, either way, stays spent.
/// Pure and lock-free so the ViewModel's site is one call each way and the rule is testable.
/// </summary>
internal sealed class GpuSelfTestRestartOffer
{
    private int _claimed;

    /// <summary>True exactly once until <see cref="Release"/>.</summary>
    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

    /// <summary>The offer was not shown: let a later refusal try again.</summary>
    public void Release() => Interlocked.Exchange(ref _claimed, 0);

    internal bool IsClaimed => Volatile.Read(ref _claimed) == 1;
}
