using System.Globalization;

namespace SkyrimJPStringPatcher.Core;

public static class LanguageDetector
{
    /// <summary>
    /// True if the text contains at least one CJK character (hiragana, katakana,
    /// or a kanji/CJK ideograph). Good enough to call a string "already Japanese".
    /// </summary>
    public static bool ContainsJapanese(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            var isHiragana = cp is >= 0x3040 and <= 0x309F;
            var isKatakana = cp is >= 0x30A0 and <= 0x30FF;
            var isCjkIdeograph = cp is >= 0x4E00 and <= 0x9FFF;
            var isCjkExtA = cp is >= 0x3400 and <= 0x4DBF;
            if (isHiragana || isKatakana || isCjkIdeograph || isCjkExtA) return true;
        }
        return false;
    }

    /// <summary>
    /// True if the text is worth treating as "needs a Japanese replacement" —
    /// i.e. it has no Japanese in it and isn't just numbers/punctuation/empty.
    /// </summary>
    public static bool IsTranslatableEnglish(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (ContainsJapanese(text)) return false;
        return text.Any(char.IsLetter);
    }
}
