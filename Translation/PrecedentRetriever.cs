using System.Text.RegularExpressions;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Finds the corpus entries most likely to be useful precedent for translating
/// a given English candidate string — TF-IDF weighted cosine similarity (the
/// "retrieval" half of the AI-chat RAG path). Each word's weight is
/// idf(w) = ln(N / (df(w)+1)) + 1, where N is the total corpus row count and
/// df(w) the number of rows containing it; the score between the candidate
/// and an entry is their shared-word dot product (sum of idf(w)^2 over the
/// overlap) divided by the product of both vectors' norms. This is the
/// standard information-retrieval technique — a search-engine-grade way to
/// say "how similar are these two pieces of text", chosen over the older raw
/// word-overlap-count scheme because that scheme had no way to normalize for
/// document length: a handful of matching words meant the same thing whether
/// the entry was a two-word name or an entire book chapter, which let a long,
/// mostly-irrelevant entry coincidentally outrank a short, highly relevant
/// one (2026-09-16 redesign).
///
/// Builds an inverted index (word -&gt; corpus entries) once up front, so
/// looking up precedents for N candidates against a corpus of size M is
/// roughly O(N * avg postings per word) instead of the naive O(N * M).
///
/// v0.5.0: scoring is also RECORD-TYPE AWARE. Word overlap alone happily offers
/// a dialogue line as precedent for an armour name; a precedent drawn from the
/// same kind of string is far more likely to carry the naming convention the
/// candidate needs (Bethesda's Japanese localization is highly templated per
/// record type). Same DSD type scores highest, same 4-char signature next
/// (an "ARMO FULL" candidate can still learn from "ARMO DESC"), everything
/// else keeps its plain similarity score — a bonus, never a filter, so a
/// strong cross-type match is still reachable when nothing same-type exists.
///
/// 2026-09-16: no NameFieldFilter pre-filter anymore. That filter existed to
/// keep an entire book chapter from outranking a short name merely because it
/// shared a couple of common words with the candidate — exactly the document-
/// length problem TF-IDF+cosine similarity already solves structurally (a
/// long document's own norm grows with its vocabulary, so a thin match scores
/// low). Verified against real data: excluding sentence-like corpus entries
/// this way was throwing away genuinely valuable precedent (e.g. an exact
/// duplicate of a whole sentence already translated elsewhere) far more often
/// than it was needed to suppress noise. An absolute per-entry length cap
/// (500 characters) is kept as a narrower, cheaper safeguard against the one
/// failure mode cosine normalization does NOT fully prevent: a very long,
/// otherwise-irrelevant document that happens to share exactly one rare word
/// with the candidate (its own high norm from having many OTHER words isn't
/// enough to zero out a sufficiently rare shared word's contribution). 500
/// was picked as the natural gap in real data between genuinely useful longer
/// precedent (dialogue/description entries, seen up to ~370 characters) and
/// this coincidental-collision failure mode (always 1,000+ characters, in
/// practice full book excerpts of several thousand).
///
/// 2026-09-16: no fixed topN either, and no separate count cap of any kind.
/// A relative score cutoff (any entry scoring below 40% of the top entry's
/// own score is dropped) replaces it — real data showed even rank 20-30
/// stayed thematically relevant once TF-IDF fixed the old scheme's noise
/// problem, so an arbitrary fixed count left value on the table; conversely
/// there is no floor forcing a minimum count either, since real data showed
/// the floor's forced extra entry was marginal at best when the top 1-2 were
/// already strong, and forcing a below-threshold entry in undermines the
/// point of having a quality bar at all. A separate "how many characters can
/// this candidate's whole batch of precedents actually cost" budget (a
/// percentage of the LLM call's own batch character limit) is the caller's
/// job, not this class's — with the 500-character per-entry cap above already
/// bounding any single entry's contribution, that caller-side budget is what
/// actually bounds the total size, so a count cap here would be redundant
/// with it, not an independent safeguard.
/// </summary>
public sealed class PrecedentRetriever
{
    private static readonly Regex WordSplit = new(@"[^A-Za-z']+", RegexOptions.Compiled);

    // 2026-09-16: "you"/"your" removed — in Japanese they render differently
    // depending on the speaker's tone (あなた/お前/君), so a reference showing
    // how they were rendered elsewhere is a real consistency signal, not noise
    // an LLM can already handle unaided. "well" added — a genuine homograph
    // (the interjection "Well, ..." vs. the noun "well" as in "Arcane Well")
    // that TF-IDF weighting alone does not disambiguate (both are common
    // enough that a single-word match still scores non-trivially).
    private static readonly HashSet<string> StopWords = new()
    {
        "the", "and", "for", "with", "this", "that", "from", "are", "was", "were", "well",
    };

