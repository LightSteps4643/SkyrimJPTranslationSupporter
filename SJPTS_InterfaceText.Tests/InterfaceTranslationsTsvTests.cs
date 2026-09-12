using SJPTS_InterfaceText;

namespace SJPTS_InterfaceText.Tests;

public class InterfaceTranslationsTsvTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"sjpts_uitext_tsv_{Guid.NewGuid():N}.tsv");

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var path = TempFile();
        try
        {
            var rows = new List<InterfaceTranslationRow>
            {
                new("$A", "Hello", "こんにちは", true),
                new("$B", "World", "", false),
            };
            InterfaceTranslationsTsv.Write(path, rows);

            var read = InterfaceTranslationsTsv.Read(path);

            Assert.Equal(rows, read);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Regression test (2026-09-12): before TsvEscaping was applied, a
    /// literal tab or newline in a value corrupted or silently dropped the row
    /// on the next Read (a newline split the row across two physical lines,
    /// the continuation line then having fewer than 4 columns and being
    /// discarded outright; a literal tab shifted every subsequent column,
    /// misreading Resolved and Notes). Round-tripping a value containing both
    /// must reproduce the exact original row.</summary>
    [Fact]
    public void WriteThenRead_RoundTrips_ValueContainingTabAndNewline()
    {
        var path = TempFile();
        try
        {
            var rows = new List<InterfaceTranslationRow>
            {
                new("$Multi", "line one\nline two\twith a tab", "一行目\n二行目\tタブ入り", true, "TranslationLocalLlm"),
            };
            InterfaceTranslationsTsv.Write(path, rows);

            var read = InterfaceTranslationsTsv.Read(path);

            Assert.Equal(rows, read);
            // Also confirm the file itself is still exactly one physical line
            // per row (the whole point of escaping) — not silently split.
            Assert.Equal(2, File.ReadAllLines(path).Length); // header + 1 row
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_NonexistentFile_ReturnsEmptyList()
    {
        var result = InterfaceTranslationsTsv.Read(Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.tsv"));
        Assert.Empty(result);
    }

    [Fact]
    public void Write_OverwritesExistingContent()
    {
        var path = TempFile();
        try
        {
            InterfaceTranslationsTsv.Write(path, new List<InterfaceTranslationRow> { new("$Old", "Old", "古い", true) });
            InterfaceTranslationsTsv.Write(path, new List<InterfaceTranslationRow> { new("$New", "New", "", false) });

            var read = InterfaceTranslationsTsv.Read(path);

            Assert.Single(read);
            Assert.Equal("$New", read[0].Key);
        }
        finally { File.Delete(path); }
    }
}
