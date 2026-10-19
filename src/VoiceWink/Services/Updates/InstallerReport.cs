using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoiceWink.Helpers;
using VoiceWink.Services.Http;

namespace VoiceWink.Services.Updates;

/// <summary>
/// The installer's report (Privacy Policy §4.5, addendum A39; §4.6 for the website): ONE attempt, from
/// Velopack's install hook, by every build on a public channel — the Store channel's build at
/// <c>https://updates.voicewink.app/&lt;channel&gt;/install-source/msstore/installer</c>, any other public
/// build (the website's) at <c>https://updates.voicewink.app/report/&lt;channel&gt;/installer</c>.
///
/// <para><b>Where it runs.</b> Inside <c>OnAfterInstallFastCallback</c> — after Velopack has placed the
/// files and before the installation has finished, so it does not confirm that the installation
/// succeeded; it runs before VoiceWink first starts, and therefore before first-run setup offers the
/// automatic-check choice. Pure managed code: no WinUI, no DI container, no Serilog (none exists in the
/// hook). It runs on EVERY execution of a public channel's Setup — a Store install, a website download, a
/// Microsoft certification run, a reinstall — and never on an update
/// (<c>OnAfterUpdateFastCallback</c> does not call it).</para>
///
/// <para><b>Gates, all before any network</b> (<see cref="SkipReason"/>): update checks built in
/// (<see cref="UpdateCheckFeature.IsEnabled"/>); a configured, public feed; and settings left by an earlier installation — read through
/// the one pre-boot reader — that do not say automatic checks are off and are not unreadable. A first
/// install has no settings file and sends; a file without the key sends (the shipped default is on).</para>
///
/// <para><b>The request.</b> <c>GET</c>, <c>User-Agent: VoiceWink</c> (the first-check report's constant),
/// no redirects, no cookies, connect bounded at <see cref="ConnectBudget"/>, the WHOLE attempt bounded at
/// <see cref="Budget"/> measured from <see cref="Start"/>, headers only. Any HTTP status ends it; it is
/// never retried. Every exception is caught — a failure to send never affects the installation.</para>
///
/// <para><b>The one trace.</b> Exactly one line in <c>install.log</c> (<c>PrereqInstaller.LogPublic</c>),
/// written by <see cref="Pending.Join"/>: <c>installer report: HTTP &lt;n&gt;</c>,
/// <c>installer report: failed (&lt;ExceptionType&gt;)</c> or <c>installer report: skipped (&lt;why&gt;)</c>
/// — never an address. The privacy text discloses this line.</para>
/// </summary>
internal static class InstallerReport
{
    /// <summary>Under the channel folder; the first-check report's path plus <c>/installer</c>.</summary>
    public const string ReportPath = InstallSourceReporter.ReportPath + "/installer";

    /// <summary>The whole attempt, from <see cref="Start"/> to the end of <see cref="Pending.Join"/>'s wait.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>DNS + TCP + TLS, inside <see cref="Budget"/>.</summary>
    public static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(3);

    internal const string LogPrefix = "installer report: ";

    /// <summary>The outcome when the attempt had not ended by the deadline, or ended BY it.</summary>
    internal const string TimedOut = "failed (TimeoutException)";

    /// <summary>The report's address: the Store channel's frozen path, or the §4.6 website path for any other public channel.</summary>
    public static Uri BuildUri(string channel, bool isStoreChannel) => isStoreChannel
        ? new($"https://updates.voicewink.app/{channel}/{ReportPath}", UriKind.Absolute)
        : InstallSourceReporter.ReportUri(channel, "installer");

    /// <summary>
    /// Why the report is not sent, or null when it is. The cheap structural gates first; the settings
    /// file is read only once they all pass (<paramref name="readAutomaticChecks"/> is not called
    /// otherwise), so a dev or tester build never opens it.
    /// </summary>
    internal static string? SkipReason(
        bool updateCheckBuilt,
        bool publicFeedConfigured,
        Func<PreBootSetting> readAutomaticChecks)
    {
        if (!updateCheckBuilt) return "update checks not built in";
        if (!publicFeedConfigured) return "no public update feed";
        return readAutomaticChecks() switch
        {
            PreBootSetting.False => "automatic update checks are off",
            PreBootSetting.Unreadable => "settings unreadable",
            _ => null,
        };
    }

