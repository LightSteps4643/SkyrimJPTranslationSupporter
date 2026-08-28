using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// TsvEscaping is the single Escape/Unescape pair every TSV writer/reader in the
/// CLI/Core pipeline shares (consolidated in v0.46.1 from 6 independent copies).
/// A wrong or drifted escaping here corrupts TSV structure silently — real data
/// has hit this with multi-paragraph BOOK DESC text carrying embedded
/// tabs/newlines. Note: the GUI project (which has no reference to Core, by
/// design) keeps its own small duplicate of this exact logic in two places —
/// MainForm.cs and TranslationDetailForm.cs — so the v0.55.4 fix below had to be
/// mirrored there too; see those files' own comments.
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

    /// <summary>v0.55.4で修正済み: 修正前は、literal backslash immediately followed
    /// by a literal 'n' or 't' did not survive the round trip. Escape() doubles the
    /// backslash; the old Unescape() ran its "\n"/"\t" replacements BEFORE its "\\"
    /// replacement as three separate whole-string passes, so the second half of the
    /// doubled backslash pair accidentally combined with the following literal
    /// 'n'/'t' into what looked like an escaped newline/tab. Fixed by rewriting
    /// Unescape as a single left-to-right scan that decides a backslash's meaning
    /// by looking only at the ONE character immediately following it in the
    /// original escaped text, then consumes both characters as a unit — so a
    /// character already used to resolve one escape can never be reused as half of
    /// a different one.</summary>
    [Fact]
    public void Unescape_RoundTrips_BackslashFollowedByLiteralNOrT()
    {
        const string original = "path\\notes.txt";

        var roundTripped = TsvEscaping.Unescape(TsvEscaping.Escape(original));

        Assert.Equal(original, roundTripped);
    }

    /// <summary>Same failure class as above but with a literal 't' instead of 'n'
    /// (the "\\t" pattern is checked before "\\\\" in the same broken order).</summary>
    [Fact]
    public void Unescape_RoundTrips_BackslashFollowedByLiteralT()
    {
        const string original = "C:\\temp\\test.txt";

        var roundTripped = TsvEscaping.Unescape(TsvEscaping.Escape(original));

        Assert.Equal(original, roundTripped);
    }

    /// <summary>Multiple consecutive backslashes (not just a pair) immediately
    /// followed by a literal 'n'/'t' -- confirms the fix consumes doubled-backslash
    /// pairs two-at-a-time from the left rather than leaving an odd one dangling
    /// into the next character.</summary>
    [Fact]
    public void Unescape_RoundTrips_MultipleConsecutiveBackslashesFollowedByLiteralN()
    {
        const string original = "a\\\\\\nb"; // a, three backslashes, n, b

        var roundTripped = TsvEscaping.Unescape(TsvEscaping.Escape(original));

        Assert.Equal(original, roundTripped);
    }

    /// <summary>A trailing backslash with nothing after it -- the "look at the next
    /// character" decision must not run off the end of the string.</summary>
    [Fact]
    public void Unescape_RoundTrips_TrailingBackslash()
    {
        const string original = "ends with a backslash\\";

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
