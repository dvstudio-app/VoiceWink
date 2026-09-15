using SharpHook.Native;

namespace VoiceWink.Helpers;

/// <summary>
/// Decides whether the modifiers a <see cref="HotkeyBinding"/> requires are held right now.
/// Pure: the physical-key probe is injected, so the whole table is unit-testable with no
/// keyboard, no hook and no native call.
/// </summary>
/// <remarks>
/// <para><b>Probed, never latched (HKY-3).</b> The modifiers are read at trigger key-DOWN with
/// <c>GetAsyncKeyState</c> rather than tracked through key-down/key-up bookkeeping. Latching
/// would add a second way for a lost key-up to strand the machine — the failure class
/// <see cref="Services.Input.HotkeyService"/>'s stuck-key recovery already exists to clean up —
/// and it would have to survive rebinds mid-hold. A probe has no state to strand.</para>
///
/// <para><b>Exact match, not superset.</b> A Ctrl+Space binding must NOT fire on
/// Ctrl+Shift+Space: that is a different chord, and the foreground app may well have bound it.
/// So the effective set is compared for equality.</para>
///
/// <para><b>The AltGr rule.</b> AltGr is delivered as a synthetic LeftControl press alongside
/// RightAlt, so on a European layout every Ctrl+X binding would fire while the user typed
/// <c>€</c> or <c>@</c> — hitting exactly the people two-key combos were added for. On an AltGr
/// layout a physically-down LeftControl therefore does not satisfy Ctrl while RightAlt is also
/// down. This generalises the 50 ms phantom-LeftControl heuristic in <c>HandleKeyDown</c>,
/// which only ever covered the RightAlt default binding.</para>
///
/// <para><b>Accepted trade, stated rather than hidden:</b> a user genuinely holding a real Ctrl
/// AND AltGr at the same time will not match a Ctrl combo. There is no signal that separates
/// that from AltGr alone — Windows reports the same two keys down — so the choice is which way
/// to be wrong, and being wrong on a chord nobody presses beats being wrong on ordinary typing.
/// RightControl is unaffected and always satisfies Ctrl, so that user has a working chord.</para>
/// </remarks>
internal static class HotkeyModifierMatch
{
    /// <summary>
    /// The modifier set effectively held, given a physical-key probe.
    /// </summary>
    /// <remarks>
    /// Assumes the trigger key is NOT itself a modifier — <see cref="HotkeyBinding.Validate"/>
    /// guarantees it for every combo, and single-key bindings never reach here (see
    /// <see cref="IsSatisfiedBy"/>). Without that guarantee a modifier trigger would count
    /// itself as held and no combo could ever match.
    /// </remarks>
    public static HotkeyModifiers Effective(Func<KeyCode, bool> isPhysicallyDown, bool altGrLayout)
    {
        var modifiers = HotkeyModifiers.None;

        // AltGr is a synthetic LeftControl alongside RightAlt. On a layout that has it, that
        // PAIR contributes nothing at all — neither Ctrl (or every Ctrl combo would fire while
        // the user typed € or @) nor Alt (or the pair would present as a held Alt and break an
        // otherwise-good match). RightControl and LeftAlt are never part of the pair, so they
        // always count for themselves.
        var rightAltDown = isPhysicallyDown(KeyCode.VcRightAlt);
        var altGrEngaged = altGrLayout && rightAltDown;

        var ctrl = isPhysicallyDown(KeyCode.VcRightControl)
            || (isPhysicallyDown(KeyCode.VcLeftControl) && !altGrEngaged);
        if (ctrl) modifiers |= HotkeyModifiers.Ctrl;

        if (isPhysicallyDown(KeyCode.VcLeftShift) || isPhysicallyDown(KeyCode.VcRightShift))
            modifiers |= HotkeyModifiers.Shift;

        // Alt and Win are OBSERVED though they cannot be bound. Omitting them would make
        // Ctrl+Alt+Space present as plain Ctrl and match a Ctrl+Space binding, suppressing a
        // chord the foreground app may own (Codex plan review round 1).
        if (isPhysicallyDown(KeyCode.VcLeftAlt) || (rightAltDown && !altGrEngaged))
            modifiers |= HotkeyModifiers.Alt;

        if (isPhysicallyDown(KeyCode.VcLeftMeta) || isPhysicallyDown(KeyCode.VcRightMeta))
            modifiers |= HotkeyModifiers.Win;

        return modifiers;
    }

    /// <summary>
    /// Whether <paramref name="binding"/>'s modifier requirement is met.
    /// </summary>
    /// <remarks>
    /// A single-key binding short-circuits to true, and that is the compatibility guarantee:
    /// before HKY-3 the hook consulted no modifier state at all, so a RightAlt binding fired
    /// whether or not Ctrl happened to be held. Falling through to the exact-match comparison
    /// would silently change that — a user holding Shift would find their hotkey dead.
    /// </remarks>
    public static bool IsSatisfiedBy(
        HotkeyBinding binding,
        Func<KeyCode, bool> isPhysicallyDown,
        bool altGrLayout)
    {
        if (binding.IsSingleKey) return true;
        return Effective(isPhysicallyDown, altGrLayout) == binding.Modifiers;
    }
}
