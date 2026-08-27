using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

public class CandidateIoTests
{
    [Fact]
    public void WriteTsv_ThenReadTsv_RoundTripsAllFields()
    {
        var path = Path.GetTempFileName();
        try
        {
            var original = new List<Candidate>
            {
                new("Skyrim.esm", "00012345", "PERK FULL", "Some\tText\nWith Escapes",
                    Index: 2, EditorId: "SomeEditorId", Context: "Some Context",
                    StaleOriginal: "Old Text", StaleTranslation: "古いテキスト",
                    Warning: "PickUpTargetClassificationFailed"),
            };

            CandidateIo.WriteTsv(path, original);
            var roundTripped = CandidateIo.ReadTsv(path);

            var c = Assert.Single(roundTripped);
            Assert.Equal(original[0], c);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTsv_ToleratesOldRowsWithoutTrailingColumns()
    {
        // v0.54.2 (known issue 21) added the "Warning" column at the end. A TSV
        // written by an older build won't have it — ReadTsv must not throw, and
        // must default the missing fields to "".
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[]
            {
                "FormId\tWinningPlugin\tRecordType\tEnglishText",
                "00012345\tSkyrim.esm\tPERK FULL\tSome Text",
            });

            var result = CandidateIo.ReadTsv(path);

            var c = Assert.Single(result);
            Assert.Equal("00012345", c.FormId);
            Assert.Equal("Skyrim.esm", c.WinningPlugin);
            Assert.Equal("PERK FULL", c.RecordType);
            Assert.Equal("Some Text", c.CurrentText);
            Assert.Equal(0, c.Index);
            Assert.Equal("", c.EditorId);
            Assert.Equal("", c.Context);
            Assert.Equal("", c.StaleOriginal);
            Assert.Equal("", c.StaleTranslation);
            Assert.Equal("", c.Warning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTsv_SkipsBlankLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[]
            {
                "FormId\tWinningPlugin\tRecordType\tEnglishText\tIndex\tEditorId\tContext\tStaleOriginal\tStaleTranslation\tWarning",
                "",
                "00012345\tSkyrim.esm\tPERK FULL\tSome Text\t0\t\t\t\t\t",
            });

            var result = CandidateIo.ReadTsv(path);

            Assert.Single(result);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
