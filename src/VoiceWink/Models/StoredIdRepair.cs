namespace VoiceWink.Models;

/// <summary>ENH-43: the one rule for a stored list where two entries share an Id.</summary>
internal static class StoredIdRepair
{
    /// <summary>ENH-43: the first entry keeps a repeated Id, so links and History keep pointing at it; every later one gets a new Id.</summary>
    /// <param name="items">The list as read.</param>
    /// <param name="idOf">The entry's Id.</param>
    /// <param name="mint">Gives the entry a new Id and marks it for the reader to save; returns the new Id.</param>
    public static void MintRepeatedIds<T>(IEnumerable<T> items, Func<T, string> idOf, Func<T, string> mint)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!seen.Add(idOf(item)))
                seen.Add(mint(item));
        }
    }
}
