using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SharpHook.Native;

namespace VoiceWink.Helpers;

/// <summary>
/// The row for one hotkey role: a modifier picker beside a key picker, presented as one value.
/// Used by the Settings page and — since HKY-8 — by the onboarding wizard.
/// </summary>
/// <remarks>
/// <para><b>Why this is a type and not another factory method (HKY-3).</b> The previous
/// <c>CreateHotkeyComboSetting</c> returned a bare <see cref="Border"/>, and every caller
/// recovered the control by walking the child Grid and taking the FIRST ComboBox it found —
/// four times over in <c>SettingsPage.BuildKeyboardShortcutsCard</c>. Adding a second ComboBox to
/// that Grid would silently rebind each of those handlers to whichever control happened to come
/// first in <c>Children</c>. Codex plan review round 1 flagged it; an explicit API is what makes
/// the second dropdown safe to add at all.</para>
///
/// <para><b>Invalid pairs are unconstructible, not merely rejected.</b> The key list is rebuilt
/// whenever the modifier changes: typing keys are offered only while Ctrl is selected, and
/// modifier keys vanish as soon as any modifier is selected. <see cref="ValueChanged"/> fires only
/// for a binding <see cref="HotkeyBinding.Validate"/> accepts, so a half-edited "No modifier + D"
/// can never reach settings — and a pair that somehow fails validation snaps the display back
/// rather than leaving it showing something the hook does not have.</para>
///
/// <para><b>A modifier change ALWAYS applies; a key it strands is dropped, never the change
/// refused</b> (owner, 2026-08-31). Rows without "None" used to refuse instead, so holding
/// <c>Ctrl + Space</c> and selecting Shift snapped the modifier back and the control looked
/// broken. The blank key box that refusal was avoiding is the honest state: it reads as "choose
/// one", <see cref="Publish"/> classifies it Incomplete so the stored binding is untouched, and it
/// is what the optional rows have always done. One behaviour for every row, one branch fewer.</para>
///
/// <para><b>The row explains nothing it does not have to.</b> A hint line describing the key
/// list's own rules shipped and was REMOVED the next day (owner, 2026-08-31): it appeared whenever
/// a modifier was selected, which is a normal state rather than a problem, so it read as a warning
/// about nothing. Only genuine hazards get text — a binding that shadows a common application
/// shortcut, or a bare modifier whose suppression costs the user characters. Do not reintroduce
/// standing explanatory copy here.</para>
///
/// <para><b>The one exception, and why it is not a reversal of that rule (HKY-10, 2026-09-10).</b>
/// This control now reports <see cref="IsIncomplete"/>, and both hosts render a line while it is
/// true. That is not the removed hint: the removed hint appeared whenever a MODIFIER was selected —
/// a normal, resolved state — whereas this appears only while the pickers hold a modifier with no
/// key, which is an edit the user has not finished. It says what to do and what the row still
/// holds, and it goes away by itself the moment a key is chosen.</para>
///
/// <para>What it fixes is a DISAGREEMENT, not a stale warning. Codex read the leftover amber note
/// as describing "a chord that is not set"; it is set — <see cref="Publish"/> returns before
/// raising, so the stored binding and the hook are untouched, and the note is the only accurate
/// thing on screen. Collapsing it (the first fix proposed) would have hidden a live hazard from a
/// user who then walked away with that chord still bound. Owner decision, 2026-09-10: say the edit
/// is unfinished and keep the hazard.</para>
///
/// <para><b>Two layouts, one control (HKY-8, 2026-08-31).</b> The owner's ask was that onboarding
/// offer the same options as Settings, and the only obstacle was width:
/// <c>OnboardingPage.BuildUI</c> caps its content at 500 px, which leaves ~150 px beside the two
/// fixed-width pickers — not enough for a row title plus the description the wizard shows. So the
/// pickers optionally STACK under the text instead of sitting beside it, and the text optionally
/// carries a second line. Both are layout-only and both default to the Settings shape, so that page
/// is untouched. Narrowing the pickers was refused: the modifier box is pinned at 148 because the
/// owner read "No modifie" at 120.</para>
///
/// <para>WinUI-3 constraint: <see cref="ComboBox"/>, <see cref="Border"/>, <see cref="Grid"/>,
/// <see cref="StackPanel"/> and <see cref="TextBlock"/> only — all already rendered by this page
/// in a CLI-launched build. No templated control the app does not already use. The one template
/// TOUCH is the key list's scroll reset, which READS a <see cref="ScrollViewer"/> out of the open
/// dropdown — reached through the XamlRoot's open popups, since WinUI does not host popup content
/// under the ComboBox — and treats its absence as "leave the scroll alone".</para>
/// </remarks>
internal sealed class HotkeyBindingEditor
{
    public const string NoneLabel = "None (disabled)";
    private const string NoModifierLabel = "No modifier";

