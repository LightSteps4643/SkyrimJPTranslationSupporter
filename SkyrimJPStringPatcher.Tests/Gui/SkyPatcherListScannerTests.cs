using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// The SkyPatcher sub-tab's list: one row per providing MOD, read straight from
/// SkyPatcherText/Translation/out_temp/&lt;MOD&gt;/translations.tsv (the GUI never asks the CLI). The MOD's name
/// comes from mod_folder_name.txt in its folder. The counts cover only rows that are offered for
/// translation: a row that cannot take effect (Inactive) or is not translatable (Excluded) is in the file
/// for the output's sake but is not part of the work, so it is counted nowhere.
/// </summary>
public class SkyPatcherListScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_sp_list_{Guid.NewGuid():N}");

    public SkyPatcherListScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Header = "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck\tSourcePath\tSourceFilter\tSourceLine\tInactive\tExcluded";

    private static string Line(string mod, string english, string japanese = "", string inactive = "", string excluded = "") =>
        string.Join('\t', $"armor/A.ini|f|{english}", mod, "ARMO FULL", english, japanese, "", "0", "", "", "armor/A.ini", "f", "1", inactive, excluded);

    private void WriteMod(string folder, string? modName, params string[] lines)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "translations.tsv"), new[] { Header }.Concat(lines));
        if (modName != null) File.WriteAllText(Path.Combine(dir, "mod_folder_name.txt"), modName);
    }

    [Fact]
    public void OneRowPerMod_NamedFromModFolderNameTxt_WithTheirCounts()
    {
        WriteMod("Armor Pack v1.2", "Armor Pack v1.2",
            Line("Armor Pack v1.2", "Fur Coat", "毛皮のコート"),
            Line("Armor Pack v1.2", "Fur Hat"),                                // untranslated, 7 characters
            Line("Armor Pack v1.2", "Fur Boots"));                              // untranslated, 9 characters

        var row = Assert.Single(SkyPatcherListScanner.Scan(_root));

        Assert.Equal("Armor Pack v1.2", row.Mod);
        Assert.Equal(3, row.Total);
        Assert.Equal(2, row.Untranslated);
        Assert.Equal(16, row.UntranslatedChars);
        Assert.Equal(100.0 / 3, row.Ratio, 6);                                  // 1 of 3 translated
    }

    [Fact]
    public void RowsThatCannotTakeEffectOrAreNotTranslatable_AreCountedNowhere()
    {
        WriteMod("Mod", "Mod",
            Line("Mod", "Fur Coat", "毛皮のコート"),
            Line("Mod", "Gated Cloak", inactive: "LineGate"),
            Line("Mod", "$TNG_TCT", excluded: "InterfaceKeyReference"),
            Line("Mod", "Fur Hat"));

        var row = Assert.Single(SkyPatcherListScanner.Scan(_root));

        Assert.Equal(2, row.Total);
        Assert.Equal(1, row.Untranslated);
        Assert.Equal(7, row.UntranslatedChars);
    }

    [Fact]
    public void WithoutModFolderNameTxt_TheFolderNameIsTheMod()
    {
        WriteMod("Plain Mod", modName: null, Line("Plain Mod", "Fur Coat"));

        Assert.Equal("Plain Mod", Assert.Single(SkyPatcherListScanner.Scan(_root)).Mod);
    }

    [Fact]
    public void AModWithNothingToTranslate_StillGetsARow_ButAnEmptyOrMissingFolderDoesNot()
    {
        WriteMod("All Done", "All Done", Line("All Done", "Fur Coat", "毛皮のコート"));
        Directory.CreateDirectory(Path.Combine(_root, "Empty"));
        WriteMod("Header Only", "Header Only");

        var rows = SkyPatcherListScanner.Scan(_root);

        var row = Assert.Single(rows);
        Assert.Equal("All Done", row.Mod);
        Assert.Equal(0, row.Untranslated);
        Assert.Equal(100.0, row.Ratio);
    }

    [Fact]
    public void RowsAreOrderedByUntranslatedCharacters_MostFirst()
    {
        WriteMod("Small", "Small", Line("Small", "Hat"));
        WriteMod("Big", "Big", Line("Big", "A Very Long Untranslated Name"));

        Assert.Equal(["Big", "Small"], SkyPatcherListScanner.Scan(_root).Select(r => r.Mod).ToArray());
    }

    [Fact]
    public void ANonexistentFolder_GivesAnEmptyList()
    {
        Assert.Empty(SkyPatcherListScanner.Scan(Path.Combine(_root, "does_not_exist")));
    }

    [Fact]
    public void TranslationStats_CountsABlankJapaneseAsUntranslated_AndEscapedTextByItsRealLength()
    {
        var rows = new List<Dictionary<string, string>>
        {
            new() { ["EnglishText"] = "Line1\\nLine2", ["Japanese"] = "" },      // 11 real characters ("\n" is one)
            new() { ["EnglishText"] = "Done", ["Japanese"] = "完了" },
        };

        var (total, untranslated, ratio, untranslatedChars, charsRatio) = TranslationStats.Compute(rows);

        Assert.Equal((2, 1, 50.0, 11L), (total, untranslated, ratio, untranslatedChars));
        Assert.Equal(100.0 * (15 - 11) / 15, charsRatio, 6);
    }
}
