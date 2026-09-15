using SharpHook.Native;

namespace VoiceWink.Helpers;

/// <summary>
/// Every hotkey binding the hook matches against, as ONE immutable snapshot.
/// </summary>
/// <remarks>
/// <para><b>Why a reference type, and why one object.</b> Before HKY-3 each role was a single
/// <c>volatile KeyCode</c> field, which is atomic on its own. A combo is a trigger PLUS a
/// modifier requirement, and <c>volatile</c> cannot be applied to a struct field — so keeping
/// them as two parallel fields would publish them separately, and the hook thread could observe
/// a NEW trigger beside the OLD modifiers. Rebinding <c>RightAlt</c> → <c>Ctrl+Space</c> would
/// then present, for one window, as "Space, no modifier required" — a binding that suppresses
/// every space the user types. Codex plan review round 1 caught this.</para>
///
/// <para>A single reference is assigned atomically, so the hook thread sees either the whole old
/// configuration or the whole new one, never a mixture. That also removes the pre-existing (and
/// benign) skew where <c>ReloadHotkeys</c> and <c>RegisterPromptHotkeys</c> wrote five fields
/// independently.</para>
///
/// <para><b>What is deliberately NOT in here:</b> the held-key latches and the active-gesture
/// key. Those are KEY-owned and written only by the hook thread, because the F4 rule is that a
/// held key finishes its gesture under the binding it STARTED with, whatever it is bound to now.
/// Folding them into a swappable snapshot would reintroduce exactly the rebind-while-held race
/// that ownership model exists to prevent.</para>
///
/// <para><b>Prompt hotkeys are single-key until HKY-7.</b> The map is trigger-keyed, so two
/// prompts cannot yet be distinguished by modifiers. They still live in the snapshot so that
/// publication stays atomic across roles and prompts together.</para>
/// </remarks>
internal sealed class HotkeyConfiguration
{
    public static readonly IReadOnlyDictionary<KeyCode, string> NoPrompts
        = new Dictionary<KeyCode, string>();

    public HotkeyConfiguration(
        HotkeyBinding recording,
        HotkeyBinding? pasteLast,
        HotkeyBinding? redoLast,
        HotkeyBinding? generateImage,
        IReadOnlyDictionary<KeyCode, string>? prompts = null)
    {
        Recording = recording;
        PasteLast = pasteLast;
        RedoLast = redoLast;
        GenerateImage = generateImage;
        // COPY rather than alias. IReadOnlyDictionary is an interface, not a guarantee: the
        // caller still holds a mutable reference to the backing Dictionary, so aliasing would
        // let a later mutation reach the hook thread outside the volatile publication this type
        // exists to provide (Codex plan review round 2). The map is small and built once per
        // prompt-set change, so the copy is free.
        Prompts = prompts is null || prompts.Count == 0
            ? NoPrompts
            : new Dictionary<KeyCode, string>(prompts);
    }

    /// <summary>Always present — an unparseable setting falls back to RightAlt, as it always did.</summary>
    public HotkeyBinding Recording { get; }

    public HotkeyBinding? PasteLast { get; }
    public HotkeyBinding? RedoLast { get; }
    public HotkeyBinding? GenerateImage { get; }

    /// <summary>Trigger key → prompt id. Single-key only until HKY-7.</summary>
    public IReadOnlyDictionary<KeyCode, string> Prompts { get; }

    /// <summary>A copy carrying a new prompt map — the swap <c>RegisterPromptHotkeys</c> performs.</summary>
    public HotkeyConfiguration WithPrompts(IReadOnlyDictionary<KeyCode, string> prompts)
        => new(Recording, PasteLast, RedoLast, GenerateImage, prompts);

    /// <summary>A copy carrying new role bindings — the swap <c>ReloadHotkeys</c> performs.</summary>
    public HotkeyConfiguration WithRoles(
        HotkeyBinding recording,
        HotkeyBinding? pasteLast,
        HotkeyBinding? redoLast,
        HotkeyBinding? generateImage)
        => new(recording, pasteLast, redoLast, generateImage, Prompts);

    /// <summary>Every role binding, in the order <c>HandleKeyDown</c> tests them.</summary>
    public IEnumerable<HotkeyBinding> RoleBindings()
    {
        if (PasteLast.HasValue) yield return PasteLast.Value;
        if (RedoLast.HasValue) yield return RedoLast.Value;
        if (GenerateImage.HasValue) yield return GenerateImage.Value;
        yield return Recording;
    }

    /// <summary>
    /// May a prompt hotkey on <paramref name="trigger"/> not be registered — because a role
    /// makes it unreachable, or because it would make a role unreachable?
    /// </summary>
    /// <remarks>
    /// <para>The FULL role↔prompt rule (<see cref="HotkeyBinding.RolePromptConflict"/>), in both
    /// directions, because registration is the last line of defence for PERSISTED state — a
    /// settings import or hand-edited prompts file bypasses every UI check. Two ways the pairing
    /// is broken:</para>
    /// <list type="bullet">
    /// <item>a SINGLE-KEY role on the prompt's trigger claims every press, so the prompt branch
    /// — matched last in <c>HandleKeyDown</c> — is never reached;</item>
    /// <item>a BARE-MODIFIER prompt blocks a COMBO role that needs that modifier: pressing Ctrl
    /// (the first key of <c>Ctrl+D</c>, every time) fires the prompt's gesture, and the
    /// active-gesture guard then suppresses the D — the combo is structurally dead and every
    /// Ctrl press starts a prompt recording. Codex post-merge review (round 7) found registration
    /// checking only the first direction while every UI path had checked both since round 2.</item>
    /// </list>
    /// <para>What is deliberately NOT blocked, pinned by tests: <c>prompt F6</c> beside
    /// <c>role Ctrl+F6</c> — a combo role does not claim every press of its trigger, so both
    /// chords work, and treating that as a conflict once DELETED live prompt hotkeys (round 1).
    /// The pre-HKY-3 code compared trigger keys alone, which was right only while every binding
    /// was single-key.</para>
    /// </remarks>
    public bool IsPromptBindingBlocked(KeyCode trigger)
    {
        var promptKey = Services.Input.HotkeyService.KeyCodeToName(trigger);
        if (promptKey is null) return true; // unnameable key cannot be a working binding

        foreach (var role in RoleBindings())
        {
            if (HotkeyBinding.RolePromptConflict(role.Canonical, promptKey)) return true;
        }
        return false;
    }
}
