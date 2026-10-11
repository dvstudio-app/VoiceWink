using System;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Velopack's install hook as one ordered, testable sequence: start the installer's report (it runs in
/// parallel and returns at once), heal Update.exe, install the Windows App Runtime, then join the report
/// for at most what is left of its budget. The report's start and join are contained — neither can throw
/// out of the hook or stop the two install steps — and the join runs even when an install step throws, so
/// its line is written either way. The two install steps keep their own contracts (each never throws).
/// </summary>
internal static class AfterInstallHook
{
    /// <summary>The production hook body (<c>Program.Main</c>'s <c>OnAfterInstallFastCallback</c>).</summary>
    public static void Run() => Run(
        InstallerReport.Start,
        UpdateExeSelfHeal.EnsureDeployed,
        static () => PrereqInstaller.InstallAlwaysWithBudget(25_000),
        static pending => pending.Join());

    internal static void Run<T>(Func<T> startReport, Action selfHeal, Action prereq, Action<T> joinReport)
        where T : class
    {
        T? pending = null;
        try { pending = startReport(); }
        catch { /* a report that cannot start is a report not sent; the install goes on */ }

        try
        {
            selfHeal();
            prereq();
        }
        finally
        {
            if (pending is not null)
            {
                try { joinReport(pending); }
                catch { /* never fail the install on the report */ }
            }
        }
    }
}
