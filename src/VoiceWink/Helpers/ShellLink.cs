using System.Diagnostics;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Hands a URL or a <c>mailto:</c> to the shell — the browser, the Microsoft Store app or the user's
/// mail app opens it, and the user finishes whatever it starts. Fail-soft: a refused launch (no app
/// associated, a policy block, a broken handler) is logged and reported as <c>false</c>, never thrown,
/// so a caller can say so on screen. Not unit-tested: it starts processes; the targets it is handed
/// are.
/// </summary>
internal static class ShellLink
{
    private static ILogger Logger => Log.ForContext(typeof(ShellLink));

    /// <summary>Open <paramref name="target"/>; false when the shell refused it.</summary>
    public static bool TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            // The scheme and the error's TYPE only, never the exception itself: a launch failure's message
            // names the whole target (a mailto's subject and body) and the working directory, which
            // carries the Windows user name — AppRestartService's rule for the same exception.
            Logger.Warning("Failed to open a {Scheme} link ({Error})",
                SchemeOf(target), Services.System.AppRestartService.DescribeError(ex));
            return false;
        }
    }

    /// <summary>
    /// VoiceWink's Microsoft Store listing in the Store app, or its web page when the Store app cannot
    /// be started (LNC-14). False only when neither opened.
    /// </summary>
    public static bool TryOpenStoreListing() =>
        TryOpen(VoiceWinkUrls.StoreListing) || TryOpen(VoiceWinkUrls.StoreListingWeb);

    private static string SchemeOf(string target)
    {
        var colon = target.IndexOf(':');
        return colon > 0 ? target[..colon] : "unknown";
    }
}
