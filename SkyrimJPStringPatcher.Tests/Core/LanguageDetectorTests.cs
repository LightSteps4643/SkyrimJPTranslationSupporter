using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// LanguageDetector.ContainsJapanese/IsTranslatableEnglish — CJK code-range
/// detection used throughout the pipeline to decide whether a winning text is
/// "already Japanese" (PickUpTargetRunner's exclusion check) or "worth
/// translating" (AutoTranslator/PromptGenerator's gating). Simple logic, but
/// it gates almost every candidate decision in the tool, so its boundary
/// values (the actual edges of the hiragana/katakana/CJK ranges, not just an
/// arbitrary example character from each block) are worth pinning down.
/// </summary>
public class LanguageDetectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ContainsJapanese_NullOrEmpty_ReturnsFalse(string? text)
    {
        Assert.False(LanguageDetector.ContainsJapanese(text));
    }

    [Theory]
    [InlineData("Steel Sword")]
    [InlineData("12345")]
    [InlineData("!@#$%")]
    [InlineData("   ")]
    public void ContainsJapanese_PlainAsciiOnly_ReturnsFalse(string text)
    {
        Assert.False(LanguageDetector.ContainsJapanese(text));
    }

    // U+3040/U+309F: hiragana block boundaries (U+3040 itself is unassigned,
    // but still inside the range this method checks against).
    [Theory]
    [InlineData("ぁ")] // ぁ, first assigned hiragana codepoint
    [InlineData("ゟ")] // last hiragana codepoint
    public void ContainsJapanese_HiraganaBoundary_ReturnsTrue(string text)
    {
        Assert.True(LanguageDetector.ContainsJapanese(text));
    }

    [Theory]
    [InlineData("゠")] // katakana block start
    [InlineData("ヿ")] // katakana block end
    public void ContainsJapanese_KatakanaBoundary_ReturnsTrue(string text)
    {
        Assert.True(LanguageDetector.ContainsJapanese(text));
    }

    [Theory]
    [InlineData("一")] // CJK unified ideographs start (一)
    [InlineData("鿿")] // CJK unified ideographs end
    public void ContainsJapanese_CjkIdeographBoundary_ReturnsTrue(string text)
    {
        Assert.True(LanguageDetector.ContainsJapanese(text));
    }

    [Theory]
    [InlineData("㐀")] // CJK extension A start
    [InlineData("䶿")] // CJK extension A end
    public void ContainsJapanese_CjkExtensionABoundary_ReturnsTrue(string text)
    {
        Assert.True(LanguageDetector.ContainsJapanese(text));
    }

    [Theory]
    [InlineData("〿")] // one codepoint below the hiragana range
    [InlineData("㄀")] // one codepoint above the katakana range's neighbor block, still outside all four ranges
    public void ContainsJapanese_JustOutsideEveryRange_ReturnsFalse(string text)
    {
        Assert.False(LanguageDetector.ContainsJapanese(text));
    }

    [Fact]
    public void ContainsJapanese_MixedEnglishAndJapanese_ReturnsTrue()
    {
        Assert.True(LanguageDetector.ContainsJapanese("Steel Sword 鋼の剣"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTranslatableEnglish_NullEmptyOrWhitespace_ReturnsFalse(string? text)
    {
        Assert.False(LanguageDetector.IsTranslatableEnglish(text));
    }

    [Fact]
    public void IsTranslatableEnglish_AlreadyJapanese_ReturnsFalse()
    {
        Assert.False(LanguageDetector.IsTranslatableEnglish("鋼の剣"));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("!@#$%")]
    [InlineData("---")]
    public void IsTranslatableEnglish_NoLetters_ReturnsFalse(string text)
    {
        Assert.False(LanguageDetector.IsTranslatableEnglish(text));
    }

    [Fact]
    public void IsTranslatableEnglish_PlainEnglishText_ReturnsTrue()
    {
        Assert.True(LanguageDetector.IsTranslatableEnglish("Steel Sword"));
    }

    [Fact]
    public void IsTranslatableEnglish_SingleLetterAmongDigits_ReturnsTrue()
    {
        // char.IsLetter is the only gate beyond "not Japanese, not blank" --
        // even one real letter among mostly-punctuation/digits is enough.
        Assert.True(LanguageDetector.IsTranslatableEnglish("Lv.5"));
    }
}
