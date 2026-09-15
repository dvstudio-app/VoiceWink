namespace VoiceWink.Helpers;

/// <summary>The four hotkey roles the app binds. Adding one here is what forces every surface to see it.</summary>
internal enum HotkeyRole
{
    Recording,
    PasteLast,
    RedoLast,
    GenerateImage,
}

/// <summary>One prompt's hotkey, as a conflict scan needs to see it.</summary>
internal readonly record struct PromptHotkey(string PromptId, string Title, string? Binding);

/// <summary>Something a candidate binding collides with, and enough to describe or clear it.</summary>
/// <remarks>
/// <paramref name="Role"/> is null exactly when the collision is with a PROMPT, so the two cases stay
/// distinguishable without a second type — surfaces treat them differently (the Enhancement dialog
/// blocks on the recording role and only warns on the rest).
/// </remarks>
internal readonly record struct HotkeyCollision(
    HotkeyRole? Role,
    string? PromptId,
    string? PromptTitle,
    string ExistingBinding)
{
    public bool IsPrompt => Role is null;

    /// <summary>What this collision is called in front of a user.</summary>
    public string Describe() => IsPrompt
        ? $"enhancement prompt \"{PromptTitle}\""
        : HotkeyConflictScan.DescribeRole(Role!.Value);
}

/// <summary>
/// The ONE authority on "does this hotkey collide with anything already bound".
/// </summary>
/// <remarks>
/// <para><b>Why this type exists (HKY-3, after five review rounds).</b> The predicates in
/// <see cref="HotkeyBinding"/> were correct from round 1; what kept being wrong was the
/// ENUMERATION. Every screen carried its own hand-written list of which hotkeys exist, and each
/// list was incomplete in a different way — the Settings page knew four roles plus prompts, the
/// Enhancement dialog kept a third copy, and onboarding knew three roles, no prompts, and had never
/// heard of the generate-image hotkey at all. Rounds 2, 3, 4 and 5 each found the same defect in a
/// different one of those copies; round 5 found that the list round 4 had just fixed was ALSO
/// incomplete. Patching the fourth list would have left onboarding still not checking prompts.</para>
///
/// <para>So the lists are gone. A surface no longer has one — it asks here. A screen cannot hold an
/// incomplete enumeration of roles if it holds no enumeration, and <c>HotkeyConflictScanTests</c>
/// asserts every <see cref="HotkeyRole"/> member is reachable, so adding a fifth role fails a test
/// rather than silently going unchecked on three screens.</para>
///
/// <para><b>Which predicate applies where is deliberate and was itself a Blocker</b> (round 1):
/// role↔role is <see cref="HotkeyBinding.Conflicts"/>, role↔prompt is
/// <see cref="HotkeyBinding.RolePromptConflict"/>, prompt↔prompt is <c>Conflicts</c> again. They
/// answer different questions — prompts are matched LAST by the hook, so a combo role does not claim
/// every press of its trigger and reporting that as a conflict once DELETED live prompt hotkeys.</para>
///
/// <para>Pure: no settings, no DI, no UI. <see cref="HotkeyBindingSnapshot"/> is the input, and the
/// caller supplies it — which is what keeps the whole decision table testable.</para>
/// </remarks>
internal static class HotkeyConflictScan
{
    /// <summary>The settings key backing each role — the one place the mapping lives.</summary>
    public static string SettingsKeyFor(HotkeyRole role) => role switch
    {
        HotkeyRole.Recording => AppDefaults.HotkeyModifier,
        HotkeyRole.PasteLast => AppDefaults.PasteLastHotkeyModifier,
        HotkeyRole.RedoLast => AppDefaults.RedoLastHotkeyModifier,
        HotkeyRole.GenerateImage => AppDefaults.GenerateImageHotkeyModifier,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unmapped hotkey role"),
    };

    /// <summary>What a role is called in front of a user.</summary>
    public static string DescribeRole(HotkeyRole role) => role switch
    {
        HotkeyRole.Recording => "your recording hotkey",
        HotkeyRole.PasteLast => "your paste-last hotkey",
        HotkeyRole.RedoLast => "your redo-last hotkey",
        HotkeyRole.GenerateImage => "your generate-image hotkey",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unnamed hotkey role"),
    };

