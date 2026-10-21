using System.Text;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Strings;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// Requirement: the rule that turns the bytes of a plugin's text into a string is the one Skyrim and the Creation
/// Kit apply, string by string — read the bytes as UTF-8; when they are not valid UTF-8, read them as
/// Windows-1252. Nothing is ever replaced by U+FFFD, and no other code page is guessed (a Shift-JIS guess reads
/// English text that carries a Windows-1252 "’" followed by a letter as a kanji).
/// Expected values come from the definitions of the two encodings: UTF-8 (U+2019 = E2 80 99, U+00E9 = C3 A9,
/// U+92FC = E9 8B BC) and Windows-1252 (0x92 = U+2019, 0xE9 = U+00E9, 0x93/0x94 =
/// U+201C/U+201D, 0x96 = U+2013, 0x97 = U+2014, 0x85 = U+2026, 0x99 = U+2122, 0x80 = U+20AC).
/// </summary>
public class Utf8ThenWindows1252EncodingTests
{
    private static readonly IMutagenEncoding Encoder = new Utf8ThenWindows1252Encoding();

    private static string Read(params byte[] bytes) => Encoder.GetString(bytes);

    [Fact]
    public void AsciiOnly_IsReadAsIs()
    {
        Assert.Equal("Steel Sword", Read(Encoding.ASCII.GetBytes("Steel Sword")));
        Assert.Equal("", Read());
    }

    [Theory]
    [InlineData(new byte[] { 0x4B, 0xE2, 0x80, 0x99, 0x73 }, "K\u2019s")]                          // ’ as UTF-8
    [InlineData(new byte[] { 0xC3, 0xA9 }, "\u00E9")]                                            // é as UTF-8
    [InlineData(new byte[] { 0xE9, 0x8B, 0xBC }, "\u92FC")]                                      // 鋼 as UTF-8
    [InlineData(new byte[] { 0xF0, 0x9F, 0x97, 0xA1 }, "\U0001F5E1")]                            // a 4-byte sequence
    public void ValidUtf8_IsReadAsUtf8(byte[] bytes, string expected)
    {
        Assert.Equal(expected, Read(bytes));
    }

    [Fact]
    public void JapaneseWrittenAsUtf8_IsReadIntact()
    {
        Assert.Equal("鋼の剣", Read(Encoding.UTF8.GetBytes("鋼の剣")));
    }

    [Theory]
    [InlineData(0x92, "\u2019")]
    [InlineData(0x91, "\u2018")]
    [InlineData(0xE9, "\u00E9")]
    [InlineData(0x93, "\u201C")]
    [InlineData(0x94, "\u201D")]
    [InlineData(0x96, "\u2013")]
    [InlineData(0x97, "\u2014")]
    [InlineData(0x85, "\u2026")]
    [InlineData(0x99, "\u2122")]
    [InlineData(0x80, "\u20AC")]
    public void BytesThatAreNotValidUtf8_AreReadAsWindows1252(int b, string expected)
    {
        Assert.Equal(expected, Read((byte)b));
    }

    [Fact]
    public void AWindows1252StringAmongAscii_IsReadAsWindows1252()
    {
        Assert.Equal("King\u2019s Blade", Read(0x4B, 0x69, 0x6E, 0x67, 0x92, 0x73, 0x20, 0x42, 0x6C, 0x61, 0x64, 0x65));
        Assert.Equal("Caf\u00E9 Sword", Read(0x43, 0x61, 0x66, 0xE9, 0x20, 0x53, 0x77, 0x6F, 0x72, 0x64));
    }

    [Theory]
    [InlineData(new byte[] { 0xE2, 0x80 }, "\u00E2\u20AC")]            // a UTF-8 sequence cut short
    [InlineData(new byte[] { 0xC0, 0x80 }, "\u00C0\u20AC")]            // an overlong form, which UTF-8 forbids
    [InlineData(new byte[] { 0x80 }, "\u20AC")]                        // a lone continuation byte
    [InlineData(new byte[] { 0x41, 0xE2, 0x80, 0x99, 0x92 }, "A\u00E2\u20AC\u2122\u2019")]   // valid UTF-8 followed by a stray byte: the whole string is Windows-1252
    public void InvalidUtf8_FallsBackForTheWholeString(byte[] bytes, string expected)
    {
        Assert.Equal(expected, Read(bytes));
    }

    [Fact]
    public void EachStringIsDecidedOnItsOwn()
    {
        Assert.Equal("K\u2019s", Read(0x4B, 0xE2, 0x80, 0x99, 0x73));      // UTF-8
        Assert.Equal("K\u2019s", Read(0x4B, 0x92, 0x73));                  // Windows-1252, in the same plugin
    }

    [Fact]
    public void ShiftJisBytes_AreNotGuessed_TheyAreReadAsWindows1252()
    {
        // Shift-JIS "鋼の剣". A guess would make English text with a Windows-1252 "’" before a letter a kanji
        // (0x92 0x73 is a valid Shift-JIS pair), so the rule has no Shift-JIS lane.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932).GetBytes("鋼の剣");
        Assert.Equal(Encoding.GetEncoding(1252).GetString(shiftJis), Read(shiftJis));
    }

    [Fact]
    public void WhenTheBytesAreAlsoValidUtf8_TheyAreReadAsUtf8_TheAcceptedAmbiguity()
    {
        // Windows-1252 "Ã©" (C3 A9) is, byte for byte, UTF-8 "é". Skyrim and the Creation Kit read it as UTF-8 too.
        Assert.Equal("\u00E9", Read(0xC3, 0xA9));
    }

    [Fact]
    public void NoByteSequenceYieldsTheReplacementCharacter()
    {
        for (var a = 0; a < 256; a++)
        {
            Assert.DoesNotContain('\uFFFD', Read((byte)a));
            for (var b = 0; b < 256; b++)
                Assert.DoesNotContain('\uFFFD', Read((byte)a, (byte)b));
        }
    }

    [Fact]
    public void ItNeverWrites_TheToolDoesNotWritePlugins()
    {
        Assert.Throws<NotSupportedException>(() => Encoder.GetByteCount("x".AsSpan()));
        Assert.Throws<NotSupportedException>(() => Encoder.GetBytes("x".AsSpan(), new byte[4]));
    }

    [Fact]
    public void ThePluginReadParameters_UseItForBothKindsOfPluginText_AndKeepTheJapaneseStringsTables()
    {
        var parameters = PluginReadParameters.Create(stringsFolder: "C:\\strings");

        var strings = parameters.StringsParam!;
        Assert.Equal(Language.Japanese, strings.TargetLanguage);
        Assert.Equal("C:\\strings", strings.StringsFolderOverride);
        Assert.IsType<Utf8ThenWindows1252Encoding>(strings.NonLocalizedEncodingOverride);
        Assert.IsType<Utf8ThenWindows1252Encoding>(strings.NonTranslatedEncodingOverride);
    }
}
