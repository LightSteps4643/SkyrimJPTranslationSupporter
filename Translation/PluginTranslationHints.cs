using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>The plugin side's finished translations, read from its Translation/out_temp/&lt;plugin&gt;/translations.tsv
/// files, as the pool of style hints the SkyPatcher run offers an LLM (so a mod's items are named the same way
/// whether the name comes from its plugin or from a SkyPatcher .ini). Only rows whose method is trusted for
/// that purpose (<see cref="SameModTrustTier"/>: not the corpus family, already reachable through "Reference
/// examples", and not a possible failure) and that really hold a translation. When several plugins translate
/// the same English text, the most trustworthy method wins; a tie keeps the first (plugin folders are read in name order).</summary>
public static class PluginTranslationHints
{
    public static IReadOnlyList<CorpusEntry> Load(string translationOutTempDir, TraceLog? trace = null)
    {
        if (!Directory.Exists(translationOutTempDir)) return [];

        var best = new Dictionary<string, (CorpusEntry Entry, int Priority)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(translationOutTempDir, "translations.tsv", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in ReadEntries(file))
            {
                var priority = SameModTrustTier.PriorityOf(entry.SourceKind);
                if (!best.TryGetValue(entry.English, out var current) || priority < current.Priority)
                    best[entry.English] = (entry, priority);
            }
        }

        trace?.Debug($"Plugin-side hint pool: {best.Count} translation(s) from {translationOutTempDir}");
        return best.Values.Select(v => v.Entry).ToList();
    }

    private static IEnumerable<CorpusEntry> ReadEntries(string path)
    {
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        if (lines.Length == 0) yield break;

        var header = lines[0].TrimStart('﻿').Split('\t');
        int Col(string name) => Array.IndexOf(header, name);
        int plugin = Col("WinningPlugin"), type = Col("RecordType"), english = Col("EnglishText"), japanese = Col("Japanese"), notes = Col("Notes");
        if (plugin < 0 || type < 0 || english < 0 || japanese < 0 || notes < 0) yield break;

        string Cell(string[] cells, int index) => index < cells.Length ? TsvEscaping.Unescape(cells[index]) : "";
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var cells = line.Split('\t');
            var jp = Cell(cells, japanese);
            var method = Cell(cells, notes);
            if (jp.Length == 0 || !SameModTrustTier.IsEligible(method)) continue;
            yield return new CorpusEntry(Cell(cells, english), jp, Cell(cells, plugin), method, Cell(cells, type));
        }
    }
}