    // 2026-09-16: rescaled from the old integer bonuses (2/1/3) to this scale
    // to match TF-IDF+cosine scores (roughly 0-1, versus the old scheme's
    // roughly 1-11) — the 3:2:1 ratio between them is preserved, but the
    // absolute size is now small enough to only break near-ties/act as a
    // secondary signal rather than override a real difference in content
    // similarity (verified against real data: zero cases found, across a
    // diverse ~600-candidate sample, where these bonuses caused an entry
    // with materially weaker content similarity to outrank a materially
    // stronger one).
    private const double SameTypeBonus = 0.04;
    private const double SameSignatureBonus = 0.02;

    // Ranked ABOVE same-type: a precedent from the very mod being translated
    // is the strongest consistency signal there is — it is that mod's own
    // established terminology, which the reader will see side by side with
    // the string being translated. Only reachable when that mod has its own
    // xTranslator import (see XTranslatorImporter), since third-party mods
    // otherwise contribute no corpus rows of their own.
    private const double SamePluginBonus = 0.06;

    /// <summary>See the class remarks for why 500 was chosen.</summary>
    private const int MaxEntryLength = 500;

    /// <summary>See the class remarks for why a relative cutoff replaced the
    /// old fixed topN as the primary selection mechanism.</summary>
    private const double RelativeScoreCutoff = 0.4;

    private readonly IReadOnlyList<CorpusEntry> _corpus;
    private readonly HashSet<string>[] _tokens;
    private readonly double[] _norm;
    private readonly Dictionary<string, int> _documentFrequency = new();
    private readonly Dictionary<string, List<int>> _invertedIndex = new();
    private readonly int _corpusCount;

    public PrecedentRetriever(IReadOnlyList<CorpusEntry> corpus)
    {
        _corpus = corpus;
        _corpusCount = corpus.Count;
        _tokens = new HashSet<string>[corpus.Count];
        _norm = new double[corpus.Count];

        for (var i = 0; i < corpus.Count; i++)
        {
            var tokens = Tokenize(corpus[i].English);
            _tokens[i] = tokens;
            foreach (var word in tokens)
                _documentFrequency[word] = _documentFrequency.GetValueOrDefault(word) + 1;
        }

        for (var i = 0; i < corpus.Count; i++)
        {
            var norm = 0.0;
            foreach (var word in _tokens[i])
            {
                var idf = Idf(word);
                norm += idf * idf;

                if (!_invertedIndex.TryGetValue(word, out var list))
                {
                    list = new List<int>();
                    _invertedIndex[word] = list;
                }
                list.Add(i);
            }
            _norm[i] = Math.Sqrt(norm);
        }
    }

    /// <param name="candidateType">The candidate's DSD type ("ARMO FULL"), used to
    /// prefer same-kind precedent. Pass "" to score purely on similarity.</param>
    /// <param name="candidatePlugin">The candidate's winning plugin, used to prefer
    /// precedent already established inside the same mod. Pass "" to ignore.</param>
    public List<CorpusEntry> FindPrecedents(string candidateText, string candidateType = "", string candidatePlugin = "")
    {
        var candidateWords = Tokenize(candidateText);
        if (candidateWords.Count == 0) return new List<CorpusEntry>();

        var candidateNorm = Math.Sqrt(candidateWords.Sum(w => Idf(w) * Idf(w)));
        if (candidateNorm == 0) return new List<CorpusEntry>();

        var dotProduct = new Dictionary<int, double>();
        foreach (var word in candidateWords)
        {
            if (!_invertedIndex.TryGetValue(word, out var indices)) continue;
            var idfSquared = Idf(word) * Idf(word);
            foreach (var idx in indices)
                dotProduct[idx] = dotProduct.GetValueOrDefault(idx) + idfSquared;
        }

        var candidateSignature = SignatureOf(candidateType);

        var scored = new List<(int Index, double Score)>();
        foreach (var (idx, dot) in dotProduct)
        {
            if (_corpus[idx].English.Length > MaxEntryLength) continue;
            if (_norm[idx] == 0) continue;

            var score = dot / (candidateNorm * _norm[idx])
                + PluginAffinity(_corpus[idx].Source, candidatePlugin)
                + TypeAffinity(_corpus[idx].DsdType, candidateType, candidateSignature);
            scored.Add((idx, score));
        }

        if (scored.Count == 0) return new List<CorpusEntry>();

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : _corpus[a.Index].English.Length.CompareTo(_corpus[b.Index].English.Length);
        });

        var cutoff = scored[0].Score * RelativeScoreCutoff;
        return scored
            .TakeWhile(s => s.Score >= cutoff)
            .Select(s => _corpus[s.Index])
            .ToList();
    }

    private double Idf(string word) => Math.Log((double)_corpusCount / (_documentFrequency.GetValueOrDefault(word) + 1)) + 1;

    private static double PluginAffinity(string entrySource, string candidatePlugin)
    {
        if (candidatePlugin.Length == 0 || entrySource.Length == 0) return 0;
        return string.Equals(entrySource, candidatePlugin, StringComparison.OrdinalIgnoreCase) ? SamePluginBonus : 0;
    }

    private static double TypeAffinity(string entryType, string candidateType, string candidateSignature)
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
