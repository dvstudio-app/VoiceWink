using SharpHook.Native;
using VoiceWink.Services.Input;

namespace VoiceWink.Helpers;

/// <summary>
/// Modifier keys, as both a binding REQUIREMENT and an observation of what is held.
/// </summary>
/// <remarks>
/// <para><b>Alt and Win are observable but NOT bindable</b> (HKY-3, 2026-08-30), and the
/// distinction is load-bearing rather than cosmetic. They cannot be REQUIRED — see
/// <see cref="HotkeyBinding"/>'s remarks for the mechanism, and HKY-6 for the design that
/// lifts it — but they must be OBSERVED, because matching compares the held set for exact
/// equality: without an Alt flag to report, holding Ctrl+Alt+Space would present as plain
/// Ctrl and match (and therefore suppress) a Ctrl+Space binding, stealing a chord the
/// foreground app may own. Codex plan review round 1 found exactly that.</para>
/// </remarks>
[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    /// <summary>Observable only — <see cref="HotkeyBinding.Validate"/> refuses it as a requirement.</summary>
    Alt = 1 << 2,
    /// <summary>Observable only — <see cref="HotkeyBinding.Validate"/> refuses it as a requirement.</summary>
    Win = 1 << 3,
}

/// <summary>Why <see cref="HotkeyBinding.TryParse"/> refused a string.</summary>
internal enum HotkeyBindingError
{
    None = 0,
    /// <summary>Null, empty or whitespace. Callers treat this as "unbound", not as a defect.</summary>
    Empty,
    /// <summary>A token matched neither a modifier name nor a key name.</summary>
    UnknownToken,
    /// <summary>The same modifier appeared twice ("Ctrl+Ctrl+D").</summary>
    DuplicateModifier,
    /// <summary>Modifiers with no key ("Ctrl", "Ctrl+Shift").</summary>
    NoTrigger,
    /// <summary>More than one non-modifier key ("Ctrl+A+B").</summary>
    MultipleTriggers,
    /// <summary>A modifier KEY used as the trigger of a combo ("Ctrl+RightAlt").</summary>
    ModifierTriggerWithModifiers,
    /// <summary>A letter, digit or Space without Ctrl ("D", "Shift+D", "Space").</summary>
    TypingKeyNeedsCtrl,
    /// <summary>Alt or Win required. Observable, but not bindable until HKY-6.</summary>
    UnsupportedModifier,
}

