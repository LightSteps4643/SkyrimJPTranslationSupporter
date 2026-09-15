using System.Text.RegularExpressions;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// TF-IDF weighted cosine similarity index over a list of corpus-like entries
/// — the shared scoring engine behind both <see cref="PrecedentRetriever"/>
/// (b: general corpus reference examples) and the same-mod hint finder for
/// issue #4 (c: this session's own earlier translations for this same mod).
/// Extracted 2026-09-16 when c was redesigned to use the same TF-IDF+cosine
/// approach as b instead of a separate Jaccard-style scheme — real data
/// showed TF-IDF meaningfully improved ranking even over c's much smaller,
/// single-mod pool (62.6% of a 182-candidate sample reordered, mostly by
/// breaking Jaccard's frequent exact ties), so sharing the one algorithm
/// made more sense than maintaining two.
///
/// Each word's weight is idf(w) = ln(N / (df(w)+1)) + 1, where N is the total
/// entry count and df(w) the number of entries containing it; the similarity
/// between a query and an entry is their shared-word dot product (sum of
/// idf(w)^2 over the overlap) divided by the product of both vectors' norms.
/// This is the standard information-retrieval technique — chosen over a
/// raw word-overlap-count scheme because that scheme has no way to normalize
/// for document length: a handful of matching words means the same thing
/// whether an entry is a two-word name or an entire book chapter, letting a
/// long, mostly-irrelevant entry coincidentally outrank a short, highly
/// relevant one.
///
/// An absolute per-entry length cap (500 characters, applied at scoring time
/// so it never even reaches the caller) guards against the one failure mode
/// cosine normalization does not fully prevent on its own: a very long,
/// otherwise-irrelevant document that happens to share exactly one rare word
/// with the query (its own high norm from having many OTHER words isn't
/// enough to zero out a sufficiently rare shared word's contribution). 500
/// was picked as the natural gap found in real data between genuinely useful
/// longer precedent (dialogue/description entries, seen up to ~370
/// characters) and this coincidental-collision failure mode (always 1,000+
/// characters, in practice full book excerpts of several thousand).
///
/// Builds an inverted index (word -&gt; entries) once up front, so scoring N
/// queries against a pool of size M is roughly O(N * avg postings per word)
/// instead of the naive O(N * M).
/// </summary>
public sealed class CorpusSimilarityIndex
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

    /// <summary>See the class remarks for why 500 was chosen.</summary>
    public const int MaxEntryLength = 500;

    private readonly IReadOnlyList<CorpusEntry> _entries;
    private readonly HashSet<string>[] _tokens;
    private readonly double[] _norm;
    private readonly Dictionary<string, int> _documentFrequency = new();
    private readonly Dictionary<string, List<int>> _invertedIndex = new();
    private readonly int _entryCount;

    public CorpusSimilarityIndex(IReadOnlyList<CorpusEntry> entries)
    {
        _entries = entries;
        _entryCount = entries.Count;
        _tokens = new HashSet<string>[entries.Count];
        _norm = new double[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            var tokens = Tokenize(entries[i].English);
            _tokens[i] = tokens;
            foreach (var word in tokens)
                _documentFrequency[word] = _documentFrequency.GetValueOrDefault(word) + 1;
        }

        for (var i = 0; i < entries.Count; i++)
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

    public IReadOnlyList<CorpusEntry> Entries => _entries;

    /// <summary>Raw cosine similarity (no bonuses applied) between
    /// <paramref name="queryText"/> and every entry that shares vocabulary
    /// with it and is within <see cref="MaxEntryLength"/>. Empty for an
    /// empty-after-tokenization or out-of-vocabulary query.</summary>
    public List<(int Index, double Score)> ScoreAgainst(string queryText)
    {
        var queryWords = Tokenize(queryText);
        if (queryWords.Count == 0) return new List<(int, double)>();

        var queryNorm = Math.Sqrt(queryWords.Sum(w => Idf(w) * Idf(w)));
        if (queryNorm == 0) return new List<(int, double)>();

        var dotProduct = new Dictionary<int, double>();
        foreach (var word in queryWords)
        {
            if (!_invertedIndex.TryGetValue(word, out var indices)) continue;
            var idfSquared = Idf(word) * Idf(word);
            foreach (var idx in indices)
                dotProduct[idx] = dotProduct.GetValueOrDefault(idx) + idfSquared;
        }

        var results = new List<(int Index, double Score)>();
        foreach (var (idx, dot) in dotProduct)
        {
            if (_entries[idx].English.Length > MaxEntryLength) continue;
            if (_norm[idx] == 0) continue;
            results.Add((idx, dot / (queryNorm * _norm[idx])));
        }
        return results;
    }

    /// <summary>Sorts descending by score (ties broken first by <paramref
    /// name="priorityOf"/> when supplied — issue #4's "c" hint finder passes
    /// its Notes trust-tier here, since a same-mod hint from a more
    /// trustworthy source should win a tie over the class's own default
    /// shorter-English-first tie-break, which still applies afterward for
    /// any tie the priority itself doesn't resolve) and keeps only entries
    /// scoring at least <paramref name="relativeCutoff"/> (default 40%) of
    /// the top entry's own score — no fixed count, no floor. See <see
    /// cref="PrecedentRetriever"/>'s remarks for why a relative cutoff
    /// replaced a fixed topN as the primary selection mechanism: real data
    /// showed even rank 20-30 stayed thematically relevant once TF-IDF fixed
    /// the old noise problem, so a fixed count left value on the table, while
    /// a floor forcing a below-threshold entry in undermines the point of
    /// having a quality bar at all. Scores passed in are expected to already
    /// include any bonuses the caller wants applied — this method itself is
    /// bonus-agnostic.</summary>
    /// <param name="priorityOf">Optional: maps an entry's index to a
    /// tie-break priority (lower wins). Omit for plain shorter-first tie-break
    /// (b's own use — <see cref="PrecedentRetriever"/> has no such concept).</param>
    public List<CorpusEntry> SelectByRelativeCutoff(List<(int Index, double Score)> scored, double relativeCutoff = 0.4, Func<int, int>? priorityOf = null)
    {
        if (scored.Count == 0) return new List<CorpusEntry>();

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            if (priorityOf != null)
            {
                var byPriority = priorityOf(a.Index).CompareTo(priorityOf(b.Index));
                if (byPriority != 0) return byPriority;
            }
            return _entries[a.Index].English.Length.CompareTo(_entries[b.Index].English.Length);
        });

        var cutoff = scored[0].Score * relativeCutoff;
        return scored
            .TakeWhile(s => s.Score >= cutoff)
            .Select(s => _entries[s.Index])
            .ToList();
    }

    public double Idf(string word) => Math.Log((double)_entryCount / (_documentFrequency.GetValueOrDefault(word) + 1)) + 1;

    public static HashSet<string> Tokenize(string text) =>
        WordSplit.Split(text)
            .Where(w => w.Length > 2)
            .Select(w => w.ToLowerInvariant())
            .Where(w => !StopWords.Contains(w))
            .ToHashSet();
}
