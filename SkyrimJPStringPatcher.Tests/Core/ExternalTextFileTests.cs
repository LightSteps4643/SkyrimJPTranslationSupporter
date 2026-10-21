using System.Text;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// Requirement: a text file written by someone else's tool (a SkyPatcher .ini, MO2's profile and settings files)
/// is read by the rule used for a plugin's text — each line as UTF-8, else as Windows-1252 — instead of as UTF-8
/// alone, which turns a Windows-1252 "é" into U+FFFD and loses the name for good.
/// Lines are split as File.ReadAllLines splits them (CRLF, LF or a lone CR; a final line break adds no empty
/// line), and a UTF-8 byte order mark is not part of the first line.
/// Expectations come from the UTF-8 and Windows-1252 definitions (U+00E9 = C3 A9 / 0xE9, U+2019 = E2 80 99 / 0x92),
/// not from the code under test.
/// </summary>
public class ExternalTextFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sjpts_tests_exttext_{Guid.NewGuid():N}");

    public ExternalTextFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string[] Read(params byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllBytes(path, bytes);
        return ExternalTextFile.ReadAllLines(path);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void LinesAreSplitOnCrLfLfAndLoneCr_AndAFinalLineBreakAddsNoEmptyLine()
    {
        Assert.Equal(["a", "b", "c", "d"], Read(Ascii("a\r\nb\nc\rd\r\n")));
        Assert.Equal(["a", "", "b"], Read(Ascii("a\n\nb")));
        Assert.Empty(Read());
    }

    [Fact]
    public void AUtf8ByteOrderMark_IsNotPartOfTheFirstLine()
    {
        var lines = Read([0xEF, 0xBB, 0xBF, .. Ascii("key=value\r\nsecond")]);

        Assert.Equal(2, lines.Length);
        Assert.Equal("key=value".Length, lines[0].Length);   // a leftover U+FEFF would make it one char longer
        Assert.Equal("key=value", lines[0]);
        Assert.Equal("second", lines[1]);
    }

    [Fact]
    public void Utf8Lines_AreReadAsUtf8()
    {
        Assert.Equal(["fullName=~鋼の剣~", "x=é"], Read([.. Encoding.UTF8.GetBytes("fullName=~鋼の剣~\r\n"), .. Ascii("x="), 0xC3, 0xA9]));
    }

    [Fact]
    public void Windows1252Lines_AreReadAsWindows1252()
    {
        Assert.Equal(["fullName=~Café~", "k=King’s"], Read([.. Ascii("fullName=~Caf"), 0xE9, .. Ascii("~\r\nk=King"), 0x92, .. Ascii("s")]));
    }

    [Fact]
    public void EachLineIsDecidedOnItsOwn()
    {
        var lines = Read([.. Ascii("a="), 0xE2, 0x80, 0x99, .. Ascii("\r\nb="), 0x92]);

        Assert.Equal(["a=’", "b=’"], lines);
    }

    [Fact]
    public void NoLineYieldsTheReplacementCharacter()
    {
        for (var b = 0; b < 256; b++)
            Assert.All(Read([.. Ascii("k="), (byte)b, .. Ascii("v")]), line => Assert.DoesNotContain('�', line));
    }
}
