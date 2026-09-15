using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Context;

/// <summary>
/// Captures text content from the active window for AI context.
/// </summary>
public sealed class ScreenCaptureService
{
    private static ILogger Logger => Log.ForContext<ScreenCaptureService>();

    /// <summary>
    /// Attempt to get text content from the active window.
    /// This is best-effort — many apps don't expose text via UI Automation.
    /// </summary>
    public string? GetActiveWindowText()
    {
        try
        {
            // Best-effort: read window title
            var hwnd = NativeInterop.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            var length = NativeInterop.GetWindowTextLength(hwnd);
            if (length == 0) return null;

            var sb = new global::System.Text.StringBuilder(length + 1);
            NativeInterop.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to capture screen text");
            return null;
        }
    }

}
