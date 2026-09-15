using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// SameModHintBlockBuilder owns the two pieces of issue #4's "c" block that
/// sit outside SameModHintFinder's own relevance logic: (1) which
/// already-resolved candidates are trustworthy enough to draw hints from —
/// see <see cref="SameModHintBlockBuilder.IsEligibleMethod"/>'s remarks for
/// why the AutoCorpus* family and the *NoJapanese variants are excluded —
/// and (2) formatting the result into the actual prompt text, including the
/// explanation shown only when the block is non-empty, and trimming to a
/// character budget (25% of the batch's own char limit) with no floor — an
/// empty result is a valid outcome, exactly like "Reference examples" having
/// none.
/// </summary>
public class SameModHintBlockBuilderTests
{
    private static CorpusEntry Entry(string en, string ja, string dsdType = "MISC FULL") =>
        new(en, ja, "SomeMod.esp", "TranslationLocalLlm", dsdType);

    [Theory]
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
    public void BuildBlock_RelevantHint_IncludesExplanationAndTheHintLine()
    {
        var block = SameModHintBlockBuilder.BuildBlock(
            new List<CorpusEntry> { Entry("Frostwind Blade", "氷風の刃") },
            new[] { ("Frostwind Dagger", "MISC FULL") },
            batchCharLimit: 6000);

        Assert.Contains("Same-mod translations", block);
        Assert.Contains("lower-confidence than", block);
        Assert.Contains("\"Frostwind Blade\"", block);
        Assert.Contains("\"氷風の刃\"", block);
    }

    /// <summary>No floor: with a budget too small to fit even the explanation
    /// plus one hint line, the whole block is omitted rather than truncated
    /// mid-explanation or forced in over budget.</summary>
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
        // idf) — the strictly increasing lengths (15/16/19 chars) make the
        // resulting order deterministic via the class's own shorter-first
        // tie-break, rather than relying on incidental dictionary order.
        var pool = new List<CorpusEntry>
        {
            Entry("Frostwind Blade", "氷風の刃"),
            Entry("Frostwind Shield", "氷風の盾"),
            Entry("Frostwind Talisman", "氷風のタリスマン"),
        };
        var candidates = new[] { ("Frostwind Dagger", "MISC FULL") };

        var fullBudgetBlock = SameModHintBlockBuilder.BuildBlock(pool, candidates, batchCharLimit: 6000);
        // 25% of 1700 = 425 chars: fits the header (357) plus the two
        // shortest hint lines but not the third-longest.
        var tightBudgetBlock = SameModHintBlockBuilder.BuildBlock(pool, candidates, batchCharLimit: 1700);

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
