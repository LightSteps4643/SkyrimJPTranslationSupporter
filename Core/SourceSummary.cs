namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// v0.37.0: formats a list of (SourceKind, Source) provenance pairs — e.g. every
/// corpus entry that corroborated one learned word/rendering — into a single
/// compact, human-readable string for the translation log's Detail column.
///
/// Exists so a log line can answer "which plugin(s) actually said this" without
/// re-deriving it by hand from corpus.tsv/Translation/import — the exact manual
/// archaeology that motivated adding source tracing in the first place (see
/// DESIGN_HISTORY.md's v0.37.0 section: tracing "Sagacious Warrior"→"サガント・
/// ウォリアー" back to a different mod's xTranslator file took several grep
/// passes across corpus.tsv, the derived dictionaries, and Translation/import).
/// </summary>
public static class SourceSummary
{
    /// <summary>How many distinct (SourceKind, Source) groups to spell out before
    /// collapsing the rest into a "ほか+N件" tail. An everyday word like "Dwarven"
    /// can be attested by dozens of plugins — listing all of them would defeat the
    /// point of a compact log line, and the first few (by corroboration strength)
    /// already answer "is this genuinely widely-attested or a one-off".</summary>
    private const int MaxGroups = 4;

    /// <summary>Groups by (SourceKind, Source) and renders each group as
    /// "kind:source" or "kind:source×N" when the same pair recurs, ordered by
    /// how many entries it accounts for (the strongest corroboration first).</summary>
    public static string Summarize(IEnumerable<(string SourceKind, string Source)> provenance)
    {
        var groups = provenance
            .GroupBy(p => (p.SourceKind, p.Source))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.SourceKind, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Source, StringComparer.Ordinal)
            .ToList();

        if (groups.Count == 0) return "";
        var shown = groups.Take(MaxGroups).Select(g =>
            g.Count() > 1 ? $"{g.Key.SourceKind}:{g.Key.Source}×{g.Count()}" : $"{g.Key.SourceKind}:{g.Key.Source}");
        var tail = groups.Count > MaxGroups ? new[] { $"ほか{groups.Count - MaxGroups}件" } : Array.Empty<string>();
        return string.Join(", ", shown.Concat(tail));
    }
}
