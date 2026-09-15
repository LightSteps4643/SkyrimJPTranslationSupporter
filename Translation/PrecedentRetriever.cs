using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Finds the corpus entries most likely to be useful precedent for translating
/// a given English candidate string — the "retrieval" half of the AI-chat RAG
/// path, layering record-type/plugin bonuses on top of the shared TF-IDF
/// weighted cosine similarity engine (<see cref="CorpusSimilarityIndex"/>;
/// see its remarks for the scoring rationale — idf weighting, the 500-
/// character per-entry cap, and why NameFieldFilter is no longer applied).
///
/// v0.5.0: scoring is also RECORD-TYPE AWARE (<see cref="RecordTypeAffinity"/>).
/// Word overlap alone happily offers a dialogue line as precedent for an
/// armour name; a precedent drawn from the same kind of string is far more
/// likely to carry the naming convention the candidate needs.
///
/// 2026-09-16: no fixed topN either, and no separate count cap of any kind.
/// A relative score cutoff (any entry scoring below 40% of the top entry's
/// own score is dropped, see <see cref="CorpusSimilarityIndex.SelectByRelativeCutoff"/>)
/// replaces it — real data showed even rank 20-30 stayed thematically
/// relevant once TF-IDF fixed the old scheme's noise problem, so an
/// arbitrary fixed count left value on the table; conversely there is no
/// floor forcing a minimum count either, since real data showed the floor's
/// forced extra entry was marginal at best when the top 1-2 were already
/// strong, and forcing a below-threshold entry in undermines the point of
/// having a quality bar at all. A separate "how many characters can this
/// candidate's whole batch of precedents actually cost" budget (a percentage
/// of the LLM call's own batch character limit) is the caller's job, not
/// this class's — with the 500-character per-entry cap already bounding any
/// single entry's contribution, that caller-side budget is what actually
/// bounds the total size, so a count cap here would be redundant with it,
/// not an independent safeguard.
/// </summary>
public sealed class PrecedentRetriever
{
    // Ranked ABOVE same-type: a precedent from the very mod being translated
    // is the strongest consistency signal there is — it is that mod's own
    // established terminology, which the reader will see side by side with
    // the string being translated. Only reachable when that mod has its own
    // xTranslator import (see XTranslatorImporter), since third-party mods
    // otherwise contribute no corpus rows of their own.
    private const double SamePluginBonus = 0.06;

    private readonly CorpusSimilarityIndex _index;

    public PrecedentRetriever(IReadOnlyList<CorpusEntry> corpus)
    {
        _index = new CorpusSimilarityIndex(corpus);
    }

    /// <param name="candidateType">The candidate's DSD type ("ARMO FULL"), used to
    /// prefer same-kind precedent. Pass "" to score purely on similarity.</param>
    /// <param name="candidatePlugin">The candidate's winning plugin, used to prefer
    /// precedent already established inside the same mod. Pass "" to ignore.</param>
    public List<CorpusEntry> FindPrecedents(string candidateText, string candidateType = "", string candidatePlugin = "")
    {
        var scored = _index.ScoreAgainst(candidateText);
        if (scored.Count == 0) return new List<CorpusEntry>();

        var corpus = _index.Entries;
        var candidateSignature = RecordTypeAffinity.SignatureOf(candidateType);

        var withBonus = scored
            .Select(s => (s.Index, Score: s.Score
                + PluginAffinity(corpus[s.Index].Source, candidatePlugin)
                + RecordTypeAffinity.Compute(corpus[s.Index].DsdType, candidateType, candidateSignature)))
            .ToList();

        return _index.SelectByRelativeCutoff(withBonus);
    }

    private static double PluginAffinity(string entrySource, string candidatePlugin)
    {
        if (candidatePlugin.Length == 0 || entrySource.Length == 0) return 0;
        return string.Equals(entrySource, candidatePlugin, StringComparison.OrdinalIgnoreCase) ? SamePluginBonus : 0;
    }
}
