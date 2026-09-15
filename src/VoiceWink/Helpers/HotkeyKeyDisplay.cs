namespace VoiceWink.Helpers;

/// <summary>
/// What a hotkey is CALLED in front of a user — "Right Alt", "Ctrl + Space" — as opposed to the
/// canonical token the settings file and the hook speak ("RightAlt", "Ctrl+Space").
/// </summary>
/// <remarks>
/// <para><b>The split is display-only, storage is untouched</b> (owner decision, 2026-08-30 — the
/// same display-vs-storage pattern as <c>ModelDisplayName</c>, and the same split the "None
/// (disabled)" label already makes with its stored <c>""</c>). Every persisted value, every
/// <see cref="HotkeyBinding"/> canonical, every log line stays exactly as before, so nothing
/// migrates and old settings files keep parsing.</para>
///
/// <para><b>The maps must stay a bijection over everything the pickers offer.</b> The pickers put
/// DISPLAY strings in their items and convert back at every read, so a display name that two
/// canonical tokens could produce — or a canonical token the reverse map does not recover — turns
/// a user's selection into a different binding. <c>HotkeyKeyDisplayTests</c> round-trips the full
/// offered key set; keep it that way when adding tokens.</para>
///
/// <para>Unknown tokens pass through unchanged in BOTH directions, so the functions are total:
/// F-keys, letters, digits, Space, and the "None (disabled)" label all map to themselves, and a
/// future canonical token is rendered verbatim (ugly beats wrong) until a row is added here.</para>
/// </remarks>
internal static class HotkeyKeyDisplay
{
    // Canonical → display for the six modifier KEY tokens — the only names that differ.
    // "Control" abbreviates to "Ctrl" per the Windows shortcut convention (Ctrl+C, Ctrl+V …).
    private static readonly (string Canonical, string Display)[] TokenMap =
    [
        ("RightAlt", "Right Alt"),
        ("LeftAlt", "Left Alt"),
        ("RightControl", "Right Ctrl"),
        ("LeftControl", "Left Ctrl"),
        ("RightShift", "Right Shift"),
        ("LeftShift", "Left Shift"),
    ];

    /// <summary>One canonical token → its display form. Identity for unmapped tokens.</summary>
    internal static string ToDisplayToken(string canonicalToken)
    {
        foreach (var (canonical, display) in TokenMap)
        {
            if (canonicalToken == canonical) return display;
        }
        return canonicalToken;
    }

    /// <summary>One display token → its canonical form. Identity for unmapped tokens.</summary>
    internal static string ToCanonicalToken(string displayToken)
    {
        foreach (var (canonical, display) in TokenMap)
        {
            if (displayToken == display) return canonical;
        }
        return displayToken;
    }

    /// <summary>
    /// A canonical binding string ("Ctrl+Space", "RightAlt") rendered for a user
    /// ("Ctrl + Space", "Right Alt"). Empty stays empty.
    /// </summary>
    internal static string Describe(string canonicalBinding)
    {
        if (canonicalBinding.Length == 0 || !canonicalBinding.Contains('+'))
            return ToDisplayToken(canonicalBinding);

        var tokens = canonicalBinding.Split('+');
        for (var i = 0; i < tokens.Length; i++) tokens[i] = ToDisplayToken(tokens[i]);
        return string.Join(" + ", tokens);
    }

    /// <summary>
    /// The inverse of <see cref="Describe"/>: a displayed binding back to its canonical string.
    /// A plain token (no " + ") reverses token-wise, so labels like "None (disabled)" — spaces,
    /// no separator — pass through unchanged.
    /// </summary>
    internal static string FromDisplay(string displayBinding)
    {
        if (displayBinding.Length == 0 || !displayBinding.Contains(" + ", StringComparison.Ordinal))
            return ToCanonicalToken(displayBinding);

        var tokens = displayBinding.Split(" + ", StringSplitOptions.None);
        for (var i = 0; i < tokens.Length; i++) tokens[i] = ToCanonicalToken(tokens[i]);
        return string.Join("+", tokens);
    }
}
