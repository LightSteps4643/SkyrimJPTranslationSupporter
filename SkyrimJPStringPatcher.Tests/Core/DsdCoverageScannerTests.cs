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

        // A dummy (empty) plugin file — only its NAME matters for Mo2InstanceReader
        // to resolve "TestMod.esp" as an active plugin; no Mutagen content needed.
        File.WriteAllText(Path.Combine(modHighDir, "TestMod.esp"), "");

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Core", "DsdCoverageScanner");
        File.Copy(Path.Combine(fixturesDir, "patch_high.json"), Path.Combine(dsdHighDir, "patch.json"));
        File.Copy(Path.Combine(fixturesDir, "patch_low.json"), Path.Combine(dsdLowDir, "patch.json"));
        File.Copy(Path.Combine(fixturesDir, "gmst_patch.json"), Path.Combine(dsdHighDir, "gmst_patch.json"));
        File.Copy(Path.Combine(fixturesDir, "broken.json"), Path.Combine(dsdBrokenDir, "broken.json"));
        File.Copy(Path.Combine(fixturesDir, "inactive_patch.json"), Path.Combine(dsdInactiveDir, "patch.json"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        // ModHigh listed first (= highest priority).
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+ModHigh\r\n+ModLow\r\n+ModBroken\r\n+ModZ\r\n");
        // Only TestMod.esp is active — NeverActivated.esp is never listed at all.
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*TestMod.esp\r\n");

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
