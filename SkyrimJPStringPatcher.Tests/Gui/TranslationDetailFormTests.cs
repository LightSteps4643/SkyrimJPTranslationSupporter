using System.Reflection;
using SkyrimJPStringPatcherGui;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// TranslationDetailForm.Unescape/Escape are private static string helpers
/// (a deliberate small duplication of Core/TsvEscaping.cs — see their own
/// remarks) with no public seam, so this reflects on them directly rather
/// than instantiating the WinForms Form itself.
/// </summary>
public class TranslationDetailFormTests
{
    private static string InvokeUnescape(string s) =>
        (string)typeof(TranslationDetailForm)
            .GetMethod("Unescape", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [s])!;

    private static string InvokeEscape(string s) =>
        (string)typeof(TranslationDetailForm)
            .GetMethod("Escape", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [s])!;

    /// <summary>v0.59.0: real-machine report — a multiline translation
    /// (a book's body text) showed its paragraph breaks correctly in the
    /// grid's resting (non-edit) display, but lost them entirely the moment
    /// the cell was clicked into edit mode, even though translations.tsv
    /// itself was confirmed correct on disk. Root cause: Unescape turned the
    /// stored "\n" escape sequence into a bare LF ('\n'). GDI+'s cell-paint
    /// renderer (used for the resting display) treats a bare LF as a line
    /// break fine, but the native Win32 multiline EDIT control behind the
    /// editing TextBox (see Grid_EditingControlShowing's tb.Multiline) does
    /// not — it needs CRLF. Fixed by having Unescape emit "\r\n" instead.</summary>
    [Fact]
    public void Unescape_EscapedNewline_ProducesCarriageReturnLineFeed_NotBareLineFeed()
    {
        var result = InvokeUnescape("line one\\nline two");
        Assert.Equal("line one\r\nline two", result);
    }

    /// <summary>Escape must still round-trip a CRLF-containing value (as
    /// Unescape now produces, and as a user's own Shift+Enter keystroke in
    /// the multiline editing TextBox naturally inserts) back to the exact
    /// same single "\n" escape sequence used before this fix — no format
    /// change to translations.tsv, no double-escaping.</summary>
    [Fact]
    public void Escape_CarriageReturnLineFeed_RoundTripsToSingleEscapedNewline()
    {
        var escaped = InvokeEscape("line one\r\nline two");
        Assert.Equal("line one\\nline two", escaped);
        Assert.Equal("line one\r\nline two", InvokeUnescape(escaped));
    }

    [Fact]
    public void Escape_Then_Unescape_RoundTrips_ForTabsAndBackslashesToo()
    {
        const string original = "path\\to\\file.txt\tafter a tab\r\nsecond line";
        Assert.Equal(original, InvokeUnescape(InvokeEscape(original)));
    }
}
