namespace VoiceWink.Helpers;

/// <summary>ONB-6: what the user is told when the selected speech model is neither a cloud model nor
/// a local catalogue row (an imported retired name, a hand-edited setting). Nothing here picks a
/// replacement: the user chooses, the recommendation is advice.</summary>
internal static class UnusableModelSelection
{
    /// <summary>The pill for a recording or retry refused on that selection (≤ the 55-unit pill budget).</summary>
    internal const string PillMessage = "Unknown speech model — pick one on Models";

    /// <summary>The Models page's Active Model subtitle for that selection: the model
    /// <see cref="DefaultLocalModel.Resolve"/> recommends for this PC and language.</summary>
    internal static string RecommendationLine(bool parakeetAvailable, string? selectedLanguage) =>
        $"Recommended for this PC: {ModelDisplayName.Resolve(DefaultLocalModel.Resolve(parakeetAvailable, selectedLanguage))}.";
}
