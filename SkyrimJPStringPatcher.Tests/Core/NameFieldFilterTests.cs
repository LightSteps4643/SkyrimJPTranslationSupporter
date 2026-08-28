using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// NameFieldFilter.LooksLikeNameField is shared by CorpusTransliterator's
/// mining input and PrecedentRetriever's reference-example index — a
/// regression here either lets sentence-like noise pollute both, or (per the
/// class's own remarks) wrongly rejects a genuine name that carries a bare
/// number/ordinal/lowercase connector. There is no Data/ file backing this
/// class (it's pure Core heuristic logic, not curated vocabulary), so these
/// cases are transcribed from the real examples already documented in the
/// class's own XML comments.
/// </summary>
public class NameFieldFilterTests
{
    [Theory]
    [InlineData("Steel Sword", true)] // ordinary Title Case name
    [InlineData("Eye of Magnus", true)] // lowercase connector "of"
    [InlineData("Blade of Woe", true)]
    [InlineData("Urag gro-Shub", true)] // Orc patronymic — "gro-" has no uppercase at position 0, but DOES contain one ("Shub" part isn't separate; "gro-Shub" as one space-separated token has an uppercase S)
    [InlineData("Fawnia Boots 2", true)] // v0.29.7: a bare-digit token is neutral, not sentence prose
    [InlineData("Wayward Knight Helmet - Faceless", true)] // v0.29.7: a bare "-" token is neutral
    [InlineData("Twilight Princess Book - Cursed, 45th Edition", true)] // v0.29.11: an ordinal token is neutral
    [InlineData("for not using casual idles", false)] // sentence-like internal note — every word lowercase, no neutral token
    [InlineData("the quick brown fox", false)]
    public void LooksLikeNameField(string eng, bool expected)
    {
        Assert.Equal(expected, NameFieldFilter.LooksLikeNameField(eng));
    }

    /// <summary>The uppercase check must scan the WHOLE word, not just the first
    /// letter — "gro-Shub" only passes because of the uppercase S mid-word.
    /// Isolates that specific rule from the full-string cases above.</summary>
    [Fact]
    public void LooksLikeNameField_UppercaseAnywhereInWord_NotJustFirstLetter()
    {
        Assert.True(NameFieldFilter.LooksLikeNameField("gro-Shub"));
        Assert.False(NameFieldFilter.LooksLikeNameField("gro-shub")); // no uppercase anywhere -> fails on its own
    }

    [Fact]
    public void LooksLikeNameField_SingleWordAllDigits_Passes()
    {
        Assert.True(NameFieldFilter.LooksLikeNameField("48"));
    }

    [Fact]
    public void LooksLikeNameField_SingleWordOrdinal_Passes()
    {
        Assert.True(NameFieldFilter.LooksLikeNameField("3rd"));
    }

    [Fact]
    public void LooksLikeNameField_LowercaseConnector_IsCaseInsensitive()
    {
        Assert.True(NameFieldFilter.LooksLikeNameField("Eye OF Magnus"));
    }
}