    private const double ModifierComboWidth = 148;
    private const double KeyComboWidth = 160;
    private const double PickerSpacing = 8;

    private readonly ComboBox _modifierCombo;
    private readonly ComboBox _keyCombo;
    private readonly bool _includeNone;

    /// <summary>Suppresses <see cref="ValueChanged"/> while the control rewrites itself.</summary>
    private bool _updating;

    /// <summary>The row to place in the settings card.</summary>
    public Border Root { get; }

    /// <summary>The current canonical binding string; empty when unbound.</summary>
    public string Value { get; private set; } = string.Empty;

    /// <summary>Raised only when the user produces a VALID, complete binding.</summary>
    public event Action<string>? ValueChanged;

    /// <summary>
    /// Whether the pickers currently show a modifier with NO key — the state a modifier change
    /// leaves behind when it strands the key (HKY-10).
    /// </summary>
    /// <remarks>
    /// <para>This is a fact about the DISPLAY, never about <see cref="Value"/>. While it is true the
    /// stored binding is deliberately untouched, so the host is describing something the box no
    /// longer shows — which is the whole reason a host needs to be told.</para>
    /// </remarks>
    public bool IsIncomplete { get; private set; }

    /// <summary>
    /// Raised when <see cref="IsIncomplete"/> FLIPS. Advisory only — a host must not persist
    /// anything from it (HKY-10).
    /// </summary>
    /// <remarks>
    /// <para><b>A second event rather than <see cref="ValueChanged"/> with an empty string</b>, which
    /// was the shape the card proposed. Both hosts treat an empty <see cref="ValueChanged"/> as an
    /// intentional CLEAR and write it: <c>SettingsPage</c> calls <c>_viewModel.SetHotkey(role, "")</c>
    /// and the wizard assigns its <c>_selected*</c> field. Reusing it here would unbind the role the
    /// user is halfway through editing — the exact outcome
    /// <see cref="KeySelection.Incomplete"/> exists to prevent.</para>
    ///
    /// <para><b>It flips rather than firing per edit, and the difference is load-bearing.</b>
    /// Rebuilding the binding you started from — Ctrl + C, modifier to None (key stranded), modifier
    /// back to Ctrl, then picking C again — ends at <c>canonical == Value</c>, where
    /// <see cref="Publish"/> returns without raising <see cref="ValueChanged"/>. A host listening
    /// only for that would never learn the row had recovered, and the note would stick.
    /// <see cref="SetIncomplete"/>(false) runs BEFORE that early return, which is what closes it.</para>
    ///
    /// <para><b>Note the key does NOT come back on its own</b>, and an earlier draft of this
    /// paragraph said it did (self-review pass). <see cref="RebuildKeyCombo"/> reads
    /// <c>previous</c> from the LIVE selection, which the outbound rebuild has already set to null
    /// — so <see cref="PreservedKey"/> is handed null on the way back and returns null. The box
    /// stays blank until the user picks a key, which is the honest state
    /// <see cref="Publish"/> classifies Incomplete; the round trip is a four-step route, not
    /// three.</para>
    /// </remarks>
    public event Action? IncompleteChanged;

