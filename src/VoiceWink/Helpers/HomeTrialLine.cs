using VoiceWink.Services.Licensing;

namespace VoiceWink.Helpers;

/// <summary>How the Home page's free-trial line renders (LNC-11).</summary>
public enum HomeTrialLineKind
{
    /// <summary>No line: a key is stored, or no trial was ever started here.</summary>
    Hidden,

    /// <summary>The countdown, in the page's ordinary subtle colour.</summary>
    Countdown,

    /// <summary>The countdown inside the last <see cref="HomeTrialLine.EmphasisWindow"/> — the
    /// amber warning colour, bold: the days a user decides in.</summary>
    CountdownEmphasized,

    /// <summary>The trial ran out and no key was entered — the amber "has ended" line.</summary>
    Ended,
}

/// <summary>
/// LNC-11 (2026-09-13, plan Section 10 item 1): the ONE decision behind the Home page's free-trial
/// line — the countdown that used to live only on the License page and in the wizard, with the last
/// three days emphasized, and its terminal state once the trial has ended. Pure: the page feeds it
/// the cached status, the remaining time and the ended flag it read from <c>LicenseService</c>
/// (three reads and no licensing write — the trial window's last-seen stamp advances as on every
/// status read, LIC-21 PR A, which is the same thing a hotkey press or the License page's own tick
/// does; `.claude/rules/licensing.md`), and renders whatever comes back. Re-resolved on every tick, so every time boundary is computed at
/// read time like every other trial predicate.
///
/// <para>Only two statuses ever show a line. <c>FirstRunGrace</c> IS the running trial (no stored
/// key + an active window), so it shows the countdown; <c>Unlicensed</c> with the ended flag is the
/// trial that ran out with no key entered, the win-back moment. Every other status has a STORED
/// key — Activated / OfflineGrace are simply licensed, and the three blocked key states already
/// land on the License page through the startup route — so the line is hidden there, and hidden
/// too for an <c>Unlicensed</c> user who never started the trial (the wizard offers the start;
/// Home does not nag). A <c>FirstRunGrace</c> with no remaining time is a snapshot torn between
/// two reads — hidden rather than "0h 00m", which the next tick corrects.</para>
/// </summary>
public sealed record HomeTrialLine(HomeTrialLineKind Kind, string Text, string LinkText)
{
    /// <summary>The line's last stretch, rendered emphasized: strictly less than three days left.</summary>
    public static readonly TimeSpan EmphasisWindow = TimeSpan.FromDays(3);

    public const string EndedText = "Your free trial has ended.";
    public const string CountdownLinkText = "Get a license";
    public const string EndedLinkText = "Enter a key or buy a license";

    public static readonly HomeTrialLine Hidden = new(HomeTrialLineKind.Hidden, string.Empty, string.Empty);

    public bool IsVisible => Kind != HomeTrialLineKind.Hidden;

    public static HomeTrialLine Resolve(LicenseStatus status, TimeSpan? remaining, bool trialEnded)
    {
        switch (status)
        {
            case LicenseStatus.FirstRunGrace:
                if (remaining is not { } r || r <= TimeSpan.Zero) return Hidden;
                var kind = r < EmphasisWindow ? HomeTrialLineKind.CountdownEmphasized : HomeTrialLineKind.Countdown;
                return new HomeTrialLine(kind, TryoutWindowCopy.FormatRemaining(r), CountdownLinkText);
            case LicenseStatus.Unlicensed:
                return trialEnded
                    ? new HomeTrialLine(HomeTrialLineKind.Ended, EndedText, EndedLinkText)
                    : Hidden;
            default:
                return Hidden;
        }
    }
}
