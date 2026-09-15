using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Reads the prompt hotkeys for a <see cref="HotkeyBindingSnapshot"/>.
/// </summary>
/// <remarks>
/// One reader, so no screen has to remember that prompts carry hotkeys too — onboarding's
/// hand-written conflict list never knew, which is half of what let it hand a role a key that
/// silently killed a prompt hotkey (Codex diff round 5).
///
/// Fail-soft and DI-resolved at the call site rather than injected, matching the surrounding
/// pages: <c>AIEnhancementService</c> is unavailable during onboarding construction on some paths,
/// and a conflict scan that cannot see prompts must degrade to "no prompt collisions" rather than
/// take a screen down. That degradation is the pre-HKY-3 behaviour, so it is never a regression.
/// </remarks>
internal static class HotkeyPrompts
{
    private static ILogger Logger => Log.ForContext(typeof(HotkeyPrompts));

    public static IReadOnlyList<PromptHotkey> Read()
    {
        try
        {
            var enhancement = App.Services.GetRequiredService<Services.AIEnhancement.AIEnhancementService>();
            return [.. enhancement.GetPrompts()
                .Where(p => !string.IsNullOrWhiteSpace(p.Hotkey))
                .Select(p => new PromptHotkey(p.Id, p.Title, p.Hotkey))];
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Prompt hotkeys unavailable for a conflict scan — treating as none");
            return [];
        }
    }
}
