using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// StringsStaging.Build merges the VFS-winning Strings/* files into one real
/// directory Mutagen can point at. Coverage showed the vanilla-game
/// Data/Strings loose-file FALLBACK path (used for any file no mod
/// overrides) was never exercised — every existing test that goes through
/// StringsStaging (indirectly, via PickUpTargetRunner.Run()) uses a fake MO2
/// instance whose gamePath never has a real Data/Strings folder.
///
/// Constructs a Mo2Instance directly (no ModOrganizer.ini/modlist.txt/
/// plugins.txt needed — Build() only reads instance.ModsDir/OverwriteDir/
/// EnabledModPriorityHighFirst/GamePath), which keeps this test far lighter
/// than going through Mo2InstanceReader.Read().
/// </summary>
public class StringsStagingTests
{
    [Fact]
    public void Build_VanillaLooseStringsFile_NoModOverridesIt_FallsBackToTheGamesOwnCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_stringsstaging_{Guid.NewGuid():N}");
        var gameDataStringsDir = Path.Combine(root, "game", "Data", "Strings");
        var modsDir = Path.Combine(root, "mods");
        Directory.CreateDirectory(gameDataStringsDir);
        Directory.CreateDirectory(modsDir);
        try
        {
            File.WriteAllText(Path.Combine(gameDataStringsDir, "Skyrim_Japanese.STRINGS"), "VANILLA-CONTENT");

            var instance = new Mo2Instance(
                GamePath: Path.Combine(root, "game"),
                ProfileName: "Default",
                ModsDir: modsDir,
                OverwriteDir: Path.Combine(root, "overwrite"),
                EnabledModPriorityHighFirst: Array.Empty<string>(), // no mods at all
                LoadOrder: Array.Empty<ResolvedPlugin>());

            var stagingDir = StringsStaging.Build(instance);
            try
            {
                var stagedFile = Path.Combine(stagingDir, "Skyrim_Japanese.STRINGS");
                Assert.True(File.Exists(stagedFile));
                Assert.Equal("VANILLA-CONTENT", File.ReadAllText(stagedFile));
            }
            finally
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A mod's own copy of a Strings/* file must win over the
    /// game's own loose copy — the vanilla fallback only fills in files
    /// nothing else already claimed.</summary>
    [Fact]
    public void Build_ModShipsTheSameFile_ModsCopyWinsOverTheVanillaFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_stringsstaging_{Guid.NewGuid():N}");
        var gameDataStringsDir = Path.Combine(root, "game", "Data", "Strings");
        var modsDir = Path.Combine(root, "mods");
        var modStringsDir = Path.Combine(modsDir, "TestMod", "Strings");
        Directory.CreateDirectory(gameDataStringsDir);
        Directory.CreateDirectory(modStringsDir);
        try
        {
            File.WriteAllText(Path.Combine(gameDataStringsDir, "Skyrim_Japanese.STRINGS"), "VANILLA-CONTENT");
            File.WriteAllText(Path.Combine(modStringsDir, "Skyrim_Japanese.STRINGS"), "MOD-CONTENT");

            var instance = new Mo2Instance(
                GamePath: Path.Combine(root, "game"),
                ProfileName: "Default",
                ModsDir: modsDir,
                OverwriteDir: Path.Combine(root, "overwrite"),
                EnabledModPriorityHighFirst: new[] { "TestMod" },
                LoadOrder: Array.Empty<ResolvedPlugin>());

            var stagingDir = StringsStaging.Build(instance);
            try
            {
                var stagedFile = Path.Combine(stagingDir, "Skyrim_Japanese.STRINGS");
                Assert.Equal("MOD-CONTENT", File.ReadAllText(stagedFile));
            }
            finally
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>No Data/Strings folder at all (a minimal/portable game
    /// install, or just a fixture that never set one up) must not throw —
    /// the fallback pass is skipped entirely.</summary>
    [Fact]
    public void Build_NoGameDataStringsFolderAtAll_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_stringsstaging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instance = new Mo2Instance(
                GamePath: AppContext.BaseDirectory,
                ProfileName: "Default",
                ModsDir: Path.Combine(root, "mods"),
                OverwriteDir: Path.Combine(root, "overwrite"),
                EnabledModPriorityHighFirst: Array.Empty<string>(),
                LoadOrder: Array.Empty<ResolvedPlugin>());

            var stagingDir = StringsStaging.Build(instance);
            try
            {
                Assert.True(Directory.Exists(stagingDir));
                Assert.Empty(Directory.EnumerateFiles(stagingDir));
            }
            finally
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
