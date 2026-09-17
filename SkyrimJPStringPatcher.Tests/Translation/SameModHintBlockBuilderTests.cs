using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// SameModHintBlockBuilder owns the pieces of issue #4's "c" block that sit
/// outside SameModHintFinder's own relevance logic: (1) which already-
/// resolved candidates are trustworthy enough to draw hints from at all —
/// see <see cref="SameModHintBlockBuilder.IsEligibleMethod"/>'s remarks for
/// why the SJPTS_AutoCorpus* family and the *NoJapanese variants are excluded —
/// (2) formatting the result into the actual prompt text (a legend + numeric
/// code per line, only for trust tiers actually present, validated against
/// real large-scale LLM testing), trimmed to a character budget (25% of the
/// batch's own char limit) with no floor, and (3) skipping any hint already
/// shown verbatim in some candidate's own "Reference examples" list.
/// </summary>
public class SameModHintBlockBuilderTests
{
    private static CorpusEntry Entry(string en, string ja, string method = "SJPTS_TranslationLocalLlm", string dsdType = "MISC FULL") =>
        new(en, ja, "SomeMod.esp", method, dsdType);

    [Theory]
    [InlineData("SJPTS_ModifiedByUser", true)]
    [InlineData("SJPTS_AutoCorpusMeaning", true)]
    [InlineData("SJPTS_AutoCorpusMeaningTranslit", true)]
    [InlineData("SJPTS_AutoCorpusTransliterate", true)]
    [InlineData("SJPTS_AutoCrossModPrecedent", true)]
    [InlineData("SJPTS_TranslationCloudLlm", true)]
    [InlineData("SJPTS_TranslationLocalLlm", true)]
    [InlineData("SJPTS_TranslationNameFallback", true)]
    [InlineData("SJPTS_AutoCorpus", false)]
    [InlineData("SJPTS_AutoCorpusDsd", false)]
    [InlineData("SJPTS_AutoCorpusImported", false)]
    [InlineData("SJPTS_AutoCorpusReferenceTaiyaku", false)]
    [InlineData("SJPTS_AutoCorpusOverride", false)]
    [InlineData("SJPTS_TranslationLocalLlmNoJapanese", false)]
    [InlineData("SJPTS_TranslationCloudLlmNoJapanese", false)]
    public void IsEligibleMethod_MatchesTheDesignedTrustTierList(string method, bool expected)
    {
        Assert.Equal(expected, SameModHintBlockBuilder.IsEligibleMethod(method));
    }

