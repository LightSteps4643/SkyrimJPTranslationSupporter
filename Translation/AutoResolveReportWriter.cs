using SkyrimJPStringPatcher.Core;
using static SkyrimJPStringPatcher.Core.TsvEscaping;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// v0.19.0: per-plugin AutoTranslator-solvability summary, written as a
/// byproduct of <see cref="PromptGenerator.RunAll"/> — the counterpart to
/// PickUpTarget's coverage_by_plugin.tsv (existing-DSD coverage), but for the
/// REMAINING gap after that: of what's left untranslated, how much can this
/// tool's own corpus/dictionary/transliteration pipeline (①②③④) resolve
/// without any AI or human?
///
/// Motivation: a MOD dropping out of an existing DSD's coverage (a new mod
/// version changed some strings, or no community translation exists at all)
/// doesn't automatically mean it needs a translation MOD hunted down or
/// hand-translated — a good chunk of it is often just re-derivable from
/// precedent already in this load order (item names built from words that
/// already have a shipped rendering, etc.). Seeing that BEFORE deciding
/// whether to go looking for an existing translation is what this answers.
/// </summary>
public static class AutoResolveReportWriter
{
    private const char Sep = '\t';
    private const int SampleMaxLength = 40;

    public static void WriteTsv(string path,
        IReadOnlyList<(string Plugin, int Count, int AutoResolved, long AutoResolvedChars, long RemainingChars, List<string> SampleRemaining)> perPlugin)
    {
        var rows = perPlugin
            .Select(p =>
            {
                var remainingCount = p.Count - p.AutoResolved;
                var totalChars = p.AutoResolvedChars + p.RemainingChars;
                return (
                    p.Plugin, TotalCount: p.Count, AutoResolvedCount: p.AutoResolved, RemainingCount: remainingCount,
                    AutoResolvedRatio: p.Count == 0 ? 100.0 : 100.0 * p.AutoResolved / p.Count,
                    TotalChars: totalChars, AutoResolvedChars: p.AutoResolvedChars, RemainingChars: p.RemainingChars,
                    AutoResolvedCharsRatio: totalChars == 0 ? 100.0 : 100.0 * p.AutoResolvedChars / totalChars,
                    SamplePreview: string.Join(" / ", p.SampleRemaining.Select(s => TextPreview.Truncate(s, SampleMaxLength))));
            })
            .OrderBy(r => r.AutoResolvedRatio) // least-auto-solvable first — same convention as coverage_by_plugin.tsv
            .ToList();

        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine(string.Join(Sep, "Plugin", "TotalCount", "AutoResolvedCount", "RemainingCount", "AutoResolvedRatio(%)",
            "TotalChars", "AutoResolvedChars", "RemainingChars", "AutoResolvedCharsRatio(%)", "SampleRemaining"));
        foreach (var r in rows)
            w.WriteLine(string.Join(Sep, r.Plugin, r.TotalCount, r.AutoResolvedCount, r.RemainingCount, r.AutoResolvedRatio.ToString("F1"),
                r.TotalChars, r.AutoResolvedChars, r.RemainingChars, r.AutoResolvedCharsRatio.ToString("F1"), Escape(r.SamplePreview)));
    }
}