    /// <summary>The inputs of one attempt, so tests can drive every gate without the build's constants.</summary>
    internal sealed record Inputs(
        bool UpdateCheckBuilt,
        bool PublicFeedConfigured,
        bool IsStoreChannel,
        string Channel,
        string SettingsFilePath);

    /// <summary>This build's inputs. Passing the constants as VALUES keeps a const <c>if</c> (CS0162) out of the code.</summary>
    internal static Inputs ProductionInputs() => new(
        UpdateCheckFeature.IsEnabled,
        UpdateFeedConfig.IsConfigured && !UpdateFeedConfig.FeedIsGated,
        UpdateFeedConfig.IsStoreChannel,
        UpdateFeedConfig.Channel,
        AppPaths.SettingsFile);

    /// <summary>The production handler: the shared install-report socket policy, <see cref="ConnectBudget"/>.</summary>
    internal static HttpMessageHandler CreateProductionHandler() =>
        VoiceWinkHttpClients.ConfigureInstallReportHandler(new SocketsHttpHandler(), ConnectBudget);

    /// <summary>The install hook's entry: starts the attempt and returns at once. Never throws.</summary>
    public static Pending Start() =>
        Start(ProductionInputs(), CreateProductionHandler, PrereqInstaller.LogPublic, Budget);

    internal static Pending Start(Inputs inputs, Func<HttpMessageHandler> handlerFactory, Action<string> log, TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = new CancellationTokenSource();
        Task<string> outcome;
        try
        {
            deadline.CancelAfter(budget);
            outcome = Task.Run(() => RunAsync(inputs, handlerFactory, deadline.Token));
        }
        catch (Exception ex)
        {
            outcome = Task.FromResult("failed (" + ex.GetType().Name + ")");
        }
        return new Pending(outcome, started, budget, deadline, log);
    }

    private static async Task<string> RunAsync(Inputs inputs, Func<HttpMessageHandler> handlerFactory, CancellationToken deadline)
    {
        try
        {
            var skip = SkipReason(
                inputs.UpdateCheckBuilt,
                inputs.PublicFeedConfigured,
                () => PreBootSettingReader.Read(inputs.SettingsFilePath, AppDefaults.AutomaticUpdateCheckEnabled));
            if (skip is not null) return "skipped (" + skip + ")";

            using var client = new HttpClient(handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(inputs.Channel, inputs.IsStoreChannel));
            request.Headers.TryAddWithoutValidation("User-Agent", InstallSourceReporter.UserAgent);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline)
                .ConfigureAwait(false);
            return "HTTP " + ((int)response.StatusCode).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return TimedOut;
        }
        catch (Exception ex)
        {
            return "failed (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>A started attempt. <see cref="Join"/> waits for what is left of the budget and writes the one line.</summary>
    internal sealed class Pending
    {
        private readonly Task<string> _outcome;
        private readonly long _started;
        private readonly TimeSpan _budget;
        private readonly CancellationTokenSource _deadline;
        private readonly Action<string> _log;

        internal Pending(Task<string> outcome, long started, TimeSpan budget, CancellationTokenSource deadline, Action<string> log)
        {
            _outcome = outcome;
            _started = started;
            _budget = budget;
            _deadline = deadline;
            _log = log;
        }

        /// <summary>
        /// Waits ONLY for what is left of the budget since <see cref="Start"/> — after the self-heal and the
        /// runtime install that is usually nothing — then writes exactly one <c>install.log</c> line. An
        /// attempt still running is cancelled and logged as a timeout. Never throws.
        /// </summary>
        public void Join()
        {
            string line;
            try
            {
                var remaining = _budget - Stopwatch.GetElapsedTime(_started);
                var done = _outcome.IsCompleted
                    || (remaining > TimeSpan.Zero && _outcome.Wait(remaining));
                if (done)
                {
                    line = _outcome.Result;
                    _deadline.Dispose();
                }
                else
                {
                    _deadline.Cancel();
                    // An attempt that completed between the wait and the cancel still reports what it got.
                    line = _outcome.IsCompleted ? _outcome.Result : TimedOut;
                }
            }
            catch (Exception ex)
            {
                line = "failed (" + (ex is AggregateException { InnerException: { } inner } ? inner : ex).GetType().Name + ")";
            }

            try { _log(LogPrefix + line); }
            catch { /* the install log is best effort; the installation must not fail on it */ }
        }
    }
}
