using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// SourceTier decides which of several competing corpus provenances is
/// trusted when the SAME English string resolves through more than one
/// source — consumed directly by AutoTranslator's ①完全一致 dedup
/// (AutoTranslator.cs:285-291), plus CorpusMeaningTranslator/
/// CorpusTransliterator/SentenceAlignmentMiner. v0.38.0 was introduced
/// after a real mistranslation ("Hevnoraak" -> "ヘブラノーク" in Cloak of
/// Hevnoraak) traced back to "dsd" being silently treated as equal to
/// "vanilla" — this class's whole reason to exist is preventing exactly
/// that class of regression, and it had no test of its own.
/// </summary>
public class SourceTierTests
{
    [Theory]
    [InlineData("override", -1)]
    [InlineData("vanilla", 0)]
    [InlineData("reference", 1)]
    [InlineData("dsd", 2)]
    [InlineData("imported", 2)]
    [InlineData("", 2)] // unrecognized/blank -- e.g. a "derived" transliteration slice with no attesting entry
    [InlineData("something-unrecognized", 2)]
    public void Of_ReturnsTheDocumentedTierForEachSourceKind(string sourceKind, int expectedTier)
    {
        Assert.Equal(expectedTier, SourceTier.Of(sourceKind));
    }

    /// <summary>The exact ranking the real "Hevnoraak" incident hinged on:
    /// "vanilla" (Bethesda's own shipped localization) must outrank "dsd"
    /// (a community translation patch) — treating them as equal was the bug.</summary>
    [Fact]
    public void Of_Vanilla_OutranksDsd()
    {
        Assert.True(SourceTier.Of("vanilla") < SourceTier.Of("dsd"));
    }

    [Fact]
    public void Of_Override_OutranksEvenVanilla()
    {
        Assert.True(SourceTier.Of("override") < SourceTier.Of("vanilla"));
    }

    [Fact]
    public void Of_Reference_RanksBelowVanillaButAboveDsd()
    {
        Assert.True(SourceTier.Of("vanilla") < SourceTier.Of("reference"));
        Assert.True(SourceTier.Of("reference") < SourceTier.Of("dsd"));
    }

    [Fact]
    public void Of_DsdAndImported_AreTied()
    {
        Assert.Equal(SourceTier.Of("dsd"), SourceTier.Of("imported"));
    }

    [Fact]
    public void OfProvenance_PicksTheBestLowestTierAmongMultipleEntries()
    {
        var provenance = new[]
        {
            ("dsd", "SomeCommunityPatch.esp"),
            ("vanilla", "Skyrim.esm"), // the best (lowest) tier present
            ("imported", "SomeXTranslatorFile.esp"),
        };

        Assert.Equal(SourceTier.Of("vanilla"), SourceTier.OfProvenance(provenance));
    }

    [Fact]
    public void OfProvenance_SingleEntry_ReturnsThatEntrysTier()
    {
        var provenance = new[] { ("reference", "skyrim_taiyaku_reference.tsv") };

        Assert.Equal(SourceTier.Of("reference"), SourceTier.OfProvenance(provenance));
    }

    /// <summary>No corroborating entry at all (e.g. a mined word nothing
    /// directly witnesses) falls back to the same "never trusted above
    /// community data" tier as an unrecognized SourceKind.</summary>
    [Fact]
    public void OfProvenance_EmptyCollection_FallsBackToTheUnrecognizedTier()
    {
        Assert.Equal(SourceTier.Of(""), SourceTier.OfProvenance(Array.Empty<(string, string)>()));
    }
}
