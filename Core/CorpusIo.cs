using static SkyrimJPStringPatcher.Core.TsvEscaping;

namespace SkyrimJPStringPatcher.Core;

/// <summary>Reads/writes the PickUpTarget -> Translation corpus interchange file (TSV).</summary>
public static class CorpusIo
{
    private const char Sep = '\t';

    /// <summary>
    /// Deduplicates exact-match rows (all 4 columns identical) before writing.
    /// A single placeholder string like "(Invisible Continue)" legitimately
    /// labels many different records across the load order, so PickUpTarget's
    /// record-by-record corpus collection adds the SAME English/Japanese/Source/
    /// SourceKind row once per record that happens to share it — verified on a
    /// real corpus: ~24% of all rows (about 7,200 of 29,587) were exact
    /// duplicates this way. Deduplicating loses no information (the corpus only
    /// needs to know a translation pair is valid precedent once, not how many
    /// records happen to use it) and this waste would only grow once quest/
    /// dialogue text is added, where common lines repeat far more than name
    /// fields do. <c>CorpusEntry</c> is a record, so structural equality (all 4
    /// fields) is exactly what <see cref="Enumerable.Distinct{T}(IEnumerable{T})"/>
    /// needs — no custom comparer required. Rows with a DIFFERENT Source/
    /// SourceKind (e.g. the same translation appearing in both a vanilla lookup
    /// and an existing DSD file) are deliberately NOT merged here; that's a
    /// separate judgment call about provenance, out of scope for this fix.
    /// </summary>
    public static void WriteTsv(string path, IEnumerable<CorpusEntry> corpus)
    {
        var before = corpus is ICollection<CorpusEntry> c ? c.Count : corpus.Count();
        var deduped = corpus.Distinct().ToList();
        if (deduped.Count < before)
            Console.WriteLine($"Corpus: removed {before - deduped.Count} exact-duplicate row(s) ({before} -> {deduped.Count})");

        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine(string.Join(Sep, "English", "Japanese", "Source", "SourceKind", "DsdType"));
        foreach (var entry in deduped)
            w.WriteLine(string.Join(Sep, Escape(entry.English), Escape(entry.Japanese), Escape(entry.Source), entry.SourceKind, entry.DsdType));
    }

    /// <summary>Tolerates a pre-v0.5.0 corpus.tsv (no DsdType column) by defaulting
    /// that field to empty, same backward-compatible approach CandidateIo uses.</summary>
    public static List<CorpusEntry> ReadTsv(string path)
    {
        var result = new List<CorpusEntry>();
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var parts = line.Split(Sep);
            if (parts.Length < 4) continue;
            var dsdType = parts.Length > 4 ? parts[4] : "";
            result.Add(new CorpusEntry(Unescape(parts[0]), Unescape(parts[1]), Unescape(parts[2]), parts[3], dsdType));
        }
        return result;
    }
}
