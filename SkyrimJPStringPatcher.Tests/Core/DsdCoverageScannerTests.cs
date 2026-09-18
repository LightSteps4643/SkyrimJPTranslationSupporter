using Mutagen.Bethesda.Plugins;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// DsdCoverageScanner.Scan resolves what a load order's EXISTING DSD json
/// files already cover, respecting MO2's VFS priority (per the class's own
/// remarks: "if two mods both ship a DSD json at the exact same relative
/// path, only the higher-priority one's content is real"). The existing
/// PickUpTarget-level tests (DsdCoverageAndStaleTests/SpecialDsdMatchingTests)
/// only ever exercise a SINGLE mod's DSD json — the multi-mod VFS-priority
/// resolution documented here was never directly tested.
///
/// Scan() takes a Mo2Instance directly and needs no Mutagen record reading,
/// so these tests build a fake MO2 instance (like DsdCoverageAndStaleTests
/// does) and call Mo2InstanceReader.Read() + DsdCoverageScanner.Scan()
/// directly — no ESP fixtures, no PickUpTargetRunner, no RunLog needed.
/// JSON fixtures live under Fixtures/Core/DsdCoverageScanner/.
/// </summary>
public class DsdCoverageScannerTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modHighDir = Path.Combine(mo2Dir, "mods", "ModHigh");
        var modLowDir = Path.Combine(mo2Dir, "mods", "ModLow");
        var modBrokenDir = Path.Combine(mo2Dir, "mods", "ModBroken");
        var modZDir = Path.Combine(mo2Dir, "mods", "ModZ");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");

        var dsdHighDir = Path.Combine(modHighDir, "SKSE", "Plugins", "DynamicStringDistributor", "TestMod.esp");
        var dsdLowDir = Path.Combine(modLowDir, "SKSE", "Plugins", "DynamicStringDistributor", "TestMod.esp");
        var dsdBrokenDir = Path.Combine(modBrokenDir, "SKSE", "Plugins", "DynamicStringDistributor", "TestMod.esp");
        var dsdInactiveDir = Path.Combine(modZDir, "SKSE", "Plugins", "DynamicStringDistributor", "NeverActivated.esp");
        Directory.CreateDirectory(dsdHighDir);
        Directory.CreateDirectory(dsdLowDir);
        Directory.CreateDirectory(dsdBrokenDir);
        Directory.CreateDirectory(dsdInactiveDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        // A dummy (empty) plugin file — only its NAME matters for Mo2InstanceReader
        // to resolve "TestMod.esp" as an active plugin; no Mutagen content needed.
        File.WriteAllText(Path.Combine(modHighDir, "TestMod.esp"), "");

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Core", "DsdCoverageScanner");
        File.Copy(Path.Combine(fixturesDir, "patch_high.json"), Path.Combine(dsdHighDir, "patch.json"));
        File.Copy(Path.Combine(fixturesDir, "patch_low.json"), Path.Combine(dsdLowDir, "patch.json"));
        File.Copy(Path.Combine(fixturesDir, "gmst_patch.json"), Path.Combine(dsdHighDir, "gmst_patch.json"));
        File.Copy(Path.Combine(fixturesDir, "broken.json"), Path.Combine(dsdBrokenDir, "broken.json"));
        File.Copy(Path.Combine(fixturesDir, "inactive_patch.json"), Path.Combine(dsdInactiveDir, "patch.json"));

        // 2026-09-18 real-data finding (VioLens/USSEP+Oblivion Interaction Icons
        // collision): two DIFFERENTLY-NAMED files within the SAME gating folder
        // ("TestMod.esp") both target FormID 000850 — DSD's real tie-break here
        // is purely the filename itself (Z-first alphabetical descending),
        // independent of MO2's own modlist.txt priority. ModLow (lower MO2
        // priority) ships the "z"-named file to prove MO2 priority is NOT what
        // decides this.
        File.Copy(Path.Combine(fixturesDir, "filename_priority_a.json"), Path.Combine(dsdHighDir, "filename_priority_a.json"));
        File.Copy(Path.Combine(fixturesDir, "filename_priority_z.json"), Path.Combine(dsdLowDir, "filename_priority_z.json"));

        // Two DIFFERENT gating folders (different plugin names entirely) both
        // target FormID 000860 — DSD's real tie-break here is the LOAD ORDER of
        // the plugin the gating FOLDER is named after (later-loading plugin's
        // folder is processed first and wins), not the filename and not MO2's
        // modlist.txt priority. The "late load" folder's file is named to sort
        // alphabetically BEFORE the "early load" folder's file, specifically to
        // prove filename comparison across folders is not what decides this.
        var modGateEarlyDir = Path.Combine(mo2Dir, "mods", "ModGateEarly");
        var modGateLateDir = Path.Combine(mo2Dir, "mods", "ModGateLate");
        var dsdGateEarlyDir = Path.Combine(modGateEarlyDir, "SKSE", "Plugins", "DynamicStringDistributor", "GateEarly.esp");
        var dsdGateLateDir = Path.Combine(modGateLateDir, "SKSE", "Plugins", "DynamicStringDistributor", "GateLate.esp");
        Directory.CreateDirectory(dsdGateEarlyDir);
        Directory.CreateDirectory(dsdGateLateDir);
        File.WriteAllText(Path.Combine(modGateEarlyDir, "GateEarly.esp"), "");
        File.WriteAllText(Path.Combine(modGateLateDir, "GateLate.esp"), "");
        File.Copy(Path.Combine(fixturesDir, "gating_folder_early_load.json"), Path.Combine(dsdGateEarlyDir, "zzz_file.json"));
        File.Copy(Path.Combine(fixturesDir, "gating_folder_late_load.json"), Path.Combine(dsdGateLateDir, "aaa_file.json"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        // ModHigh listed first (= highest priority).
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+ModHigh\r\n+ModLow\r\n+ModBroken\r\n+ModZ\r\n+ModGateEarly\r\n+ModGateLate\r\n");
        // Only TestMod.esp is active — NeverActivated.esp is never listed at all.
        // GateEarly.esp loads BEFORE GateLate.esp (lower load-order index) —
        // per DSD's real rule, the LATER-loading plugin's gating folder should win.
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*TestMod.esp\r\n*GateEarly.esp\r\n*GateLate.esp\r\n");

        return mo2Dir;
    }

    private static DsdCoverageIndex ScanFixture(string root)
    {
        var mo2Dir = BuildFakeMo2Instance(root);
        var instance = Mo2InstanceReader.Read(mo2Dir);
        return DsdCoverageScanner.Scan(instance);
    }

    [Fact]
    public void Scan_TwoModsShipDsdAtTheSameRelativePath_HigherPriorityModsContentWins()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdscanner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var coverage = ScanFixture(root);

            var formKey = FormKey.Factory("000800:TestMod.esp");
            var entry = coverage.ByFormTypeIndex[(formKey, "WEAP FULL", 0)];

            Assert.Equal("高優先度の翻訳", entry.TranslatedString);
            Assert.Equal("High Priority Original", entry.OriginalRecorded);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>DSD only reads a plugin-named folder if that PLUGIN is
    /// active in plugins.txt — a DSD json sitting under a gating folder for
    /// a plugin that was never activated (even though the MOD shipping it
    /// is itself enabled) must be invisible.</summary>
    [Fact]
    public void Scan_DsdJsonUnderAnInactivePluginsGatingFolder_IsIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdscanner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var coverage = ScanFixture(root);

            var formKey = FormKey.Factory("000900:NeverActivated.esp");
            Assert.False(coverage.ByFormTypeIndex.ContainsKey((formKey, "WEAP FULL", 0)));
            Assert.DoesNotContain(coverage.ByFormTypeIndex.Values, e => e.TranslatedString == "幽霊の翻訳");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A malformed DSD json from one mod must not crash the whole
    /// scan, and must not prevent OTHER (validly-formed) DSD json files —
    /// even ones sharing the same gating folder — from being read.</summary>
    [Fact]
    public void Scan_OneModsMalformedDsdJson_DoesNotBlockOtherValidFilesInTheSameGatingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdscanner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var coverage = ScanFixture(root);

            var formKey = FormKey.Factory("000800:TestMod.esp");
            Assert.True(coverage.ByFormTypeIndex.ContainsKey((formKey, "WEAP FULL", 0)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-18 real-data finding: within the SAME gating folder,
    /// DSD's real tie-break for colliding (FormID, Type, Index) entries is the
    /// filename itself, sorted alphabetically descending (Z first, wins) —
    /// completely independent of MO2's own modlist.txt mod-priority order
    /// (verified against DSD's actual source, Manager.cpp's processFiles:
    /// `std::ranges::sort(files, std::greater<>{})`). ModLow (the LOWER MO2
    /// priority mod) ships the "z"-named file specifically to prove MO2
    /// priority is not what decides the winner here.</summary>
    [Fact]
    public void Scan_TwoFilesInSameGatingFolder_ZNamedFileWinsRegardlessOfMo2Priority()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdscanner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var coverage = ScanFixture(root);

            var formKey = FormKey.Factory("000850:TestMod.esp");
            var entry = coverage.ByFormTypeIndex[(formKey, "WEAP FULL", 0)];

            Assert.Equal("zで始まるファイルの訳", entry.TranslatedString);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-18 real-data finding (VioLens/USSEP vs. Oblivion
    /// Interaction Icons colliding on the exact same FormID+Type+Index via two
    /// entirely unrelated gating-plugin folders): DSD resolves a collision
    /// across DIFFERENT gating folders by the LOAD ORDER of the plugin each
    /// folder is named after — the folder for the LATER-loading plugin is
    /// processed first and wins (Manager.cpp's processFolders: descending by
    /// each plugin's own load-order index). This is independent of filename
    /// (the losing folder's file is named "zzz_..." — alphabetically it would
    /// "win" a naive cross-folder filename comparison) and independent of MO2's
    /// modlist.txt mod priority (which never enters into it at all, since the
    /// two files don't share a relative path).</summary>
    [Fact]
    public void Scan_TwoDifferentGatingFoldersCollideOnSameFormId_LaterLoadingGatingPluginWins()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdscanner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var coverage = ScanFixture(root);

            var formKey = FormKey.Factory("000860:TestMod.esp");
            var entry = coverage.ByFormTypeIndex[(formKey, "WEAP FULL", 0)];

            Assert.Equal("後にロードされるゲートフォルダの訳", entry.TranslatedString);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Scan_GmstEntry_IsIndexedByBothEditorIdAndFormId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdscanner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var coverage = ScanFixture(root);

            var byEditorId = coverage.ByEditorId["GMST DATA|sTestGmstSetting"];
            Assert.Equal("設定のテキスト", byEditorId.TranslatedString);

            var formKey = FormKey.Factory("000801:TestMod.esp");
            var byFormId = coverage.ByFormTypeIndex[(formKey, "GMST DATA", 0)];
            Assert.Equal("設定のテキスト", byFormId.TranslatedString);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
