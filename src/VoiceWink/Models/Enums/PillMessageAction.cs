namespace VoiceWink.Models.Enums;

/// <summary>
/// What a click on a MESSAGE pill's body does (LNC-11, 2026-09-13). A message pill is plain text
/// for every caller but one: the license gate's refusal ("Your free trial has ended — open the
/// License page") names a page the user then has to find, so that pill OPENS it. Carried on
/// <c>PillContent.Message</c> so the renderer can show the cue (tooltip + underline) and App can
/// route the click by CONTENT rather than by matching the message text; the default keeps every
/// other message pill inert, and the drag handle (PILL-4) is untouched either way — a press that
/// crosses the drag threshold is a drag, a press that does not is the click.
/// </summary>
public enum PillMessageAction
{
    /// <summary>Plain text; a click on the body does nothing (the drag handle still works).</summary>
    None,

    /// <summary>Open the License page in the main window (restoring it from the tray if needed)
    /// and dismiss the pill — the reason has been seen.</summary>
    OpenLicensePage,
}
