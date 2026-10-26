using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Every public build's installation report (A42 D1 for the website, owner 2026-10-19; A45 for the
/// Microsoft Store, owner 2026-10-25, privacy v9 §4): sent by the app, ONCE, right after the user accepts
/// the privacy policy in first-run setup, to <see cref="InstallerReport.BuildUri"/> — the website's
/// <c>report/&lt;channel&gt;/installer</c> or the Store channel's frozen
/// <c>&lt;channel&gt;/install-source/msstore/installer</c>. Until then the installer sent it during
/// installation, before the user had seen the policy. Not sent when the user does not accept.
///
/// <para><b>Not tied to the automatic-update setting</b> (D1): nothing here reads it. The user's
/// objection right (policy §10) is the way to stop it.</para>
///
/// <para><b>"Once" is the caller's half:</b> <c>OnboardingPage</c> calls <see cref="SendAfterFirstAcceptance"/>
/// only when no acceptance was recorded before (<c>LegalAcceptanceState.IsFirstAcceptance</c>) and only
/// after the acceptance write succeeded. A re-run of setup, the re-accept prompt for a changed policy and
/// a reinstall that kept its settings all have a recorded acceptance and send nothing. Stated limits:
/// "Delete all app data" deletes the recorded acceptance, so the next setup's acceptance sends again;
/// and the one attempt is not persisted, so closing the app within its 5 s (or no network then) means
/// it is never sent — the privacy-safe direction (Kimi diff r1 A4).</para>
///
/// <para><b>The request</b> is <see cref="InstallerReport.SendAsync"/>'s, through the same handler: one
/// <c>GET</c>, <c>User-Agent: VoiceWink</c>, no redirects, no cookies, bounded at
/// <see cref="InstallerReport.Budget"/>, never retried. One log line, never an address.</para>
/// </summary>
internal static class WebsiteInstallReport
{
    private static ILogger Logger => Log.ForContext(typeof(WebsiteInstallReport));

    internal const string LogPrefix = "Installation report: ";

    /// <summary>Why the report is not sent, or null when it is. No setting is read (D1).</summary>
    internal static string? SkipReason(bool updateCheckBuilt, bool publicFeedConfigured, bool installed)
    {
        if (!updateCheckBuilt) return "update checks not built in";
        if (!publicFeedConfigured) return "no public update feed";
        if (!installed) return "not an installed copy";
        return null;
    }

    /// <summary>
    /// Starts the one attempt on the thread pool and returns at once. Never throws. Call only after the
    /// first acceptance of the privacy policy was saved.
    /// </summary>
    public static void SendAfterFirstAcceptance()
    {
        try
        {
            var inputs = InstallerReport.ProductionInputs();
            var installed = InstallRoot.FromBaseDirectory(AppContext.BaseDirectory) is not null;
            _ = Task.Run(() => RunAsync(inputs, installed, InstallerReport.CreateProductionHandler, InstallerReport.Budget, WriteLog));
        }
        catch (Exception ex)
        {
            WriteLog(LogPrefix + "failed (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>One attempt; returns the logged outcome. Never throws.</summary>
    internal static async Task<string> RunAsync(
        InstallerReport.Inputs inputs, bool installed, Func<HttpMessageHandler> handlerFactory, TimeSpan budget, Action<string> log)
    {
        string outcome;
        try
        {
            var skip = SkipReason(inputs.UpdateCheckBuilt, inputs.PublicFeedConfigured, installed);
            if (skip is not null)
            {
                outcome = "skipped (" + skip + ")";
            }
            else
            {
                using var deadline = new CancellationTokenSource(budget);
                outcome = await InstallerReport
                    .SendAsync(InstallerReport.BuildUri(inputs.Channel, inputs.IsStoreChannel), handlerFactory, deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            outcome = "failed (" + ex.GetType().Name + ")";
        }

        try { log(LogPrefix + outcome); }
        catch { /* logging is best effort */ }
        return outcome;
    }

    private static void WriteLog(string line) => Logger.Information("{Line:l}", line);
}
