using System.Reflection;
using SkyrimJPStringPatcherGui;
using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// 2026-09-16: TranslationDetailForm.Escape/Unescape used to be a deliberate
/// small duplication of Core/TsvEscaping.cs's own logic (GUI has no project
/// reference to Core). Now `Escape` calls `TsvEscaping.Escape` (via the
/// file-level link already used by InterfaceTextPanel.cs/InterfaceTextDetailForm.cs
/// — see SkyrimJPStringPatcherGui.csproj) directly at its one call site, so
/// there's no `TranslationDetailForm.Escape` left to reflect on — tests that
/// need the escaped form call `TsvEscaping.Escape` directly instead. Only
/// `Unescape` remains a private static helper on `TranslationDetailForm`
/// itself, since it layers a form-specific display adjustment (CRLF for the
/// Win32 multiline edit control) on top of the shared `TsvEscaping.Unescape`
/// — that one still has no public seam, so this reflects on it directly
/// rather than instantiating the WinForms Form itself. Plain tab/backslash/
/// newline round-tripping (no CRLF quirk involved) is Core's own
/// responsibility now and is covered by Core/TsvEscapingTests.cs instead of
/// being re-tested here.
/// </summary>
public class TranslationDetailFormTests
{
    private static string InvokeUnescape(string s) =>
        (string)typeof(TranslationDetailForm)
            .GetMethod("Unescape", BindingFlags.NonPublic | BindingFlags.Static)!
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

    /// <summary>`TsvEscaping.Escape` must still round-trip a CRLF-containing
    /// value (as this form's own Unescape now produces, and as a user's own
    /// Shift+Enter keystroke in the multiline editing TextBox naturally
    /// inserts) back to the exact same single "\n" escape sequence used
    /// before this form ever needed the CRLF adjustment — no format change to
    /// translations.tsv, no double-escaping. Exercises the actual boundary
    /// between the shared Escape and this form's own Unescape, not just
    /// Core's own Escape/Unescape pair in isolation (already covered by
    /// Core/TsvEscapingTests.cs).</summary>
    [Fact]
    public void Escape_CarriageReturnLineFeed_RoundTripsToSingleEscapedNewline()
    {
        var escaped = TsvEscaping.Escape("line one\r\nline two");
        Assert.Equal("line one\\nline two", escaped);
        Assert.Equal("line one\r\nline two", InvokeUnescape(escaped));
    }
}
