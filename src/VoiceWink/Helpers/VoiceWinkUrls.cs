namespace VoiceWink.Helpers;

/// <summary>
/// External URLs the app links to — centralized so the CTAs move with one edit
/// when the LemonSqueezy merchant profile or marketing site is restructured.
///
/// <para>The <c>voicewink.app</c> marketing domain is the stable surface. The
/// <c>/</c>, <c>/buy</c>, and <c>/byok-setup</c> paths are a frozen contract owned by the
/// <c>voicewink-site</c> web repo — they are baked into every shipped installer and must never
/// be renamed. (<c>/trial</c> was one of them until LIC-21, 2026-09-06: no build after v1.76.368
/// emits it, and the site keeps the route answering only for the installers already in the
/// field — it is not a reason to restore a trial-key URL here.) Per the SITE-1 decision
/// (docs/plans/2026-06-17-2227-voicewink-marketing-commerce-site/50-decision.md),
/// <c>/buy</c> is a <b>302 to <c>/pricing</c></b> (not a 301 to a checkout): tier
/// selection happens on the pricing page, and the per-variant LS checkout URLs live
/// only in the site repo. Shipping a direct LS variant URL from the app would
/// hard-code the current variant id into every old installer forever.</para>
///
/// <para><b>The <c>?src=app-…</c> query string is the link's source tag</b> (2026-09-18). A click from
/// inside a desktop app reaches the browser with no referrer, so on the site's own analytics every
/// visit from these links read as "Direct" — indistinguishable from a typed address. The site's
/// first-party beacon reads the <c>src</c> parameter on the page it lands on and files the visit
/// under it; the tag must be one of the beacon's closed list (dvstudio-metrics
/// <c>beacon/src/lib.js</c>, <c>SOURCE_TAGS</c> — <c>app</c>, <c>app-buy</c>, <c>app-byok</c>,
/// <c>app-reinstall</c>), anything else is discarded by the site, so a new tag here is a Worker
/// deploy there first. The tag names where the link was published, never the user; the website's
/// Cookie Notice (v5) and Privacy Policy §5 say so. The site's <c>/buy</c> redirect keeps the query
/// string (measured 2026-09-18: <c>/buy?src=probe</c> answers <c>302 /?src=probe#pricing</c>, and the
/// website's deploy smoke pins it). The PATHS stay the frozen contract above; the query string is not
/// part of it.</para>
/// </summary>
internal static class VoiceWinkUrls
{
    /// <summary>Marketing landing page.</summary>
    public const string Marketing = "https://voicewink.app/?src=app";

    /// <summary>
    /// Buy-a-license CTA. Once <c>voicewink.app/buy</c> is wired up it 302s at the web
    /// layer to <c>/pricing</c>, where the per-variant LemonSqueezy checkout links live —
    /// avoiding a hard-coded variant URL baked into old installers. The mapping is owned
    /// by the <c>voicewink-site</c> repo (see the class remarks).
    /// </summary>
    public const string Buy = "https://voicewink.app/buy?src=app-buy";

    // No trial-key URL: the free trial is local and needs no key (LIC-21, owner decision
    // 2026-09-06 — the Lemon Squeezy $0 trial variant and its /trial route were retired; free keys
    // for testers are 100 %-off discount codes on the paid variants, handed out by the owner).

    /// <summary>User-facing BYOK / self-serve setup guide.</summary>
    public const string ByokSetup = "https://voicewink.app/byok-setup?src=app-byok";

    /// <summary>
    /// The install page, opened by the legal-bundle dialog's "exit and reinstall" button. Tagged
    /// <c>app-reinstall</c> so those arrivals can be told from the ordinary install traffic.
    /// </summary>
    public const string Reinstall = "https://voicewink.app/install?src=app-reinstall";

    /// <summary>Support contact (REL-3 "Report a problem"). Routed via the DV Studio domain.</summary>
    public const string SupportEmail = "support@dvstudio.app";

    /// <summary>
    /// LemonSqueezy self-serve order lookup — where users recover a lost license key
    /// via the email they purchased with.
    /// </summary>
    public const string LostKey = "https://app.lemonsqueezy.com/my-orders";

    /// <summary>
    /// LemonSqueezy customer portal — subscription / receipts / email update.
    /// Until the LS merchant profile is provisioned, this forwards to
    /// <see cref="LostKey"/> so there's a single source of truth for the placeholder
    /// URL (no silent drift between two "same today, different tomorrow" constants).
    /// Swap to its own const when the real portal slug lands.
    /// </summary>
    public static string CustomerPortal => LostKey;
}
