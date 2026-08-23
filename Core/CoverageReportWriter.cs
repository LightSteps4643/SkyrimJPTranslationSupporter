using static SkyrimJPStringPatcher.Core.TsvEscaping;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// v0.18.0: per-plugin translation coverage summary — how much of a plugin's
/// translatable text is already covered by an existing DSD file (some
/// community translation pack, or a previous run of this tool) versus still
/// needing work, in BOTH count and character terms.
///
/// Motivation: the direct-edit translation workflow (see DESIGN_NOTES.md,
/// the Sentinel.esp / Legendary Elder Scrolls Loading Screen.esl trials) made
/// clear that self-translating is expensive (context window and time), so the
/// user's revised strategy is "prefer an existing community translation;
/// self-translate only the gap." Deciding that per plugin requires knowing,
/// BEFORE spending any translation effort, how big that gap actually is — a
/// plugin already 95% covered is a different call than one at 0%. Character
/// counts matter alongside raw counts because they are what actually predict
/// translation cost (see Book Covers Skyrim.esp: fewer than 350 candidate
/// ROWS, but several of those rows are entire novels — a count-only view
/// would have looked "almost done" right up until someone opened it).
/// </summary>
public static class CoverageReportWriter
{
    private const char Sep = '\t';

    /// <summary>How many sample untranslated strings to preview per plugin, and
    /// how long each preview is truncated to — enough to recognize "this is a
    /// short item name" vs. "this is a full paragraph" without opening
    /// candidates.tsv, not a substitute for it.</summary>
    private const int SampleCount = 3;
    private const int SampleMaxLength = 40;

    public static void WriteTsv(string path, IReadOnlyList<Candidate> candidates, IReadOnlyDictionary<string, (int Count, long Chars)> coveredByPlugin)
    {
        var uncoveredByPlugin = candidates
            .GroupBy(c => c.WinningPlugin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var plugins = coveredByPlugin.Keys
            .Concat(uncoveredByPlugin.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var rows = new List<(string Plugin, int TotalCount, int TranslatedCount, int UntranslatedCount, double TranslatedRatio,
            long TotalChars, long TranslatedChars, long UntranslatedChars, double TranslatedCharsRatio, string SamplePreview)>();

        foreach (var plugin in plugins)
        {
            var (translatedCount, translatedChars) = coveredByPlugin.GetValueOrDefault(plugin);
            var untranslated = uncoveredByPlugin.GetValueOrDefault(plugin) ?? new List<Candidate>();
            var untranslatedCount = untranslated.Count;
            var untranslatedChars = untranslated.Sum(c => (long)c.CurrentText.Length);

            var totalCount = translatedCount + untranslatedCount;
            var totalChars = translatedChars + untranslatedChars;

            var sample = string.Join(" / ", untranslated.Take(SampleCount).Select(c => TextPreview.Truncate(c.CurrentText, SampleMaxLength)));

            rows.Add((plugin, totalCount, translatedCount, untranslatedCount,
                totalCount == 0 ? 100.0 : 100.0 * translatedCount / totalCount,
                totalChars, translatedChars, untranslatedChars,
                totalChars == 0 ? 100.0 : 100.0 * translatedChars / totalChars,
                sample));
        }

        // Least-covered first — that's the "where should I even look first" order
        // the user actually wants when deciding whether to hunt for an existing
        // translation MOD or accept a self-translation gap.
        rows.Sort((a, b) => a.TranslatedRatio.CompareTo(b.TranslatedRatio));

        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine(string.Join(Sep, "Plugin", "TotalCount", "TranslatedCount", "UntranslatedCount", "TranslatedRatio(%)",
            "TotalChars", "TranslatedChars", "UntranslatedChars", "TranslatedCharsRatio(%)", "SampleUntranslated"));
        foreach (var r in rows)
            w.WriteLine(string.Join(Sep, r.Plugin, r.TotalCount, r.TranslatedCount, r.UntranslatedCount, r.TranslatedRatio.ToString("F1"),
                r.TotalChars, r.TranslatedChars, r.UntranslatedChars, r.TranslatedCharsRatio.ToString("F1"), Escape(r.SamplePreview)));
    }

    /// <summary>One row of a written coverage_by_plugin.tsv, read back — used by
    /// Translation's PluginSummaryWriter (v0.20.0) to combine PickUpTarget's
    /// "already covered by existing DSD" view with Translation's own "resolved
    /// by AutoTranslator" view into one per-plugin recommendation.</summary>
    public sealed record CoverageRow(string Plugin, int TotalCount, int TranslatedCount, int UntranslatedCount, double TranslatedRatio,
        long TotalChars, long TranslatedChars, long UntranslatedChars, double TranslatedCharsRatio, string SampleUntranslated);

    public static List<CoverageRow> ReadTsv(string path)
    {
        var result = new List<CoverageRow>();
        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8).Skip(1))
        {
            if (line.Length == 0) continue;
            var p = line.Split(Sep);
            if (p.Length < 10) continue;
            result.Add(new CoverageRow(p[0], int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), double.Parse(p[4]),
                long.Parse(p[5]), long.Parse(p[6]), long.Parse(p[7]), double.Parse(p[8]), Unescape(p[9])));
        }
        return result;
    }
}
