using System;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Velopack's install hook as one ordered, testable sequence: heal Update.exe, then install the Windows App
/// Runtime. Each step keeps its own contract (never throws). The installer sends no report since privacy v9
/// (A45): every public build's installation report is the app's, after the privacy policy is accepted
/// (<see cref="WebsiteInstallReport"/>).
/// </summary>
internal static class AfterInstallHook
{
    /// <summary>The production hook body (<c>Program.Main</c>'s <c>OnAfterInstallFastCallback</c>).</summary>
    public static void Run() => Run(
        UpdateExeSelfHeal.EnsureDeployed,
        static () => PrereqInstaller.InstallAlwaysWithBudget(25_000));

    internal static void Run(Action selfHeal, Action prereq)
    {
        selfHeal();
        prereq();
    }
}
