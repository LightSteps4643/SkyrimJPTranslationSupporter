using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// SentenceAlignmentMiner recovers English->katakana pairs that appear only
/// inside running prose (never as a standalone name), by statistical
/// co-occurrence (Dice coefficient) across the whole corpus, filtered by 3
/// precision checks (initial-sound agreement, katakana/letter length ratio,
/// similarity to Transliterator's phonetic guess) and a corpus-witness
/// verification pass. Called from CorpusTransliterator.Build — a materially
/// different mechanism from that class's own "multi-segment growth pass",
/// which CorpusTransliteratorTests explicitly scoped out; this class was
/// simply never tested at all until now.
///
/// One shared synthetic fixture (Fixtures/Translation/SentenceAlignmentMiner/
/// corpus.tsv) isolates each gate:
/// - "frost"/"フロスト": co-occurs in 2 entries, clears every filter and is
///   witnessed by those same 2 entries (each carries only 1 katakana run) ->
///   the happy path.
/// - "shadow"/"シャドウ": co-occurs in 2 entries (clears cooccurrence/Dice),
///   but every entry containing the pair also carries 2 OTHER unrelated
///   katakana runs (3 total each) -> exceeds MaxRunsForWitness, so the
///   pair is mined but then rejected for lack of a clean witness.
/// - "magic"/"ラプトル": co-occurs in 2 entries (clears cooccurrence/Dice),
///   but the initial sound ('M' vs a katakana run starting with ラ) never
///   agrees -> rejected by the phonetic plausibility gate before the
///   witness step is even reached.
/// </summary>
public class SentenceAlignmentMinerTests
{
    private static readonly IReadOnlyList<CorpusEntry> Corpus = CorpusIo.ReadTsv(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "SentenceAlignmentMiner", "corpus.tsv"));

    [Fact]
    public void Mine_WordOnlyEverSeenInSentences_RecoveredWithItsKatakana()
    {
        var result = SentenceAlignmentMiner.Mine(Corpus);

        Assert.True(result.ContainsKey("frost"));
        Assert.Equal("フロスト", result["frost"].Katakana);
        Assert.NotEmpty(result["frost"].SourceSummary);
    }

    /// <summary>Direct test of the MinCooccurrence gate itself, via the
    /// class's own public configuration knob rather than a production-code
    /// break: "frost"/"フロスト" co-occurs exactly twice, which clears the
    /// default Thorough profile's threshold (2) but falls short of Fast's
    /// stricter one (3) — so switching profiles alone must flip the
    /// result.</summary>
    [Fact]
    public void Mine_PairBelowTheActiveProfilesMinCooccurrence_IsNeverConsidered()
    {
        TuningProfile.Use(TuningProfile.Fast);
        try
        {
            var result = SentenceAlignmentMiner.Mine(Corpus);

            Assert.False(result.ContainsKey("frost"));
        }
        finally
        {
            TuningProfile.Use(TuningProfile.Thorough); // restore the default so other tests aren't affected
        }
    }

    /// <summary>Co-occurrence and Dice alone are not enough — a mined pair
    /// still needs at least one LOW-NOISE corpus entry (<=2 katakana runs)
    /// that directly exhibits it. Every entry attesting "shadow"/"シャドウ"
    /// here carries 3 katakana runs, so none can serve as a witness.</summary>
    [Fact]
    public void Mine_PairWithOnlyHighNoiseWitnesses_IsRejected()
    {
        var result = SentenceAlignmentMiner.Mine(Corpus);

        Assert.False(result.ContainsKey("shadow"));
    }

    /// <summary>The Plausible() gate (initial-sound agreement + length ratio
    /// + phonetic similarity to Transliterator's guess, combined) rejects a
    /// mismatched pair even when it otherwise clears co-occurrence and Dice
    /// — "magic" paired with "ラプトル" (phonetically unrelated) must never
    /// be trusted purely on co-occurrence statistics. Verified as the
    /// combined gate (not one specific sub-check in isolation): "magic" is
    /// implausible on BOTH initial sound (M vs ラ) and phonetic similarity
    /// to Transliterator's own guess, so either sub-check alone already
    /// rejects it — see this class's own verification notes in
    /// DESIGN_NOTES.md.</summary>
    [Fact]
    public void Mine_PhoneticallyImplausiblePair_IsRejected()
    {
        var result = SentenceAlignmentMiner.Mine(Corpus);

        Assert.False(result.ContainsKey("magic"));
    }
}
