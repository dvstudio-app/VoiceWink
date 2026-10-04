namespace VoiceWink.Helpers;

/// <summary>
/// 2026-10-03 (owner rule: speed stars must not jump around without the user knowing why): how far
/// this PC sits from its estimate, learned from the models it has MEASURED and applied to the ones it
/// has not, so one speed check moves every row of its kind together instead of leaving the others
/// looking faster than they are. Shared by the speech models (<see cref="ModelRatings.Rate"/>) and the
/// built-in models (<c>LocalModelRecommendation</c>). Pure.
/// </summary>
internal static class SpeedCalibration
{
    /// <summary>The median of measured / estimate over this PC's measured models; 1 when none. A
    /// pair with a non-positive figure is ignored.</summary>
    internal static double Factor(IEnumerable<(int Measured, int Estimate)> pairs)
    {
        var ratios = pairs.Where(p => p.Measured > 0 && p.Estimate > 0)
            .Select(p => (double)p.Measured / p.Estimate)
            .OrderBy(r => r)
            .ToArray();
        if (ratios.Length == 0) return 1.0;
        var mid = ratios.Length / 2;
        return ratios.Length % 2 == 1 ? ratios[mid] : (ratios[mid - 1] + ratios[mid]) / 2;
    }

    /// <summary>An estimate scaled by the factor, rounded to whole milliseconds, never below 1.</summary>
    internal static int Apply(int estimateMs, double factor)
        => (int)Math.Max(1, Math.Min(int.MaxValue, Math.Round(estimateMs * factor)));
}

/// <summary>
/// 2026-10-03: the one line that announces a change in a card's speed stars ("Speed ratings were
/// updated for this PC."), so a check or a GPU change never moves stars silently. The card keeps a
/// fingerprint of the stars it last showed (an app-managed setting); the next render compares. Pure.
/// </summary>
internal static class SpeedRatingsNotice
{
    internal const string Text = "Speed ratings were updated for this PC.";

    /// <summary>The value an UPGRADED install starts from (<see cref="SeedUpgrade"/>): "compare with
    /// what the build before this one showed". A fresh install has no value at all, so its first
    /// render is the baseline and says nothing.</summary>
    internal const string LegacyMarker = "legacy";

    /// <summary>At launch, before any page renders: an install that finished onboarding under a
    /// build without this notice gets <see cref="LegacyMarker"/> on both keys, so the first render
    /// of the new stars is announced (self-review: an ARM64 laptop's Parakeet went ★5 → ★4 silently).
    /// Presence-based, one-way, idempotent, fail-soft — the <c>FillerWordsMigration</c> shape.</summary>
    internal static void SeedUpgrade(Services.System.SettingsService settings)
    {
        try
        {
            if (!settings.GetBool(AppDefaults.HasCompletedOnboarding)) return;
            foreach (var key in new[] { AppDefaults.SpeedRatingsShownModels, AppDefaults.SpeedRatingsShownEngine })
            {
                if (!settings.Contains(key)) settings.SetString(key, LegacyMarker);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug("Speed ratings upgrade seed skipped: {ExceptionType}", ex.GetType().Name);
        }
    }

    /// <summary>A stable fingerprint of what a card shows: each row's id and speed star, in id order.</summary>
    internal static string Fingerprint(IEnumerable<(string Id, int Stars)> rows)
        => string.Join(";", rows.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.Id}={r.Stars}"));

    /// <summary>Whether to show the line: only when a fingerprint was seen before and a row present in
    /// BOTH now shows a different star — a first render, a row that appeared or went (a catalog change),
    /// or an unchanged card says nothing.</summary>
    internal static bool ShouldAnnounce(string? previous, string current)
    {
        if (string.IsNullOrEmpty(previous)) return false;
        var before = Parse(previous);
        foreach (var (id, stars) in Parse(current))
        {
            if (before.TryGetValue(id, out var old) && old != stars) return true;
        }
        return false;
    }

    /// <summary>One page's view of a card's notice: reads and writes the last-shown fingerprint, and
    /// once the line has been shown keeps it for the life of the page — the card rebuilds on many
    /// triggers (a selection, a finished download), and a line that vanished at the next rebuild
    /// would not have been seen. The new fingerprint is saved at once, so the NEXT page does not
    /// repeat the line.</summary>
    internal sealed class Tracker(Func<string?> read, Action<string> write)
    {
        private bool _announced;

        /// <summary>Record what the card shows now; true while the line should be shown.
        /// <paramref name="legacy"/> is what the build before this one showed, compared against on the
        /// first render after an upgrade (<see cref="LegacyMarker"/>).</summary>
        internal bool Show(string fingerprint, string? legacy = null)
        {
            string? previous;
            try { previous = read(); } catch { previous = null; }
            if (previous == LegacyMarker) previous = legacy;
            if (!_announced && ShouldAnnounce(previous, fingerprint)) _announced = true;
            if (!string.Equals(previous, fingerprint, StringComparison.Ordinal))
            {
                try { write(fingerprint); } catch { /* a notice must never take a page down */ }
            }
            return _announced;
        }
    }

    private static Dictionary<string, int> Parse(string fingerprint)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in fingerprint.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.LastIndexOf('=');
            if (eq > 0 && int.TryParse(part[(eq + 1)..], out var stars)) map[part[..eq]] = stars;
        }
        return map;
    }
}
