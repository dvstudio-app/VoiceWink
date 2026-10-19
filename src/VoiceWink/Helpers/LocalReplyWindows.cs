using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-15: how long a LOCAL model's streamed reply may stay silent. Two clocks inside the call's
/// total deadline (60 s built-in, 110 s Local server), which stays as the backstop:
/// <list type="bullet">
/// <item>the FIRST-TEXT wait, from the moment the chat request is sent until the first chunk carrying
/// text — a straight line in the prompt's length, because reading the prompt scales with it;</item>
/// <item>the STALL window, between two chunks carrying text.</item>
/// </list>
/// A null clock is not armed. Both are set by measurement (<see cref="LocalReplyWindowCalibration"/>,
/// <c>tools/local-reply-timing</c>), never guessed. <see cref="Enforce"/> false is WATCH-ONLY: a clock
/// that runs out is recorded (<see cref="LocalReplyProgress.WouldHaveStalled"/>) and logged, and the
/// reply goes on (owner decision 2026-10-18: no healthy reply cut off until field logs show the clocks
/// fire only on stuck models). <see cref="OnCpu"/> replaces this set on the built-in engine's processor
/// route, which is several times slower than the graphics route.
/// </summary>
internal sealed record LocalReplyWindows(
    double? FirstTextBaseMs, double? FirstTextMsPerChar, TimeSpan? Stall, bool Enforce = false, LocalReplyWindows? OnCpu = null)
{
    /// <summary>No clock armed: the reply is bounded by the total deadline only.</summary>
    internal static readonly LocalReplyWindows Unarmed = new(null, null, null);

    // Measured 2026-10-18 on three machines (RTX 3080 desktop, Arc 140V laptop, Snapdragon X laptop),
    // docs/plans/2026-10-17-lai15-stall-check/measurements/*.csv, by `local-reply-timing --calibrate`.

    /// <summary>The processor route, every protocol: the first text of a long dictation took 55–80 s,
    /// past what a first-text clock can usefully bound, so only the stall window is set (3 × 4,288 ms).</summary>
    internal static readonly LocalReplyWindows Processor = new(null, null, TimeSpan.FromMilliseconds(12_864));

    /// <summary>The built-in engine: graphics route (3 × the upper envelope, 3 × 956 ms), and
    /// <see cref="Processor"/> when the child runs on the processor.</summary>
    internal static readonly LocalReplyWindows BuiltIn = new(11_278, 2.3829, TimeSpan.FromMilliseconds(2_868), OnCpu: Processor);

    /// <summary>The shipped windows per local provider; <see cref="Unarmed"/> for every other one.
    /// Local server gets the processor's: a user's own server may have no graphics card, and every
    /// measured one had.</summary>
    internal static LocalReplyWindows For(AIProvider provider) => provider switch
    {
        AIProvider.OnThisPc => BuiltIn,
        AIProvider.LocalServer => Processor,
        _ => Unarmed,
    };

    /// <summary>The set for the route the reply runs on. The processor set takes this set's
    /// <see cref="Enforce"/>, so arming one provider arms both its routes.</summary>
    internal LocalReplyWindows ForRoute(bool onGpu)
        => onGpu || OnCpu is null ? this : OnCpu with { Enforce = Enforce };

    /// <summary>The first-text wait for a prompt of <paramref name="promptChars"/> characters
    /// (system + user message), or null when not armed.</summary>
    internal TimeSpan? FirstTextFor(int promptChars)
    {
        if (FirstTextBaseMs is not { } baseMs || FirstTextMsPerChar is not { } perChar)
            return null;
        var ms = Math.Max(0, baseMs + perChar * Math.Max(0, promptChars));
        return TimeSpan.FromMilliseconds(Math.Ceiling(ms));
    }
}
