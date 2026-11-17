namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>How a built-in model's first-use check ended, as far as a waiting dictation cares (LAI-32).</summary>
internal enum FirstUseCheckOutcome
{
    /// <summary>The check settled without a failed GPU: the model may serve the dictation now.</summary>
    Ready,
    /// <summary>A failed GPU, a busy card, a check that did not finish, a delete or a close: no enhancement for that dictation.</summary>
    NotMade,
}

/// <summary>LAI-32: the dictation was refused because the model's first-use check is still running; <see cref="Settled"/> ends with it.</summary>
internal sealed class BuiltInModelWarmingUpException : InvalidOperationException
{
    internal BuiltInModelWarmingUpException(string modelId, Task<FirstUseCheckOutcome> settled)
        : base(OnThisPcEngine.GettingReadyMessage)
    {
        ModelId = modelId;
        Settled = settled;
    }

    internal string ModelId { get; }

    internal Task<FirstUseCheckOutcome> Settled { get; }
}
