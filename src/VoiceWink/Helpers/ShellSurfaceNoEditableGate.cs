namespace VoiceWink.Helpers;

/// <summary>PST-5: blocks Ctrl+V into a Windows shell surface (taskbar, tray overflow, desktop) whose focused element is not editable.</summary>
internal static class ShellSurfaceNoEditableGate
{
    /// <summary>Exact top-level classes of shell surfaces with no text field of their own besides an Edit.</summary>
    private static readonly HashSet<string> ShellSurfaceClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
        "Progman",
        "WorkerW",
    };

    /// <summary>Stage 1 (native): a listed shell surface with no Win32 caret; the focus HWND may be zero (empty taskbar/desktop).</summary>
    public static bool ShouldProbe(
        bool routeConfident,
        PasteFocusRoute.Route route,
        string? targetClass,
        IntPtr targetWindow,
        IntPtr caretHwnd)
        => routeConfident
           && route == PasteFocusRoute.Route.NormalUiaRestore
           && targetWindow != IntPtr.Zero
           && targetClass is { Length: > 0 }
           && ShellSurfaceClasses.Contains(targetClass)
           && caretHwnd == IntPtr.Zero;

    /// <summary>Stage 2: block only on a READ shape that is not editable; an unread shape fails open.</summary>
    public static bool ShouldBlock(UiaShapeWithIdentity? probed, PreRestoreStage? stage)
    {
        if (probed is not { } p)
            return false;
        // The element a rescue or fallback already verified keeps its pass (PST-3/6 identity binding).
        if (stage is { UsesIdentityBoundDriftNet: true } passed
            && NoEditableFocusGate.RuntimeIdsEqual(p.RuntimeId, passed.VerifiedRuntimeId))
            return false;
        return !IsEditable(p.Shape);
    }

    internal static bool IsEditable(UiaElementShape shape)
        => NoEditableFocusGate.IsEditableShapedForCapturedRescue(shape)
           || (shape.HasValuePattern && !shape.ValueIsReadOnly);

    /// <summary>Runs Stage 2 with the caller's bounded probe and renders the flags-only log detail.</summary>
    public static (bool Block, string Detail) Evaluate(PreRestoreStage? stage, Func<UiaShapeWithIdentity?> probeShapeWithIdentity)
    {
        var probed = probeShapeWithIdentity();
        var detail = probed is { Shape: var s }
            ? $"controlType={s.ControlTypeId} text={s.HasTextPattern} value={s.HasValuePattern} readOnly={s.ValueIsReadOnly} focusable={s.IsKeyboardFocusable}"
            : "shape probe failed";
        return (ShouldBlock(probed, stage), detail);
    }
}
