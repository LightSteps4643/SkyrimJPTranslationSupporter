namespace SkyrimJPStringPatcher.Core;

public static class SafeEnumeration
{
    /// <summary>Iterates <paramref name="source"/>, invoking <paramref name="onItem"/>
    /// per element. Two independent failure points are both guarded, and treated
    /// differently, because they mean different things:
    ///
    /// - If the ENUMERATOR ITSELF throws (a corrupt record/subrecord breaking
    ///   Mutagen's lazy binary parse mid-iteration — confirmed real case:
    ///   DESIGN_NOTES.md known issue 21, a malformed PERK entry-point effect),
    ///   this stops iterating and reports via <paramref name="onError"/> instead
    ///   of propagating. C#'s `foreach` cannot recover from a MoveNext()
    ///   exception (the enumerator's internal state is no longer trustworthy),
    ///   so on this failure it abandons whatever remains of THIS particular
    ///   sequence — the caller decides what that means at its own granularity
    ///   (skip the rest of a plugin, or just the rest of one record's
    ///   nested/extra fields).
    /// - If <paramref name="onItem"/> ITSELF throws (e.g. processing a
    ///   successfully-yielded item touches a lazily-bound field that turns out
    ///   to be corrupt only when actually read), this is a SEPARATE failure mode
    ///   from the above: the enumerator's own state is unaffected (MoveNext/
    ///   Current already succeeded), so it is reported via
    ///   <paramref name="onError"/> and iteration CONTINUES with the next item —
    ///   losing only the one failing item, not the rest of the sequence. (Found
    ///   v0.55.2: this call was originally left outside the try/catch entirely,
    ///   so an onItem exception propagated straight past this helper, silently
    ///   defeating the fail-open protection every PickUpTargetRunner.cs call
    ///   site relies on it for.)
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
            try
            {
                onItem(current);
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }
    }
}
