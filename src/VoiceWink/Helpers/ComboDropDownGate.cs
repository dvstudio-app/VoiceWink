using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// The ONE rule for changing a ComboBox's rows from code: never while its drop-down
/// is open or still closing. Rows replaced under an open drop-down break WinUI's drop-down state,
/// and the next open or close fails inside WinUI and ends the process (2026-10-04: E_FAIL in
/// <c>CarouselPanel::ComputePixelOffset</c>, reproduced in a WinUI harness for editable and
/// pick-only boxes alike, with a plain <c>StackPanel</c> list too).
/// <para>A change that arrives then is not dropped. The caller hands over a replay; the latest one
/// runs one dispatcher turn after <c>DropDownClosed</c>, because WinUI still updates the control
/// after raising that event, and a reopen before that turn keeps the box waiting for the next
/// close. The replay re-reads current state and re-checks its own staleness (a rebuilt page, a
/// closed dialog, a superseded load) — the gate decides only WHEN.</para>
/// <para>Use it wherever a box's rows can change while the user may have it open: after an await,
/// or from an event the user did not raise in that box. A change to the selection alone has no
/// measured failure and needs no gate. A change made in answer to the
/// user acting on ANOTHER control needs no gate — that control's click has already closed this
/// box. Take the gate when the box is built (<see cref="For"/> or <see cref="Watch"/>), so it sees
/// the first opening.</para>
/// </summary>
internal sealed class ComboDropDownGate
{
    private static readonly ConditionalWeakTable<ComboBox, ComboDropDownGate> Gates = new();
    private static ILogger Logger => Log.ForContext(typeof(ComboDropDownGate));

    private readonly ComboBox _combo;
    private bool _busy;      // DropDownOpened until one turn after DropDownClosed
    private int _openings;   // a reopen invalidates a pending settle
    private Action? _replay; // the latest deferred change

    private ComboDropDownGate(ComboBox combo)
    {
        _combo = combo;
        combo.DropDownOpened += (_, _) => { _busy = true; _openings++; };
        combo.DropDownClosed += (_, _) =>
        {
            var closed = _openings;
            if (!combo.DispatcherQueue.TryEnqueue(() => Settle(closed)))
                _busy = false; // the queue is shutting down
        };
    }

    /// <summary>The gate for this box, shared by every caller.</summary>
    internal static ComboDropDownGate For(ComboBox combo)
        => Gates.GetValue(combo, static c => new ComboDropDownGate(c));

    /// <summary>Takes the gate of each box, for boxes changed together through the static
    /// <see cref="TryDefer(Action, ComboBox?[])"/>.</summary>
    internal static void Watch(params ComboBox?[] combos)
    {
        foreach (var combo in combos)
            if (combo is not null) For(combo);
    }

    /// <summary>True while the drop-down is open or WinUI is still finishing its close.</summary>
    internal bool IsBusy => _busy || _combo.IsDropDownOpen;

    /// <summary>While the box is busy: keeps <paramref name="replay"/> in place of any earlier one
    /// and returns true, so the caller changes nothing now. Otherwise returns false.</summary>
    internal bool TryDefer(Action replay)
    {
        if (!IsBusy) return false;
        _replay = replay;
        return true;
    }

    /// <summary>The same rule for one change that touches several boxes: deferred while any of them
    /// is busy, and replayed when that one settles (the replay asks again).</summary>
    internal static bool TryDefer(Action replay, params ComboBox?[] combos)
    {
        foreach (var combo in combos)
            if (combo is not null && For(combo).TryDefer(replay)) return true;
        return false;
    }

    private void Settle(int closed)
    {
        if (closed != _openings || _combo.IsDropDownOpen) return;
        _busy = false;
        var replay = _replay;
        _replay = null;
        // This runs from a dispatcher callback, where an exception would end the app; the same
        // change made at once runs inside its caller's own error handling.
        try
        {
            replay?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "A list change held back while its drop-down was open failed");
        }
    }
}