    [Fact]
    public void BuildBlock_EmptyPool_ReturnsEmptyString()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry>(),
            new[] { ("Frostwind Dagger", "WEAP FULL") },
            batchCharLimit: 6000);

        Assert.Equal("", block);
    }

    [Fact]
    public void BuildBlock_EmptyCandidateList_ReturnsEmptyString()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃") },
            Array.Empty<(string, string)>(),
            batchCharLimit: 6000);

        Assert.Equal("", block);
    }

    [Fact]
    public void BuildBlock_NoRelevantPoolEntry_ReturnsEmptyString()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃") },
            new[] { ("Xyzzyx Quuxfoo", "MISC FULL") },
            batchCharLimit: 6000);

        Assert.Equal("", block);
    }

    [Fact]
    public void BuildBlock_RelevantHint_IncludesLegendCodeAndTheHintLine()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃", "SJPTS_TranslationLocalLlm") },
            new[] { ("Frostwind Dagger", "MISC FULL") },
            batchCharLimit: 6000);

        Assert.Contains("Same-mod translations", block);
        Assert.Contains("defer to \"Reference examples\"", block);
        // legend entry for SJPTS_TranslationLocalLlm (priority 7)
        Assert.Contains("7=translated by a local LLM", block);
        // hint line references the same code, tab-separated
        Assert.Contains("\"Frostwind Blade\"\t\"氷風の刃\"\t7", block);
    }

    /// <summary>Only the trust tiers actually present get a legend entry —
    /// no dangling/unused codes bloating the block.</summary>
    [Fact]
    public void BuildBlock_LegendOnlyListsTrustTiersActuallyPresent()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃", "SJPTS_TranslationLocalLlm") },
            new[] { ("Frostwind Dagger", "MISC FULL") },
            batchCharLimit: 6000);

        Assert.Contains("7=translated by a local LLM", block);
        Assert.DoesNotContain("1=manually corrected", block);
        Assert.DoesNotContain("2=meaning-translated", block);
    }

    /// <summary>A tie in content score is broken by trust tier, not string
    /// length — a human's SJPTS_ModifiedByUser edit outranks an LLM's own earlier
    /// guess even though it is the LONGER string here (deliberately, so a
    /// pass can only mean priority actually won, not an accidental length
    /// tie-break in the same direction).</summary>
    [Fact]
    public void BuildBlock_TiedScore_HigherTrustTierListedFirstEvenIfLonger()
    {
        // Both share only "frostwind" with the candidate; "ward" and
        // "benediction" are each unique to their own entry (df=1), so both
        // entries get identical idf-based norms and tie exactly on score.
        var pool = new List<CorpusEntry>
        {
            Entry("Frostwind Ward", "氷風の盾", "SJPTS_TranslationLocalLlm"),
            Entry("Frostwind Benediction", "氷風の祝福", "SJPTS_ModifiedByUser"),
        };

        var block = SameModHintBlockBuilder.BuildBlock(pool, new[] { ("Frostwind Dagger", "MISC FULL") }, batchCharLimit: 6000);

        var userLineIndex = block.IndexOf("Frostwind Benediction", StringComparison.Ordinal);
        var llmLineIndex = block.IndexOf("Frostwind Ward", StringComparison.Ordinal);
        Assert.True(userLineIndex >= 0 && llmLineIndex >= 0 && userLineIndex < llmLineIndex,
            "the longer SJPTS_ModifiedByUser hint (higher trust tier) should still be listed before the shorter, same-scoring SJPTS_TranslationLocalLlm hint");
    }

    /// <summary>A hint already shown verbatim in some candidate's own
    /// "Reference examples" (b) is skipped here to avoid a redundant,
    /// budget-wasting duplicate.</summary>
    [Fact]
    public void BuildBlock_HintAlreadyShownInReferenceExamples_Skipped()
    {
        var pool = new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃") };
        var candidates = new[] { ("Frostwind Dagger", "MISC FULL") };

        var withoutDedup = SameModHintBlockBuilder.BuildBlock(pool, candidates, batchCharLimit: 6000);
        var withDedup = SameModHintBlockBuilder.BuildBlock(pool, candidates, batchCharLimit: 6000,
            alreadyShown: new HashSet<string> { "Frostwind Blade" });

        Assert.Contains("Frostwind Blade", withoutDedup);
        Assert.Equal("", withDedup);
    }

    /// <summary>No floor: with a budget too small to fit even the header
    /// (intro + legend) plus one hint line, the whole block is omitted rather
    /// than truncated mid-header or forced in over budget.</summary>
    [Fact]
    public void BuildBlock_BudgetTooSmallForEvenOneHint_ReturnsEmptyString()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃") },
            new[] { ("Frostwind Dagger", "MISC FULL") },
            batchCharLimit: 4); // 25% of 4 = 1 character — cannot fit anything

        Assert.Equal("", block);
    }

    /// <summary>2026-09-18 real-data finding (Light Greatswords.esp): every
    /// "X Light Greatsword" candidate was translated as "Xの光のグレートソード"
    /// (word-for-word "Light"=光) despite a human having filled in
    /// mod_glossary.tsv with "Light Greatsword" -> "軽大剣" — the softer
    /// "keep wording consistent, defer to Reference examples if conflict"
    /// framing wasn't enough to override the model's own dictionary instinct,
    /// especially against a competing "Known translations for words: ...
    /// Light=光..." hint. Verified via a real local-LLM (gemma4:26b,
    /// reasoning off) A/B test that separating SJPTS_ModifiedByUser hints into
    /// their own imperative "IMPORTANT" block fixes this (see management
    /// repo's design docs). This is a SJPTS_ModifiedByUser-only test —
    /// other trust tiers keep the existing softer wording (see
    /// BuildBlock_LegendOnlyListsTrustTiersActuallyPresent, unchanged).</summary>
    [Fact]
    public void BuildBlock_ModifiedByUserHint_UsesStrongerImportantWording()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry> { Entry("Light Greatsword", "軽大剣", "SJPTS_ModifiedByUser") },
            new[] { ("Daedric Light Greatsword", "WEAP FULL") },
            batchCharLimit: 6000);

        Assert.Contains("IMPORTANT", block);
        Assert.Contains("explicitly confirmed by a human", block);
        Assert.Contains("\"Light Greatsword\" -> \"軽大剣\"", block);
        // The stronger wording explicitly overrides "Reference examples" for
        // this tier — the opposite of the softer tier's "defer to Reference
        // examples if conflict" rule.
        Assert.Contains("overriding your own judgment", block);
    }

    /// <summary>A mix of a human-confirmed hint and an ordinary (lower-trust)
    /// same-mod hint must produce BOTH sub-blocks — the confirmed one first,
    /// with its own stronger wording, and the rest in the existing softer
    /// "Same-mod translations so far" block, unchanged in format.</summary>
    [Fact]
    public void BuildBlock_MixOfConfirmedAndOtherHints_ProducesBothSubBlocksInOrder()
    {
        var pool = new List<CorpusEntry>
        {
            Entry("Light Greatsword", "軽大剣", "SJPTS_ModifiedByUser"),
            Entry("Frostwind Ward", "氷風の盾", "SJPTS_TranslationLocalLlm"),
        };
        var candidates = new[] { ("Daedric Light Greatsword", "WEAP FULL"), ("Frostwind Ward Dagger", "MISC FULL") };

        var block = SameModHintBlockBuilder.BuildBlock(pool, candidates, batchCharLimit: 6000);

        Assert.Contains("IMPORTANT", block);
        Assert.Contains("\"Light Greatsword\" -> \"軽大剣\"", block);
        Assert.Contains("Same-mod translations so far", block);
        Assert.Contains("7=translated by a local LLM", block);
        Assert.Contains("\"Frostwind Ward\"\t\"氷風の盾\"\t7", block);

        var importantIndex = block.IndexOf("IMPORTANT", StringComparison.Ordinal);
        var softIndex = block.IndexOf("Same-mod translations so far", StringComparison.Ordinal);
        Assert.True(importantIndex >= 0 && softIndex >= 0 && importantIndex < softIndex,
            "the human-confirmed IMPORTANT block should appear before the softer same-mod hints block");

        // The confirmed-tier hint must not ALSO be duplicated into the softer
        // block's numbered legend/list.
        Assert.DoesNotContain("1=manually corrected", block);
    }

    [Fact]
    public void BuildBlock_ManyHints_StopsOnceBudgetExceeded()
    {
        // All three share only "frostwind" with the candidate and score
        // identically (each entry's other word is unique to it, so equal
        // idf) AND share the same trust tier — the strictly increasing
        // lengths (15/16/19 chars) make the resulting order deterministic
        // via the class's own shorter-first final tie-break.
        var pool = new List<CorpusEntry>
        {
            Entry("Frostwind Blade", "氷風の刃"),
            Entry("Frostwind Shield", "氷風の盾"),
            Entry("Frostwind Talisman", "氷風のタリスマン"),
        };
        var candidates = new[] { ("Frostwind Dagger", "MISC FULL") };

        var fullBudgetBlock = SameModHintBlockBuilder.BuildBlock(pool, candidates, batchCharLimit: 6000);
        // header (intro+legend) is 363 chars; hint lines are 31/32/38 chars.
        // 25% of 1760 = 440 chars: fits header+31+32=426 but not +38=464.
        var tightBudgetBlock = SameModHintBlockBuilder.BuildBlock(pool, candidates, batchCharLimit: 1760);

        // At a generous budget, all three equally-relevant hints fit.
        Assert.Contains("Frostwind Blade", fullBudgetBlock);
        Assert.Contains("Frostwind Shield", fullBudgetBlock);
        Assert.Contains("Frostwind Talisman", fullBudgetBlock);

        // At a tighter budget, only as many as fit are included — never zero
        // when at least one fits — and the block is still well-formed.
        Assert.True(tightBudgetBlock.Length < fullBudgetBlock.Length);
        Assert.Contains("Same-mod translations", tightBudgetBlock);
        Assert.Contains("Frostwind Blade", tightBudgetBlock);
        Assert.Contains("Frostwind Shield", tightBudgetBlock);
        Assert.DoesNotContain("Frostwind Talisman", tightBudgetBlock);
    }
}
