using System.Text;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.SkyPatcher;

namespace SkyrimJPStringPatcher.Tests.SkyPatcher;

/// <summary>
/// Requirements for writing SkyPatcher .ini files from translated rows:
///  - the output mirrors the original: each source .ini gets one output file at the same path with
///    the output folder inserted after the type folder (armor/X.ini -> armor/zzz_SJPTS/X.ini), the
///    original file name unchanged so a plugin-gated name (Plugin.esp.ini) keeps its gate;
///  - every row is written, in the original order — a row without a translation (not translatable,
///    cannot take effect, still untranslated) is written back as its ORIGINAL name, so the order in
///    which SkyPatcher applies things, and therefore the winner for every record, stays as it was;
///  - each line keeps the original line's filter part exactly and sets only the name(s): never other
///    operations; a line that named both fullName and shortName is one line again;
///  - the name is wrapped in ~…~ (grammar §7), so a translation containing the wrapper character is
///    written with the full-width tilde, and a line break (which would end the line) becomes a space;
///  - a comment before each translated name records the original and the method, so the next scan
///    can carry the translation forward; the file is UTF-8 without BOM.
/// </summary>
public class SkyPatcherIniGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_skypatcher_gen_{Guid.NewGuid():N}");

    public SkyPatcherIniGeneratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Header = "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck\tSourcePath\tSourceFilter\tSourceLine\tInactive\tExcluded";

    private static string Row(string mod, string path, string filter, int line, string recordType, string english, string japanese = "", string notes = "", string occurrenceKey = "1", string inactive = "", string excluded = "") =>
        string.Join('\t', $"{path}|{filter}|{occurrenceKey}", mod, recordType, english, japanese, notes, "0", "", "", path, filter, line, inactive, excluded);

    private string WriteTranslations(params string[] rows)
    {
        var dir = Path.Combine(_root, "in");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "translations.tsv");
        File.WriteAllLines(path, new[] { Header }.Concat(rows), new UTF8Encoding(true));
        return path;
    }

    private string Generate(string input, IReadOnlyCollection<string>? mods = null)
    {
        var outDir = Path.Combine(_root, "out");
        using var log = RunLog.Open(Path.Combine(_root, "log"), "SkyPatcherGenerate");
        SkyPatcherIniGenerator.Run(input, outDir, log, mods);
        return Path.Combine(outDir, "SKSE", "Plugins", "SkyPatcher");
    }

    private static string[] Lines(string path) => File.ReadAllLines(path);

    // ---- where the files go and what a line looks like --------------------------

    [Fact]
    public void AFile_IsWrittenAtTheSamePathWithTheOutputFolderAfterTheTypeFolder_AndKeepsItsName()
    {
        var input = WriteTranslations(
            Row("Sentinel", "armor/.Sentinel/Sentinel.esp.ini", "filterByArmors=ArmorBanditCuirass", 1, "ARMO FULL", "Fur Coat", "毛皮のコート", "SJPTS_AutoCorpusMeaning"));

        var root = Generate(input);

        Assert.True(File.Exists(Path.Combine(root, "armor", "zzz_SJPTS", ".Sentinel", "Sentinel.esp.ini")));
    }

    [Fact]
    public void ATranslatedLine_KeepsTheOriginalFilter_SetsOnlyTheName_AndIsPrecededByAComment()
    {
        var input = WriteTranslations(
            Row("Sentinel", "armor/S.ini", "filterByArmors=ArmorBanditCuirass:hasPlugins=Sentinel.esp", 3, "ARMO FULL", "Fur Coat", "毛皮のコート", "SJPTS_AutoCorpusMeaning"));

        var lines = Lines(Path.Combine(Generate(input), "armor", "zzz_SJPTS", "S.ini"));

        Assert.Equal("; SJPTS fullName | SJPTS_AutoCorpusMeaning | Fur Coat", lines[^2]);
        Assert.Equal("filterByArmors=ArmorBanditCuirass:hasPlugins=Sentinel.esp:fullName=~毛皮のコート~", lines[^1]);
    }

    [Fact]
    public void ALineWithoutAFilter_IsJustTheName()
    {
        var input = WriteTranslations(Row("M", "armor/A.ini", "", 1, "ARMO FULL", "Everything", "すべて", "SJPTS_X"));

        Assert.Equal("fullName=~すべて~", Lines(Path.Combine(Generate(input), "armor", "zzz_SJPTS", "A.ini"))[^1]);
    }

    [Fact]
    public void TheFile_IsUtf8WithoutBom_WithWindowsLineEndings_AndSaysWhereItCameFrom()
    {
        var input = WriteTranslations(Row("M", "armor/A.ini", "filterByArmors=X.esp|1", 1, "ARMO FULL", "Coat", "コート", "SJPTS_X"));

        var bytes = File.ReadAllBytes(Path.Combine(Generate(input), "armor", "zzz_SJPTS", "A.ini"));

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = new UTF8Encoding(false).GetString(bytes);
        Assert.StartsWith("; Generated by SJPTS", text);
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    // ---- every row is written, in the original order -------------------------------

    [Fact]
    public void RowsAreWrittenInTheOriginalLineOrder_WhateverOrderTheyAreStoredIn()
    {
        var input = WriteTranslations(
            Row("M", "armor/A.ini", "filterByArmors=X.esp|3", 30, "ARMO FULL", "Third", "三番目", "SJPTS_X"),
            Row("M", "armor/A.ini", "filterByArmors=X.esp|1", 10, "ARMO FULL", "First", "一番目", "SJPTS_X"),
            Row("M", "armor/A.ini", "filterByArmors=X.esp|2", 20, "ARMO FULL", "Second", "二番目", "SJPTS_X"));

        var lines = Lines(Path.Combine(Generate(input), "armor", "zzz_SJPTS", "A.ini")).Where(l => !l.StartsWith(';')).ToArray();

        Assert.Equal(
            ["filterByArmors=X.esp|1:fullName=~一番目~", "filterByArmors=X.esp|2:fullName=~二番目~", "filterByArmors=X.esp|3:fullName=~三番目~"],
            lines);
    }

    [Fact]
    public void ARowWithoutATranslation_IsWrittenBackWithItsOriginalName_AndNoComment()
    {
        var input = WriteTranslations(
            Row("M", "armor/A.ini", "filterByArmors=X.esp|1", 1, "ARMO FULL", "Still English"),                                                    // not translated yet
            Row("M", "armor/A.ini", "filterByArmors=X.esp|2", 2, "ARMO FULL", "$TNG_TCT", excluded: "InterfaceKeyReference"),                         // not translatable
            Row("M", "armor/A.ini", "filterByArmors=X.esp|3", 3, "ARMO FULL", "毛皮の帽子", excluded: "AlreadyJapanese"),                            // already Japanese
            Row("M", "armor/A.ini", "filterByArmors=X.esp|4", 4, "ARMO FULL", "Gated", inactive: "LineGate"));                                         // cannot take effect

        var all = Lines(Path.Combine(Generate(input), "armor", "zzz_SJPTS", "A.ini"));

        Assert.Equal(
            [
                "filterByArmors=X.esp|1:fullName=~Still English~",
                "filterByArmors=X.esp|2:fullName=~$TNG_TCT~",
                "filterByArmors=X.esp|3:fullName=~毛皮の帽子~",
                "filterByArmors=X.esp|4:fullName=~Gated~",
            ],
            all.Where(l => !l.StartsWith(';')).ToArray());
        Assert.DoesNotContain(all, l => l.StartsWith("; SJPTS "));
    }

    [Fact]
    public void TheSameFilterTwiceInOneFile_StaysTwoLines_InOrder()
    {
        var input = WriteTranslations(
            Row("M", "armor/A.ini", "filterByArmors=X.esp|1", 1, "ARMO FULL", "Gold Boots", "金のブーツ", "SJPTS_X", occurrenceKey: "1"),
            Row("M", "armor/A.ini", "filterByArmors=X.esp|1", 2, "ARMO FULL", "Wings", "翼", "SJPTS_X", occurrenceKey: "2"));

        var lines = Lines(Path.Combine(Generate(input), "armor", "zzz_SJPTS", "A.ini")).Where(l => !l.StartsWith(';')).ToArray();

        Assert.Equal(["filterByArmors=X.esp|1:fullName=~金のブーツ~", "filterByArmors=X.esp|1:fullName=~翼~"], lines);
    }

    [Fact]
    public void ANpcLineThatSetBothNames_IsOneLineAgain_AndAnUntranslatedHalfKeepsItsOriginal()
    {
        var input = WriteTranslations(
            Row("M", "npc/N.ini", "filterByNpcs=Skyrim.esm|7", 1, "NPC_ FULL", "Dragonborn"),                                  // not translated
            Row("M", "npc/N.ini", "filterByNpcs=Skyrim.esm|7", 1, "NPC_ SHRT", "Dovah", "ドヴァー", "SJPTS_X"));

        var lines = Lines(Path.Combine(Generate(input), "npc", "zzz_SJPTS", "N.ini")).Where(l => !l.StartsWith(';')).ToArray();

        Assert.Equal(["filterByNpcs=Skyrim.esm|7:fullName=~Dragonborn~:shortName=~ドヴァー~"], lines);
    }

    // ---- what the name text may contain ------------------------------------------

    [Fact]
    public void TheWrapperCharacterInATranslation_IsWrittenAsTheFullWidthTilde_AndLineBreaksBecomeSpaces()
    {
        var input = WriteTranslations(
            Row("M", "armor/A.ini", "filterByArmors=X.esp|1", 1, "ARMO FULL", "Ring", "指輪~改", "SJPTS_ModifiedByUser"),
            Row("M", "armor/A.ini", "filterByArmors=X.esp|2", 2, "ARMO FULL", "Two Lines", "一行目\\n二行目", "SJPTS_ModifiedByUser"));   // TSV-escaped newline

        var lines = Lines(Path.Combine(Generate(input), "armor", "zzz_SJPTS", "A.ini")).Where(l => !l.StartsWith(';')).ToArray();

        Assert.Equal(["filterByArmors=X.esp|1:fullName=~指輪～改~", "filterByArmors=X.esp|2:fullName=~一行目 二行目~"], lines);
    }

    [Fact]
    public void WhatIsWrittenCanBeReadBackAsTheSameFilterAndNames()
    {
        var input = WriteTranslations(
            Row("M", "npc/N.ini", "filterByNpcs=Skyrim.esm|7:hasPluginsOr=A.esp,B.esp", 5, "NPC_ FULL", "Alpha", "アルファ", "SJPTS_X"),
            Row("M", "npc/N.ini", "filterByNpcs=Skyrim.esm|7:hasPluginsOr=A.esp,B.esp", 5, "NPC_ SHRT", "Beta", "ベータ", "SJPTS_X"));

        var written = Lines(Path.Combine(Generate(input), "npc", "zzz_SJPTS", "N.ini")).Last();

        var parsed = SkyPatcherLineParser.Parse(written, 1)!;
        Assert.Equal("filterByNpcs=Skyrim.esm|7:hasPluginsOr=A.esp,B.esp", parsed.FilterPart);
        Assert.Equal([(SkyPatcherNameKind.FullName, "アルファ"), (SkyPatcherNameKind.ShortName, "ベータ")], parsed.NameOps.Select(o => (o.Kind, o.Text)).ToArray());
    }

    // ---- which rows are used -------------------------------------------------------

    [Fact]
    public void RowsOfOtherSourcesAndOtherMods_AreLeftOut()
    {
        var plugin = string.Join('\t', "00012345:Skyrim.esm", "Skyrim.esm", "ARMO FULL", "Steel", "鋼", "SJPTS_X", "0", "", "", "", "", "", "", "");   // a plugin row: no SourcePath
        var input = WriteTranslations(
            plugin,
            Row("ModA", "armor/A.ini", "filterByArmors=X.esp|1", 1, "ARMO FULL", "From A", "Aから", "SJPTS_X"),
            Row("ModB", "armor/B.ini", "filterByArmors=X.esp|2", 1, "ARMO FULL", "From B", "Bから", "SJPTS_X"));

        var root = Generate(input, ["ModA"]);

        Assert.True(File.Exists(Path.Combine(root, "armor", "zzz_SJPTS", "A.ini")));
        Assert.False(File.Exists(Path.Combine(root, "armor", "zzz_SJPTS", "B.ini")));
        Assert.Single(Directory.GetFiles(root, "*.ini", SearchOption.AllDirectories));
    }

    [Fact]
    public void ADirectoryInput_ReadsEveryModsTranslationsTsv()
    {
        var inDir = Path.Combine(_root, "in");
        foreach (var (mod, file) in new[] { ("ModA", "A.ini"), ("ModB", "B.ini") })
        {
            Directory.CreateDirectory(Path.Combine(inDir, mod));
            File.WriteAllLines(Path.Combine(inDir, mod, "translations.tsv"),
                [Header, Row(mod, $"armor/{file}", "filterByArmors=X.esp|1", 1, "ARMO FULL", "Name", "名前", "SJPTS_X")]);
        }

        var root = Generate(inDir);

        Assert.True(File.Exists(Path.Combine(root, "armor", "zzz_SJPTS", "A.ini")));
        Assert.True(File.Exists(Path.Combine(root, "armor", "zzz_SJPTS", "B.ini")));
    }

    // ---- the whole loop: scan -> (translate) -> generate -> scan again -----------------

    [Fact]
    public void WhatWeWrite_IsReadBackByTheScanner_AsAnEarlierTranslation()
    {
        // A mod ships a .ini; the scan finds its rows; we "translate" one and generate; the output is
        // installed as a mod and scanned again: the translation must come back as carried forward,
        // and our own output must not be read as a source.
        var mods = Path.Combine(_root, "mods");
        var source = Path.Combine(mods, "Sentinel", "SKSE", "Plugins", "SkyPatcher", "armor", "S.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllLines(source, ["filterByArmors=ArmorBanditCuirass:fullName=~Fur Coat~", "filterByArmors=ArmorBanditHelmet:fullName=~Fur Hat~"]);
        var overwrite = Path.Combine(_root, "overwrite");
        Directory.CreateDirectory(overwrite);
        Mo2Instance Instance(params string[] enabled) => new(Path.Combine(_root, "game"), "Default", mods, overwrite, enabled, []);

        var first = SkyPatcherScanner.Scan(Instance("Sentinel")).Candidates;
        var rows = first.Select(c => Row(c.WinningPlugin, c.SourcePath, c.SourceFilter, c.SourceLine, c.RecordType, c.CurrentText,
            c.CurrentText == "Fur Coat" ? "毛皮のコート" : "", c.CurrentText == "Fur Coat" ? "SJPTS_AutoCorpusMeaning" : "")).ToArray();
        var generated = Generate(WriteTranslations(rows));

        // "Install" the generated output as a mod.
        var outputMod = Path.Combine(mods, "SJPTS Output", "SKSE", "Plugins", "SkyPatcher");
        CopyDirectory(generated, outputMod);

        var second = SkyPatcherScanner.Scan(Instance("SJPTS Output", "Sentinel")).Candidates;

        Assert.Equal(2, second.Count);
        var coat = second.Single(c => c.CurrentText == "Fur Coat");
        Assert.Equal("毛皮のコート", coat.DsdCoveredJapanese);
        Assert.Equal("SJPTS_AutoCorpusMeaning", coat.DsdCoveredNotes);
        Assert.Equal("", second.Single(c => c.CurrentText == "Fur Hat").DsdCoveredJapanese);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
