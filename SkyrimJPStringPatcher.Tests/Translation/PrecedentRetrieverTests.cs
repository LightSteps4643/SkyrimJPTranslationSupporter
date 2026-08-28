using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// PrecedentRetriever ranks corpus entries as "参考例" (reference examples)
/// shown to the AI-chat/local-LLM translation step — word-overlap scoring
/// with three tiered bonuses (same plugin > same DSD type > same 4-char
/// record signature), a NameFieldFilter pre-filter to keep sentence-like
/// corpus noise out of the index, and a shorter-text tie-break. Wrong
/// ranking here doesn't corrupt data, but it silently degrades what
/// precedent the translator sees — previously untested.
///
/// One shared fixture (Fixtures/Translation/PrecedentRetriever/corpus.tsv)
/// covers every scenario. Each scenario uses its own exclusive set of nouns
/// (frostbound/giant/wolf, relic/ancient/boots/journal, gilded/elven/...,
/// tempered/iron/axe/lockpick, goblin) with NO shared word across scenarios,
/// so one scenario's DsdType/plugin never leaks a bonus into another
/// scenario's ranking via accidental word overlap.
/// </summary>
public class PrecedentRetrieverTests
{
    private static readonly IReadOnlyList<CorpusEntry> Corpus = CorpusIo.ReadTsv(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "PrecedentRetriever", "corpus.tsv"));

    [Fact]
    public void FindPrecedents_MoreOverlappingWords_RanksAboveFewerOverlappingWords()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // "Frostbound Giant" shares 2 words (frostbound, giant) with the candidate;
        // "Frostbound Wolf" shares only 1 (frostbound).
        var results = retriever.FindPrecedents("Frostbound Giant Blade", topN: 2);

        Assert.Equal(new[] { "Frostbound Giant", "Frostbound Wolf" }, results.Select(r => r.English));
    }

    [Fact]
    public void FindPrecedents_SameDsdType_RanksAboveDifferentTypeWithEqualWordOverlap()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // Both "Relic Ancient Boots" (ARMO FULL) and "Relic Ancient Journal" (BOOK FULL)
        // share exactly 2 words (relic, ancient) with the candidate — only the DSD
        // type match should separate them.
        var results = retriever.FindPrecedents("Relic Ancient Helm", topN: 2, candidateType: "ARMO FULL");

        Assert.Equal(new[] { "Relic Ancient Boots", "Relic Ancient Journal" }, results.Select(r => r.English));
    }

    [Fact]
    public void FindPrecedents_SameRecordSignature_RanksAboveNoSignatureMatch_ButBelowSameType()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // All three share 2 words (gilded, elven) with the candidate:
        // "Gilded Elven Sword" (WEAP FULL, same type), "Gilded Elven Description"
        // (WEAP DESC, same 4-char signature "WEAP" only), "Gilded Elven Ring"
        // (ARMO FULL, no match at all).
        var results = retriever.FindPrecedents("Gilded Elven Bow", topN: 3, candidateType: "WEAP FULL");

        Assert.Equal(
            new[] { "Gilded Elven Sword", "Gilded Elven Description", "Gilded Elven Ring" },
            results.Select(r => r.English));
    }

    /// <summary>v0.6.0: the same-plugin bonus (3) is deliberately ranked ABOVE
    /// the same-type bonus (2) — a precedent from the very mod being
    /// translated is the strongest consistency signal there is.</summary>
    [Fact]
    public void FindPrecedents_SamePluginBonus_OutranksSameTypeBonus()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // "Tempered Iron Axe" (MISC FULL, same type as candidate, different plugin) vs.
        // "Tempered Iron Lockpick" (WEAP FULL, different type, but SAME plugin as candidate).
        var results = retriever.FindPrecedents("Tempered Iron Pick", topN: 2, candidateType: "MISC FULL", candidatePlugin: "CandidateMod.esp");

        Assert.Equal(new[] { "Tempered Iron Lockpick", "Tempered Iron Axe" }, results.Select(r => r.English));
    }

    /// <summary>Same filter CorpusTransliterator's mining input uses (see
    /// NameFieldFilter's own remarks): a sentence-like corpus entry must never
    /// surface as a misleading "参考例", even when it shares vocabulary with
    /// the candidate.</summary>
    [Fact]
    public void FindPrecedents_SentenceLikeCorpusEntry_NeverSurfacesAsAPrecedent()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Goblin Something Unique", topN: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void FindPrecedents_CandidateOfOnlyStopWordsAndShortWords_ReturnsEmpty()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // "the"/"and"/"for" are stop words; "of" is 2 characters (below the
        // length-3 floor) — nothing survives tokenization.
        var results = retriever.FindPrecedents("The And For Of", topN: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void FindPrecedents_NoOverlappingVocabularyAtAll_ReturnsEmpty()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Xyzzyx Quuxfoo Wibblesnort", topN: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void FindPrecedents_TopNTruncatesToTheRequestedCount()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Frostbound Giant Blade", topN: 1);

        var single = Assert.Single(results);
        Assert.Equal("Frostbound Giant", single.English);
    }

    /// <summary>Passing "" for type/plugin (PromptGenerator's own precedent
    /// call for word-level hints does this) must score purely on word
    /// overlap, applying neither bonus.</summary>
    [Fact]
    public void FindPrecedents_EmptyTypeAndPlugin_AppliesNoBonuses()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Relic Ancient Helm", topN: 2, candidateType: "", candidatePlugin: "");

        // Both still score 2 (word overlap only) with no bonus to break the tie by score —
        // the class's own tie-break (shorter English first) decides: "Relic Ancient Boots"
        // (19 chars) is shorter than "Relic Ancient Journal" (21 chars).
        Assert.Equal(new[] { "Relic Ancient Boots", "Relic Ancient Journal" }, results.Select(r => r.English));
    }
}