    /// <param name="title">The row label.</param>
    /// <param name="includeNone">Whether the key picker offers "None (disabled)".</param>
    /// <param name="currentValue">The canonical binding to display initially.</param>
    /// <param name="description">
    /// An optional second line under the title. Null — the Settings shape — renders nothing.
    /// </param>
    /// <param name="stackPickers">
    /// Place the pickers BELOW the text rather than beside it. For narrow hosts; see the layout
    /// paragraph in the type's remarks.
    /// </param>
    public HotkeyBindingEditor(
        string title,
        bool includeNone,
        string currentValue,
        string? description = null,
        bool stackPickers = false)
    {
        _includeNone = includeNone;

        // 148: wide enough for "No modifier" and "Ctrl + Shift" beside the chevron — at 120 the
        // owner's first live look read "No modifie" (2026-08-30).
        _modifierCombo = new ComboBox { Width = ModifierComboWidth, VerticalAlignment = VerticalAlignment.Center };
        _keyCombo = new ComboBox { Width = KeyComboWidth, VerticalAlignment = VerticalAlignment.Center };
        AppTheme.AllowParentScroll(_modifierCombo);
        AppTheme.AllowParentScroll(_keyCombo);

        foreach (var modifiers in HotkeyBinding.SelectableModifiers)
            _modifierCombo.Items.Add(DescribeModifiers(modifiers));

        SetValue(currentValue);

        // A modifier change ALWAYS applies; a key it strands is simply dropped, leaving the key
        // box blank until the user picks a valid one (owner, 2026-08-31).
        //
        // This replaces a REFUSAL that only ever fired on a row without "None": holding
        // Ctrl + Space and selecting Shift silently snapped the modifier back, so the control
        // appeared broken — the owner reported exactly that. The refusal existed to stop the row
        // blanking (Grok diff r1), but a blank key box is the honest state: it reads as "choose
        // one", `Publish` classifies it Incomplete so the stored binding is untouched, and the
        // optional rows have always behaved this way. Uniform behaviour, one branch fewer, and
        // the state the discarded hint existed to explain no longer arises.
        _modifierCombo.SelectionChanged += (_, _) =>
        {
            if (_updating) return;

            // The key list depends on the modifier (typing keys need Ctrl), so rebuild it before
            // deciding whether the pair is still valid.
            RebuildKeyCombo(preserveSelection: true);
            Publish();
        };
        _keyCombo.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            Publish();
        };

