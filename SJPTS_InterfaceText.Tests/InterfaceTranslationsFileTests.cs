using SJPTS_InterfaceText;

namespace SJPTS_InterfaceText.Tests;

public class InterfaceTranslationsFileTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"sjpts_uitext_test_{Guid.NewGuid():N}.txt");

    [Fact]
    public void WriteThenParse_RoundTripsKeysAndValues()
    {
        var path = TempFile();
        try
        {
            var entries = new List<(string Key, string Value)>
            {
                ("$Foo", "Hello World"),
                ("$Bar_OptionText", "Enable <font color='#dd3333'>Feature</font>"),
            };
            InterfaceTranslationsFile.Write(path, entries);

            var parsed = InterfaceTranslationsFile.Parse(path);

            Assert.Equal(2, parsed.Count);
            Assert.Equal(("$Foo", "Hello World"), parsed[0]);
            Assert.Equal(("$Bar_OptionText", "Enable <font color='#dd3333'>Feature</font>"), parsed[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_ProducesUtf16LeWithBom()
    {
        var path = TempFile();
        try
        {
            InterfaceTranslationsFile.Write(path, new List<(string, string)> { ("$K", "日本語テスト") });
            var bytes = File.ReadAllBytes(path);

            Assert.True(bytes.Length >= 2);
            Assert.Equal(0xFF, bytes[0]); // UTF-16LE BOM: FF FE
            Assert.Equal(0xFE, bytes[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parse_SkipsBlankLinesAndMalformedLinesWithoutTab()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "$A\tValueA\n\n$NoTabHere\n$B\tValueB\n", new System.Text.UnicodeEncoding(false, true));

            var parsed = InterfaceTranslationsFile.Parse(path);

            Assert.Equal(2, parsed.Count);
            Assert.Equal(("$A", "ValueA"), parsed[0]);
            Assert.Equal(("$B", "ValueB"), parsed[1]);
        }
        finally { File.Delete(path); }
    }

    /// <summary>2026-09-12 real-data finding: a mod shipping a plain-ASCII,
    /// BOM-less "aaa_english.txt" was silently misread as UTF-16LE (the old
    /// hardcoded fallback), producing 0 entries with no error. File.
    /// ReadAllText's own BOM sniffing only kicks in when a BOM IS present, so
    /// the passed-in fallback encoding matters for a genuinely BOM-less
    /// source — UTF-8 is now that fallback (ASCII is a strict subset, so this
    /// covers both).</summary>
    [Fact]
    public void Parse_BomLessAsciiFile_ParsesCorrectly()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                "$qlie_Use\t<font face='$Iconographia'>W</font>\r\n$qlie_Drink\t<font face='$Iconographia'>J</font>\r\n",
                new System.Text.UTF8Encoding(false));

            var parsed = InterfaceTranslationsFile.Parse(path);

            Assert.Equal(2, parsed.Count);
            Assert.Equal("$qlie_Use", parsed[0].Key);
            Assert.Equal("<font face='$Iconographia'>W</font>", parsed[0].Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parse_DuplicateKey_KeepsLastValueButOriginalPosition()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "$A\tFirst\n$B\tOnly\n$A\tSecond\n", new System.Text.UnicodeEncoding(false, true));

            var parsed = InterfaceTranslationsFile.Parse(path);

            Assert.Equal(2, parsed.Count); // still just 2 distinct keys, in first-seen order
            Assert.Equal("$A", parsed[0].Key);
            Assert.Equal("Second", parsed[0].Value); // last occurrence wins
            Assert.Equal("$B", parsed[1].Key);
        }
        finally { File.Delete(path); }
    }
}
