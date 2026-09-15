namespace VoiceWink.Helpers;

/// <summary>
/// HKY-4: which recording hotkey a FIRST RUN should offer, given what the keyboard layouts do
/// with Right Alt.
/// </summary>
/// <remarks>
/// <para><b>Why:</b> the factory default <c>RightAlt</c> IS AltGr on Belgian, German, French,
/// Nordic and Polish layouts, so a new European user loses <c>€ @ [ ] { }</c> until they discover
/// the setting. <c>RightControl</c> types no character on ANY layout, so that failure mode cannot
/// recur — and being a single key it keeps the one-finger tap/hold gesture the whole hotkey state
/// machine is built around (owner decision 2026-08-30, research on the HKY-4 card: Ctrl+Space —
/// the closest peer precedent — collides with IDE autocomplete everywhere and with the CJK IME
/// toggle, which Windows does not reliably let go of).</para>
///
/// <para><b>An existing install is provably untouched — except by an explicit Reset:</b> the ONLY
/// input that can produce <c>RightControl</c> is an ABSENT persisted value, and completing
/// onboarding always persists the selection — so any install that has run the wizard passes its
/// stored value straight through, relaunches included. The wizard passes null ONLY when the
/// settings key does not exist (<c>SettingsService.Contains</c>), never the fallback a read would
/// supply. The second caller (since 2026-09-13) is <c>SettingsViewModel.ResetAllSettings</c>,
/// which passes null by definition: a reset is the moment nothing is chosen, and the table's
/// "RightAlt" written verbatim there undid this seed for exactly the users it exists for.</para>
///
/// <para>The caller asks <c>KeyboardLayoutProfile.AnyInstalledLayoutNeedsAltGrForTyping()</c> —
/// the NARROW essential-characters question, not the hook's broad AltGr probe: United
/// States-International has € on AltGr yet every essential character off it, and seeding away
/// from Right Alt there was the owner-reported false alarm (2026-08-30). It fails toward TRUE,
/// so a probe failure seeds <c>RightControl</c>. Acceptable in this direction: Right Ctrl is as
/// harmless as Right Alt on a US layout, while the reverse failure would keep eating AltGr for
/// the users this exists for.</para>
///
/// <para><b>Owner-accepted residual, stated rather than implied:</b> a bound bare modifier's
/// key-down is SUPPRESSED (it reaches neither the foreground app nor OS key state), so keyboard
/// shortcuts pressed THROUGH Right Ctrl — Right-Ctrl+V and kin — are absorbed while it is the
/// hotkey. Same mechanism as the RightAlt default it replaces; the trade the owner took
/// (2026-08-30, HKY-4 card) is that those chords merely duplicate Left Ctrl, where the AltGr
/// victims lose typed characters outright. <c>HotkeyWarnings</c> states exactly this cost under
/// the picker, so the seed never hides it.</para>
/// </remarks>
internal static class HotkeyDefaultSeed
{
    /// <summary>The wizard's initial recording hotkey. See the class remarks for the contract.</summary>
    internal static string Resolve(string? persisted, bool anyLayoutUsesAltGr)
        => persisted ?? (anyLayoutUsesAltGr ? "RightControl" : "RightAlt");
}
