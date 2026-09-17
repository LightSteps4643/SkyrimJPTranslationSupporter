using System.Reflection;
using SkyrimJPStringPatcherGui;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>InterfaceTextDetailForm.ReadTsv/WriteTsv are private static helpers
/// with no public seam (same reasoning as TranslationDetailFormTests.cs), so
/// this reflects on them directly rather than instantiating the WinForms Form
/// itself. Covers the 2026-09-12 fix: these now round-trip via a file-level
/// link to Core/TsvEscaping.cs (SkyrimJPStringPatcherGui.csproj) instead of
/// writing values raw — a literal tab/newline in a value used to corrupt or
/// silently drop the row on the next read.</summary>
public class InterfaceTextDetailFormTsvTests
{
    private static List<InterfaceTranslationRow> InvokeReadTsv(string path) =>
        (List<InterfaceTranslationRow>)typeof(InterfaceTextDetailForm)
            .GetMethod("ReadTsv", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path])!;

    private static void InvokeWriteTsv(string path, IReadOnlyList<InterfaceTranslationRow> rows) =>
        typeof(InterfaceTextDetailForm)
            .GetMethod("WriteTsv", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path, rows]);

    [Fact]
    public void WriteThenRead_RoundTrips_ValueContainingTabAndNewline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_interfacetextdetail_tsv_{Guid.NewGuid():N}.tsv");
        try
        {
            var rows = new List<InterfaceTranslationRow>
            {
                new("$Multi", "line one\nline two\twith a tab", "一行目\n二行目\tタブ入り", true, "SJPTS_ModifiedByUser"),
            };
            InvokeWriteTsv(path, rows);

            var read = InvokeReadTsv(path);

            Assert.Equal(rows, read);
            Assert.Equal(2, File.ReadAllLines(path).Length); // header + 1 row, not split by the embedded newline
        }
        finally { File.Delete(path); }
    }

    /// <summary>2026-09-18: TranslationCheck column round-trips through
    /// ReadTsv/WriteTsv, mirroring SJPTS_InterfaceText/InterfaceTranslationsTsv's
    /// own version of this column.</summary>
    [Fact]
    public void WriteThenRead_RoundTrips_TranslationCheckColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_interfacetextdetail_tsv_{Guid.NewGuid():N}.tsv");
        try
        {
            var rows = new List<InterfaceTranslationRow>
            {
                new("$Foo", "Hello", "This is 剣", true, "SJPTS_TranslationLocalLlm", "ContainsMostOfAlphaNumeric"),
                new("$Bar", "World", "完全な日本語", true, "SJPTS_AutoCorpus", ""), // ①経由は空欄のまま
            };
            InvokeWriteTsv(path, rows);

            var read = InvokeReadTsv(path);

            Assert.Equal(rows, read);
            var header = File.ReadAllLines(path)[0];
            Assert.Contains("TranslationCheck", header.Split('\t'));
        }
        finally { File.Delete(path); }
    }
}
