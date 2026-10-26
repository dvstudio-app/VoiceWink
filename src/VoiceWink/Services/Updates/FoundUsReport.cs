using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Updates;

/// <summary>LNC-14 (privacy v8 §4, A44): sends the picked found-us answer once as <c>report/&lt;channel&gt;/found-us-&lt;tag&gt;</c>; only HTTP 204 counts.</summary>
public sealed class FoundUsReport
{
    private static ILogger Logger => Log.ForContext<FoundUsReport>();

    internal const string LogPrefix = "Found-us report: ";
    internal const string Delivered = "HTTP 204";

    private readonly SettingsService _settings;
    private readonly InstallerReport.Inputs _inputs;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly TimeSpan _budget;
    private readonly Action<string> _log;
    private int _sending;

    public FoundUsReport(SettingsService settings)
        : this(settings, InstallerReport.ProductionInputs(), InstallerReport.CreateProductionHandler, InstallerReport.Budget, WriteLog)
    {
    }

    internal FoundUsReport(
        SettingsService settings, InstallerReport.Inputs inputs, Func<HttpMessageHandler> handlerFactory, TimeSpan budget, Action<string> log)
    {
        _settings = settings;
        _inputs = inputs;
        _handlerFactory = handlerFactory;
        _budget = budget;
        _log = log;
    }

    /// <summary>Whether the License page may ask: a build that can send, no send in flight, and no answer delivered yet.</summary>
    public bool CanAsk =>
        Volatile.Read(ref _sending) == 0 && SkipReason(_inputs) is null && !_settings.GetBool(AppDefaults.FoundUsAnswered);

    /// <summary>Why this build cannot send the answer, or null when it can.</summary>
    internal static string? SkipReason(InstallerReport.Inputs inputs)
    {
        if (!inputs.UpdateCheckBuilt) return "update checks not built in";
        if (!inputs.PublicFeedConfigured) return "no public update feed";
        return null;
    }

    /// <summary>Sends the answer once; true only on <see cref="Delivered"/>, which also stops the question. Never throws.</summary>
    public async Task<bool> SendAsync(string tag)
    {
        if (!FeedbackLinks.IsFoundUsTag(tag) || !CanAsk) return false;
        if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return false;
        string outcome;
        try
        {
            try
            {
                using var deadline = new CancellationTokenSource(_budget);
                outcome = await InstallerReport
                    .SendAsync(InstallSourceReporter.ReportUri(_inputs.Channel, "found-us-" + tag), _handlerFactory, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = "failed (" + ex.GetType().Name + ")";
            }

            // A delivered answer counts even when the flag cannot be written; the question then simply returns.
            if (outcome == Delivered)
            {
                try { _settings.SetBool(AppDefaults.FoundUsAnswered, true); }
                catch (Exception ex) { outcome += ", answered flag not saved (" + ex.GetType().Name + ")"; }
            }
        }
        finally
        {
            Volatile.Write(ref _sending, 0);
        }

        var delivered = outcome.StartsWith(Delivered, StringComparison.Ordinal);
        try { _log(LogPrefix + tag + ": " + outcome); }
        catch { /* logging is best effort */ }
        return delivered;
    }

    private static void WriteLog(string line) => Logger.Information("{Line:l}", line);
}
