namespace VoiceWink.Helpers;

/// <summary>
/// LNC-11 (2026-09-13): the once-per-session claim behind the automatic navigation to the License
/// page on the FIRST recording press the license gate refuses because the free trial has ended.
///
/// <para>Why it exists: the trial is the sales rep — no email, no account — so the moment it ends
/// while the app is running is the one win-back moment the app owns, and until this the only thing
/// that happened at that moment was a pill naming a page the user then had to find. The gate opens
/// the page for them ONCE: a user who read the page and went back to work is not yanked to it on
/// every later press (the pill still says why, and its click reopens the page at will).</para>
///
/// <para>Only a TRIAL-ENDED refusal claims. Every other blocked status (an expired or invalid key, a
/// disabled key, a stale cache) is out of scope by design: those users have a key and the startup
/// route already lands them on the License page at the next launch. A non-claiming call never
/// consumes the claim, so a key-shaped refusal earlier in the session does not spend the trial's.</para>
///
/// <para>Per process, not persisted: "once per session" is the rule the plan set (Section 10 item 1),
/// and a persisted latch would silence the redirect for the whole remaining life of the install
/// after one press. Thread-safe by a single interlocked exchange — the one caller, the record
/// gate, is reached from the hotkey hook's dispatch, the tray menu and the UI buttons.</para>
/// </summary>
public sealed class TrialEndRedirect
{
    private int _claimed;

    /// <summary>
    /// True exactly once per instance, and only for a call with <paramref name="trialEnded"/>
    /// set; a call without it returns false AND leaves the claim available.
    /// </summary>
    public bool TryClaim(bool trialEnded)
    {
        if (!trialEnded) return false;
        return Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }

    /// <summary>Whether the session's one automatic navigation has already happened.</summary>
    public bool IsClaimed => Volatile.Read(ref _claimed) == 1;
}
