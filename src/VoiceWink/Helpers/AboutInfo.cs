using VoiceWink.Services.Support;

namespace VoiceWink.Helpers;

/// <summary>
/// Publisher / copyright strings for the in-app About card (LGL-4), plus the support-mail
/// helpers (the running version, the shared footer, the suggestion <c>mailto:</c>). Kept as a pure helper
/// (no WinUI types) so the rendered text is unit-testable without constructing a Page.
/// Mirrors the csproj <c>Company</c> / <c>Copyright</c> metadata and the LICENSE header.
/// </summary>
public static class AboutInfo
{
    public const string Publisher = "DV Studio";
    public const string Copyright = "Copyright © 2025-2026 Dieter Verlaeckt / DV Studio";
    public const string Attribution = "Inspired by VoiceInk · GPL v3";

    /// <summary>
    /// The third-party licence manifest (LGL-6 item 2). The csproj ships it beside the binary
    /// through a Content <c>&lt;Link&gt;</c> of exactly this name — <c>AboutInfoTests</c>
    /// pins the two together — and the About card's "Third-party licenses" link opens it.
    /// </summary>
    public const string ThirdPartyNoticesFileName = "THIRD-PARTY-NOTICES.md";

    /// <summary>Where the shipped manifest lives at runtime: beside the executing assembly.</summary>
    public static string ThirdPartyNoticesPath =>
        Path.Combine(AppContext.BaseDirectory, ThirdPartyNoticesFileName);

    /// <summary>
    /// The running build as the About card prints it (three components — <c>1.85.380</c>), read
    /// from this assembly. The ONE reader for the About page, What's new and both support emails;
    /// the version helpers below take it as a parameter so tests can pin their text.
    /// </summary>
    public static string CurrentVersion =>
        typeof(AboutInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Build the About-card body text for a given (already-formatted) version string.</summary>
    public static string BuildText(string version) =>
        $"VoiceWink v{version}\n" +
        $"Published by {Publisher}\n" +
        $"{Copyright}\n" +
        Attribution;

    /// <summary>Subject the "Suggest an improvement" email opens with.</summary>
    public const string SuggestionSubject = "VoiceWink suggestion";

    /// <summary>
    /// The footer both support emails end with — a blank line after the user's text (two when the
    /// footer is the whole body, as in the suggestion mail), a separator, then the About card's own
    /// version line — so a report or a suggestion about something a later build already does can be
    /// answered as such without opening a bundle (a report sent without logs carries no other
    /// version). CRLF, the canonical line break for a Simple MAPI note and the RFC 6068 form for a
    /// <c>mailto:</c> body, so one footer serves both transports.
    /// </summary>
    public static string MailFooter(string version) =>
        $"\r\n\r\n--\r\nVoiceWink v{version}";

    /// <summary>
    /// The <c>mailto:</c> behind the Support card's "Suggest an improvement" button: the support
    /// address, <see cref="SuggestionSubject"/>, and <see cref="MailFooter"/> for the given version
    /// as the whole body. Composed through <see cref="SupportBundle.BuildMailto"/> — the problem
    /// report's own mailto fallback — so the two share one escaping and length budget.
    /// </summary>
    public static string BuildSuggestionMailto(string version) =>
        SupportBundle.BuildMailto(
            VoiceWinkUrls.SupportEmail,
            SuggestionSubject,
            MailFooter(version));
}
