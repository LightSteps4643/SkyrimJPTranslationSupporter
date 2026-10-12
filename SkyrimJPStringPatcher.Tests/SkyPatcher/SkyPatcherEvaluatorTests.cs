using SkyrimJPStringPatcher.SkyPatcher;

namespace SkyrimJPStringPatcher.Tests.SkyPatcher;

/// <summary>
/// Requirements for deciding which name-setting lines of one SkyPatcher .ini can actually take
/// effect (grammar §2 file/line gates and conflict resolution, §4 addressing, §6 hasPlugins).
/// Every line gets one entry per name operation, each either active ("" ) or inactive with a
/// reason. The principle behind every judgement: when we cannot be sure a line is ineffective,
/// it stays ACTIVE — a wrongly "inactive" line would be left untranslated and show English in
/// game, while a wrongly "active" one only costs a needless translation.
/// </summary>
public class SkyPatcherEvaluatorTests
{
    private static SkyPatcherContext Context(string[] activePlugins, string[]? settingsLines = null) =>
        new(activePlugins, settingsLines == null ? SkyPatcherSettings.Default : SkyPatcherSettings.Parse(settingsLines));

    private static readonly string[] Masters = ["Skyrim.esm", "Update.esm"];

    private static List<SkyPatcherEntry> Evaluate(string path, string[] lines, SkyPatcherContext? context = null) =>
        SkyPatcherEvaluator.Evaluate(path, lines, context ?? Context(Masters));

    // ---- what an entry says ------------------------------------------------

    [Fact]
    public void AnEntry_CarriesWhereItCameFromAndWhatItIs()
    {
        var entry = Assert.Single(Evaluate("armor/Mark Unique Items.ini",
            ["; header", "filterByArmors=Skyrim.esm|15516:fullName=~* Emperor's Robes~"]));

        Assert.Equal("armor/Mark Unique Items.ini", entry.SourcePath);
        Assert.Equal(2, entry.LineNumber);                       // 1-based, comment lines count
        Assert.Equal("filterByArmors=Skyrim.esm|15516", entry.FilterPart);
        Assert.Equal("ARMO FULL", entry.DsdType);
        Assert.Equal("* Emperor's Robes", entry.Text);
        Assert.Equal("", entry.Inactive);
    }

    [Fact]
    public void ANpcLineWithBothNames_GivesOneEntryEach_WithTheirOwnDsdTypes_AndOneSharedRowKey()
    {
        var entries = Evaluate("npc/Mod.ini", ["filterByNpcs=Skyrim.esm|7:fullName=~Alpha~:shortName=~Beta~"]);

        Assert.Equal(["NPC_ FULL", "NPC_ SHRT"], entries.Select(e => e.DsdType).ToArray());
        Assert.Single(entries.Select(e => e.RowKey).Distinct());
    }

    [Fact]
    public void ALineOfARecordTypeWithoutDisplayNames_YieldsNothing()
    {
        Assert.Empty(Evaluate("race/Mod.ini", ["filterByRaces=Skyrim.esm|13740:fullName=~Nord~"]));
    }

    [Fact]
    public void RowKey_IsPathFilterAndOccurrence_AndOccurrenceCountsLinesWithTheSameFilterInTheFile()
    {
        var entries = Evaluate("armor/A.ini",
        [
            "filterByArmors=X.esp|1:fullName=~One~",
            "filterByArmors=X.esp|2:fullName=~Other~",
            "filterByArmors=X.esp|1:fullName=~Again~",
        ]);

        Assert.Equal(["armor/A.ini|filterByArmors=X.esp|1|1", "armor/A.ini|filterByArmors=X.esp|2|1", "armor/A.ini|filterByArmors=X.esp|1|2"],
            entries.Select(e => e.RowKey).ToArray());
        // Another file's identical filter starts counting again; the path keeps the keys apart.
        Assert.Equal("armor/B.ini|filterByArmors=X.esp|1|1", Evaluate("armor/B.ini", ["filterByArmors=X.esp|1:fullName=~One~"])[0].RowKey);
    }

    // ---- file-level gates ----------------------------------------------------

    [Fact]
    public void ADisabledType_MakesEveryLineOfItsSubfolderInactive()
    {
        var context = Context(Masters, ["[Patcher]", "iEnableArmorPatching=0"]);

        var entry = Assert.Single(Evaluate("armor/A.ini", ["filterByArmors=X.esp|1:fullName=~One~"], context));

        Assert.Equal(SkyPatcherInactive.TypeDisabled, entry.Inactive);
    }

