using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>UI-5: the main window's one ContentDialog slot — WinUI throws on a second ShowAsync per XamlRoot.</summary>
internal sealed class DialogLease
{
    /// <summary>The lease for the main window, the only XamlRoot these dialogs share.</summary>
    public static DialogLease MainWindow { get; } = new();

    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <summary>The slot, or null at once (logged, naming <paramref name="dialog"/>) when another dialog holds it.</summary>
    public IDisposable? TryAcquire(string dialog)
    {
        if (_slot.Wait(0)) return new Lease(_slot);
        Log.ForContext<DialogLease>().Information("{Dialog} not shown — another dialog is open", dialog);
        return null;
    }

    /// <summary>UI-5b: shows <paramref name="dialog"/> in the slot; None, without showing it, while another dialog holds it.</summary>
    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, string name)
    {
        using var slot = TryAcquire(name);
        return slot is null ? ContentDialogResult.None : await dialog.ShowAsync();
    }

    /// <summary>Waits for the slot — for a dialog the user asked for that must not be dropped.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _slot.WaitAsync(ct);
        return new Lease(_slot);
    }

    private sealed class Lease(SemaphoreSlim slot) : IDisposable
    {
        private int _released;

        // Idempotent: a second Dispose must not free a slot another dialog now holds.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slot.Release();
        }
    }
}
