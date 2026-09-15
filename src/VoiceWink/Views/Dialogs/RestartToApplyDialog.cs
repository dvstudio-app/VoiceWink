using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// TRN-59: the offer that follows a flip of the GPU-acceleration toggle — the setting is already
/// written when this opens; the dialog only decides WHEN it applies. "Restart now" runs
/// <see cref="AppRestartService.TryRestart"/>; "Later" leaves the setting to apply at the next
/// start, which this dialog is now the only place that says (UI-12 removed the row's standing copy). A refusal keeps the dialog OPEN with the outcome's sentence under
/// the body, and "Restart now" stays enabled only where pressing it again can help
/// (<see cref="AppRestartResult.CanRetry"/>) — a recording ends, a transient failure clears; an
/// unresolvable launcher does not. A throw out of the service is a refusal too, never an unhandled
/// exception: the click handler runs inside WinUI's callback, where the page's own catch around
/// <c>ShowAsync</c> cannot see it.
///
/// <para>Plain <c>ContentDialog</c>, which the app already ships in several places — no templated
/// control the CLI build has not rendered. All UI in code-behind, per the WinUI 3 constraints.
/// Nothing here writes a setting.</para>
/// </summary>
public sealed class RestartToApplyDialog : ContentDialog
{
    private static ILogger Logger => Log.ForContext<RestartToApplyDialog>();

    private readonly AppRestartService _restart;
    private readonly AppRestartReason _reason;
    private readonly TextBlock _status;

    /// <summary>The toggle's offer (TRN-59) — body from the flip's direction.</summary>
    public RestartToApplyDialog(AppRestartService restart, bool gpuAccelerationOn)
        : this(restart, BodyText(gpuAccelerationOn), AppRestartReason.GpuAccelerationToggle)
    {
    }

    /// <summary>TRN-64: the same dialog with the body a caller supplies — the self-test refusal's
    /// offer (Codex r2 F4: the toggle sentence "GPU acceleration is now on/off" cannot present a
    /// detected failure). The reason reaches <see cref="AppRestartService.TryRestart"/>'s log line.</summary>
    public RestartToApplyDialog(AppRestartService restart, string bodyText, AppRestartReason reason)
    {
        _restart = restart;
        _reason = reason;

        Title = "Restart VoiceWink?";
        PrimaryButtonText = "Restart now";
        CloseButtonText = "Later";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;

        _status = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = bodyText,
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(_status);
        Content = body;

        PrimaryButtonClick += OnRestartNow;
    }

    /// <summary>The body: what changed, that it needs a restart, and that "Later" is a real choice.</summary>
    private static string BodyText(bool gpuAccelerationOn)
        => $"GPU acceleration is now {(gpuAccelerationOn ? "on" : "off")}. It takes effect the next " +
           "time VoiceWink starts. Restart now, or choose Later and it applies the next time you open VoiceWink.";

    /// <summary>TRN-64: the body for a refused Whisper decode — what the check found, that the
    /// restart is the FIX rather than a chore, and what still works meanwhile. App-authored; the
    /// only device-derived text is the adapter model name.
    ///
    /// <para>UI-13 rewrote the middle sentence (owner-reported 2026-09-04, from the first live
    /// failure — both engines scoring 0 of 9 words on an Adreno X1-85). It read "Whisper won't
    /// use the graphics card again until VoiceWink restarts on the processor", and said the
    /// opposite of what happens twice over: "until" promises the graphics card comes BACK after
    /// the restart, when the restart is precisely what ends its use for good, and "restarts on
    /// the processor" attaches to where the restart happens rather than where Whisper lands. The
    /// replacement names the restart as the cure. <c>RestartToApplyDialogTests</c> pins the
    /// PROPERTIES, not the prose — this copy had no test at all before it drifted.</para>
    ///
    /// <para><b>It names no destination, and that is the whole of Codex's r1+r2 Blocker.</b> Two
    /// drafts claimed one — "the restart moves it to the processor, where it runs correctly", then
    /// "Restart to move it to the processor" — and both are promises this code cannot keep.
    /// <c>GpuWarmupMarker.Write</c> is best-effort and returns false on failure;
    /// <c>GpuWarmup.RecordWhisperVerdict</c> deliberately keeps the pin in memory anyway (right: a
    /// session that has proved the GPU wrong must not keep decoding on it because a file would not
    /// write), and only the log LEVEL consumes that durability bool (REL-30). So on an unwritable
    /// marker the next start reads no pin, <c>App</c> re-enables Vulkan, and the user restarts into
    /// this same dialog. The second draft was defended here as "purpose, not outcome"; that
    /// distinction does not survive contact with a reader, for whom "restart to move it to the
    /// processor" simply says the restart moves it there.</para>
    ///
    /// <para>"Restart VoiceWink to try Whisper again" is true in BOTH worlds — the try succeeds on
    /// the processor, or it fails and says so again — which is the most this sentence can honestly
    /// carry while the durability gap stands. That gap is [[TRN-66]], and its fix (carry the pin to
    /// the successor the way <c>RestartHandoff</c> carries the predecessor pid) is what would earn
    /// the destination back. Dropping it also retires a second overclaim: on Inconclusive nothing
    /// was ever proved wrong about the GPU, so joining finding to fix in one causal clause said
    /// more than the check knew.</para></summary>
    internal static string GpuSelfTestBody(GpuSelfTestRefusedException refusal)
    {
        var gpu = refusal.GpuName ?? GpuToggleAvailability.UnnamedGpu;
        // Only Fail earns the accusation, and the test is written that way round on purpose: every
        // other kind — Inconclusive today, anything added later — falls to the neutral sentence.
        // "produces wrong text on this PC's <adapter>" is a claim about the user's hardware, and a
        // check that did not prove it must never make it. The arms were the other way up until
        // UI-13, so an Unstable refusal reaching here would have accused a GPU nothing had measured.
        var finding = refusal.Kind == GpuSelfTestRefusedException.Reason.Fail
            ? $"The GPU check found that Whisper produces wrong text on this PC's {gpu}."
            : $"The GPU check for Whisper did not finish on this PC's {gpu}.";
        return finding + " Whisper won't transcribe again this session. Restart VoiceWink to try " +
               "Whisper again. Later is fine: Parakeet and cloud models keep working, and your " +
               "recording is kept for a retry.";
    }

    private void OnRestartNow(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        AppRestartResult result;
        try
        {
            result = _restart.TryRestart(_reason);
        }
        catch (Exception ex)
        {
            // The service releases its lease on any throw; here the throw only has to become a
            // refusal the user can read rather than an unhandled exception that ends the process.
            Logger.Warning(ex, "Restart now failed unexpectedly; the change applies at the next start");
            result = AppRestartResult.LaunchFailed(ex.GetType().Name);
        }

        if (result.Outcome == AppRestartOutcome.Initiated)
        {
            // The dialog closes; the graceful quit is queued on the dispatcher behind it.
            return;
        }

        // Refused: stay open and say why. The setting is written either way.
        args.Cancel = true;
        _status.Text = result.Describe() ?? "";
        _status.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = result.CanRetry;
    }
}
