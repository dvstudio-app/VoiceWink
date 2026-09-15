using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceWink.Helpers;

/// <summary>
/// Pure, unit-testable decision helpers for the paste focus path. No P/Invoke, no I/O —
/// the caller (<c>ClipboardService</c>) gathers a window snapshot and a self-PID and asks
/// these helpers what to do, so the policy can be tested without a real keyboard/window.
/// </summary>
internal static class PasteFocusRoute
{
    /// <summary>How to handle DOM-focus restoration before sending Ctrl+V.</summary>
    internal enum Route
    {
        /// <summary>Default: do the UIA SetFocus restore (works for Electron / in-process Chromium).</summary>
        NormalUiaRestore,

        /// <summary>
        /// Out-of-process WebView2 host (e.g. new Microsoft Teams): the editable lives in a
        /// separate renderer process, so our UIA SetFocus targets the host tree and can blur
        /// the already-focused DOM box. Skip the UIA step and paste into the box that already
        /// holds DOM focus.
        /// </summary>
        SkipUiaOopWebView2,
    }

    /// <summary>
    /// Window snapshot for the route decision. <see cref="CrossProcessChildProcessNames"/> holds
    /// the process names of descendant windows owned by a DIFFERENT process than the target host
    /// (an in-process Electron app contributes none; an out-of-process WebView2 host contributes
    /// <c>msedgewebview2</c>). All fields are best-effort; missing data fails closed to
    /// <see cref="Route.NormalUiaRestore"/>.
    /// </summary>
    internal readonly record struct Snapshot(
        string TargetClass,
        IReadOnlyList<string> CrossProcessChildProcessNames);

    /// <summary>The WebView2 renderer host process name (without extension).</summary>
    private const string WebView2ProcessName = "msedgewebview2";

    /// <summary>
    /// Decide the paste route. Out-of-process WebView2 is identified by a cross-process
    /// <c>msedgewebview2</c> descendant — the strong, fail-closed discriminator. The
    /// <c>TeamsWebView</c> top-level class is only a logged hint, never the authority
    /// (so a future Teams window-tree change can't silently mis-route).
    /// </summary>
    internal static (Route Route, string Reason) Decide(Snapshot s)
    {
        var names = s.CrossProcessChildProcessNames ?? Array.Empty<string>();
        var hasWebView2 = names.Any(n =>
            !string.IsNullOrEmpty(n) &&
            n.IndexOf(WebView2ProcessName, StringComparison.OrdinalIgnoreCase) >= 0);

        if (hasWebView2)
            return (Route.SkipUiaOopWebView2, $"oop-webview2 (msedgewebview2 child; targetClass=\"{s.TargetClass}\")");

        var teamsHint = string.Equals(s.TargetClass, "TeamsWebView", StringComparison.OrdinalIgnoreCase);
        return (Route.NormalUiaRestore,
            teamsHint
                ? "normal (TeamsWebView class but no msedgewebview2 child observed — fail-closed)"
                : "normal");
    }
}
