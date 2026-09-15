using VoiceWink.Models;
using VoiceWink.ViewModels;

namespace VoiceWink.Services.AppMode;

/// <summary>Result of adding an App-Mode from a template together with its default enhancement.</summary>
public sealed record AppModeAddResult(
    bool AppModeAdded,
    DefaultEnhancementOutcome Outcome,
    string? EnhancementTitle,
    string AppModeName);

/// <summary>
/// Adds an App-Mode from a template AND ensures its default enhancement exists and is linked, in one
/// unit-testable place (no UI). It PREFLIGHTS whether the app-mode add will be accepted (read-only)
/// BEFORE adding any enhancement, so a <b>rejected</b> add persists nothing. This is NOT full
/// atomicity: persistence is two ordered writes (enhancement, then app-mode-with-link — owner
/// decision over an atomic batch; see the session decision doc), so a process crash in the narrow
/// window between them could leave a re-added enhancement without its app-mode (the re-seed would
/// then keep that prompt). That residual window is an accepted trade-off, not eliminated here.
/// Mutates the singleton <see cref="EnhancementViewModel"/> collection, so it is UI-thread-affine —
/// call it on the UI thread (the App-Mode page does).
/// </summary>
public sealed class AppModeSetupCoordinator
{
    private readonly AppModeManager _appModes;
    private readonly EnhancementViewModel _enhancement;

    public AppModeSetupCoordinator(AppModeManager appModes, EnhancementViewModel enhancement)
    {
        _appModes = appModes;
        _enhancement = enhancement;
    }

    public AppModeAddResult AddAppModeFromTemplate(AppModeTemplate template)
    {
        // Preflight (read-only): if the app-mode add would be rejected, add NOTHING — so the
        // rejection path never persists a stray enhancement prompt.
        if (!_appModes.CanAddFromTemplate(template))
            return new AppModeAddResult(false, DefaultEnhancementOutcome.NoKey, null, template.Name);

        // Resolve/add the default enhancement (a write only when genuinely new), then add the
        // app-mode already linked to it. The preflight makes the add succeed under normal
        // conditions; it is not an atomic barrier (see the class remarks on the residual window).
        var resolution = _enhancement.ResolveOrAddDefaultEnhancement(template.DefaultEnhancementKey);
        var added = _appModes.AddFromTemplate(template, resolution.PromptId);
        return new AppModeAddResult(added, resolution.Outcome, resolution.EnhancementTitle, template.Name);
    }
}

/// <summary>Pure enum-to-notice mapping for the App-Mode add flow — no UI, fully testable.</summary>
public static class AppModeAddNotice
{
    /// <summary>The user-facing notice for an add result, or null when nothing needs saying: a
    /// rejected add, no declared default enhancement, an existing one reused, or a developer-only
    /// UnknownKey (forbidden by the structural test).</summary>
    public static string? Message(AppModeAddResult r)
    {
        if (!r.AppModeAdded) return null;
        return r.Outcome switch
        {
            // Template app-modes are disabled by default, so "linked to", not "uses".
            DefaultEnhancementOutcome.Added =>
                $"Added the “{r.EnhancementTitle}” enhancement and linked it to the {r.AppModeName} app mode.",
            DefaultEnhancementOutcome.TitleConflict =>
                $"{r.AppModeName} was added, but its default “{r.EnhancementTitle}” enhancement wasn’t added because a prompt with that name already exists.",
            DefaultEnhancementOutcome.AmbiguousSeedKey =>
                $"{r.AppModeName} was added, but its default enhancement couldn’t be linked because more than one prompt uses its key.",
            _ => null,
        };
    }
}
