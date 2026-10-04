using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// How a provider dropdown is laid out (owner, 2026-09-30): the providers that run on this PC first -
/// The built-in models, then Local server - under an "On this PC" heading, then the cloud providers
/// under "Cloud", in enum order. Headings appear only when both groups do; a list with one group
/// (the image providers, all cloud) shows no heading at all.
/// </summary>
/// <remarks>Pure, so the order and the headings are pinned by <c>ProviderListLayoutTests</c>; the
/// combos render the rows through <see cref="AppTheme.AddProviderItems"/>, where a heading is a
/// disabled item and never a selectable one.</remarks>
internal static class ProviderListLayout
{
    internal const string LocalHeading = "On this PC";
    internal const string CloudHeading = "Cloud";

    /// <summary>A heading (Provider null) or a provider (Heading null).</summary>
    internal readonly record struct Row(string? Heading, AIProvider? Provider);

    internal static bool IsLocal(AIProvider provider) => provider is AIProvider.OnThisPc or AIProvider.LocalServer;

    /// <summary>The providers in display order: the built-in models, Local server, then everything else
    /// in the order given. Duplicates are dropped.</summary>
    internal static IEnumerable<AIProvider> Ordered(IEnumerable<AIProvider> providers)
    {
        var list = providers.Distinct().ToList();
        return list.Where(IsLocal).OrderBy(p => p == AIProvider.OnThisPc ? 0 : 1)
            .Concat(list.Where(p => !IsLocal(p)));
    }

    /// <summary>The rows a combo shows, headings included when both groups are present.</summary>
    internal static IReadOnlyList<Row> Rows(IEnumerable<AIProvider> providers)
    {
        var ordered = Ordered(providers).ToList();
        var local = ordered.Where(IsLocal).ToList();
        var cloud = ordered.Where(p => !IsLocal(p)).ToList();
        if (local.Count == 0 || cloud.Count == 0)
            return ordered.Select(p => new Row(null, p)).ToList();
        var rows = new List<Row> { new(LocalHeading, null) };
        rows.AddRange(local.Select(p => new Row(null, p)));
        rows.Add(new Row(CloudHeading, null));
        rows.AddRange(cloud.Select(p => new Row(null, p)));
        return rows;
    }
}
