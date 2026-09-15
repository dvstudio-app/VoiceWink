using System.Globalization;

namespace VoiceWink.Helpers;

/// <summary>
/// Decides whether a provider-published retirement date means a model is GONE, and should therefore
/// drop out of curated discovery.
///
/// <para><b>The rule: hide only once the retirement date has arrived.</b> A model whose retirement
/// is announced for next month still works this month, so it stays in the dropdown until the day it
/// stops working (owner decision, 2026-08-03).</para>
///
/// <para><b>Why the old rule was wrong.</b> <c>OpenRouterModelCatalog</c> hid any row carrying a
/// non-blank <c>expiration_date</c>, which reads "has a date" as "gone". The live catalog disproves
/// that twice over: on 2026-08-03 <c>z-ai/glm-5-turbo</c> and <c>z-ai/glm-5v-turbo</c> published
/// <c>2098-12-31</c> while answering <c>GET /api/v1/models/{id}/endpoints</c> with HTTP 200 and a
/// live Z.AI endpoint — hidden for 72 years — and <c>z-ai/glm-4.5</c> was hidden five months before
/// a retirement it will serve normally right up to.</para>
///
/// <para><b>Accepted trade-off.</b> A model retiring in a few days stays selectable until it dies,
/// so a user can pick one that breaks that week. Judged acceptable: the exclusion is
/// DISCOVERY-ONLY and never blocked an already-persisted selection anyway, so the app has always
/// let someone keep using a model right up to its retirement — this only makes the dropdown agree
/// with that. Upcoming retirements are reported separately by the weekly
/// <c>/vw-model-review</c>, which reads the raw catalog and does not depend on this rule.</para>
///
/// <para><b>These dates are mutable in both directions.</b> <c>z-ai/glm-4.5v</c> published
/// <c>2026-12-31</c> on 2026-07-30 and published NO date on 2026-08-03 — OpenRouter withdrew a
/// scheduled retirement. So the verdict is never cached, persisted, or migrated: it stays a pure
/// function of the row as fetched plus today, and a persisted selection is never migrated, cleared,
/// or runtime-blocked.</para>
///
/// <para><b>An unparsable STRING counts as retired, which is the pre-existing behaviour</b> — any
/// non-blank string hid the model before this type existed. It matters if a provider changes its
/// date FORMAT: "we cannot read this date" must not silently un-hide every genuinely dead model.
/// A wrong JSON KIND is different and deliberately never reaches here as text: the fetch layer
/// collapses it to <see langword="null"/> (also pre-existing), so it reads as "no retirement
/// announced". Passing raw JSON text through instead was tried and rejected in diff review — it
/// inverted shipped behaviour on a path this change was not meant to touch, and a catalog-wide
/// schema-kind change would then have hidden EVERY row and emptied the dropdown, the exact failure
/// <c>ProviderModelList.RawCount</c> exists to keep impossible.</para>
///
/// Pure: no HTTP, no filesystem, no clock — <paramref name="today"/> is injected by the caller, the
/// same way <c>isChatModel</c> is. Pinned by <c>ModelRetirementTests</c>.
/// </summary>
internal static class ModelRetirement
{
    /// <summary>
    /// The ISO forms accepted after the date-only shape fails. OpenRouter sends date-only
    /// (<c>2098-12-31</c>); Mistral publishes its sibling <c>deprecation</c> field as a full
    /// timestamp (<c>2026-07-31T12:00:00Z</c>), so the timestamp shapes are what a format drift on
    /// either provider would most plausibly look like. An EXPLICIT set rather than a general
    /// <c>TryParse</c>: the general parser accepts far more than ISO syntax and would let a
    /// locale-shaped string through.
    /// </summary>
    private static readonly string[] TimestampFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    };

    /// <summary>
    /// True when <paramref name="publishedDate"/> says the model has already retired and should
    /// drop out of curated discovery. A future date — however near — returns false.
    ///
    /// <para><paramref name="publishedDate"/> must be <see langword="null"/> when the provider
    /// omitted the field (or sent JSON null); see the type doc for why a wrong JSON kind arrives
    /// that way rather than as text.</para>
    /// </summary>
    /// <param name="publishedDate">The provider's published retirement date, or null when absent.</param>
    /// <param name="today">The caller's current date, injected so this stays pure.</param>
    internal static bool HasRetired(string? publishedDate, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(publishedDate))
            return false;

        if (!TryParseDate(publishedDate.Trim(), out var retiresOn))
            return true;

        // Inclusive: on the day itself the model is treated as gone. The provider's cut-off hour is
        // unpublished, so a one-day-either-way guess is unavoidable; erring toward "gone" on the
        // day matches the field's meaning better than offering a model mid-shutdown.
        return retiresOn <= today;
    }

    /// <summary>
    /// Culture-invariant by construction: a machine on a <c>dd/MM</c> or non-Gregorian locale must
    /// not curate a different model list than one in the US. The timestamp path treats an
    /// offset-less value as UTC and normalizes an offset-bearing one to UTC before taking the date
    /// (<see cref="DateTimeStyles.AssumeUniversal"/> + <see cref="DateTimeStyles.AdjustToUniversal"/>);
    /// without both, an offset-less timestamp would be read in the machine's local zone and could
    /// land on a different day either side of midnight.
    /// </summary>
    private static bool TryParseDate(string value, out DateOnly parsed)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out parsed))
            return true;

        if (DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture,
                                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                         out var timestamp))
        {
            parsed = DateOnly.FromDateTime(timestamp.UtcDateTime);
            return true;
        }

        parsed = default;
        return false;
    }
}