    [Theory]
    [InlineData("armor/Sentinel/Sentinel.esp.ini", "Sentinel.esp", true, "")]
    [InlineData("armor/Sentinel/Sentinel.esp.ini", "Sentinel.esp", false, SkyPatcherInactive.FileGate)]
    [InlineData("armor/Foo.ESM.ini", "foo.esm", true, "")]                       // plugin names compare case-insensitively
    [InlineData("armor/Foo.esl.ini", "Other.esp", false, SkyPatcherInactive.FileGate)]
    [InlineData("armor/Mark Unique Items.ini", "Anything.esp", false, "")]    // not a plugin filename: always loaded
    [InlineData("armor/Foo.esp.bak.ini", "Foo.esp", false, "")]               // the name must END in the plugin filename + .ini
    public void FileNamedAfterAPlugin_IsReadOnlyWhenThatPluginIsActive(string path, string pluginName, bool pluginActive, string expectedInactive)
    {
        var active = pluginActive ? Masters.Append(pluginName).ToArray() : Masters;

        var entry = Assert.Single(Evaluate(path, ["filterByArmors=Skyrim.esm|15516:fullName=~X~"], Context(active)));

        Assert.Equal(expectedInactive, entry.Inactive);
    }

    // ---- line gates (hasPlugins / hasPluginsOr) -----------------------------

    [Theory]
    [InlineData("hasPlugins=A.esp,B.esp", new[] { "A.esp", "B.esp" }, "")]                               // AND: all present
    [InlineData("hasPlugins=A.esp,B.esp", new[] { "A.esp" }, SkyPatcherInactive.LineGate)]               // AND: one missing
    [InlineData("hasPluginsOr=A.esp,B.esp", new[] { "B.esp" }, "")]                                       // OR: any present
    [InlineData("hasPluginsOr=A.esp,B.esp", new string[0], SkyPatcherInactive.LineGate)]                 // OR: none present
    [InlineData("hasPlugins=a.ESP", new[] { "A.esp" }, "")]                                               // case-insensitive
    public void HasPlugins_GatesTheLine(string gate, string[] extraActive, string expectedInactive)
    {
        var entry = Assert.Single(Evaluate("armor/A.ini", [$"filterByArmors=Skyrim.esm|15516:{gate}:fullName=~X~"], Context(Masters.Concat(extraActive).ToArray())));

        Assert.Equal(expectedInactive, entry.Inactive);
    }

    [Fact]
    public void BothGateFamiliesOnOneLine_MustBothPass()
    {
        // Grammar §5: every filter family present on the line must pass.
        var entry = Assert.Single(Evaluate("armor/A.ini",
            ["filterByArmors=Skyrim.esm|15516:hasPlugins=A.esp:hasPluginsOr=B.esp,C.esp:fullName=~X~"], Context([.. Masters, "A.esp"])));

        Assert.Equal(SkyPatcherInactive.LineGate, entry.Inactive);
    }

    // ---- targets in plugins that are not loaded -----------------------------

    [Theory]
    [InlineData("filterByArmors=Gone.esp|800", SkyPatcherInactive.TargetPluginMissing)]                   // the form cannot be resolved: SkyPatcher skips it silently
    [InlineData("filterByArmors=Gone.esp|800,Skyrim.esm|15516", "")]                                       // one target still exists
    [InlineData("filterByArmors=Gone.esp|800,ArmorIronCuirass", "")]                                       // an EditorID may resolve: never judged
    [InlineData("filterByArmors=ArmorIronCuirass", "")]
    [InlineData("filterByKeywords=ArmorHeavy", "")]                                                        // no primary target at all
    public void AnEntryWhoseEveryFormIdTargetIsInAnUnloadedPlugin_IsInactive(string filter, string expectedInactive)
    {
        var entry = Assert.Single(Evaluate("armor/A.ini", [$"{filter}:fullName=~X~"]));

        Assert.Equal(expectedInactive, entry.Inactive);
    }

    // ---- overridden by a later line of the same file ------------------------

    [Fact]
    public void LaterLineSettingTheSameRecordsName_OverridesTheEarlierOne()
    {
        // The real shape of ELLE's Devil Parade .ini: the same record named repeatedly, the last one wins.
        var entries = Evaluate("armor/Devil.ini",
        [
            "filterByArmors=Devil.esp|D6A:fullName=~Gold Boots~",
            "filterByArmors=Devil.esp|D6A:fullName=~Silver Boots~",
            "filterByArmors=Devil.esp|D6A:fullName=~Devil Parade Wings~",
        ], Context([.. Masters, "Devil.esp"]));

        Assert.Equal([SkyPatcherInactive.OverriddenInSameFile, SkyPatcherInactive.OverriddenInSameFile, ""], entries.Select(e => e.Inactive).ToArray());
    }

