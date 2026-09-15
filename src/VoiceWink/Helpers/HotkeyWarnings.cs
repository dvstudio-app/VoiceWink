using SharpHook.Native;

namespace VoiceWink.Helpers;

/// <summary>
/// The ONE copy of the amber advisory shown under a hotkey choice — shared by the Settings rows and
/// the onboarding wizard, which previously carried two hand-maintained copies of the same switch
/// (each page's comment pointed at the other; HKY-4 made them diverge, which is what forced the
/// extraction).
/// </summary>
/// <remarks>
/// <para><b>HKY-8 (2026-08-31) moved the OUTER rule here too.</b> The bare-modifier switch was
/// extracted in HKY-4, but the two-branch rule around it — shadow first, bare key otherwise — stayed
/// inline in <c>SettingsPage.UpdateHotkeyWarning</c>, because onboarding could not create a combo
/// and therefore never needed the shadow branch. The moment the wizard became a combo editor that
/// stopped being true: a first-run user could bind <c>Ctrl+C</c> and be told nothing. Re-extracting
/// the half that was left behind is what stops the copies diverging a second time.</para>
///
/// <para>Only a bare modifier carries the <see cref="BareKeyWarning"/> hazards — as part of a combo
/// the same key is pressed deliberately alongside another — so that arm is consulted only for
/// single-key bindings.</para>
///
/// <para><b>The Right Ctrl and Right Alt arms state the SUPPRESSION cost precisely, because the
/// generic copy contradicted the HKY-4 seed.</b> A bound bare modifier's key-down is suppressed
/// (it reaches neither the foreground app nor OS key state), so chords THROUGH that key are
/// absorbed while it is bound. For the seeded Right Ctrl default that is an owner-accepted
/// residual (HKY-4 card: those chords duplicate Left Ctrl, where AltGr victims lose characters
/// outright) — but the old text, "Ctrl is used by many keyboard shortcuts", read as a warning
/// against the app's own suggestion on every AltGr first run. Left Ctrl keeps the stronger copy:
/// it IS the hand most shortcuts are pressed with.</para>
///
/// <para>The Right Alt arm is NEW and layout-gated: before HKY-4 nothing warned the one user the
/// whole feature exists for — someone on an AltGr layout picking Right Alt back — while Shift got
/// a warning about capitals. <paramref name="anyAltGrLayout"/> comes from
/// <see cref="KeyboardLayoutProfile.AnyInstalledLayoutNeedsAltGrForTyping"/> — the NARROW
/// essential-characters probe, so United States-International (€ on AltGr, essentials off it)
/// does not warn (owner-reported false alarm, 2026-08-30). It fails toward true; on a probe
/// failure the warning shows for a US-layout user, mildly wrong copy in the safe
/// direction.</para>
/// </remarks>
internal static class HotkeyWarnings
{
    /// <summary>
    /// The amber advisory for a whole binding, or null when it carries none. The ONE rule both the
    /// Settings rows and the onboarding wizard render.
    /// </summary>
    /// <remarks>
    /// <para><b>Branch order is load-bearing and the two arms are disjoint by construction</b>, which
    /// is why this reads as a priority rather than as two independent messages:
    /// <see cref="HotkeyBinding.ShadowsCommonAppShortcut"/> returns false unless the modifier set is
    /// exactly Ctrl, so a shadowing binding is never single-key and can never also produce a
    /// <see cref="BareKeyWarning"/>. Stated because the plan for HKY-8 proposed a test row for
    /// "shadow AND single-key, shadow wins" — a pair no input can construct (Codex plan review).
    /// The order stays explicit anyway: if the shadow set ever grows a bare-key member, one message
    /// still shows rather than two, and it is the more specific one.</para>
    ///
    /// <para><paramref name="anyAltGrLayout"/> is threaded through rather than probed here so the
    /// rule stays pure — both callers evaluate
    /// <see cref="KeyboardLayoutProfile.AnyInstalledLayoutNeedsAltGrForTyping"/> once per screen
    /// build rather than once per keystroke.</para>
    /// </remarks>
    internal static string? BindingWarning(HotkeyBinding binding, bool anyAltGrLayout)
    {
        if (HotkeyBinding.ShadowsCommonAppShortcut(binding))
        {
            return $"{HotkeyKeyDisplay.Describe(binding.Canonical)} is a common application shortcut — VoiceWink will take it over.";
        }

        return binding.IsSingleKey ? BareKeyWarning(binding.Trigger, anyAltGrLayout) : null;
    }

