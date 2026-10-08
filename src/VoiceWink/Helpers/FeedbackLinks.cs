using VoiceWink.Services.Support;

namespace VoiceWink.Helpers;

/// <summary>
/// LNC-14 (sales plan v4 task 1.4): when the app offers its rating, review and feedback links, as pure
/// decisions, plus the links' text and the mails they open. Three surfaces read it:
/// <list type="bullet">
/// <item>(a) Home — one neutral line, "Rate VoiceWink in the Microsoft Store", for a Microsoft Store
/// install after <see cref="StoreRatingAfterDaysOfUse"/> days of use, shown once (the state lives in
/// <see cref="StoreRatingPrompt"/>);</item>
/// <item>(b) the License page's Activated panel — how did you find VoiceWink, star on GitHub, leave a
/// review (after <see cref="ReviewAfterDaysOfUse"/> days of use), tell a friend, copy the link;</item>
/// <item>(c) the License page's key box once the free trial has ended — "Not buying? Tell me why in one
/// line".</item>
/// </list>
///
/// <para><b>No condition here reads how the user feels about VoiceWink</b> — install source, days of use,
/// and the licence panel only. Asking only the satisfied for a rating is the review gating the Microsoft
/// Store policy, the FTC and the EU's UCPD rules name; there is no satisfaction question anywhere, and no
/// reward. <b>The app sends nothing:</b> every link opens the Store, the browser or the user's own mail
/// app, and the user finishes it there (a mail the app sent would be advertising e-mail under Belgian law,
/// WER XII.13).</para>
///
/// <para>Owner decisions of 2026-10-06: "Leave a review" opens the Microsoft Store listing for every
/// install, Store or website; the shared link is <see cref="VoiceWinkUrls.Marketing"/>, whose
/// <c>?src=app</c> is the site's existing tag for links published inside the app.</para>
/// </summary>
public static class FeedbackLinks
{
    /// <summary>Days of use before the one-time Store rating line can appear ("about seven").</summary>
    public const int StoreRatingAfterDaysOfUse = 7;

    /// <summary>Days of use before "Leave a review" appears on the License page ("about five").</summary>
    public const int ReviewAfterDaysOfUse = 5;

    public const string StoreRatingText = "Rate VoiceWink in the Microsoft Store";
    public const string FoundUsText = "How did you find VoiceWink?";
    public const string StarOnGitHubText = "Star on GitHub";
    public const string LeaveReviewText = "Leave a review";
    public const string TellAFriendText = "Tell a friend";
    public const string CopyLinkText = "Copy link";
    public const string WhyNotBuyingText = "Not buying? Tell me why in one line";

    public const string FoundUsSubject = "How I found VoiceWink";
    public const string WhyNotBuyingSubject = "Why I'm not buying VoiceWink";
    public const string TellAFriendSubject = "VoiceWink";

    /// <summary>The Tell-a-friend mail's whole body: what VoiceWink is, and where it is.</summary>
    public static string TellAFriendBody => $"VoiceWink, voice-to-text for Windows: {VoiceWinkUrls.Marketing}";

    /// <summary>
    /// (a) Whether the Store rating line may appear now: a Microsoft Store install, enough days of use,
    /// and never seen before.
    /// </summary>
    public static bool StoreRatingLineDue(bool isStoreInstall, int daysOfUse, bool alreadyShown) =>
        isStoreInstall && daysOfUse >= StoreRatingAfterDaysOfUse && !alreadyShown;

    /// <summary>
    /// (b) and (c) for the License page, beside <see cref="LicensePanelPlan.Resolve"/> — the page renders
    /// what comes back and decides nothing itself (the LIC-13 rule).
    /// </summary>
    /// <param name="plan">The page's resolved panel plan.</param>
    /// <param name="daysOfUse">Days of use so far (<c>UsageDays.Count</c>).</param>
    public static LicenseFeedbackPlan ForLicensePanel(LicensePanelPlan plan, int daysOfUse)
    {
        // "After purchase" is the Activated panel. OfflineGrace holds a bought key too, but its panel is
        // a warning about an unchecked licence, not the place for these.
        var purchased = plan.Kind == LicensePanelKind.Activated;
        return new LicenseFeedbackPlan(
            ShowsPurchaseLinks: purchased,
            ShowsReview: purchased && daysOfUse >= ReviewAfterDaysOfUse,
            // The used-up free trial's key box — the plan's own decision, the one that makes Buy lead
            // there, so the two can never disagree (its skew guard included).
            AsksWhyNotBuying: plan.TrialEndedKeyBox);
    }

    /// <summary>(b) "How did you find VoiceWink?" — to support, with the version footer.</summary>
    public static string BuildFoundUsMailto(string version) =>
        SupportBundle.BuildMailto(VoiceWinkUrls.SupportEmail, FoundUsSubject, AboutInfo.MailFooter(version));

    /// <summary>(c) "Not buying? Tell me why in one line" — to support, with the version footer.</summary>
    public static string BuildWhyNotBuyingMailto(string version) =>
        SupportBundle.BuildMailto(VoiceWinkUrls.SupportEmail, WhyNotBuyingSubject, AboutInfo.MailFooter(version));

    /// <summary>
    /// (b) "Tell a friend" — no recipient (the user picks one in their own mail app; <c>mailto:?…</c> is
    /// valid RFC 6068), the product in one line and the site link.
    /// </summary>
    public static string BuildTellAFriendMailto() =>
        SupportBundle.BuildMailto(string.Empty, TellAFriendSubject, TellAFriendBody);
}

/// <summary>Which LNC-14 links the License page shows on the panel it is rendering.</summary>
/// <param name="ShowsPurchaseLinks">(b) How did you find VoiceWink, Star on GitHub, Tell a friend, Copy link.</param>
/// <param name="ShowsReview">(b) Leave a review — only with the purchase links, after enough days of use.</param>
/// <param name="AsksWhyNotBuying">(c) "Not buying? Tell me why in one line".</param>
public readonly record struct LicenseFeedbackPlan(bool ShowsPurchaseLinks, bool ShowsReview, bool AsksWhyNotBuying);