        // With nothing selected the popup would otherwise open wherever it last sat — mid-list,
        // for a user who now has to pick from the top (owner, 2026-08-31).
        //
        // It must go through GetOpenPopupsForXamlRoot, NOT a walk down from the ComboBox: WinUI
        // re-parents popup content into the XamlRoot's PopupRoot, so the dropdown is NOT in the
        // ComboBox's own visual subtree and a descendant walk silently finds nothing (read-only
        // review, 2026-08-31 — the first version of this shipped as exactly that no-op). Fail-soft
        // either way, but the miss is LOGGED rather than silent, so a dead feature is visible in a
        // support bundle instead of being believed.
        _keyCombo.DropDownOpened += (_, _) =>
        {
            if (_keyCombo.SelectedIndex >= 0) return;
            try
            {
                var viewer = FindOpenDropDownScrollViewer();
                if (viewer is null)
                {
                    Serilog.Log.Debug("Hotkey key list: no ScrollViewer in the open dropdown — scroll left as it was");
                    return;
                }
                // Only a reset that was NEEDED and then declined is worth a line: ChangeView
                // returns false when the view is already where it was asked to go (Codex diff r1).
                if (viewer.VerticalOffset > 0
                    && !viewer.ChangeView(null, 0, null, disableAnimation: true))
                {
                    Serilog.Log.Debug("Hotkey key list: scroll reset was declined");
                }
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "Hotkey key list: could not reset scroll"); }
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        };

        // One element either way, so the two layouts below differ only in where it goes.
        FrameworkElement text = titleBlock;
        if (!string.IsNullOrEmpty(description))
        {
            var descriptionBlock = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                TextWrapping = TextWrapping.Wrap
            };
            text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { titleBlock, descriptionBlock }
            };
        }

        var pickers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = PickerSpacing,
            HorizontalAlignment = stackPickers ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = stackPickers ? new Thickness(0, 10, 0, 0) : new Thickness(0)
        };
        pickers.Children.Add(_modifierCombo);
        pickers.Children.Add(_keyCombo);

        UIElement body;
        if (stackPickers)
        {
            body = new StackPanel { Children = { text, pickers } };
        }
        else
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(pickers, 1);
            grid.Children.Add(text);
            grid.Children.Add(pickers);
            body = grid;
        }

        Root = new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body
        };
    }

    /// <summary>
    /// Set the displayed binding programmatically. Deliberately does NOT raise
    /// <see cref="ValueChanged"/> — callers use it to revert a rejected edit or to clear a role
    /// they have just taken over, and re-entering their own handler would loop.
    /// </summary>
    public void SetValue(string? canonical)
    {
        _updating = true;
        try
        {
            var parsed = HotkeyBinding.TryParse(canonical, out var binding, out _)
                ? binding
                : (HotkeyBinding?)null;

            _modifierCombo.SelectedItem = DescribeModifiers(parsed?.Modifiers ?? HotkeyModifiers.None);
            RebuildKeyCombo(preserveSelection: false);

            var keyName = parsed.HasValue
                ? Services.Input.HotkeyService.KeyCodeToName(parsed.Value.Trigger)
                : null;
            _keyCombo.SelectedItem = keyName is null
                ? (_includeNone ? NoneLabel : null)
                : HotkeyKeyDisplay.ToDisplayToken(keyName);

            Value = parsed?.Canonical ?? string.Empty;

            // HKY-10: a revert is the OTHER way out of the incomplete state, and it does not go
            // through Publish. Every host calls this to put a rejected or superseded binding back
            // on screen, so without this the note would survive the very edit that resolved it.
            SetIncomplete(IsIncompleteSelection(DisplayedKeyToken()));
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>
    /// The scroller inside the key list's OPEN dropdown, or null when it cannot be reached.
    /// </summary>
    /// <remarks>
    /// <para>The dropdown lives in the <see cref="XamlRoot"/>'s popup root rather than under the
    /// <see cref="ComboBox"/>, so it is reached through the open-popup list.</para>
    ///
    /// <para><b>Ownership is CHECKED, not assumed from ordering.</b> The API documents a
    /// collection of every open popup and promises nothing about order (Codex diff r1), so the
    /// popup is only accepted when its own subtree carries one of this list's items — otherwise a
    /// tooltip or another popup that happened to be open could have its scroller reset instead.
    /// Newest-first only decides which candidate is tried first.</para>
    /// </remarks>
    private ScrollViewer? FindOpenDropDownScrollViewer()
    {
        var xamlRoot = _keyCombo.XamlRoot;
        if (xamlRoot is null) return null;

        var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
        for (var i = popups.Count - 1; i >= 0; i--)
        {
            if (popups[i].Child is not { } child) continue;
            if (!HostsOurItems(child)) continue;

            var viewer = FindScrollViewer(child);
            if (viewer is not null) return viewer;
        }
        return null;
    }

    /// <summary>Does this subtree render one of the key list's own items?</summary>
    private bool HostsOurItems(DependencyObject root)
    {
        if (root is ComboBoxItem item
            && item.Content is string text
            && _keyCombo.Items.Contains(text))
        {
            return true;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (HostsOurItems(VisualTreeHelper.GetChild(root, i))) return true;
        }
        return false;
    }

    /// <summary>
    /// The first <see cref="ScrollViewer"/> in a subtree, or null if there is none.
    /// </summary>
    /// <remarks>
    /// Used only to put the key list back at the top when it opens with nothing selected —
    /// <see cref="ComboBox"/> surfaces no scroll API — so every caller treats null as "leave it
    /// where it is" rather than as a fault.
    /// </remarks>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;

            var nested = FindScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>Enable or disable both pickers together.</summary>
    public void SetEnabled(bool enabled)
    {
        _modifierCombo.IsEnabled = enabled;
        _keyCombo.IsEnabled = enabled;
    }

    /// <summary>
    /// May <paramref name="keyName"/> be paired with <paramref name="modifiers"/>? Mirrors the key
    /// list's own filter — <see cref="RebuildKeyCombo"/> is its only consumer.
    /// </summary>
    internal static bool KeyAllowedWith(HotkeyModifiers modifiers, string keyName)
    {
        if (keyName == NoneLabel) return true;
        if (TypingKeyBlockedBy(modifiers, keyName)) return false;
        // A combo's trigger is never itself a modifier key (HotkeyBinding.Validate), so offering
        // one alongside a modifier can only build a pair that cannot be stored.
        if (modifiers != HotkeyModifiers.None
            && Services.Input.HotkeyService.ModifierKeys.Contains(keyName, StringComparer.Ordinal))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// One half of <see cref="KeyAllowedWith"/>: a typing key needs Ctrl, because Shift+D just
    /// types "D" and a bare "D" binding would suppress every D the user types.
    /// </summary>
    /// <remarks>
    /// It was ALSO the predicate behind a modifier-change refusal, deliberately narrower than the
    /// full rule so that picking Ctrl with the default <c>RightAlt</c> selected was not rejected
    /// on the way to <c>Ctrl+Space</c> (Grok diff review r2). That refusal is gone — a stranded
    /// key is now simply dropped, for every row (owner, 2026-08-31) — so this is once again just
    /// half of the list rule. Kept separate because the halves are independently meaningful and
    /// separately tested, not because a second caller needs it.
    /// </remarks>
    internal static bool TypingKeyBlockedBy(HotkeyModifiers modifiers, string keyName)
        => Services.Input.HotkeyService.TypingKeys.Contains(keyName, StringComparer.Ordinal)
           && (modifiers & HotkeyModifiers.Ctrl) == 0;

    private HotkeyModifiers SelectedModifiers()
    {
        var label = _modifierCombo.SelectedItem as string;
        foreach (var modifiers in HotkeyBinding.SelectableModifiers)
        {
            if (DescribeModifiers(modifiers) == label) return modifiers;
        }
        return HotkeyModifiers.None;
    }

    /// <summary>
    /// Rebuild the key list for the selected modifier: typing keys appear only with Ctrl, and
    /// MODIFIER keys disappear as soon as any modifier is selected.
    /// </summary>
    /// <remarks>
    /// <para>Dropping the modifier keys is what closes the worst reachable defect in this control
    /// (Grok diff review r2), and it happened on the DEFAULT row at the FIRST action: recording is
    /// <c>RightAlt</c>, the user picks <c>Ctrl</c> on the way to <c>Ctrl+Space</c>, the old rebuild
    /// preserved <c>RightAlt</c> because it was still offered, and
    /// <see cref="HotkeyBinding.Validate"/> then rejected the pair — so <see cref="Publish"/>
    /// returned without publishing OR reverting. The row displayed a complete-looking
    /// <c>Ctrl + RightAlt</c> while <see cref="Value"/> and the hook still held <c>RightAlt</c>:
    /// Settings lying about the binding, permanently, if the user walked away.</para>
    ///
    /// <para><b>The fix is here and NOT in a refusal</b>, deliberately: refusing Ctrl
    /// while a modifier key is selected would block <c>RightAlt</c> → <c>Ctrl+Space</c> outright,
    /// because Space is only offered once Ctrl is selected. So the modifier change is kept, the
    /// now-invalid key is dropped, and nothing publishes until the user picks a valid key — a
    /// visibly empty key box that says "choose one", not a plausible wrong pair.</para>
    /// </remarks>
    private void RebuildKeyCombo(bool preserveSelection)
    {
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            var previous = preserveSelection ? _keyCombo.SelectedItem as string : null;
            var modifiers = SelectedModifiers();
            var allowTypingKeys = (modifiers & HotkeyModifiers.Ctrl) != 0;

            _keyCombo.Items.Clear();
            AppTheme.PopulateHotkeyCombo(_keyCombo, _includeNone, allowTypingKeys);

            // ONE rule drives the list, so what is offered and what Validate accepts cannot drift.
            // The rule speaks canonical names; the items are display names, so the removal maps.
            foreach (var name in Services.Input.HotkeyService.AllKeys)
            {
                if (!KeyAllowedWith(modifiers, name))
                    _keyCombo.Items.Remove(HotkeyKeyDisplay.ToDisplayToken(name));
            }

            _keyCombo.SelectedItem = PreservedKey(previous, _keyCombo.Items);
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    /// <summary>
    /// What a rebuild leaves selected: the previous key when it is still offered, otherwise
    /// NOTHING — an incomplete row, for EVERY row kind.
    /// </summary>
    /// <remarks>
    /// <para><b>Auto-selecting "None (disabled)" here was settings data loss</b> (Grok diff review
    /// r3), and it was a regression introduced by r2's own fix. Once modifier keys started leaving
    /// the list, an optional role holding <c>RightAlt</c> lost its selection the moment the user
    /// picked Ctrl on the way to <c>Ctrl+Space</c> — this method then chose <c>NoneLabel</c>, and
    /// <see cref="Publish"/> could not tell that from the user deliberately choosing None, so it
    /// wrote <c>""</c> and unbound paste-last. Finish the edit and it returns; click away and the
    /// hotkey is simply gone.</para>
    ///
    /// <para>The recording row never hit it because it has no None to fall back on — which is the
    /// tell that the two cases were one question wearing two answers. <c>null</c> (incomplete) and
    /// <c>NoneLabel</c> (chosen) are now distinct everywhere, and an AUTOMATIC selection is never
    /// an intentional clear.</para>
    /// </remarks>
    internal static string? PreservedKey(string? previous, IEnumerable<object> offered)
        => previous is not null && offered.Contains(previous) ? previous : null;

    /// <summary>What the key picker's current selection means.</summary>
    internal enum KeySelection
    {
        /// <summary>Nothing selected — a partial edit. NEVER written: the stored binding stands.</summary>
        Incomplete,
        /// <summary>The user chose "None (disabled)" — a real value meaning unbound.</summary>
        Clear,
        /// <summary>A real key is selected.</summary>
        Key
    }

    internal static KeySelection Classify(string? keyName) => keyName switch
    {
        null => KeySelection.Incomplete,
        NoneLabel => KeySelection.Clear,
        _ => KeySelection.Key
    };

    /// <summary>
    /// Whether a key-picker selection is a partial edit — the ONE definition, shared by
    /// <see cref="Publish"/> and <see cref="SetValue"/> (HKY-10).
    /// </summary>
    /// <remarks>
    /// <para>The null test is logically the SAME case as <see cref="KeySelection.Incomplete"/>
    /// (<see cref="Classify"/>'s null branch IS Incomplete, pinned by
    /// <c>Classify_SeparatesIncompleteFromAnIntentionalClear</c>). It is stated separately, and the
    /// parameter carries <see cref="NotNullWhenAttribute"/>, so the compiler's flow analysis can
    /// prove <c>keyName</c> non-null at Publish's call site — a fact Classify's contract guarantees
    /// but a plain <c>bool</c> return cannot convey (CS8604 otherwise). Before HKY-10 the same job
    /// was done by an inline <c>keyName is null || …</c>; extracting it is what keeps Publish and
    /// SetValue from growing two answers to the same question.</para>
    /// </remarks>
    private static bool IsIncompleteSelection([NotNullWhen(false)] string? keyName)
        => keyName is null || Classify(keyName) == KeySelection.Incomplete;

    /// <summary>
    /// The canonical key token the key picker currently shows, or null when it shows nothing.
    /// </summary>
    /// <remarks>Items are display names ("Right Alt"); everything downstream speaks canonical
    /// ("RightAlt"). <see cref="NoneLabel"/> and null pass through the conversion unchanged, so
    /// <see cref="Classify"/> still sees them.</remarks>
    private string? DisplayedKeyToken()
    {
        var displayed = _keyCombo.SelectedItem as string;
        return displayed is null ? null : HotkeyKeyDisplay.ToCanonicalToken(displayed);
    }

    /// <summary>Set <see cref="IsIncomplete"/>, raising <see cref="IncompleteChanged"/> on a flip.</summary>
    /// <remarks>
    /// <para>Raised even while <c>_updating</c> is set, unlike <see cref="ValueChanged"/>. That flag
    /// exists to stop a programmatic rewrite re-entering a host's PERSISTING handler; this event
    /// persists nothing, and suppressing it there is precisely how a revert would leave the note up
    /// after <see cref="SetValue"/> put a complete binding back on screen.</para>
    /// </remarks>
    private void SetIncomplete(bool value)
    {
        if (IsIncomplete == value) return;
        IsIncomplete = value;
        IncompleteChanged?.Invoke();
    }

    /// <summary>
    /// Compose the two pickers into a binding and raise <see cref="ValueChanged"/> — but only if
    /// the result is a complete, valid binding, or an intentional clear.
    /// </summary>
    private void Publish()
    {
        var keyName = DisplayedKeyToken();

        // A partial edit never writes, whether or not this row offers None. Conflating the two
        // is what unbound optional roles mid-edit — see PreservedKey's remarks.
        //
        // HKY-10: it also TELLS the host, which it previously did not. The host's advisory is
        // computed from the STORED binding and refreshed only inside a ValueChanged handler, so a
        // modifier change that stranded the key left an amber hazard note describing a chord the
        // box no longer showed. The note was not stale — the stored binding really is still that
        // chord — but the screen and the note disagreed, and the honest repair is to say the edit
        // is unfinished, not to hide the hazard.
        if (IsIncompleteSelection(keyName))
        {
            SetIncomplete(true);
            return;
        }

        SetIncomplete(false);
        var selection = Classify(keyName);

        if (selection == KeySelection.Clear)
        {
            // The user chose None. A row without None cannot reach here (it never offers it).
            if (!_includeNone) return;
            if (Value.Length == 0) return;
            Value = string.Empty;
            ValueChanged?.Invoke(Value);
            return;
        }

        var trigger = Services.Input.HotkeyService.MapKeyCode(keyName);
        if (!trigger.HasValue) return;

        var candidate = new HotkeyBinding(SelectedModifiers(), trigger.Value);
        if (HotkeyBinding.Validate(candidate) != HotkeyBindingError.None)
        {
            // SNAP BACK rather than return silently. A bare `return` here is exactly how the row
            // came to display an unpublished `Ctrl + RightAlt` (see RebuildKeyCombo's remarks):
            // the pickers kept the invalid pair on screen while Value and the hook held the old
            // binding. The key list no longer offers a pair that reaches this branch, so this is
            // the second line of defence for a shape only the list can produce — kept because
            // this precise failure has already happened once in this change.
            SetValue(Value);
            return;
        }

        var canonical = candidate.Canonical;
        if (canonical == Value) return;
        Value = canonical;
        ValueChanged?.Invoke(canonical);
    }

    /// <summary>The modifier picker's label for a modifier set. Also the reverse lookup's key.</summary>
    internal static string DescribeModifiers(HotkeyModifiers modifiers) => modifiers switch
    {
        HotkeyModifiers.None => NoModifierLabel,
        HotkeyModifiers.Ctrl => "Ctrl",
        HotkeyModifiers.Shift => "Shift",
        _ when modifiers == (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift) => "Ctrl + Shift",
        _ => NoModifierLabel
    };
}