    /// <summary>
    /// Everything <paramref name="candidate"/> would collide with if bound to
    /// <paramref name="editing"/>. Empty when it is free to take.
    /// </summary>
    public static IReadOnlyList<HotkeyCollision> ForRole(
        HotkeyBindingSnapshot snapshot, string? candidate, HotkeyRole editing)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return [];

        var found = new List<HotkeyCollision>();
        foreach (var role in HotkeyBindingSnapshot.AllRoles)
        {
            if (role == editing) continue;
            var existing = snapshot[role];
            if (HotkeyBinding.Conflicts(candidate, existing))
                found.Add(new HotkeyCollision(role, null, null, existing!));
        }

        foreach (var prompt in snapshot.Prompts)
        {
            if (HotkeyBinding.RolePromptConflict(candidate, prompt.Binding))
                found.Add(new HotkeyCollision(null, prompt.PromptId, prompt.Title, prompt.Binding!));
        }

        return found;
    }

    /// <summary>
    /// Everything <paramref name="candidate"/> would collide with if assigned to the prompt
    /// <paramref name="editingPromptId"/>.
    /// </summary>
    public static IReadOnlyList<HotkeyCollision> ForPrompt(
        HotkeyBindingSnapshot snapshot, string? candidate, string? editingPromptId)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return [];

        var found = new List<HotkeyCollision>();
        foreach (var role in HotkeyBindingSnapshot.AllRoles)
        {
            // Direction matters: the ROLE is the role and the PROMPT is the candidate here, the
            // reverse of ForRole. RolePromptConflict is not symmetric in what it names.
            var existing = snapshot[role];
            if (HotkeyBinding.RolePromptConflict(existing, candidate))
                found.Add(new HotkeyCollision(role, null, null, existing!));
        }

        foreach (var prompt in snapshot.Prompts)
        {
            if (string.Equals(prompt.PromptId, editingPromptId, StringComparison.Ordinal)) continue;
            if (HotkeyBinding.Conflicts(candidate, prompt.Binding))
                found.Add(new HotkeyCollision(null, prompt.PromptId, prompt.Title, prompt.Binding!));
        }

        return found;
    }
}

/// <summary>
/// Every hotkey currently bound, as one value. The input to <see cref="HotkeyConflictScan"/>.
/// </summary>
/// <remarks>
/// Roles are held in a map keyed by the enum rather than four named fields, so enumeration is
/// STRUCTURAL: <see cref="AllRoles"/> comes from the enum itself and a new role cannot be missed by
/// a scan loop. That is the property the four hand-written lists this replaces did not have.
/// </remarks>
internal sealed class HotkeyBindingSnapshot
{
    /// <summary>Every role, from the enum — never a hand-maintained list.</summary>
    public static readonly HotkeyRole[] AllRoles = Enum.GetValues<HotkeyRole>();

    private readonly IReadOnlyDictionary<HotkeyRole, string?> _roles;

    public HotkeyBindingSnapshot(
        IReadOnlyDictionary<HotkeyRole, string?> roles,
        IReadOnlyList<PromptHotkey>? prompts = null)
    {
        _roles = roles;
        Prompts = prompts ?? [];
    }

    public IReadOnlyList<PromptHotkey> Prompts { get; }

    /// <summary>The binding for a role, or null when unbound.</summary>
    public string? this[HotkeyRole role] => _roles.TryGetValue(role, out var value) ? value : null;

    /// <summary>Build from a settings reader — the one place the four keys are read together.</summary>
    public static HotkeyBindingSnapshot FromSettings(
        Func<string, string, string> getString,
        IReadOnlyList<PromptHotkey>? prompts = null)
    {
        var roles = new Dictionary<HotkeyRole, string?>();
        foreach (var role in AllRoles)
        {
            var fallback = role == HotkeyRole.Recording ? "RightAlt" : string.Empty;
            roles[role] = getString(HotkeyConflictScan.SettingsKeyFor(role), fallback);
        }
        return new HotkeyBindingSnapshot(roles, prompts);
    }

    /// <summary>A copy with one role changed — how a surface asks "what if I bound this?".</summary>
    public HotkeyBindingSnapshot With(HotkeyRole role, string? binding)
    {
        var roles = new Dictionary<HotkeyRole, string?>();
        foreach (var existing in AllRoles) roles[existing] = this[existing];
        roles[role] = binding;
        return new HotkeyBindingSnapshot(roles, Prompts);
    }
}
