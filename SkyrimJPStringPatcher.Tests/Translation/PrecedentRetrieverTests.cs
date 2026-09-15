using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// PrecedentRetriever ranks corpus entries as "参考例" (reference examples)
/// shown to the AI-chat/local-LLM translation step — TF-IDF weighted cosine
/// similarity (idf(w) = ln(N/(df(w)+1))+1, dot = sum of idf(w)^2 over shared
/// words, normalized by both vectors' norms) with three tiered fixed-point
/// bonuses (same plugin 0.06 &gt; same DSD type 0.04 &gt; same 4-char record
/// signature 0.02), an absolute per-entry length cap (500 chars, to keep an
/// entire book chapter from being offered as a "参考例" just because it
/// coincidentally shares one rare word), and a relative score cutoff (any
/// entry scoring below 40% of the top entry's score is dropped — no fixed
/// count, no floor: a candidate with nothing useful gets zero precedents).
/// No NameFieldFilter pre-filter anymore — cosine similarity already
/// normalizes for document length, so a sentence-like corpus entry can
/// surface when it is genuinely the best match. Wrong ranking here doesn't
/// corrupt data, but it silently degrades what precedent the translator sees.
///
/// One shared fixture (Fixtures/Translation/PrecedentRetriever/corpus.tsv)
/// covers every scenario. Each scenario uses its own exclusive set of nouns
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
        var results = retriever.FindPrecedents("Frostbound Giant Blade");

        Assert.Equal(new[] { "Frostbound Giant", "Frostbound Wolf" }, results.Select(r => r.English));
    }

    [Fact]
    public void FindPrecedents_SameDsdType_RanksAboveDifferentTypeWithEqualWordOverlap()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // Both "Relic Ancient Boots" (ARMO FULL) and "Relic Ancient Journal" (BOOK FULL)
        // share exactly the same words (relic, ancient) with the candidate — only the DSD
        // type match bonus should separate them.
        var results = retriever.FindPrecedents("Relic Ancient Helm", candidateType: "ARMO FULL");

        Assert.Equal(new[] { "Relic Ancient Boots", "Relic Ancient Journal" }, results.Select(r => r.English));
    }

    [Fact]
    public void FindPrecedents_SameRecordSignature_RanksAboveNoSignatureMatch_ButBelowSameType()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // All three share the same words (gilded, elven) with the candidate:
        // "Gilded Elven Sword" (WEAP FULL, same type), "Gilded Elven Description"
        // (WEAP DESC, same 4-char signature "WEAP" only), "Gilded Elven Ring"
        // (ARMO FULL, no type/signature match at all).
        var results = retriever.FindPrecedents("Gilded Elven Bow", candidateType: "WEAP FULL");

        Assert.Equal(
            new[] { "Gilded Elven Sword", "Gilded Elven Description", "Gilded Elven Ring" },
            results.Select(r => r.English));
    }

    /// <summary>The same-plugin bonus (0.06) is deliberately ranked ABOVE
    /// the same-type bonus (0.04) — a precedent from the very mod being
    /// translated is the strongest consistency signal there is.</summary>
    [Fact]
    public void FindPrecedents_SamePluginBonus_OutranksSameTypeBonus()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // "Tempered Iron Axe" (MISC FULL, same type as candidate, different plugin) vs.
        // "Tempered Iron Lockpick" (WEAP FULL, different type, but SAME plugin as candidate).
        var results = retriever.FindPrecedents("Tempered Iron Pick", candidateType: "MISC FULL", candidatePlugin: "CandidateMod.esp");

        Assert.Equal(new[] { "Tempered Iron Lockpick", "Tempered Iron Axe" }, results.Select(r => r.English));
    }

    /// <summary>2026-09-16: NameFieldFilter was removed from the index — cosine
    /// similarity already normalizes for document length, so a sentence-like
    /// corpus entry ("goblin internal note text for testing") is no longer
    /// hard-excluded and can surface like any other entry when it shares
    /// vocabulary with the candidate.</summary>
    [Fact]
    public void FindPrecedents_SentenceLikeCorpusEntry_CanSurfaceWhenItSharesVocabulary()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Goblin Something Unique");

        Assert.Equal(new[] { "goblin internal note text for testing" }, results.Select(r => r.English));
    }

    /// <summary>A genuinely relevant sentence-like entry (an INFO NAM1 line) can
    /// even be the SOLE precedent offered, outscoring a shorter, more superficially
    /// "name-like" entry that only shares one of its four words.</summary>
    [Fact]
    public void FindPrecedents_RelevantSentenceEntry_OutscoresWeakerShortEntry()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Quicksilver amulets near the market");

        Assert.Equal(new[] { "The merchant sells quicksilver amulets near the market square." }, results.Select(r => r.English));
    }

    /// <summary>An entry over the absolute 500-character cap is excluded from
    /// consideration entirely, even when it is the only entry that shares
    /// substantial vocabulary with the candidate — this is what keeps an entire
    /// book chapter from being offered as a "参考例" merely because it happens
    /// to contain a few of the candidate's words.</summary>
    [Fact]
    public void FindPrecedents_EntryOverLengthCap_ExcludedEvenWhenOtherwiseTheOnlyMatch()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("A wandering pendant maker's tale");

        Assert.Equal(new[] { "Moonstone Pendant" }, results.Select(r => r.English));
    }

    /// <summary>A candidate can score highly against one entry and only weakly
    /// (below 40% of the top score) against another — the weak one must not
    /// surface at all; there is no floor forcing a minimum count.</summary>
    [Fact]
    public void FindPrecedents_ScoreBelowRelativeCutoff_Excluded()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Ember Chalice Shrine");

        Assert.Equal(new[] { "Ember Chalice" }, results.Select(r => r.English));
    }

    /// <summary>"well" is a stop word (2026-09-16: added because it is a genuine
    /// homograph — the interjection "Well, ..." vs. the noun "well" as in
    /// "Arcane Well" — that TF-IDF alone does not disambiguate), so a
    /// conversational "Well, ..." must not spuriously match an item named
    /// "Arcane Well".</summary>
    [Fact]
    public void FindPrecedents_WellIsAStopWord_NoSpuriousHomographMatch()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Well, that's unfortunate.");

        Assert.Empty(results);
    }

    /// <summary>"you"/"your" are deliberately NOT stop words (2026-09-16: removed
    /// from the old list) — in Japanese they render differently depending on the
    /// speaker's tone (あなた/お前/君), so seeing how they were rendered elsewhere
    /// is a real consistency signal, not noise.</summary>
    [Fact]
    public void FindPrecedents_YouAndYourAreNotStopWords_ParticipateInScoring()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // "Your Loyal Guardian" shares "your" AND "loyal" with the candidate;
        // "The Loyal Guardian" shares only "loyal". If "your" were stopped, both
        // would tie on word overlap alone.
        var results = retriever.FindPrecedents("Your Loyal Servant");

        Assert.Equal(new[] { "Your Loyal Guardian", "The Loyal Guardian" }, results.Select(r => r.English));
    }

    [Fact]
    public void FindPrecedents_CandidateOfOnlyStopWordsAndShortWords_ReturnsEmpty()
    {
        var retriever = new PrecedentRetriever(Corpus);

        // "the"/"and"/"for" are stop words; "of" is 2 characters (below the
        // length-3 floor) — nothing survives tokenization.
        var results = retriever.FindPrecedents("The And For Of");

        Assert.Empty(results);
    }

    [Fact]
    public void FindPrecedents_NoOverlappingVocabularyAtAll_ReturnsEmpty()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Xyzzyx Quuxfoo Wibblesnort");

        Assert.Empty(results);
    }

    /// <summary>Passing "" for type/plugin (PromptGenerator's own precedent
    /// call for word-level hints does this) must score purely on word
    /// overlap, applying neither bonus.</summary>
    [Fact]
    public void FindPrecedents_EmptyTypeAndPlugin_AppliesNoBonuses()
    {
        var retriever = new PrecedentRetriever(Corpus);

        var results = retriever.FindPrecedents("Relic Ancient Helm", candidateType: "", candidatePlugin: "");

        // Both still score equally (word overlap only) with no bonus to break the tie by
        // score — the class's own tie-break (shorter English first) decides:
        // "Relic Ancient Boots" (19 chars) is shorter than "Relic Ancient Journal" (21 chars).
        Assert.Equal(new[] { "Relic Ancient Boots", "Relic Ancient Journal" }, results.Select(r => r.English));
    }
}
