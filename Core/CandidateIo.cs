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
        w.WriteLine(string.Join(Sep, "FormId", "WinningPlugin", "RecordType", "EnglishText", "Index", "EditorId", "Context", "StaleOriginal", "StaleTranslation"));
        foreach (var c in candidates)
            w.WriteLine(string.Join(Sep, c.FormId, c.WinningPlugin, c.RecordType, Escape(c.CurrentText), c.Index, Escape(c.EditorId), Escape(c.Context),
                Escape(c.StaleOriginal), Escape(c.StaleTranslation)));
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
            result.Add(new Candidate(parts[1], parts[0], parts[2], Unescape(parts[3]), index, editorId, context, staleOriginal, staleTranslation));
        }
        return result;
    }
}
