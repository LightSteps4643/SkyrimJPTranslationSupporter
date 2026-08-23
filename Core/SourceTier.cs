namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Trust ranking across the SourceKind values a corpus entry can carry, used
/// wherever two competing candidate translations from different provenances must
/// be compared. v0.38.0: introduced after a real mistranslation ("Hevnoraak" →
/// "ヘブラノーク" in Cloak of Hevnoraak) traced back to "dsd" being silently
/// treated as equal to "vanilla" — DSD is an existing COMMUNITY translation patch,
/// not Bethesda's own shipped localization, and must not out-rank it.
///
///   -1. override   — Data/phrase_overrides.tsv, a human-curated correction for
///                    a SPECIFIC English string, added after finding it resolve
///                    wrong through every other tier at once (see v0.44.0's
///                    DESIGN_NOTES.md section) — outranks even vanilla, since
///                    it exists precisely to overrule vanilla data that's
///                    correct in the sense it was recorded for but wrong when
///                    reused as this string's whole-candidate/phrase precedent.
///    0. vanilla    — this load order's own scan of real EN/JA record pairs
///                    (Bethesda's own shipped localization, or a mod that ships
///                    its own genuine Japanese localization).
///    1. reference   — Data/skyrim_taiyaku_reference.tsv, a third-party
///                    recompilation of the same official shipped localization;
///                    equally official content, but not this tool's own verified
///                    read of the installed data, so kept one step below vanilla.
///    2. dsd/imported — community-authored translation work: an existing DSD
///                    translation patch, or an xTranslator import. Tied at the
///                    bottom — "already packaged as DSD" vs. "still in
///                    xTranslator" says nothing about correctness.
/// </summary>
public static class SourceTier
{
    public static int Of(string sourceKind) => sourceKind switch
    {
        "override" => -1,
        "vanilla" => 0,
        "reference" => 1,
        "dsd" or "imported" => 2,
        _ => 2, // unrecognized/blank (e.g. a "derived" transliteration slice with no single attesting entry) — never trusted above community data
    };

    /// <summary>The best (lowest) tier among a set of corroborating (SourceKind, Source)
    /// pairs — used when a learned entry is backed by several corpus rows and only the
    /// strongest one should decide how much to trust it.</summary>
    public static int OfProvenance(IEnumerable<(string SourceKind, string Source)> provenance)
    {
        var best = int.MaxValue;
        foreach (var (sourceKind, _) in provenance)
        {
            var tier = Of(sourceKind);
            if (tier < best) best = tier;
        }
        return best == int.MaxValue ? Of("") : best;
    }
}
