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
                new("$Multi", "line one\nline two\twith a tab", "一行目\n二行目\tタブ入り", true, "ModifiedByUser"),
            };
            InvokeWriteTsv(path, rows);

            var read = InvokeReadTsv(path);

            Assert.Equal(rows, read);
            Assert.Equal(2, File.ReadAllLines(path).Length); // header + 1 row, not split by the embedded newline
        }
        finally { File.Delete(path); }
    }
}
