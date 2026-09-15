namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Shared "does this corpus entry's DSD record type match the candidate's"
/// bonus logic — used by both <see cref="PrecedentRetriever"/> (b) and the
/// same-mod hint finder (c, issue #4). Bethesda's Japanese localization is
/// highly templated per record type, so a precedent drawn from the same kind
/// of string is far more likely to carry the naming convention the candidate
/// needs. Same DSD type scores highest, same 4-char signature next (an
/// "ARMO FULL" candidate can still learn from "ARMO DESC"), everything else
/// keeps its plain similarity score — a bonus, never a filter, so a strong
/// cross-type match is still reachable when nothing same-type exists.
/// </summary>
internal static class RecordTypeAffinity
{
    // 2026-09-16: rescaled from the old integer bonuses (2/1) to this scale
    // to match TF-IDF+cosine scores (roughly 0-1, versus the old scheme's
    // roughly 1-11) — the 2:1 ratio between them is preserved, but the
    // absolute size is now small enough to only break near-ties/act as a
    // secondary signal rather than override a real difference in content
    // similarity (verified against real data: zero cases found, across a
    // diverse ~600-candidate sample, where these bonuses caused an entry
    // with materially weaker content similarity to outrank a materially
    // stronger one).
    public const double SameTypeBonus = 0.04;
    public const double SameSignatureBonus = 0.02;

    public static double Compute(string entryType, string candidateType, string candidateSignature)
    {
        if (candidateType.Length == 0 || entryType.Length == 0) return 0;
        if (string.Equals(entryType, candidateType, StringComparison.OrdinalIgnoreCase)) return SameTypeBonus;
        if (string.Equals(SignatureOf(entryType), candidateSignature, StringComparison.OrdinalIgnoreCase)) return SameSignatureBonus;
        return 0;
    }

    /// <summary>The 4-char xEdit record signature embedded in a DSD type string
    /// ("ARMO FULL" -&gt; "ARMO").</summary>
    public static string SignatureOf(string dsdType)
    {
        var space = dsdType.IndexOf(' ');
        return space > 0 ? dsdType[..space] : dsdType;
    }
}