    [Theory]
    [InlineData("Skyrim.esm|15516", "skyrim.esm|0x015516")]     // case, a 0x prefix and leading zeros do not make a different record
    [InlineData("Skyrim.esm|15516", "Skyrim.esm|00015516")]
    public void TheSameFormWrittenDifferently_IsTheSameTarget(string first, string second)
    {
        var entries = Evaluate("armor/A.ini", [$"filterByArmors={first}:fullName=~One~", $"filterByArmors={second}:fullName=~Two~"]);

        Assert.Equal(SkyPatcherInactive.OverriddenInSameFile, entries[0].Inactive);
        Assert.Equal("", entries[1].Inactive);
    }

    [Fact]
    public void ALaterLineThatCoversAllOfTheEarlierOnesTargets_OverridesIt_ButNotTheOtherWayRound()
    {
        var covers = Evaluate("armor/A.ini",
            ["filterByArmors=Skyrim.esm|1:fullName=~One~", "filterByArmors=Skyrim.esm|1,Skyrim.esm|2:fullName=~Two~"]);
        Assert.Equal(SkyPatcherInactive.OverriddenInSameFile, covers[0].Inactive);
        Assert.Equal("", covers[1].Inactive);

        var coveredOnly = Evaluate("armor/A.ini",
            ["filterByArmors=Skyrim.esm|1,Skyrim.esm|2:fullName=~One~", "filterByArmors=Skyrim.esm|1:fullName=~Two~"]);
        Assert.Equal("", coveredOnly[0].Inactive);   // record 2 still takes its name from line 1
        Assert.Equal("", coveredOnly[1].Inactive);
    }

    [Fact]
    public void FullNameAndShortName_AreSeparateFields_ANewShortNameDoesNotOverrideAFullName()
    {
        var entries = Evaluate("npc/A.ini",
            ["filterByNpcs=Skyrim.esm|7:fullName=~Alpha~", "filterByNpcs=Skyrim.esm|7:shortName=~Beta~"]);

        Assert.All(entries, e => Assert.Equal("", e.Inactive));
    }

    [Theory]
    [InlineData("filterByArmors=Skyrim.esm|1", "filterByArmors=ArmorIronCuirass")]                  // FormID vs EditorID: never unified
    [InlineData("filterByArmors=Skyrim.esm|1:filterByKeywords=K", "filterByArmors=Skyrim.esm|1")]   // the later line has fewer conditions
    [InlineData("filterByArmors=Skyrim.esm|1", "filterByArmors=Skyrim.esm|1:filterByKeywords=K")]   // the later line has more conditions
    [InlineData("filterByArmors=Skyrim.esm|1", "filterByArmors=Skyrim.esm|2")]                      // different record
    [InlineData("filterByKeywords=K", "filterByKeywords=K")]                                          // no primary target: cannot tell which records
    public void WhenTheTwoLinesAreNotProvablyAboutTheSameRecords_NeitherOverridesTheOther(string firstFilter, string secondFilter)
    {
        var entries = Evaluate("armor/A.ini", [$"{firstFilter}:fullName=~One~", $"{secondFilter}:fullName=~Two~"]);

        Assert.All(entries, e => Assert.Equal("", e.Inactive));
    }

    [Fact]
    public void TheSameExtraConditionsOnBothLines_StillCountAsTheSameRecords()
    {
        var entries = Evaluate("armor/A.ini",
            ["filterByArmors=Skyrim.esm|1:filterByKeywords=K:fullName=~One~", "filterByArmors=Skyrim.esm|1:filterByKeywords=K:fullName=~Two~"]);

        Assert.Equal(SkyPatcherInactive.OverriddenInSameFile, entries[0].Inactive);
    }

    [Fact]
    public void ALaterLineThatCannotApply_DoesNotOverrideAnEarlierOne()
    {
        // The second line is gated off (hasPlugins), so SkyPatcher never runs it: the first one still wins.
        var entries = Evaluate("armor/A.ini",
        [
            "filterByArmors=Skyrim.esm|1:fullName=~One~",
            "filterByArmors=Skyrim.esm|1:hasPlugins=Missing.esp:fullName=~Two~",
        ]);

        Assert.Equal("", entries[0].Inactive);
        Assert.Equal(SkyPatcherInactive.LineGate, entries[1].Inactive);
    }

    [Fact]
    public void OverridesAreNeverJudgedAcrossFiles()
    {
        // Each file is evaluated on its own: how SkyPatcher orders different files is not settled
        // precisely enough to call a line overridden.
        var first = Evaluate("armor/A.ini", ["filterByArmors=Skyrim.esm|1:fullName=~One~"]);
        var second = Evaluate("armor/B.ini", ["filterByArmors=Skyrim.esm|1:fullName=~Two~"]);

        Assert.Equal("", Assert.Single(first).Inactive);
        Assert.Equal("", Assert.Single(second).Inactive);
    }
}
