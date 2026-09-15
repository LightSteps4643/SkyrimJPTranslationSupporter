using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// issue #4's "c" block: finds this session's OWN earlier translations for
/// THIS SAME mod that are relevant to the batch about to be sent, so a
/// re-batch after a `finish_reason=length` truncation (or any later batch in
/// the same run) doesn't drift from wording already chosen earlier in the
/// same session — the reported bug this exists to fix: "GreatSword"
/// translated one way in batch 1, a different way in batch 2.
///
/// Reuses the same TF-IDF+cosine engine as <see cref="PrecedentRetriever"/>
/// (<see cref="CorpusSimilarityIndex"/>) and the same record-type bonus
/// (<see cref="RecordTypeAffinity"/>) — 2026-09-16, after real data showed
/// TF-IDF meaningfully improved ranking even over this class's much smaller,
/// single-mod pool compared to the Jaccard-style scheme originally designed
/// for it (62.6% of a 182-candidate real-data sample reordered, mostly by
/// breaking Jaccard's frequent exact ties on short, similarly-shaped
/// sentences) — but differs from it in two ways:
///
/// 1. No same-plugin bonus. The whole pool passed to the constructor is
///    already scoped to one mod's own translations, so every entry would
///    trivially "match" on plugin, making that bonus meaningless here.
///
/// 2. Relevance is evaluated against the WHOLE batch of still-unresolved
///    candidates at once, not one candidate at a time — a pool entry's score
///    is its BEST score against any candidate in the batch — because this is
///    a single block shared once by the entire batch (like the fixed
///    instruction text), not a per-candidate lookup the way "Reference
///    examples" is.
///
/// The caller is responsible for: building the pool (this mod's own already-
/// resolved candidates, restricted to the trust-tier Notes this hint is
/// allowed to draw on), fitting the result into the batch's character
/// budget, omitting the section entirely when this returns empty, and
/// skipping any entry whose English text is already shown verbatim in some
/// candidate's own "Reference examples" list (avoiding a duplicate hint).
/// </summary>
public sealed class SameModHintFinder
{
    private readonly CorpusSimilarityIndex _index;

    public SameModHintFinder(IReadOnlyList<CorpusEntry> pool)
    {
        _index = new CorpusSimilarityIndex(pool);
    }

    /// <param name="candidates">The batch's still-unresolved candidates (English
    /// text and DSD record type) to find same-mod hints relevant to ANY of them.</param>
    public List<CorpusEntry> FindHints(IReadOnlyList<(string Text, string RecordType)> candidates)
    {
        if (candidates.Count == 0) return new List<CorpusEntry>();

        var pool = _index.Entries;
        var best = new Dictionary<int, double>();

        foreach (var (text, recordType) in candidates)
        {
            var scored = _index.ScoreAgainst(text);
            if (scored.Count == 0) continue;

            var signature = RecordTypeAffinity.SignatureOf(recordType);
            foreach (var (idx, score) in scored)
            {
                var total = score + RecordTypeAffinity.Compute(pool[idx].DsdType, recordType, signature);
                if (!best.TryGetValue(idx, out var existing) || total > existing)
                    best[idx] = total;
            }
        }

        if (best.Count == 0) return new List<CorpusEntry>();

        // 2026-09-16: a tie is broken by Notes trust tier (SameModTrustTier) —
        // a hint from a more trustworthy source (e.g. a human's ModifiedByUser
        // edit) should win a tie over one from a lower tier (e.g. an LLM's own
        // earlier guess), not by incidental string length.
        var scoredList = best.Select(kv => (Index: kv.Key, Score: kv.Value)).ToList();
        return _index.SelectByRelativeCutoff(scoredList, priorityOf: idx => SameModTrustTier.PriorityOf(pool[idx].SourceKind));
    }
}
