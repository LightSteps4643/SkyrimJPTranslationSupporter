using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.SkyPatcher;

namespace SkyrimJPStringPatcher.Tests.SkyPatcher;

/// <summary>
/// Requirements for scanning an MO2 instance's SkyPatcher .ini files into candidates:
///  - only what the game would read: enabled mods, and per relative path the VFS winner (Overwrite
///    first, then the highest-priority mod); loose .ini files under SKSE/Plugins/SkyPatcher/&lt;type&gt;/;
///  - the providing MOD (the folder the winning file sits in) is the candidate's WinningPlugin;
///  - every name line becomes a row — translatable, inactive (cannot take effect) or excluded
///    (cannot/must not be translated) — because the output reproduces all effective lines in order;
///  - our own earlier output (the zzz_SJPTS folder) is never a source: it is read back to carry its
///    translations forward, and an original that changed since marks the old translation stale;
///  - every row is accounted for in exactly one category (nothing silently dropped).
/// </summary>
public class SkyPatcherScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_skypatcher_scan_{Guid.NewGuid():N}");
    private string ModsDir => Path.Combine(_root, "mods");
    private string OverwriteDir => Path.Combine(_root, "overwrite");

    public SkyPatcherScannerTests()
    {
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(OverwriteDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Add(string mod, string relative, params string[] lines)
    {
        var baseDir = mod == "<overwrite>" ? OverwriteDir : Path.Combine(ModsDir, mod);
        var path = Path.Combine(baseDir, "SKSE", "Plugins", "SkyPatcher", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    private string AddRaw(string mod, string relativeFromModRoot, string content)
    {
        var path = Path.Combine(ModsDir, mod, relativeFromModRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private Mo2Instance Instance(string[] enabledHighFirst, string[]? plugins = null) =>
        new(Path.Combine(_root, "game"), "Default", ModsDir, OverwriteDir, enabledHighFirst,
            (plugins ?? ["Skyrim.esm"]).Select(p => new ResolvedPlugin(p, Path.Combine(_root, p))).ToList());

    // ---- what a row says -----------------------------------------------------

    [Fact]
    public void ANameLine_BecomesARowAttributedToItsProvidingMod()
    {
        Add("Mark Unique Items", "armor/Mark Unique Items.ini", "; header", "filterByArmors=Skyrim.esm|15516:fullName=~* Emperor's Robes~");

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["Mark Unique Items"])).Candidates);

        Assert.Equal("Mark Unique Items", c.WinningPlugin);
        Assert.Equal("armor/Mark Unique Items.ini|filterByArmors=Skyrim.esm|15516|1", c.FormId);   // the row key
        Assert.Equal("ARMO FULL", c.RecordType);
        Assert.Equal("* Emperor's Robes", c.CurrentText);
        Assert.Equal(0, c.Index);
        Assert.Equal("armor/Mark Unique Items.ini", c.SourcePath);
        Assert.Equal("filterByArmors=Skyrim.esm|15516", c.SourceFilter);
        Assert.Equal(2, c.SourceLine);
        Assert.Equal("", c.Inactive);
        Assert.Equal("", c.Excluded);
    }

    [Fact]
    public void FilesInSubfoldersAndWithDottedFolderNames_AreRead_AndTypeFolderCaseIsIgnored()
    {
        Add("Sentinel", "Armor/.Sentinel/Sentinel.esp.ini", "filterByArmors=ArmorBanditCuirass:fullName=~Fur Coat~");

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["Sentinel"], ["Skyrim.esm", "Sentinel.esp"])).Candidates);

        Assert.Equal("Armor/.Sentinel/Sentinel.esp.ini", c.SourcePath);
        Assert.Equal("ARMO FULL", c.RecordType);
        Assert.Equal("", c.Inactive);
    }

    // ---- which files count ---------------------------------------------------

    [Fact]
    public void OnlyTheVfsWinnerOfARelativePathIsRead()
    {
        Add("LowMod", "armor/Shared.ini", "filterByArmors=Skyrim.esm|1:fullName=~From Low~");
        Add("HighMod", "armor/Shared.ini", "filterByArmors=Skyrim.esm|1:fullName=~From High~");

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["HighMod", "LowMod"])).Candidates);

        Assert.Equal("From High", c.CurrentText);
        Assert.Equal("HighMod", c.WinningPlugin);
    }

    [Fact]
    public void OverwriteBeatsEveryMod_AndIsAttributedToIt()
    {
        Add("HighMod", "armor/Shared.ini", "filterByArmors=Skyrim.esm|1:fullName=~From Mod~");
        Add("<overwrite>", "armor/Shared.ini", "filterByArmors=Skyrim.esm|1:fullName=~From Overwrite~");

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["HighMod"])).Candidates);

        Assert.Equal("From Overwrite", c.CurrentText);
        Assert.Equal("Overwrite", c.WinningPlugin);
    }

    [Fact]
    public void ADisabledMod_IsNotRead()
    {
        Add("Disabled", "armor/A.ini", "filterByArmors=Skyrim.esm|1:fullName=~Never~");
        Add("Enabled", "armor/B.ini", "filterByArmors=Skyrim.esm|2:fullName=~Seen~");

        var result = SkyPatcherScanner.Scan(Instance(["Enabled"]));   // "Disabled" is not in the enabled list

        Assert.Equal("Seen", Assert.Single(result.Candidates).CurrentText);
    }

    [Fact]
    public void NonIniFiles_UnknownFolders_AndPathsOutsideSkyPatcher_AreIgnored()
    {
        Add("Mod", "armor/readme.txt", "filterByArmors=Skyrim.esm|1:fullName=~Not An Ini~");
        Add("Mod", "race/Races.ini", "filterByRaces=Skyrim.esm|13740:fullName=~Nord~");              // race sets no display name
        Add("Mod", "Loose.ini", "filterByArmors=Skyrim.esm|1:fullName=~Not In A Type Folder~");        // directly under SkyPatcher/
        AddRaw("Mod", "SKSE/Plugins/Other/armor/X.ini", "filterByArmors=Skyrim.esm|1:fullName=~Other Plugin~");
        Add("Mod", "armor/Real.ini", "filterByArmors=Skyrim.esm|2:fullName=~Real~");

        var result = SkyPatcherScanner.Scan(Instance(["Mod"]));

        Assert.Equal("Real", Assert.Single(result.Candidates).CurrentText);
    }

    // ---- gates and settings ---------------------------------------------------

    [Fact]
    public void AFileNamedAfterAnAbsentPlugin_IsInactiveByTheFileGate_AndStillListed()
    {
        Add("Sentinel", "armor/Sentinel.esp.ini", "filterByArmors=Skyrim.esm|1:fullName=~Fur Coat~");

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["Sentinel"])).Candidates);   // Sentinel.esp not in the load order

        Assert.Equal(SkyPatcherInactive.FileGate, c.Inactive);
    }

    [Fact]
    public void SkyPatcherIniFromTheWinningMod_CanSwitchATypeOff()
    {
        AddRaw("SkyPatcher", "SKSE/Plugins/SkyPatcher.ini", "[Patcher]\r\niEnableArmorPatching=0\r\n");
        Add("Mod", "armor/A.ini", "filterByArmors=Skyrim.esm|1:fullName=~X~");
        Add("Mod", "weapon/W.ini", "filterByWeapons=Skyrim.esm|2:fullName=~Y~");

        var rows = SkyPatcherScanner.Scan(Instance(["Mod", "SkyPatcher"])).Candidates;

        Assert.Equal(SkyPatcherInactive.TypeDisabled, rows.Single(r => r.RecordType == "ARMO FULL").Inactive);
        Assert.Equal("", rows.Single(r => r.RecordType == "WEAP FULL").Inactive);
    }

    // ---- excluded rows and the self-audit --------------------------------------

    [Fact]
    public void ARowThatCannotBeTranslated_IsKeptWithItsReason()
    {
        Add("Mod", "armor/A.ini",
            "filterByArmors=Skyrim.esm|1:fullName=~毛皮のコート~",
            "filterByArmors=Skyrim.esm|2:fullName=~$TNG_TCT~",
            "filterByArmors=Skyrim.esm|3:fullName=~Fur Coat~");

        var rows = SkyPatcherScanner.Scan(Instance(["Mod"])).Candidates;

        Assert.Equal([SkyPatcherExclusion.AlreadyJapanese, SkyPatcherExclusion.InterfaceKeyReference, ""], rows.Select(r => r.Excluded).ToArray());
    }

    [Fact]
    public void EveryRow_IsCountedInExactlyOneCategory()
    {
        Add("Mod", "armor/A.ini",
            "filterByArmors=Skyrim.esm|1:fullName=~Fur Coat~",                       // translatable
            "filterByArmors=Skyrim.esm|2:fullName=~毛皮の帽子~",                      // excluded
            "filterByArmors=Skyrim.esm|3:hasPlugins=Missing.esp:fullName=~Gated~",    // inactive (line gate)
            "filterByArmors=Skyrim.esm|4:fullName=~Old~",
            "filterByArmors=Skyrim.esm|4:fullName=~New~");                             // the line above is overridden -> inactive

        var result = SkyPatcherScanner.Scan(Instance(["Mod"]));

        Assert.Equal(5, result.Stats.TotalRows);
        Assert.Equal(2, result.Stats.Translatable);                                    // "Fur Coat" and "New"
        Assert.Equal(1, result.Stats.Excluded[SkyPatcherExclusion.AlreadyJapanese]);
        Assert.Equal(1, result.Stats.Inactive[SkyPatcherInactive.LineGate]);
        Assert.Equal(1, result.Stats.Inactive[SkyPatcherInactive.OverriddenInSameFile]);
        Assert.Equal(result.Stats.TotalRows,
            result.Stats.Translatable + result.Stats.Excluded.Values.Sum() + result.Stats.Inactive.Values.Sum());
    }

    // ---- our own earlier output -----------------------------------------------

    private const string Original = "filterByArmors=Skyrim.esm|15516:fullName=~Fur Coat~";

    [Fact]
    public void OurOwnOutputFolder_IsNeverASource()
    {
        Add("Mod", "armor/A.ini", Original);
        Add("SJPTS Output", "armor/zzz_SJPTS/A.ini", "; SJPTS fullName | SJPTS_TranslationLocalLlm | Fur Coat", "filterByArmors=Skyrim.esm|15516:fullName=~毛皮のコート~");

        var result = SkyPatcherScanner.Scan(Instance(["SJPTS Output", "Mod"]));

        Assert.Single(result.Candidates);
        Assert.Equal("Mod", result.Candidates[0].WinningPlugin);
    }

    [Fact]
    public void AnEarlierTranslationOfTheSameOriginal_IsCarriedForward()
    {
        Add("Mod", "armor/A.ini", Original);
        Add("SJPTS Output", "armor/zzz_SJPTS/A.ini", "; SJPTS fullName | SJPTS_TranslationLocalLlm | Fur Coat", "filterByArmors=Skyrim.esm|15516:fullName=~毛皮のコート~");

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["SJPTS Output", "Mod"])).Candidates);

        Assert.Equal("毛皮のコート", c.DsdCoveredJapanese);
        Assert.Equal("SJPTS_TranslationLocalLlm", c.DsdCoveredNotes);
        Assert.Equal("", c.StaleOriginal);
    }

    [Fact]
    public void AnEarlierTranslationOfADifferentOriginal_IsOnlyOfferedAsStale()
    {
        Add("Mod", "armor/A.ini", "filterByArmors=Skyrim.esm|15516:fullName=~Fur Coat v2~");
        Add("SJPTS Output", "armor/zzz_SJPTS/A.ini", "; SJPTS fullName | SJPTS_TranslationLocalLlm | Fur Coat", "filterByArmors=Skyrim.esm|15516:fullName=~毛皮のコート~");

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["SJPTS Output", "Mod"])).Candidates);

        Assert.Equal("", c.DsdCoveredJapanese);
        Assert.Equal("Fur Coat", c.StaleOriginal);
        Assert.Equal("毛皮のコート", c.StaleTranslation);
    }

    [Fact]
    public void AnOutputLineThatWasWrittenBackUntranslated_CarriesNothingForward()
    {
        Add("Mod", "armor/A.ini", Original);
        Add("SJPTS Output", "armor/zzz_SJPTS/A.ini", "; SJPTS fullName |  | Fur Coat", Original);   // translation == original

        var c = Assert.Single(SkyPatcherScanner.Scan(Instance(["SJPTS Output", "Mod"])).Candidates);

        Assert.Equal("", c.DsdCoveredJapanese);
        Assert.Equal("", c.StaleOriginal);
    }

    [Fact]
    public void ThePreviousOutput_IsMatchedByTheOriginalsFilterAndOccurrence_NotByPosition()
    {
        // The original has the same filter twice; the output mirrors both, so occurrences line up.
        Add("Mod", "armor/A.ini",
            "filterByArmors=Skyrim.esm|1:fullName=~First~",
            "filterByArmors=Skyrim.esm|1:fullName=~Second~");
        Add("SJPTS Output", "armor/zzz_SJPTS/A.ini",
            "; SJPTS fullName | SJPTS_X | First", "filterByArmors=Skyrim.esm|1:fullName=~最初~",
            "; SJPTS fullName | SJPTS_Y | Second", "filterByArmors=Skyrim.esm|1:fullName=~二番目~");

        var rows = SkyPatcherScanner.Scan(Instance(["SJPTS Output", "Mod"])).Candidates;

        Assert.Equal(["最初", "二番目"], rows.Select(r => r.DsdCoveredJapanese).ToArray());
    }
}
