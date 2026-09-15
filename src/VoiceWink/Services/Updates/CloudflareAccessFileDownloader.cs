using System.Collections.Generic;
using System.Net.Http;
using Velopack.Sources;

namespace VoiceWink.Services.Updates;

/// <summary>
/// <see cref="HttpClientFileDownloader"/> that injects the Cloudflare Access service-token
/// headers (<c>CF-Access-Client-Id</c> / <c>CF-Access-Client-Secret</c>) on <b>every</b>
/// request — the release-feed manifest, full packages, and deltas alike.
///
/// <para><see cref="SimpleWebSource"/> routes all of those through this downloader's
/// <see cref="HttpClientFileDownloader.CreateHttpClient"/> factory (proven for the manifest
/// fetch by <c>VelopackManifestAuthProbeTests</c>), so a single override authenticates the
/// whole gated private tester feed.</para>
///
/// <para>On a PUBLIC channel build (UPD-7) both baked credentials are empty, so this same class
/// attaches nothing and the feed is read ungated — the release pipeline's step 3b verifies the
/// token is absent from the published binary. No second downloader exists for that case on
/// purpose: one class, one behaviour, pinned by <c>CloudflareAccessFileDownloaderTests</c>.</para>
///
/// <para>The credentials are build-time-baked (see <c>UpdateFeedConfig</c>). They are NOT a
/// substitute for the launch license gate: an embedded service token is extractable from the
/// binary, which is acceptable for a trusted pre-launch ring (it defeats random public
/// discovery) but is obscurity-plus, not DRM.</para>
/// </summary>
internal sealed class CloudflareAccessFileDownloader : HttpClientFileDownloader
{
    private const string ClientIdHeader = "CF-Access-Client-Id";
    private const string ClientSecretHeader = "CF-Access-Client-Secret";

    private readonly string _clientId;
    private readonly string _clientSecret;

    public CloudflareAccessFileDownloader(string clientId, string clientSecret)
    {
        _clientId = clientId ?? string.Empty;
        _clientSecret = clientSecret ?? string.Empty;
    }

    protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers, double timeout)
    {
        var client = base.CreateHttpClient(headers, timeout);

        // Only attach when both are present — an empty pair (dev build) leaves the client
        // un-gated rather than sending blank credentials that Cloudflare would reject.
        if (_clientId.Length > 0 && _clientSecret.Length > 0)
        {
            // Remove-then-add is defensive against a base implementation that already set them.
            client.DefaultRequestHeaders.Remove(ClientIdHeader);
            client.DefaultRequestHeaders.Remove(ClientSecretHeader);
            client.DefaultRequestHeaders.Add(ClientIdHeader, _clientId);
            client.DefaultRequestHeaders.Add(ClientSecretHeader, _clientSecret);
        }

        return client;
    }
}
