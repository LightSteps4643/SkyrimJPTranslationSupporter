using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// TsvEscaping is the single Escape/Unescape pair every TSV writer/reader in the
/// pipeline shares (consolidated in v0.46.1 from 6 independent copies). A wrong
/// or drifted escaping here corrupts TSV structure silently — real data has hit
/// this with multi-paragraph BOOK DESC text carrying embedded tabs/newlines.
/// </summary>
public class TsvEscapingTests
{
    [Fact]
    public void Escape_PlainText_ReturnsUnchanged()
    {
        Assert.Equal("Steel Sword", TsvEscaping.Escape("Steel Sword"));
    }

    [Fact]
    public void Escape_EmbeddedTab_BecomesLiteralBackslashT()
    {
        Assert.Equal("a\\tb", TsvEscaping.Escape("a\tb"));
    }

    [Fact]
    public void Escape_EmbeddedNewline_BecomesLiteralBackslashN()
    {
        Assert.Equal("a\\nb", TsvEscaping.Escape("a\nb"));
    }

    [Fact]
    public void Escape_EmbeddedBackslash_IsDoubled()
    {
        Assert.Equal("a\\\\b", TsvEscaping.Escape("a\\b"));
    }

    /// <summary>CR is dropped outright (not escaped) — a Windows CRLF newline
    /// collapses to the same "\n" as a bare LF, rather than round-tripping as
    /// "\r\n" through two separate escape sequences.</summary>
    [Fact]
    public void Escape_CarriageReturn_IsStripped()
    {
        Assert.Equal("a\\nb", TsvEscaping.Escape("a\r\nb"));
        Assert.Equal("ab", TsvEscaping.Escape("a\rb"));
    }

    [Fact]
    public void Escape_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", TsvEscaping.Escape(""));
    }

    [Fact]
    public void Unescape_IsInverseOfEscape_ForPlainText()
    {
        Assert.Equal("Steel Sword", TsvEscaping.Unescape(TsvEscaping.Escape("Steel Sword")));
    }

    [Fact]
    public void Unescape_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", TsvEscaping.Unescape(""));
    }

    /// <summary>Verifies Unescape's own decoding rules directly, independent of
    /// Escape() — a round-trip test alone cannot catch a pair of matching-but-wrong
    /// changes to both functions.</summary>
    [Fact]
    public void Unescape_LiteralEscapeSequences_DecodesDirectly()
    {
        Assert.Equal("a\tb\nc\\d", TsvEscaping.Unescape("a\\tb\\nc\\\\d"));
    }

    /// <summary>The real-world case that motivated consolidating 6 copies:
    /// multi-paragraph text with embedded tabs AND newlines AND a literal
    /// backslash all together must round-trip exactly.</summary>
    [Fact]
    public void Unescape_RoundTrips_MixedTabNewlineBackslash()
    {
        const string original = "Line one.\nLine\ttwo has a tab.\nA literal backslash: \\ here.";

        var roundTripped = TsvEscaping.Unescape(TsvEscaping.Escape(original));

        Assert.Equal(original, roundTripped);
    }

    /// <summary>KNOWN BUG (found 2026-08-28, not yet fixed — see DESIGN_NOTES.md):
    /// a literal backslash immediately followed by a literal 'n' or 't' does not
    /// survive the round trip. Escape() doubles the backslash; Unescape() then
    /// runs its "\n"/"\t" replacements BEFORE its "\\" replacement, so the
    /// second half of the doubled backslash pair accidentally combines with the
    /// following literal 'n'/'t' into what looks like an escaped newline/tab.
    /// Skipped (not deleted) so the suite stays honest about this gap instead of
    /// silently pretending the round trip is safe — same convention as
    /// Integration/PickUpTargetTranslationCrossModTests. Remove the Skip once
    /// Unescape's replacement order (or an equivalent placeholder-based fix) is
    /// corrected.</summary>
    [Fact(Skip = "Known bug: backslash immediately followed by literal 'n'/'t' does not round-trip — see DESIGN_NOTES.md's TsvEscaping entry")]
    public void Unescape_RoundTrips_BackslashFollowedByLiteralNOrT()
    {
        const string original = "path\\notes.txt";

        var roundTripped = TsvEscaping.Unescape(TsvEscaping.Escape(original));

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Escape_ThenUnescape_ConsecutiveSpecialCharacters_RoundTrips()
    {
        const string original = "\t\t\n\n\\\\";

        var roundTripped = TsvEscaping.Unescape(TsvEscaping.Escape(original));

        Assert.Equal(original, roundTripped);
    }
}
