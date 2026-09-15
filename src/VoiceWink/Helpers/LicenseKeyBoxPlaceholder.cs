namespace VoiceWink.Helpers;

/// <summary>
/// What the licence key box shows while it is EMPTY — its placeholder (LIC-28, owner UAT 181.13,
/// 2026-09-09). When a key is stored, that key in its MASKED form (first four, stars, last four — the
/// only form the view model ever exposes); otherwise the format hint. Pure and tested for the usual
/// reason: the WinUI pages cannot be constructed in the test host, and the hint literal had been
/// duplicated across the License page's key-box factory and the onboarding wizard's key box.
///
/// <para>Placeholder, never the box's <c>Text</c>, is the whole design. The box's Text is what the
/// Activate command sends to Lemon Squeezy: a masked value there would be SUBMITTED, as stars, and
/// report "invalid" on a key that is fine; the real key pre-filled would let one click re-activate the
/// same key and consume a second seat — the refresh icon beside the chip is the retry that costs none
/// (LIC-25). A placeholder is never submitted and disappears the moment the user types. It replaces
/// the "Stored key:" sentences LIC-13 and LIC-27 had put ABOVE an empty box, which the owner read as
/// that box's label.</para>
/// </summary>
public static class LicenseKeyBoxPlaceholder
{
    /// <summary>The format hint shown when no key is stored.</summary>
    public const string Default = "XXXX-XXXX-XXXX-XXXX";

    /// <param name="maskedKey">
    /// <c>LicenseViewModel.MaskedKey</c> — <c>LicenseService.GetMaskedLicenseKey()</c>, null when
    /// nothing is stored. Never the raw key: this method has no way to mask, on purpose.
    /// </param>
    public static string Resolve(string? maskedKey)
        => string.IsNullOrWhiteSpace(maskedKey) ? Default : maskedKey;
}
