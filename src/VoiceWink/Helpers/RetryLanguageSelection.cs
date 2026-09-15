using VoiceWink.Models;
using VoiceWink.ViewModels;

namespace VoiceWink.Helpers;

/// <summary>One row of the retry picker's language combo. Plain data — see the class remarks.</summary>
/// <remarks>
/// A DATA row bound through <c>DisplayMemberPath</c>, never a <c>ComboBoxItem</c> with a
/// <c>UIElement</c> content: a closed ComboBox renders the selected item's content in its own
/// presenter — the SAME instance the dropdown container claims when the popup opens — and that
/// hand-off throws <c>COMException 0x800F1000</c> "Element is already the child of another element",
/// killing the process. Four live fatal crashes on 2026-08-03; the rule is in root CLAUDE.md.
/// </remarks>
/// <param name="Code">The ISO code (or <c>"auto"</c>) that rides onto the retry context.</param>
/// <param name="Display">What the combo renders, bound via <c>DisplayMemberPath</c>.</param>
internal sealed record LanguageRow(string Code, string Display);

/// <summary>
/// The retry picker's language combo: which rows a given model may offer, and — the part that needs
/// state — what the user actually ASKED for as distinct from what the combo currently displays
/// (TRN-17).
///
/// <para><b>The failure it prevents, inside a single dialog session.</b> Global language Chinese, a
/// failed cloud attempt. The user picks Parakeet; <c>zh</c> is not among its 25 codes, so the combo
/// can only show Auto. They then decide Parakeet is wrong and pick Whisper Small — which recognises
/// Chinese perfectly. If the dialog read the ask off the combo, Chinese is silently gone by then and
/// Chinese audio gets transcribed under auto-detect. Keeping the ask separate restores it.</para>
///
/// <para>This is IMG-12's <c>QualitySelectionTracker</c> problem and it takes the same shape
/// deliberately: <see cref="Requested"/> moves ONLY on a real user pick, and repopulating the combo
/// happens inside <see cref="BeginPopulate"/> so the <c>SelectionChanged</c> events that fires
/// cannot be mistaken for one.</para>
///
/// <para><b>Depth, not a bool</b>, and disposed in a <c>using</c>: a nested populate must not clear
/// the outer scope early, and an exception thrown mid-populate must not latch it on — a latched
/// scope would silently stop recording the user's choices for the rest of the dialog.</para>
///
/// <para><b>Residual, same as IMG-12's.</b> This assumes WinUI raises <c>SelectionChanged</c>
/// synchronously from the <c>ItemsSource</c>/<c>SelectedIndex</c> assignment. A runtime that
/// deferred it would record the DISPLAYED value as the ask — a sticky clamp, which is the safe
/// direction: never above what was asked.</para>
///
/// <para>Pure and UI-free, so the interleavings are testable without a XamlRoot.</para>
/// </summary>
internal sealed class RetryLanguageSelection
{
    private int _populateDepth;

    /// <summary>What the user has asked for — the ISO code, or <c>"auto"</c>.
    /// Survives a model switch that could not offer it.</summary>
    internal string Requested { get; private set; }

    internal RetryLanguageSelection(string? requested)
        => Requested = string.IsNullOrWhiteSpace(requested)
            ? EffectiveTranscriptionLanguage.Auto
            : requested!;

    /// <summary>True while a programmatic repopulate is in progress.</summary>
    internal bool IsPopulating => _populateDepth > 0;

    /// <summary>
    /// The rows to bind for <paramref name="model"/>, and the index to select.
    ///
    /// <para>Auto is always row 0 and always present, which is what lets the confirm button ignore
    /// the language entirely — there is no "nothing selected" state to guard.</para>
    ///
    /// <para>A model with a DECLARED coverage set (only Parakeet today) offers Auto plus that set;
    /// <c>null</c> coverage — Whisper, every cloud row, anything unlisted — offers the full picker
    /// list, because <see cref="ModelLanguageSupport"/> deliberately makes no negative claim there
    /// and narrowing on our own dropdown's authority is exactly what it refuses to do.</para>
    ///
    /// <para>Never mutates <see cref="Requested"/>: the clamp is what the user SEES, not what they
    /// asked for.</para>
    /// </summary>
    internal static (IReadOnlyList<LanguageRow> Rows, int SelectedIndex) RowsFor(
        TranscriptionModelInfo? model, string requested)
    {
        var declared = ModelLanguageSupport.SupportedCodesFor(model);

        var codes = ModelManagementViewModel.SupportedLanguages
            .Where(c => !string.Equals(c, EffectiveTranscriptionLanguage.Auto, StringComparison.OrdinalIgnoreCase))
            .Where(c => declared is null || declared.Contains(c))
            // Alphabetical by DISPLAY name, matching every other language picker in the app
            // (ModelsPage, onboarding) — a user scanning for "Dutch" should not have to know it is
            // filed under "nl".
            .OrderBy(ModelManagementViewModel.GetLanguageDisplayName, StringComparer.OrdinalIgnoreCase);

        var rows = new List<LanguageRow>
        {
            new(EffectiveTranscriptionLanguage.Auto, "Auto-detect"),
        };
        rows.AddRange(codes.Select(c => new LanguageRow(c, ModelManagementViewModel.GetLanguageDisplayName(c))));

        // Case-insensitive, because the ask can arrive as "EN" or "Auto" from an imported or
        // hand-edited settings file — the same reason EffectiveTranscriptionLanguage compares tags
        // that way. An ordinal match would clamp a language that IS on offer.
        var index = rows.FindIndex(r => string.Equals(r.Code, requested, StringComparison.OrdinalIgnoreCase));
        return (rows, index >= 0 ? index : 0);
    }

    /// <summary>
    /// Open a populate scope. Selection changes raised inside it are the dialog's own, not the
    /// user's, and leave <see cref="Requested"/> alone.
    /// </summary>
    internal IDisposable BeginPopulate()
    {
        _populateDepth++;
        return new PopulateScope(this);
    }

    /// <summary>Record a real user choice. Ignored while a populate scope is open.</summary>
    internal void UserPicked(string? code)
    {
        if (IsPopulating || string.IsNullOrWhiteSpace(code)) return;
        Requested = code!;
    }

    private sealed class PopulateScope(RetryLanguageSelection owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            // Idempotent: a double dispose (a `using` plus a stray manual call) must not decrement
            // twice and reopen the scope while an outer one is still running.
            if (_disposed) return;
            _disposed = true;
            owner._populateDepth--;
        }
    }
}
