using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// SameModHintFinder is issue #4's "c" block: a shared, once-per-batch hint
/// listing this session's OWN earlier translations for THIS SAME mod, so a
/// re-batch after a `finish_reason=length` truncation (or any later batch in
/// the same run) doesn't drift from wording already chosen earlier in the
/// same session (the reported bug: "GreatSword" translated one way in batch
/// 1, a different way in batch 2).
///
/// Reuses the same TF-IDF+cosine engine as PrecedentRetriever
/// (<see cref="CorpusSimilarityIndex"/>) and the same record-type bonus
/// (<see cref="RecordTypeAffinity"/>), but differs in two ways: (1) there is
/// no same-plugin bonus — the whole pool is already scoped to one mod's own
/// translations, so every entry would trivially "match", making the bonus
/// meaningless; (2) relevance is evaluated against the WHOLE batch of still-
/// unresolved candidates at once (a pool entry's score is its best score
/// against ANY candidate in the batch), since this is a single block shared
/// by the entire batch, not a per-candidate lookup.
///
/// One shared fixture (Fixtures/Translation/SameModHintFinder/pool.tsv)
/// covers every scenario, each using its own exclusive vocabulary so one
/// scenario's DsdType never leaks a bonus into another's ranking.
/// </summary>
public class SameModHintFinderTests
{
    private static readonly IReadOnlyList<CorpusEntry> Pool = CorpusIo.ReadTsv(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "SameModHintFinder", "pool.tsv"));

    /// <summary>A pool entry only needs to be relevant to ONE candidate in the
    /// batch to surface — "Frostwind Blade" is only relevant to the first
    /// candidate, "Ember Totem Guardian" only to the second, but both appear
    /// in the single shared result.</summary>
    [Fact]
    public void FindHints_PoolEntryRelevantToAnyBatchCandidate_Surfaces()
    {
        var finder = new SameModHintFinder(Pool);

        var results = finder.FindHints(new[]
        {
            ("Frostwind Dagger", "WEAP FULL"),
            ("Ember Totem Ritual", "MISC FULL"),
        });

        Assert.Equal(
            new[] { "Ember Totem Guardian", "Frostwind Blade" },
            results.Select(r => r.English));
    }

    /// <summary>"Sunfire Ring" (ARMO FULL) and "Sunfire Chronicle" (BOOK FULL)
    /// share identical word overlap with the ARMO FULL candidate — only the
    /// record-type bonus (same as PrecedentRetriever's) should separate them.
    /// No same-plugin bonus exists in this class at all.</summary>
    [Fact]
    public void FindHints_SameRecordType_RanksAboveDifferentType()
    {
        var finder = new SameModHintFinder(Pool);

        var results = finder.FindHints(new[] { ("Sunfire Amulet", "ARMO FULL") });

        Assert.Equal(new[] { "Sunfire Ring", "Sunfire Chronicle" }, results.Select(r => r.English));
    }

    /// <summary>The 500-character entry cap (same as PrecedentRetriever's,
    /// via the shared CorpusSimilarityIndex) still applies: the long entry
    /// sharing "ledger" is excluded even though it would otherwise share more
    /// words with the candidate than "Moonlit Ledger" does.</summary>
    [Fact]
    public void FindHints_EntryOverLengthCap_Excluded()
    {
        var finder = new SameModHintFinder(Pool);

        var results = finder.FindHints(new[] { ("A merchant's old ledger", "MISC FULL") });

        Assert.Equal(new[] { "Moonlit Ledger" }, results.Select(r => r.English));
    }

    /// <summary>The relative score cutoff (40% of the top score, same as
    /// PrecedentRetriever's) still applies: "Coil of Distant Memory and
    /// Forgotten Song" shares "coil" but is diluted by its own extra words,
    /// falling below the cutoff relative to the exact "Ashen Coil" match.</summary>
    [Fact]
    public void FindHints_ScoreBelowRelativeCutoff_Excluded()
    {
        var finder = new SameModHintFinder(Pool);

        var results = finder.FindHints(new[] { ("Ashen Coil of Power", "MISC FULL") });

        Assert.Equal(new[] { "Ashen Coil" }, results.Select(r => r.English));
    }

    [Fact]
    public void FindHints_NoBatchCandidateSharesVocabularyWithAnyPoolEntry_ReturnsEmpty()
    {
        var finder = new SameModHintFinder(Pool);

        var results = finder.FindHints(new[] { ("Xyzzyx Quuxfoo", "MISC FULL") });

        Assert.Empty(results);
    }

    [Fact]
    public void FindHints_EmptyBatch_ReturnsEmpty()
    {
        var finder = new SameModHintFinder(Pool);

        var results = finder.FindHints(Array.Empty<(string, string)>());

        Assert.Empty(results);
    }
}
