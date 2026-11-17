using VoiceWink.Services.AIEnhancement.LocalEngine;

namespace VoiceWink.Helpers;

/// <summary>LAI-30: the switch the start-up dialog offers — the card's offer, its download size, and the marker it is recorded under.</summary>
internal sealed record RetiredModelOfferPrompt(LocalModelEntry Retired, LocalModelEntry Pick, RetiredOfferKind Kind, long DownloadBytes, string Marker);

/// <summary>LAI-30: whether the start-up dialog offers the retired model's switch; pure, so the dialog never decides.</summary>
internal static class RetiredModelOfferPolicy
{
    /// <summary>The offer, or null: AI enhancement on, the card shows Switch or Download and switch, and this version has not shown it for this model.</summary>
    /// <param name="selectedModelId">The built-in models' selected model, or null when another provider is stored.</param>
    /// <param name="shownMarker">The marker of the last offer shown, "" when none.</param>
    internal static RetiredModelOfferPrompt? Decide(bool aiEnabled, string? selectedModelId, Func<string, bool> isInstalled,
        LocalModelEntry? pick, string? pendingTargetId, string appVersion, string shownMarker)
    {
        if (!aiEnabled || selectedModelId is null || LocalModelCatalog.Find(selectedModelId) is not { } retired)
            return null;
        var installed = isInstalled(retired.Id);
        var offer = RetiredModelSwitch.Offer(retired, installed, pick, pick is not null && isInstalled(pick.Id),
            pendingTargetId is null ? null : LocalModelCatalog.Find(pendingTargetId));
        if (offer.Kind is not (RetiredOfferKind.Switch or RetiredOfferKind.DownloadAndSwitch) || pick is null)
            return null;
        var marker = MarkerFor(appVersion, retired.Id);
        if (WasShown(shownMarker, marker))
            return null;
        return new(retired, pick, offer.Kind, pick.ToDownloadDescriptor().FileSizeBytes, marker);
    }

    /// <summary>One offer per app version per retired model: "Not now" asks again at the next update.</summary>
    internal static string MarkerFor(string appVersion, string retiredId) => $"{appVersion}|{retiredId}";

    /// <summary>Whether the stored markers (newline-separated) hold this one.</summary>
    internal static bool WasShown(string shownMarkers, string marker)
        => shownMarkers.Split('\n', StringSplitOptions.RemoveEmptyEntries).Contains(marker, StringComparer.Ordinal);

    /// <summary>The stored markers plus this one, keeping only this version's, so a second retired model never re-arms the first.</summary>
    internal static string AddMarker(string shownMarkers, string appVersion, string marker)
        => string.Join("\n", shownMarkers.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.StartsWith(appVersion + "|", StringComparison.Ordinal))
            .Append(marker).Distinct(StringComparer.Ordinal));

    /// <summary>The dialog's title.</summary>
    internal static string Title(RetiredModelOfferPrompt offer) => $"Switch to {offer.Pick.DisplayName}?";

    /// <summary>The dialog's text: the card's line plus what happens to the model in use.</summary>
    internal static string Body(RetiredModelOfferPrompt offer)
        => $"{offer.Pick.DisplayName} is recommended for this PC. {offer.Retired.DisplayName} stays in use until the switch is done.";

    /// <summary>The primary button: the card's label, with the download size when there is one.</summary>
    internal static string PrimaryButton(RetiredModelOfferPrompt offer) => offer.Kind == RetiredOfferKind.DownloadAndSwitch
        ? $"Download and switch ({DownloadProgressText.Size(offer.DownloadBytes)})"
        : "Switch";
}
