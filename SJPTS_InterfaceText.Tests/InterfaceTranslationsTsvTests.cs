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
