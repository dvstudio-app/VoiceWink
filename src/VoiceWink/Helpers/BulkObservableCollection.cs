using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace VoiceWink.Helpers;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with an atomic <see cref="ReplaceAll"/> that swaps the
/// entire contents behind a single Reset notification. A Clear + N×Add repopulate raises N+1
/// CollectionChanged events; against a live editable WinUI ComboBox each one re-enters the
/// control's item plumbing, and that churn corrupts native combo/popup state (fatal
/// COMException 0x80070490 on the next dropdown open — the AI Enhancement provider-switch
/// crash). Collections consumed by CollectionChanged-driven UI must repopulate through
/// <see cref="ReplaceAll"/>, never Clear + Add loops.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces the whole collection contents with <paramref name="items"/>, raising exactly one
    /// Reset event. The input is materialized before any mutation, so a lazy enumerable that
    /// throws mid-iteration leaves the collection untouched, and <c>ReplaceAll(this)</c> is safe.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var snapshot = items.ToList();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
