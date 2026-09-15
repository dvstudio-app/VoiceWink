using Serilog;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Context;

/// <summary>
/// Selected text capture via clipboard trick (Ctrl+C, read, restore).
/// Sends Ctrl+C, reads clipboard, restores original content.
///
/// <para><b>IMG-BG (2026-07-16):</b> the whole capture → Ctrl+C → read → restore transaction
/// holds the <see cref="ClipboardService"/> write lease. Now that a background image
/// generation can complete at any moment, an unserialized transaction could interleave with
/// the job's clipboard write — the image landing between this read and restore would be
/// silently overwritten by the restore. Under the lease the transaction uses ONLY the
/// lock-free cores exposed on <see cref="ClipboardService.WriteLease"/> — never the public
/// locked entry points, which would re-enter the non-reentrant semaphore and self-deadlock.
/// The Ctrl+C injection and settle delay are constructor-injectable so tests can drive the
/// transaction without real keyboard/clipboard operations (work-laptop rule).</para>
/// </summary>
public sealed class SelectedTextService
{
    private static ILogger Logger => Log.ForContext<SelectedTextService>();

    private static readonly TimeSpan CopySettleDelay = TimeSpan.FromMilliseconds(100);

    private readonly Func<Task<IDisposable>> _acquireLease;
    private readonly Func<IDisposable, Task<object?>> _captureSnapshot;
    private readonly Func<IDisposable, string?> _readText;
    private readonly Action _sendCtrlC;
    private readonly Func<Task> _settle;
    private readonly Func<IDisposable, object, Task> _restoreSnapshot;

    public SelectedTextService(ClipboardService clipboard)
        : this(
            acquireLease: async () => await clipboard.AcquireWriteLeaseAsync().ConfigureAwait(false),
            captureSnapshot: async lease => await ((ClipboardService.WriteLease)lease).CaptureSnapshotAsync().ConfigureAwait(false),
            readText: lease => ((ClipboardService.WriteLease)lease).GetText(),
            sendCtrlC: SendCtrlC,
            settle: () => Task.Delay(CopySettleDelay),
            restoreSnapshot: async (lease, snapshot) =>
                await ((ClipboardService.WriteLease)lease).RestoreSnapshotAsync(
                    (ClipboardService.ClipboardSnapshot)snapshot).ConfigureAwait(false))
    {
    }

    /// <summary>Test seam: every side effect of the transaction is injectable.</summary>
    internal SelectedTextService(
        Func<Task<IDisposable>> acquireLease,
        Func<IDisposable, Task<object?>> captureSnapshot,
        Func<IDisposable, string?> readText,
        Action sendCtrlC,
        Func<Task> settle,
        Func<IDisposable, object, Task> restoreSnapshot)
    {
        _acquireLease = acquireLease;
        _captureSnapshot = captureSnapshot;
        _readText = readText;
        _sendCtrlC = sendCtrlC;
        _settle = settle;
        _restoreSnapshot = restoreSnapshot;
    }

    /// <summary>
    /// Attempt to capture selected text in the active application.
    /// </summary>
    public async Task<string?> GetSelectedTextAsync()
    {
        try
        {
            // Lease held across the ENTIRE transaction; released in the finally even when a
            // step throws, so a failed capture can never wedge every other clipboard writer.
            var lease = await _acquireLease().ConfigureAwait(false);
            try
            {
                // Save current clipboard
                var savedSnapshot = await _captureSnapshot(lease).ConfigureAwait(false);
                var savedText = _readText(lease);

                // Send Ctrl+C
                _sendCtrlC();
                await _settle().ConfigureAwait(false);

                // Read clipboard
                var selected = _readText(lease);

                // Restore clipboard
                if (savedSnapshot != null)
                {
                    await _restoreSnapshot(lease, savedSnapshot).ConfigureAwait(false);
                }

                // Only return if different from what was already there
                if (selected != null && selected != savedText)
                    return selected;

                return null;
            }
            finally
            {
                lease.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to capture selected text");
            return null;
        }
    }

    private static void SendCtrlC()
    {
        var inputs = new Helpers.NativeInterop.INPUT[4];
        inputs[0] = Helpers.NativeInterop.CreateKeyInput(Helpers.NativeInterop.VK_CONTROL, false);
        inputs[1] = Helpers.NativeInterop.CreateKeyInput(0x43 /* C */, false);
        inputs[2] = Helpers.NativeInterop.CreateKeyInput(0x43, true);
        inputs[3] = Helpers.NativeInterop.CreateKeyInput(Helpers.NativeInterop.VK_CONTROL, true);

        Helpers.NativeInterop.SendInput((uint)inputs.Length, inputs,
            global::System.Runtime.InteropServices.Marshal.SizeOf<Helpers.NativeInterop.INPUT>());
    }
}