    /// <summary>
    /// The line shown while a row's pickers hold a modifier with NO key yet (HKY-10).
    /// </summary>
    /// <remarks>
    /// <para><b>It ACCOMPANIES <see cref="BindingWarning"/>, never replaces it.</b> The stored
    /// binding is untouched while a row is incomplete — <c>HotkeyBindingEditor.Publish</c> returns
    /// before raising — so any hazard that binding carries is still live and still has to be shown.
    /// Both hosts render this first, in the same slot as the wizard's refusal note: it explains an
    /// action the user just took, where the rest describe standing state.</para>
    ///
    /// <para><b>Two arms because "still" needs something to name</b> — and the SECOND one is the
    /// routine case, not the first. A row reaches the incomplete state by having a key STRANDED,
    /// which means it was bound a moment ago, so <paramref name="storedBinding"/> is normally
    /// non-empty and the sentence names that chord.</para>
    ///
    /// <para><b>An unbound optional row does NOT reach it by changing its modifier</b>, and an
    /// earlier draft of these remarks said it did (Codex diff r1 Blocker — the same false premise
    /// also reached a UAT row, which is how it was caught). "None (disabled)" is a const on
    /// <c>HotkeyBindingEditor</c>, NOT a member of <c>HotkeyService.AllKeys</c>, so
    /// <c>RebuildKeyCombo</c>'s removal loop — which iterates exactly that set — can never remove
    /// it: None survives every modifier, <c>PreservedKey</c> keeps it, and <c>Publish</c> reads an
    /// intentional clear rather than an incomplete edit. <b>That is correct and must not be
    /// "fixed":</b> nothing was stranded, the row is off because the user turned it off, and a
    /// "choose a key" line over a resolved None selection is precisely the standing explanatory
    /// copy <c>HotkeyBindingEditor</c>'s remarks forbid — a hint line shipped once and the owner
    /// removed it the next day for appearing in a normal state. The reachable optional-row case is
    /// a BOUND one: paste-last at Ctrl + J, modifier to No modifier, J stranded, "…it stays
    /// Ctrl + J."</para>
    ///
    /// <para>So the empty arm guards a DEGENERATE input rather than a routine one — a row that is
    /// incomplete while holding nothing (an empty or unparseable persisted value on a row whose
    /// host is already subscribed). It is kept because the parameter's own type admits null and
    /// empty, and <see cref="HotkeyKeyDisplay.Describe"/> given an empty string would put a blank
    /// gap after "stays"; a total function over its declared domain is not speculative
    /// hardening.</para>
    ///
    /// <para>Lives here rather than in either page for the reason the whole class exists: this copy
    /// is rendered by the Settings rows AND the onboarding wizard, and the two hand-maintained
    /// copies that preceded <see cref="BindingWarning"/> diverged.</para>
    /// </remarks>
    /// <param name="rowTitle">The row's own label, e.g. "Recording hotkey".</param>
    /// <param name="storedBinding">The canonical binding the row still holds; empty when unbound.</param>
    internal static string IncompleteBindingNote(string rowTitle, string? storedBinding)
        => string.IsNullOrEmpty(storedBinding)
            ? $"{rowTitle}: choose a key. Until you do, it stays off."
            : $"{rowTitle}: choose a key. Until you do, it stays {HotkeyKeyDisplay.Describe(storedBinding)}.";

    /// <summary>The advisory for a bare-modifier trigger, or null when the key carries none.</summary>
    internal static string? BareKeyWarning(KeyCode trigger, bool anyAltGrLayout) => trigger switch
    {
        KeyCode.VcLeftShift or KeyCode.VcRightShift
            => "Shift may interfere with typing (capitals and special characters).",
        KeyCode.VcLeftAlt
            => "Left Alt may conflict with application menu bars.",
        KeyCode.VcLeftControl
            => "Ctrl is used by many keyboard shortcuts (Ctrl+C, Ctrl+V, etc.).",
        KeyCode.VcRightControl
            => "Shortcuts pressed with Right Ctrl won't work while it is your hotkey — Left Ctrl still does.",
        KeyCode.VcRightAlt when anyAltGrLayout
            => "Right Alt types characters like € and @ on your keyboard layout — those stop working while it is your hotkey.",
        _ => null
    };
}
