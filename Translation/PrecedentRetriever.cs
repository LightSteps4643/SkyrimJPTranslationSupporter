using System.Text.RegularExpressions;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Finds the corpus entries most likely to be useful precedent for translating
/// a given English candidate string — simple word-overlap scoring (no
/// embeddings/ML needed at this scale; this is the "retrieval" half of the
/// AI-chat RAG path).
///
/// Builds an inverted index (word -&gt; corpus entries) once up front, so
/// looking up precedents for N candidates against a corpus of size M is
/// roughly O(N * avg postings per word) instead of the naive O(N * M)
/// (re-tokenizing and rescanning the whole corpus per candidate) — this
/// mattered once the caller started asking for precedents across an entire
/// load order's worth of candidates (tens of thousands) rather than one
/// mod's handful.
///
/// v0.5.0: scoring is now RECORD-TYPE AWARE. Word overlap alone happily offers
/// a dialogue line as precedent for an armour name; a precedent drawn from the
/// same kind of string is far more likely to carry the naming convention the
/// candidate needs (Bethesda's Japanese localization is highly templated per
/// record type). Same DSD type scores highest, same 4-char signature next
/// (an "ARMO FULL" candidate can still learn from "ARMO DESC"), everything
/// else keeps its plain overlap score — a bonus, never a filter, so a
/// strong cross-type match is still reachable when nothing same-type exists.
/// </summary>
public sealed class PrecedentRetriever
{
    private static readonly Regex WordSplit = new(@"[^A-Za-z']+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new()
    {
        "the", "and", "for", "with", "your", "you", "this", "that", "from", "are", "was", "were",
    };

    // Scaled so affinity breaks ties and lifts a near-miss, but cannot
    // manufacture a match out of an entry that shares no vocabulary at all
    // (an entry with zero overlapping words is never scored in the first place).
    private const int SameTypeBonus = 2;
    private const int SameSignatureBonus = 1;

    // v0.6.0. Ranked ABOVE same-type: a precedent from the very mod being
    // translated is the strongest consistency signal there is — it is that mod's
    // own established terminology, which the reader will see side by side with
    // the string being translated. Only reachable when that mod has its own
    // xTranslator import (see XTranslatorImporter), since third-party mods
    // otherwise contribute no corpus rows of their own — v0.33.0 retired the
    // in-session reflux mechanism this bonus used to also draw on.
    private const int SamePluginBonus = 3;

    private readonly IReadOnlyList<CorpusEntry> _corpus;
    private readonly Dictionary<string, List<int>> _invertedIndex = new();

    public PrecedentRetriever(IReadOnlyList<CorpusEntry> corpus)
    {
        _corpus = corpus;
        for (var i = 0; i < corpus.Count; i++)
        {
            // Skip corpus entries that aren't real name-field text (e.g. FACT's
            // internal developer notes like "used for combat") — same filter as
            // CorpusTransliterator's mining input, applied here so this noise
            // doesn't surface as a misleading "参考例" in AI-chat prompts.
            if (!NameFieldFilter.LooksLikeNameField(corpus[i].English)) continue;

            foreach (var word in Tokenize(corpus[i].English))
            {
                if (!_invertedIndex.TryGetValue(word, out var list))
                {
                    list = new List<int>();
                    _invertedIndex[word] = list;
                }
                list.Add(i);
            }
        }
    }

    /// <param name="candidateType">The candidate's DSD type ("ARMO FULL"), used to
    /// prefer same-kind precedent. Pass "" to score purely on word overlap.</param>
    /// <param name="candidatePlugin">The candidate's winning plugin, used to prefer
    /// precedent already established inside the same mod. Pass "" to ignore.</param>
    public List<CorpusEntry> FindPrecedents(string candidateText, int topN = 5, string candidateType = "", string candidatePlugin = "")
    {
        var candidateWords = Tokenize(candidateText);
        if (candidateWords.Count == 0) return new List<CorpusEntry>();

        var scores = new Dictionary<int, int>();
        foreach (var word in candidateWords)
        {
            if (!_invertedIndex.TryGetValue(word, out var indices)) continue;
            foreach (var idx in indices)
                scores[idx] = scores.GetValueOrDefault(idx) + 1;
        }

        var candidateSignature = SignatureOf(candidateType);

        return scores
            .OrderByDescending(kv => kv.Value
                + TypeAffinity(_corpus[kv.Key].DsdType, candidateType, candidateSignature)
                + PluginAffinity(_corpus[kv.Key].Source, candidatePlugin))
            .ThenBy(kv => _corpus[kv.Key].English.Length) // prefer shorter/more focused matches when tied
            .Take(topN)
            .Select(kv => _corpus[kv.Key])
            .ToList();
    }

    private static int PluginAffinity(string entrySource, string candidatePlugin)
    {
        if (candidatePlugin.Length == 0 || entrySource.Length == 0) return 0;
        return string.Equals(entrySource, candidatePlugin, StringComparison.OrdinalIgnoreCase) ? SamePluginBonus : 0;
    }

    private static int TypeAffinity(string entryType, string candidateType, string candidateSignature)
    {
        if (candidateType.Length == 0 || entryType.Length == 0) return 0;
        if (string.Equals(entryType, candidateType, StringComparison.OrdinalIgnoreCase)) return SameTypeBonus;
        if (string.Equals(SignatureOf(entryType), candidateSignature, StringComparison.OrdinalIgnoreCase)) return SameSignatureBonus;
        return 0;
    }

    /// <summary>The 4-char xEdit record signature embedded in a DSD type string
    /// ("ARMO FULL" -&gt; "ARMO").</summary>
    private static string SignatureOf(string dsdType)
    {
        var space = dsdType.IndexOf(' ');
        return space > 0 ? dsdType[..space] : dsdType;
    }

    private static HashSet<string> Tokenize(string text) =>
        WordSplit.Split(text)
            .Where(w => w.Length > 2)
            .Select(w => w.ToLowerInvariant())
            .Where(w => !StopWords.Contains(w))
            .ToHashSet();
}