/// <summary>
/// A hotkey binding: zero or more <see cref="HotkeyModifiers"/> plus exactly one trigger key.
/// Pure — no native calls, no settings access — so the whole decision table is unit-testable.
/// </summary>
/// <remarks>
/// <para><b>Zero modifiers is the pre-HKY-3 behaviour, exactly.</b> "RightAlt" parses to
/// <c>{ None, VcRightAlt }</c> and renders back as "RightAlt", so every value already in
/// <c>settings.json</c> round-trips byte-for-byte. There is no schema change and no migration:
/// each hotkey role is still ONE string.</para>
///
/// <para><b>The load-bearing invariant: a combo's trigger is never itself a modifier key.</b>
/// <see cref="HotkeyBindingError.ModifierTriggerWithModifiers"/> enforces it. This is not
/// tidiness — it is what makes the feature safe to add to a state machine this old. Every
/// existing single-key modifier binding keeps taking the identical code path it took before:
/// side-specific matching, no modifier probe, and the paste-window key-up pass-through,
/// <c>GetPhantomProneModifierVks</c> and the AltGr ownership transfer in
/// <see cref="HotkeyService"/> all reached exactly as before. A combo cannot regress any of
/// them because a combo can never enter them.</para>
///
/// <para><b>Why no Alt and no Win.</b> A modifier key-DOWN can never be suppressed — at that
/// instant we do not yet know whether the combo will complete — so the foreground app always
/// sees Alt-down, and we then suppress the trigger, so it sees nothing before Alt-up. Windows
/// activates the menu bar on a bare Alt press and opens Start on a bare Win press, so an
/// Alt-based binding would pop the target app's menu on every dictation. Suppressing the Alt
/// key-up makes it worse, not better: a suppressed low-level-hook event never updates OS key
/// state, so the OS would believe Alt was latched down after every recording — the
/// phantom-stuck-modifier bug <c>ModifierReleasePlan</c> exists to clean up, manufactured on
/// purpose on the happy path. The real fix is a masking keystroke injected from the hook
/// callback thread, which needs injected-event filtering and a share of the
/// LowLevelHooksTimeout budget. That is HKY-6's design. Ctrl and Shift need none of it:
/// Ctrl-up and Shift-up are inert in every app.</para>
/// </remarks>
internal readonly record struct HotkeyBinding(HotkeyModifiers Modifiers, KeyCode Trigger)
{
    /// <summary>
    /// Modifier token spellings accepted on input, in canonical RENDER order. Canonical output is
    /// the first of each pair. Alt and Win parse — so a stored or hand-edited "Alt+D" produces the
    /// honest <see cref="HotkeyBindingError.UnsupportedModifier"/> rather than a misleading
    /// "unknown token" — and are then refused by <see cref="Validate"/>.
    /// </summary>
    private static readonly (string Canonical, string Alias, HotkeyModifiers Flag)[] ModifierTokens =
    [
        ("Ctrl", "Control", HotkeyModifiers.Ctrl),
        ("Shift", "Shift", HotkeyModifiers.Shift),
        ("Alt", "Alt", HotkeyModifiers.Alt),
        ("Win", "Meta", HotkeyModifiers.Win),
    ];

    /// <summary>The modifiers a binding may REQUIRE. Alt and Win are observable but not bindable.</summary>
    public const HotkeyModifiers BindableModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;

    /// <summary>Every modifier combination the Settings editor offers, in display order.</summary>
    public static readonly HotkeyModifiers[] SelectableModifiers =
    [
        HotkeyModifiers.None,
        HotkeyModifiers.Ctrl,
        HotkeyModifiers.Shift,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Shift,
    ];

    /// <summary>True when this binding needs no modifier — i.e. it behaves exactly as a pre-HKY-3 binding.</summary>
    public bool IsSingleKey => Modifiers == HotkeyModifiers.None;

    /// <summary>
    /// The canonical string form: modifiers in a FIXED order (Ctrl, Shift) then the key name,
    /// joined by "+". A single-key binding renders as the bare key name.
    /// </summary>
    public string Canonical
    {
        get
        {
            var keyName = HotkeyService.KeyCodeToName(Trigger);
            if (keyName is null) return string.Empty;
            if (Modifiers == HotkeyModifiers.None) return keyName;

            var parts = new List<string>(3);
            foreach (var (canonical, _, flag) in ModifierTokens)
            {
                if ((Modifiers & flag) != 0) parts.Add(canonical);
            }
            parts.Add(keyName);
            return string.Join("+", parts);
        }
    }

    public override string ToString() => Canonical;

    /// <summary>
    /// Parse a stored or user-selected binding string. Accepts any casing and surrounding
    /// whitespace on each token; <paramref name="binding"/> is only meaningful when this
    /// returns true.
    /// </summary>
    public static bool TryParse(string? value, out HotkeyBinding binding, out HotkeyBindingError error)
    {
        binding = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = HotkeyBindingError.Empty;
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        KeyCode? trigger = null;

        foreach (var rawToken in value.Split('+'))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                // "Ctrl+", "Ctrl++D", " +D" — a separator with nothing beside it.
                error = HotkeyBindingError.UnknownToken;
                return false;
            }

            var flag = MatchModifierToken(token);
            if (flag != HotkeyModifiers.None)
            {
                if ((modifiers & flag) != 0)
                {
                    error = HotkeyBindingError.DuplicateModifier;
                    return false;
                }
                modifiers |= flag;
                continue;
            }

            var keyName = MatchKeyName(token);
            if (keyName is null)
            {
                error = HotkeyBindingError.UnknownToken;
                return false;
            }
            if (trigger.HasValue)
            {
                error = HotkeyBindingError.MultipleTriggers;
                return false;
            }
            trigger = HotkeyService.MapKeyCode(keyName);
        }

        if (!trigger.HasValue)
        {
            error = HotkeyBindingError.NoTrigger;
            return false;
        }

        var candidate = new HotkeyBinding(modifiers, trigger.Value);
        error = Validate(candidate);
        if (error != HotkeyBindingError.None) return false;

        binding = candidate;
        return true;
    }

    /// <summary>
    /// Semantic rules that survive parsing. Kept separate from token scanning so the UI can
    /// validate a (modifier, key) pair the user assembled from two dropdowns without
    /// round-tripping it through a string first.
    /// </summary>
    public static HotkeyBindingError Validate(HotkeyBinding candidate)
    {
        if ((candidate.Modifiers & ~BindableModifiers) != 0)
        {
            // Observable, deliberately not bindable — see HotkeyModifiers' remarks and HKY-6.
            return HotkeyBindingError.UnsupportedModifier;
        }

        if (candidate.Modifiers != HotkeyModifiers.None
            && HotkeyService.IsModifierKeyCode(candidate.Trigger))
        {
            // See the type remarks: combos never have a modifier trigger.
            return HotkeyBindingError.ModifierTriggerWithModifiers;
        }

        if (IsTypingKey(candidate.Trigger) && (candidate.Modifiers & HotkeyModifiers.Ctrl) == 0)
        {
            // A bare "D" binding would suppress every D the user types. Shift is NOT enough:
            // Shift+D types "D" and Shift+Space types a space, so a Shift-only combo over a
            // typing key eats ordinary typing just as thoroughly as a bare key does.
            return HotkeyBindingError.TypingKeyNeedsCtrl;
        }

        return HotkeyBindingError.None;
    }

    /// <summary>
    /// True when the trigger is a key people type with: a letter, a digit, or Space.
    /// Drives the Ctrl requirement in <see cref="Validate"/>.
    /// </summary>
    public static bool IsTypingKey(KeyCode keyCode)
    {
        var name = HotkeyService.KeyCodeToName(keyCode);
        return name is not null && HotkeyService.TypingKeys.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    /// True for combos that shadow a near-universal application shortcut (Ctrl+C, Ctrl+V, …).
    /// ADVISORY only — the Settings page confirms rather than refuses, because "universal" is
    /// a claim about other people's apps and a user who wants Ctrl+P for dictation is entitled
    /// to it. Deliberately NOT an <see cref="HotkeyBindingError"/>: an error would make the
    /// binding unstorable.
    /// </summary>
    public static bool ShadowsCommonAppShortcut(HotkeyBinding binding)
    {
        if (binding.Modifiers != HotkeyModifiers.Ctrl) return false;
        var name = HotkeyService.KeyCodeToName(binding.Trigger);
        return name is "A" or "C" or "F" or "N" or "O" or "P" or "S" or "T" or "V" or "W" or "X" or "Y" or "Z";
    }

    /// <summary>
    /// Do two stored binding strings clash — i.e. can both not be bound at once?
    /// </summary>
    /// <remarks>
    /// <para>THE one comparison every conflict check must use (Settings, the prompt dialog, the
    /// view model). Raw string equality is not sufficient in two directions, and both are
    /// reachable: <c>"Control + space"</c> and <c>"Ctrl+Space"</c> are the same binding spelled
    /// differently, while <c>"F6"</c> and <c>"Ctrl+F6"</c> are different strings that still
    /// clash — the single-key one requires no modifiers, so whichever role is tested first in
    /// <c>HandleKeyDown</c> wins BOTH presses and the other hotkey is silently dead.</para>
    ///
    /// <para>Unparseable values never clash with anything: they are already treated as disabled,
    /// and reporting a conflict against one would block a user from binding a key that is not
    /// actually in use (Codex plan review round 2).</para>
    /// </remarks>
    public static bool Conflicts(string? a, string? b)
    {
        if (!TryParse(a, out var first, out _)) return false;
        if (!TryParse(b, out var second, out _)) return false;
        if (first.Equals(second)) return true;
        if (first.Trigger == second.Trigger && (first.IsSingleKey || second.IsSingleKey)) return true;
        return BareModifierBlocksCombo(first, second) || BareModifierBlocksCombo(second, first);
    }

    /// <summary>
    /// Does a BARE modifier hotkey swallow a combo that requires that same modifier?
    /// </summary>
    /// <remarks>
    /// The triggers differ, so nothing above catches it — but the two cannot coexist (Codex diff
    /// review r2). Binding one hotkey to <c>LeftControl</c> and another to <c>Ctrl+D</c> means
    /// pressing Ctrl starts the bare hotkey's gesture, and <c>HandleKeyDown</c>'s
    /// "another hotkey is active" guard then suppresses the D that was supposed to complete the
    /// combo: the combo can never fire while its own modifier is bound to something else. Reported
    /// as a conflict so the UI makes the user choose, rather than storing a silently dead hotkey.
    /// </remarks>
    private static bool BareModifierBlocksCombo(HotkeyBinding bare, HotkeyBinding combo)
    {
        if (!bare.IsSingleKey || combo.IsSingleKey) return false;
        var supplied = ModifierSuppliedBy(bare.Trigger);
        return supplied != HotkeyModifiers.None && (combo.Modifiers & supplied) != 0;
    }

    /// <summary>Which bindable modifier a modifier KEY provides, or None for a non-modifier key.</summary>
    private static HotkeyModifiers ModifierSuppliedBy(KeyCode trigger) => trigger switch
    {
        KeyCode.VcLeftControl or KeyCode.VcRightControl => HotkeyModifiers.Ctrl,
        KeyCode.VcLeftShift or KeyCode.VcRightShift => HotkeyModifiers.Shift,
        _ => HotkeyModifiers.None
    };

    /// <summary>
    /// Would a PROMPT hotkey be unreachable because a ROLE claims every press of its trigger?
    /// </summary>
    /// <remarks>
    /// <para><b>Not the same question as <see cref="Conflicts"/>, and using that one here destroys
    /// data</b> (Grok diff review round 1). <c>Conflicts</c> is role-vs-role, where the roles are
    /// tested in a fixed order and a single-key match short-circuits — so <c>paste-last F6</c> really
    /// does kill <c>recording Ctrl+F6</c>. Prompts are tested LAST, only once no role branch claimed
    /// the press, so a COMBO role does not shadow them: <c>prompt F6</c> beside
    /// <c>recording Ctrl+F6</c> is two working chords.</para>
    ///
    /// <para>With <c>Conflicts</c> on this path, binding recording to <c>Ctrl+F6</c> told the user
    /// "Ctrl+F6 is assigned to enhancement prompt X. It will be cleared" and then cleared it — a
    /// destructive action justified by a false claim, on a prompt that would have kept working.</para>
    /// </remarks>
    public static bool RoleShadowsPrompt(string? roleBinding, string? promptKey)
    {
        if (!TryParse(roleBinding, out var role, out _)) return false;
        if (!TryParse(promptKey, out var prompt, out _)) return false;
        // Only a single-key role claims every press of its trigger. (Prompts are single-key until
        // HKY-7; the check is written to stay correct when they are not.)
        return role.IsSingleKey && prompt.IsSingleKey && role.Trigger == prompt.Trigger;
    }

    /// <summary>
    /// The question every role-vs-prompt UI check asks: must the user resolve this pairing?
    /// </summary>
    /// <remarks>
    /// TWO independent ways a role and a prompt can be incompatible, and they point in opposite
    /// directions — which is why one predicate serves the UI and the two precise ones stay
    /// separate underneath:
    /// <list type="bullet">
    /// <item>the ROLE hides the PROMPT (<see cref="RoleShadowsPrompt"/>) — a single-key role
    /// claims every press of the trigger, so the prompt branch is never reached;</item>
    /// <item>the PROMPT hides the ROLE (<see cref="BareModifierBlocksCombo"/>) — a bare
    /// <c>LeftControl</c> prompt starts its own gesture, and the active-gesture guard then eats
    /// the key that was supposed to complete a <c>Ctrl+D</c> role.</item>
    /// </list>
    /// Both directions are tested even though prompts are single-key today, so this stays correct
    /// when HKY-7 gives them modifiers.
    /// </remarks>
    public static bool RolePromptConflict(string? roleBinding, string? promptKey)
    {
        if (RoleShadowsPrompt(roleBinding, promptKey)) return true;
        if (!TryParse(roleBinding, out var role, out _)) return false;
        if (!TryParse(promptKey, out var prompt, out _)) return false;
        return BareModifierBlocksCombo(prompt, role) || BareModifierBlocksCombo(role, prompt);
    }

    private static HotkeyModifiers MatchModifierToken(string token)
    {
        foreach (var (canonical, alias, flag) in ModifierTokens)
        {
            if (string.Equals(token, canonical, StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, alias, StringComparison.OrdinalIgnoreCase))
            {
                return flag;
            }
        }
        return HotkeyModifiers.None;
    }

    /// <summary>
    /// Resolve a token to its canonically-spelled key name, case-insensitively.
    /// <see cref="HotkeyService.MapKeyCode"/> is an exact-match switch, so a stored "rightalt"
    /// would otherwise fail to parse while rendering fine.
    /// </summary>
    private static string? MatchKeyName(string token)
    {
        foreach (var name in HotkeyService.AllKeys)
        {
            if (string.Equals(token, name, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }
}
