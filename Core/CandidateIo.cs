using static SkyrimJPStringPatcher.Core.TsvEscaping;

namespace SkyrimJPStringPatcher.Core;

/// <summary>Reads/writes the Stage 1 -> Stage 2/3 candidates interchange file (TSV).
/// Kept dependency-free (no Mutagen types) so later stages don't need to know
/// anything about Mutagen/MO2 at all — they only ever read this file.</summary>
public static class CandidateIo
{
    private const char Sep = '\t';

    public static void WriteTsv(string path, IEnumerable<Candidate> candidates)
    {
        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine(string.Join(Sep, "FormId", "WinningPlugin", "RecordType", "EnglishText", "Index", "EditorId", "Context", "StaleOriginal", "StaleTranslation", "Warning", "CrossModPrecedentJapanese", "CrossModPrecedentNeedsReview", "DsdCoveredJapanese", "DsdCoveredNotes", "SourcePath", "SourceFilter", "SourceLine", "Inactive", "Excluded"));
        foreach (var c in candidates)
            w.WriteLine(string.Join(Sep, c.FormId, c.WinningPlugin, c.RecordType, Escape(c.CurrentText), c.Index, Escape(c.EditorId), Escape(c.Context),
                Escape(c.StaleOriginal), Escape(c.StaleTranslation), Escape(c.Warning), Escape(c.CrossModPrecedentJapanese), c.CrossModPrecedentNeedsReview ? "1" : "0",
                Escape(c.DsdCoveredJapanese), Escape(c.DsdCoveredNotes),
                Escape(c.SourcePath), Escape(c.SourceFilter), c.SourceLine > 0 ? c.SourceLine.ToString() : "", Escape(c.Inactive), Escape(c.Excluded)));
    }

    public static List<Candidate> ReadTsv(string path)
    {
        var result = new List<Candidate>();
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        foreach (var line in lines.Skip(1)) // header
        {
            if (line.Length == 0) continue;
            var parts = line.Split(Sep);
            if (parts.Length < 4) continue;
            var index = parts.Length > 4 && int.TryParse(parts[4], out var i) ? i : 0;
            var editorId = parts.Length > 5 ? Unescape(parts[5]) : "";
            var context = parts.Length > 6 ? Unescape(parts[6]) : "";
            var staleOriginal = parts.Length > 7 ? Unescape(parts[7]) : "";
            var staleTranslation = parts.Length > 8 ? Unescape(parts[8]) : "";
            var warning = parts.Length > 9 ? Unescape(parts[9]) : "";
            var crossModPrecedentJapanese = parts.Length > 10 ? Unescape(parts[10]) : "";
            var crossModPrecedentNeedsReview = parts.Length > 11 && parts[11] == "1";
            var dsdCoveredJapanese = parts.Length > 12 ? Unescape(parts[12]) : "";
            var dsdCoveredNotes = parts.Length > 13 ? Unescape(parts[13]) : "";
            var sourcePath = parts.Length > 14 ? Unescape(parts[14]) : "";
            var sourceFilter = parts.Length > 15 ? Unescape(parts[15]) : "";
            var sourceLine = parts.Length > 16 && int.TryParse(parts[16], out var sl) ? sl : 0;
            var inactive = parts.Length > 17 ? Unescape(parts[17]) : "";
            var excluded = parts.Length > 18 ? Unescape(parts[18]) : "";
            result.Add(new Candidate(parts[1], parts[0], parts[2], Unescape(parts[3]), index, editorId, context, staleOriginal, staleTranslation, warning,
                crossModPrecedentJapanese, crossModPrecedentNeedsReview, dsdCoveredJapanese, dsdCoveredNotes,
                sourcePath, sourceFilter, sourceLine, inactive, excluded));
        }
        return result;
    }
}
