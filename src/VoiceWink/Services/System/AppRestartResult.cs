using global::System;
using global::System.Collections.Generic;

namespace VoiceWink.Services.System;

/// <summary>Why a restart was requested. One value today; the enum exists so the log line and any
/// later caller name the trigger rather than the mechanism.</summary>
public enum AppRestartReason
{
    /// <summary>The GPU-acceleration toggle was flipped and the user chose "Restart now" (TRN-59).</summary>
    GpuAccelerationToggle,
    /// <summary>TRN-64: Whisper refused the GPU after its self-test failed or did not finish, and
    /// the user chose "Restart now" to run it on the processor. No setting was written on this
    /// path — the pin is already in the marker — so the flush step has nothing pending.</summary>
    GpuSelfTestFailed,
}

/// <summary>What <see cref="AppRestartService.TryRestart"/> did.</summary>
public enum AppRestartOutcome
{
    /// <summary>The successor is started and the graceful quit is queued. The process is exiting.</summary>
    Initiated,
    /// <summary>Something is in flight that a restart would cut short — a recording, a transcription,
    /// a model download, an update apply, a data erasure. Nothing was changed.</summary>
    Busy,
    /// <summary>No launcher could be resolved for this process (<c>LauncherKind.Unavailable</c>).
    /// Nothing was changed; retrying cannot help.</summary>
    LauncherUnavailable,
    /// <summary>The settings could not be made durable, so a restart would have come back with the
    /// OLD value — the defect this feature exists to remove. Nothing was spawned.</summary>
    SettingsNotDurable,
    /// <summary>The successor could not be started. Nothing was quit; the settings ARE durable, so
    /// the change still applies at the next start.</summary>
    LaunchFailed,
}

/// <summary>
/// The outcome of one restart attempt, with the sentence the dialog shows after a refusal. Pure and
/// case-tabled so the copy for every refusal is pinned without a WinUI host.
/// </summary>
public sealed record AppRestartResult(AppRestartOutcome Outcome, IReadOnlyList<string> Blockers, string? Detail)
{
    public static AppRestartResult Initiated { get; } = new(AppRestartOutcome.Initiated, Array.Empty<string>(), null);

    public static AppRestartResult BusyWith(IReadOnlyList<string> blockers)
        => new(AppRestartOutcome.Busy, blockers, null);

    public static AppRestartResult LauncherUnavailable { get; } = new(AppRestartOutcome.LauncherUnavailable, Array.Empty<string>(), null);

    public static AppRestartResult SettingsNotDurable(string? detail)
        => new(AppRestartOutcome.SettingsNotDurable, Array.Empty<string>(), detail);

    public static AppRestartResult LaunchFailed(string? detail)
        => new(AppRestartOutcome.LaunchFailed, Array.Empty<string>(), detail);

    /// <summary>Whether "Restart now" can sensibly be pressed again. Busy clears when the work ends;
    /// a settings or launch failure may have been transient; an unresolvable launcher will not
    /// change by pressing the button.</summary>
    public bool CanRetry => Outcome is AppRestartOutcome.Busy or AppRestartOutcome.SettingsNotDurable or AppRestartOutcome.LaunchFailed;

    /// <summary>The refusal sentence, or <c>null</c> for <see cref="AppRestartOutcome.Initiated"/>.
    /// Every refusal says what the user can do. Busy and LaunchFailed also say that "Later" still
    /// works — the setting IS written (the flush precedes the spawn), and the refusal sentence
    /// itself says it applies at the next start (UI-12 removed the row's standing copy). <see cref="Detail"/> is path-free by construction
    /// (<c>AppRestartService.DescribeError</c>).</summary>
    public string? Describe()
    {
        var detail = string.IsNullOrWhiteSpace(Detail) ? "" : $" ({Detail})";
        return Outcome switch
        {
            AppRestartOutcome.Initiated => null,
            AppRestartOutcome.Busy =>
                $"VoiceWink is busy ({string.Join(", ", Blockers)}). Wait for it to finish and try again, " +
                "or choose Later — the change still applies the next time VoiceWink starts.",
            AppRestartOutcome.LauncherUnavailable =>
                "VoiceWink couldn't work out how to start itself again. Close VoiceWink and open it " +
                "again to apply the change.",
            AppRestartOutcome.SettingsNotDurable =>
                $"The setting couldn't be saved{detail}, so VoiceWink wasn't restarted. Try again.",
            AppRestartOutcome.LaunchFailed =>
                $"VoiceWink couldn't start a new copy of itself{detail}. Try again, or choose Later — " +
                "the change still applies the next time VoiceWink starts.",
            _ => "VoiceWink couldn't restart. Close VoiceWink and open it again to apply the change.",
        };
    }
}
