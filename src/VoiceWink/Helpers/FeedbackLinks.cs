using VoiceWink.Services.Support;

namespace VoiceWink.Helpers;

/// <summary>
/// LNC-14 (sales plan v4 task 1.4): when the app offers its rating, review and feedback links, as pure
/// decisions, plus the links' text and the mails and share links they open. Three surfaces read it:
/// <list type="bullet">
/// <item>(a) Home — one neutral line, "Rate VoiceWink in the Microsoft Store", for a Microsoft Store
/// install after <see cref="StoreRatingAfterDaysOfUse"/> days of use, shown once (the state lives in
/// <see cref="StoreRatingPrompt"/>);</item>
/// <item>(b) the License page's Activated panel — how did you find VoiceWink (a one-click answer,
/// <c>FoundUsDialog</c>), star on GitHub, leave a review (after <see cref="ReviewAfterDaysOfUse"/> days of
/// use), tell a friend (<c>TellAFriendDialog</c>);</item>
/// <item>(c) the License page's key box once the free trial has ended — "Not buying? Tell me why in one
/// line".</item>
/// </list>
///
/// <para><b>No condition here reads how the user feels about VoiceWink</b> — install source, days of use,
/// and the licence panel only. Asking only the satisfied for a rating is the review gating the Microsoft
/// Store policy, the FTC and the EU's UCPD rules name; there is no satisfaction question anywhere, and no
/// reward. <b>The app sends no mail and posts nothing:</b> every link opens the Store, the browser or the
/// user's own mail app, and the user finishes it there (a mail the app sent would be advertising e-mail
/// under Belgian law, WER XII.13). The one thing the app sends itself is the found-us answer, a fixed tag
/// with no identifier (privacy v8 §4, A44).</para>
///
/// <para>Owner decisions: "Leave a review" opens the Microsoft Store listing for every install, Store or
/// website (2026-10-06); a shared link carries <see cref="VoiceWinkUrls.Share"/>'s <c>app-share</c> tag
/// (2026-10-25).</para>
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

    public const string FoundUsPrivacyLine = "Your answer goes to DV Studio with no name, email or ID.";
    public const string NotNowText = "Not now";
    public const string FoundUsSending = "Sending…";
    public const string FoundUsThanks = "Thanks.";
    public const string FoundUsFailure = "Couldn't send your answer. Try again later.";

    public const string CopyMessageText = "Copy message";
    public const string CloseText = "Close";
    public const string ShareCopied = "Copied.";
    public const string ShareLinkCopied = "Link copied.";
    public const string ShareCopyFailure = "Couldn't copy the message.";
    public const string ShareLinkCopyFailure = "Couldn't copy the link.";
    public const string ShareMailFailure = "Couldn't open your email app.";
    public const string ShareBrowserFailure = "Couldn't open your browser.";
    public const string ShareTooLong = "Too long to send. Shorten it or copy it.";

    public const string WhyNotBuyingSubject = "Why I'm not buying VoiceWink";
    public const string ShareSubject = "VoiceWink";

    /// <summary>The found-us answers (label shown, tag sent); the tags are the Worker's closed list, so a new one deploys there first (A44).</summary>
    public static readonly IReadOnlyList<(string Label, string Tag)> FoundUsChoices =
    [
        ("Search engine", "search"),
        ("AI assistant", "ai"),
        ("Microsoft Store", "msstore"),
        ("Reddit", "reddit"),
        ("Hacker News", "hn"),
        ("YouTube", "youtube"),
        ("Friend or colleague", "friend"),
        ("Article or blog", "article"),
        ("Other", "other"),
    ];

    /// <summary>Whether <paramref name="tag"/> is one of <see cref="FoundUsChoices"/> (ordinal).</summary>
    public static bool IsFoundUsTag(string? tag) => FoundUsChoices.Any(c => string.Equals(c.Tag, tag, StringComparison.Ordinal));

    /// <summary>Longest share link the dialog opens; Windows refuses longer URLs (INTERNET_MAX_URL_LENGTH 2048).</summary>
    public const int MaxShareUrlLength = SupportBundle.MaxMailtoChars;

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
    /// <param name="canAskFoundUs">A build that can send the found-us answer, and none delivered yet
    /// (<c>FoundUsReport.CanAsk</c>).</param>
    public static LicenseFeedbackPlan ForLicensePanel(LicensePanelPlan plan, int daysOfUse, bool canAskFoundUs)
    {
        // "After purchase" is the Activated panel. OfflineGrace holds a bought key too, but its panel is
        // a warning about an unchecked licence, not the place for these.
        var purchased = plan.Kind == LicensePanelKind.Activated;
        return new LicenseFeedbackPlan(
            ShowsPurchaseLinks: purchased,
            ShowsFoundUs: purchased && canAskFoundUs,
            ShowsReview: purchased && daysOfUse >= ReviewAfterDaysOfUse,
            // The used-up free trial's key box — the plan's own decision, the one that makes Buy lead
            // there, so the two can never disagree (its skew guard included).
            AsksWhyNotBuying: plan.TrialEndedKeyBox);
    }

    /// <summary>(c) "Not buying? Tell me why in one line" — to support, with the version footer.</summary>
    public static string BuildWhyNotBuyingMailto(string version) =>
        SupportBundle.BuildMailto(VoiceWinkUrls.SupportEmail, WhyNotBuyingSubject, AboutInfo.MailFooter(version));

    /// <summary>(b) Tell a friend's pre-filled message; the trial length is named only when <see cref="TryoutWindowCopy.Describe"/> quotes it in whole days.</summary>
    public static string ShareMessage(TimeSpan trialWindow)
    {
        var trial = TryoutWindowCopy.Describe(trialWindow) is not null && trialWindow.TotalDays >= 1
                    && trialWindow.TotalDays % 1 == 0
            ? $"Free {(int)trialWindow.TotalDays}-day trial"
            : "Free trial";
        return "I use VoiceWink to dictate on Windows: press a key, speak, and the text appears where I'm typing. "
               + $"{trial}: {VoiceWinkUrls.Share}";
    }

    /// <summary>The message for the clipboard, CRLF line breaks; null when blank, since writing "" empties the clipboard.</summary>
    public static string? ShareClipboardText(string message) =>
        string.IsNullOrWhiteSpace(message) ? null : Lines(message).Replace("\n", "\r\n");

    /// <summary>The message as a mail with no recipient (<c>mailto:?…</c> is valid RFC 6068); null when too long to open.</summary>
    public static string? BuildShareMailto(string message) =>
        Capped("mailto:?subject=" + Uri.EscapeDataString(ShareSubject)
               + "&body=" + Uri.EscapeDataString(Lines(message).Replace("\n", "\r\n")));

    /// <summary>WhatsApp's share link (the user picks the chat); null when too long to open.</summary>
    public static string? BuildWhatsAppShareUrl(string message) =>
        Capped("https://wa.me/?text=" + Uri.EscapeDataString(Lines(message)));

    /// <summary>LinkedIn's share link — it takes the link only, never text.</summary>
    public static string BuildLinkedInShareUrl() =>
        "https://www.linkedin.com/sharing/share-offsite/?url=" + Uri.EscapeDataString(VoiceWinkUrls.Share);

    /// <summary>X's compose link; null when too long to open.</summary>
    public static string? BuildXShareUrl(string message) =>
        Capped("https://x.com/intent/post?text=" + Uri.EscapeDataString(Lines(message)));

    // A WinUI TextBox separates lines with a bare CR.
    private static string Lines(string message) => message.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string? Capped(string url) => url.Length <= MaxShareUrlLength ? url : null;
}

/// <summary>Which LNC-14 links the License page shows on the panel it is rendering.</summary>
/// <param name="ShowsPurchaseLinks">(b) Star on GitHub and Tell a friend.</param>
/// <param name="ShowsFoundUs">(b) How did you find VoiceWink — with the purchase links, until an answer is delivered.</param>
/// <param name="ShowsReview">(b) Leave a review — only with the purchase links, after enough days of use.</param>
/// <param name="AsksWhyNotBuying">(c) "Not buying? Tell me why in one line".</param>
public readonly record struct LicenseFeedbackPlan(bool ShowsPurchaseLinks, bool ShowsFoundUs, bool ShowsReview, bool AsksWhyNotBuying);
