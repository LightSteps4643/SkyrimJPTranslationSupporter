using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// SameModHintBlockBuilder owns the pieces of issue #4's "c" block that sit
/// outside SameModHintFinder's own relevance logic: (1) which already-
/// resolved candidates are trustworthy enough to draw hints from at all —
/// see <see cref="SameModHintBlockBuilder.IsEligibleMethod"/>'s remarks for
/// why the AutoCorpus* family and the *NoJapanese variants are excluded —
/// (2) formatting the result into the actual prompt text (a legend + numeric
/// code per line, only for trust tiers actually present, validated against
/// real large-scale LLM testing), trimmed to a character budget (25% of the
/// batch's own char limit) with no floor, and (3) skipping any hint already
/// shown verbatim in some candidate's own "Reference examples" list.
/// </summary>
public class SameModHintBlockBuilderTests
{
    private static CorpusEntry Entry(string en, string ja, string method = "TranslationLocalLlm", string dsdType = "MISC FULL") =>
        new(en, ja, "SomeMod.esp", method, dsdType);

    [Theory]
    [InlineData("ModifiedByUser", true)]
    [InlineData("AutoCorpusMeaning", true)]
    [InlineData("AutoCorpusMeaningTranslit", true)]
    [InlineData("AutoCorpusTransliterate", true)]
    [InlineData("AutoCrossModPrecedent", true)]
    [InlineData("TranslationCloudLlm", true)]
    [InlineData("TranslationLocalLlm", true)]
    [InlineData("TranslationNameFallback", true)]
    [InlineData("AutoCorpus", false)]
    [InlineData("AutoCorpusDsd", false)]
    [InlineData("AutoCorpusImported", false)]
    [InlineData("AutoCorpusReferenceTaiyaku", false)]
    [InlineData("AutoCorpusOverride", false)]
    [InlineData("TranslationLocalLlmNoJapanese", false)]
    [InlineData("TranslationCloudLlmNoJapanese", false)]
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
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃", "TranslationLocalLlm") },
            new[] { ("Frostwind Dagger", "MISC FULL") },
            batchCharLimit: 6000);

        Assert.Contains("Same-mod translations", block);
        Assert.Contains("defer to \"Reference examples\"", block);
        // legend entry for TranslationLocalLlm (priority 7)
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
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃", "TranslationLocalLlm") },
            new[] { ("Frostwind Dagger", "MISC FULL") },
            batchCharLimit: 6000);

        Assert.Contains("7=translated by a local LLM", block);
        Assert.DoesNotContain("1=manually corrected", block);
        Assert.DoesNotContain("2=meaning-translated", block);
    }

    /// <summary>A tie in content score is broken by trust tier, not string
    /// length — a human's ModifiedByUser edit outranks an LLM's own earlier
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
            Entry("Frostwind Ward", "氷風の盾", "TranslationLocalLlm"),
            Entry("Frostwind Benediction", "氷風の祝福", "ModifiedByUser"),
        };

        var block = SameModHintBlockBuilder.BuildBlock(pool, new[] { ("Frostwind Dagger", "MISC FULL") }, batchCharLimit: 6000);

        var userLineIndex = block.IndexOf("Frostwind Benediction", StringComparison.Ordinal);
        var llmLineIndex = block.IndexOf("Frostwind Ward", StringComparison.Ordinal);
        Assert.True(userLineIndex >= 0 && llmLineIndex >= 0 && userLineIndex < llmLineIndex,
            "the longer ModifiedByUser hint (higher trust tier) should still be listed before the shorter, same-scoring TranslationLocalLlm hint");
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
