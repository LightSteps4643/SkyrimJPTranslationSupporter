namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// A plain word-level English→Japanese dictionary — just a "word\tjapanese"
/// TSV loaded into a case-insensitive lookup. Originally backed by a JMdict-
/// derived file (Data/en_ja_dict.tsv), removed in v0.29.5 after real data
/// turned up homograph mistranslations it couldn't filter out on its own
/// (e.g. "Ward"→"区" instead of the defensive spell — see AutoTranslator's and
/// NameFallbackTranslator's remarks). Now backs only the hand-curated,
/// name-only glossary (Data/name_glossary.tsv), where every entry is a
/// reviewed choice rather than an unattended dictionary lookup.
/// </summary>
public sealed class EnJaDictionary
{
    private readonly Dictionary<string, string> _entries;

    private EnJaDictionary(Dictionary<string, string> entries) => _entries = entries;

    public static EnJaDictionary Load(string path)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            entries[line[..tab]] = line[(tab + 1)..];
        }
        return new EnJaDictionary(entries);
    }

    public bool TryTranslateWord(string word, out string japanese) => _entries.TryGetValue(word, out japanese!);

    public int Count => _entries.Count;
}
