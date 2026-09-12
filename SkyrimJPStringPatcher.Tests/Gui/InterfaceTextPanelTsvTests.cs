using System.Reflection;
using SkyrimJPStringPatcherGui;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>InterfaceTextPanel.ReadInterfaceTranslationsTsv is a private static
/// helper with no public seam, so this reflects on it directly rather than
/// instantiating the WinForms Form itself (same reasoning as
/// TranslationDetailFormTests.cs / InterfaceTextDetailFormTsvTests.cs). Covers
/// the 2026-09-12 fix: this now Unescapes via the file-level link to
/// Core/TsvEscaping.cs — before that, a value written with an escaped tab/
/// newline (by SJPTS_InterfaceText/InterfaceTranslationsTsv.cs's own Write)
/// would have shown up here with the literal two-character escape sequence
/// still in it instead of the real character.</summary>
public class InterfaceTextPanelTsvTests
{
    private static List<(string Key, string English, string Japanese, bool Resolved)> InvokeRead(string path) =>
        (List<(string, string, string, bool)>)typeof(InterfaceTextPanel)
            .GetMethod("ReadInterfaceTranslationsTsv", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path])!;

    [Fact]
    public void Read_UnescapesTabAndNewlineWrittenByCli()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_interfacetextpanel_tsv_{Guid.NewGuid():N}.tsv");
        try
        {
            // Mirrors exactly what SJPTS_InterfaceText/InterfaceTranslationsTsv.cs's
            // own Write now produces for a value containing a tab/newline.
            File.WriteAllLines(path, new[]
            {
                "Key\tEnglish\tJapanese\tResolved\tNotes",
                "$Multi\tline one\\nline two\\twith a tab\t一行目\\n二行目\\tタブ入り\t1\tTranslationLocalLlm",
            });

            var rows = InvokeRead(path);

            var row = Assert.Single(rows);
            Assert.Equal("line one\nline two\twith a tab", row.English);
            Assert.Equal("一行目\n二行目\tタブ入り", row.Japanese);
            Assert.True(row.Resolved);
        }
        finally { File.Delete(path); }
    }
}
