using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoiceWink.Helpers;
using VoiceWink.Services.Http;

namespace VoiceWink.Services.Updates;

/// <summary>
/// The installation report's address and the one request the app's reports share
/// (<see cref="WebsiteInstallReport"/>, <see cref="FoundUsReport"/>).
///
/// <para><b>The installer sends nothing since privacy v9</b> (A45, owner 2026-10-25): the Store channel's
/// build used to send its report from Velopack's install hook, before VoiceWink first started. Every public
/// build's report is now the app's, once, right after the privacy policy is accepted. The Store channel keeps
/// its frozen address (<c>&lt;channel&gt;/install-source/msstore/installer</c>), so the Worker and the
/// metrics read it unchanged; the website's stays <c>report/&lt;channel&gt;/installer</c>.</para>
///
/// <para><b>The request.</b> <c>GET</c>, <c>User-Agent: VoiceWink</c> (the first-check report's constant),
/// no redirects, no cookies, connect bounded at <see cref="ConnectBudget"/>, the whole attempt bounded by the
/// caller's deadline, headers only. Any HTTP status ends it; it is never retried. Every exception is caught.</para>
/// </summary>
internal static class InstallerReport
{
    /// <summary>Under the channel folder; the first-check report's path plus <c>/installer</c>.</summary>
    public const string ReportPath = InstallSourceReporter.ReportPath + "/installer";

    /// <summary>The whole attempt.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>DNS + TCP + TLS, inside <see cref="Budget"/>.</summary>
    public static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(3);

    /// <summary>The outcome when the attempt had not ended by the deadline.</summary>
    internal const string TimedOut = "failed (TimeoutException)";

    /// <summary>The report's address: the Store channel's frozen path, or the §4.6 path for any other public channel.</summary>
    public static Uri BuildUri(string channel, bool isStoreChannel) => isStoreChannel
        ? new($"https://updates.voicewink.app/{channel}/{ReportPath}", UriKind.Absolute)
        : InstallSourceReporter.ReportUri(channel, "installer");

    /// <summary>The inputs of one attempt, so tests can drive every gate without the build's constants.</summary>
    internal sealed record Inputs(
        bool UpdateCheckBuilt,
        bool PublicFeedConfigured,
        bool IsStoreChannel,
        string Channel);

    /// <summary>This build's inputs. Passing the constants as VALUES keeps a const <c>if</c> (CS0162) out of the code.</summary>
    internal static Inputs ProductionInputs() => new(
        UpdateCheckFeature.IsEnabled,
        UpdateFeedConfig.IsConfigured && !UpdateFeedConfig.FeedIsGated,
        UpdateFeedConfig.IsStoreChannel,
        UpdateFeedConfig.Channel);

    /// <summary>The production handler: the shared install-report socket policy, <see cref="ConnectBudget"/>.</summary>
    internal static HttpMessageHandler CreateProductionHandler() =>
        VoiceWinkHttpClients.ConfigureInstallReportHandler(new SocketsHttpHandler(), ConnectBudget);

    /// <summary>
    /// The one request every report makes: <c>GET</c>, <c>User-Agent: VoiceWink</c>, headers only, any
    /// status ends it, never retried. Returns the outcome (<c>HTTP &lt;n&gt;</c>, <see cref="TimedOut"/> or
    /// <c>failed (&lt;ExceptionType&gt;)</c>), never an address. Never throws.
    /// </summary>
    internal static async Task<string> SendAsync(Uri uri, Func<HttpMessageHandler> handlerFactory, CancellationToken deadline)
    {
        try
        {
            using var client = new HttpClient(handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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
}
