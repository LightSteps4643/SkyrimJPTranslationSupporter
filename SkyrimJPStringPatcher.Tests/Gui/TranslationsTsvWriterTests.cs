using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// The detail window rewrites a plugin's translations.tsv when the user saves
/// edits. Requirement: saving must never lose a column. It used to write a
/// fixed set of nine columns, which would silently drop the columns SkyPatcher
/// rows carry after them (SourcePath, SourceFilter, SourceLine, Inactive) — and
/// any column a later version adds. What an edit changes is exactly the edited
/// row's Japanese, Notes (becomes SJPTS_ModifiedByUser) and TranslationCheck
/// (cleared: a human has reviewed it); everything else is written back as read.
/// </summary>
public class TranslationsTsvWriterTests
{
    private const string FullHeader =
        "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck\tSourcePath\tSourceFilter\tSourceLine\tInactive";

    private static List<Dictionary<string, string>> Parse(string header, params string[] lines)
    {
        var columns = header.Split('\t');
        return lines.Select(line =>
        {
            var cells = line.Split('\t');
            return columns.Select((c, i) => (c, v: i < cells.Length ? cells[i] : "")).ToDictionary(x => x.c, x => x.v);
        }).ToList();
    }

    [Fact]
    public void ColumnsBeyondTheNineBase_AreWrittenBackUnchanged_InTheirOriginalOrder()
    {
        const string untouched = "k1\tSentinel\tARMO FULL\tFur Coat\t毛皮のコート\tSJPTS_AutoCorpusMeaning\t0\t\t\tarmor/Sentinel.esp.ini\tfilterByArmors=ArmorBanditCuirass\t3\t";
        const string edited = "k2\tSentinel\tARMO FULL\tFur Hat\t\t\t0\t\t\tarmor/Sentinel.esp.ini\tfilterByArmors=ArmorBanditHelmet\t4\t";
        var rows = Parse(FullHeader, untouched, edited);

        var lines = TranslationsTsvWriter.BuildLines(FullHeader.Split('\t'), rows,
            row => row["EnglishText"] == "Fur Hat" ? "毛皮の帽子" : null);

        Assert.Equal(FullHeader, lines[0]);
        Assert.Equal(untouched, lines[1]);
        Assert.Equal(
            "k2\tSentinel\tARMO FULL\tFur Hat\t毛皮の帽子\tSJPTS_ModifiedByUser\t0\t\t\tarmor/Sentinel.esp.ini\tfilterByArmors=ArmorBanditHelmet\t4\t",
            lines[2]);
    }

    [Fact]
    public void EditedRow_ClearsTranslationCheck_AndMarksNotesAsModifiedByUser()
    {
        const string header = "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck";
        var rows = Parse(header, "k1\tMod.esp\tBOOK FULL\tA Book\t本\tSJPTS_TranslationLocalLlm\t0\t\tNeedsReview");

        var lines = TranslationsTsvWriter.BuildLines(header.Split('\t'), rows, _ => "書物");

        Assert.Equal("k1\tMod.esp\tBOOK FULL\tA Book\t書物\tSJPTS_ModifiedByUser\t0\t\t", lines[1]);
    }

    [Fact]
    public void EditedJapanese_IsEscapedForTsv()
    {
        const string header = "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck";
        var rows = Parse(header, "k1\tMod.esp\tBOOK FULL\tLine\t\t\t0\t\t");

        var lines = TranslationsTsvWriter.BuildLines(header.Split('\t'), rows, _ => "一行目\n二行目\tタブ");

        Assert.Equal("k1\tMod.esp\tBOOK FULL\tLine\t一行目\\n二行目\\tタブ\tSJPTS_ModifiedByUser\t0\t\t", lines[1]);
    }

    /// <summary>A translations.tsv written by an older build lacks the later base
    /// columns (EditorId, TranslationCheck). Saving upgrades it to the nine base
    /// columns, as before — existing columns keep their positions and the missing
    /// ones are appended.</summary>
    [Fact]
    public void FileMissingLaterBaseColumns_GainsThem_WithoutMovingTheExistingOnes()
    {
        const string header = "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex";
        var rows = Parse(header, "k1\tMod.esp\tARMO FULL\tHelm\t兜\tSJPTS_AutoCorpusExact\t0");

        var lines = TranslationsTsvWriter.BuildLines(header.Split('\t'), rows, _ => null);

        Assert.Equal("FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck", lines[0]);
        Assert.Equal("k1\tMod.esp\tARMO FULL\tHelm\t兜\tSJPTS_AutoCorpusExact\t0\t\t", lines[1]);
    }

    [Fact]
    public void ReadHeader_ReturnsTheColumnsInFileOrder()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, FullHeader + "\n");

            Assert.Equal(FullHeader.Split('\t'), TsvReader.ReadHeader(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadHeader_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_tests_nonexistent_{Guid.NewGuid():N}.tsv");

        Assert.Empty(TsvReader.ReadHeader(path));
    }
}
