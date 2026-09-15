namespace VoiceWink.Helpers;

/// <summary>
/// An sRGB colour as three bytes — the WinRT-free half of a palette entry (LIC-20). <c>AppTheme</c>
/// turns one of these into a <c>Windows.UI.Color</c> through <c>FromSwatch</c>; the test host cannot
/// call <c>ColorHelper.FromArgb</c> (the Windows App SDK is not bootstrapped there), so the VALUES
/// that carry a contrast rule live here, where a test can read them.
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// The five palette fields the small-text contrast rule covers: the two backgrounds text lands on,
/// and the three colours that carry sentences or badges. Everything else in the palette stays inline
/// in <c>AppTheme</c>; these five are here so <c>ThemeTextContrastTests</c> pins the SHIPPED numbers
/// rather than hex literals copied into a test (a harness must measure the shipped path).
/// </summary>
public readonly record struct ThemeSwatchSet(Rgb ContentBg, Rgb CardBg, Rgb AccentAmber, Rgb FailureText, Rgb WarningText);

/// <summary>
/// The dark and light swatch sets <c>AppTheme</c>'s palettes are built from (LIC-20). The rule the
/// tests pin: <c>FailureText</c> and <c>WarningText</c> reach ≥ 4.5:1 (WCAG AA, small text) against
/// BOTH <c>ContentBg</c> and <c>CardBg</c> in BOTH themes, and the dark <c>WarningText</c> IS the
/// dark accent, so the dark theme — the owner's UAT theme — looks exactly as it did.
///
/// <para>Why the accent could not simply be used as text: <c>#FF9500</c> at 12 px measures 1.97:1
/// on the light page and 2.2:1 on the white card (8.8:1 / 7.6:1 in the dark theme). The accent
/// remains the colour of borders, chips-as-badges and the pill tones; the *Text* colours are for
/// sentences.</para>
/// </summary>
public static class ThemeSwatches
{
    public static readonly ThemeSwatchSet Dark = new(
        ContentBg: new Rgb(13, 13, 15),        // #0D0D0F
        CardBg: new Rgb(30, 30, 34),           // #1E1E22
        AccentAmber: new Rgb(255, 149, 0),     // #FF9500
        FailureText: new Rgb(255, 107, 107),   // #FF6B6B — 7.0:1 on ContentBg, 6.0:1 on CardBg (LIC-19)
        WarningText: new Rgb(255, 149, 0));    // = the accent: 8.8:1 on ContentBg, 7.6:1 on CardBg

    public static readonly ThemeSwatchSet Light = new(
        ContentBg: new Rgb(242, 242, 247),     // #F2F2F7
        CardBg: new Rgb(255, 255, 255),        // white
        AccentAmber: new Rgb(255, 149, 0),     // #FF9500 — 1.97:1 on ContentBg as text: the LIC-20 defect
        FailureText: new Rgb(179, 38, 30),     // #B3261E — 5.9:1 on ContentBg, 6.5:1 on CardBg (LIC-19)
        WarningText: new Rgb(154, 91, 0));     // #9A5B00 — 4.86:1 on ContentBg, 5.43:1 on CardBg: the lightest amber clearing both (0.36 of headroom on the page)
}

/// <summary>
/// WCAG 2.x contrast ratio between two sRGB colours: relative luminance through the sRGB transfer
/// curve (0.2126 R + 0.7152 G + 0.0722 B), then (lighter + 0.05) / (darker + 0.05). Pure arithmetic;
/// the vectors in <c>ThemeTextContrastTests</c> (white/black = 21:1, #767676/white = 4.54:1, #777777/white
/// = 4.48:1) pin the curve, not just the endpoints.
/// </summary>
public static class WcagContrast
{
    public static double Ratio(Rgb a, Rgb b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static double Luminance(Rgb c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
