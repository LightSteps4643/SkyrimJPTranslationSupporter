namespace SkyrimJPStringPatcher.Core;

public static class SafeEnumeration
{
    /// <summary>Iterates <paramref name="source"/>, invoking <paramref name="onItem"/>
    /// per element. If the ENUMERATOR ITSELF throws (a corrupt record/subrecord
    /// breaking Mutagen's lazy binary parse mid-iteration — confirmed real case:
    /// DESIGN_NOTES.md known issue 21, a malformed PERK entry-point effect), this
    /// stops iterating and reports via <paramref name="onError"/> instead of
    /// propagating. C#'s `foreach` cannot recover from a MoveNext() exception (the
    /// enumerator's internal state is no longer trustworthy), so on error this
    /// abandons whatever remains of THIS particular sequence — the caller decides
    /// what that means at its own granularity (skip the rest of a plugin, or just
    /// the rest of one record's nested/extra fields).
    ///
    /// Pulled out of PickUpTargetRunner (v0.55.0a) so it can be unit-tested without
    /// any Mutagen/MO2 dependency — it is a generic IEnumerable helper, not
    /// Skyrim-specific logic.</summary>
    public static void SafeForEach<T>(IEnumerable<T> source, Action<T> onItem, Action<Exception> onError)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            T current;
            try
            {
                if (!enumerator.MoveNext()) return;
                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                onError(ex);
                return;
            }
            onItem(current);
        }
    }
}
