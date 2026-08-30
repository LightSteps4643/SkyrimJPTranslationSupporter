using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// Mo2InstanceReader resolves "which physical file does MO2's VFS actually
/// serve" for every active plugin, and separately merges loose-asset
/// directories the same way. Getting this wrong silently applies a
/// translation to the WRONG mod's copy of a file — an error with no visible
/// cause from the user's side.
///
/// Built as a single synthetic MO2 instance tree per test (via
/// <see cref="BuildInstance"/>), created fresh under the OS temp directory —
/// not a checked-in fixture, since ModOrganizer.ini's gamePath must be a real
/// absolute path resolvable at test time. Deleted in a finally block.
///
/// The one fixture covers every scenario together (mirrors the corpus-fixture
/// style used elsewhere in this suite): a plugin-priority conflict, an
/// overwrite-wins-over-everything conflict, a plugin that exists in only one
/// mod, a plugin active in plugins.txt whose only physical copy sits in a
/// DISABLED mod folder (a real, explicitly-handled MO2 edge case per the
/// class's own remarks), a plugin present on disk but never activated, and
/// the DLC/CC "implicit master" force-load path that a real v0.11.1 bug once
/// silently dropped (see DESIGN_NOTES.md).
/// </summary>
public class Mo2InstanceReaderTests
{
    private static string BuildInstance(string root)
    {
        var instanceDir = Path.Combine(root, "instance");
        var gameDir = Path.Combine(root, "game");
        var dataDir = Path.Combine(gameDir, "Data");
        var modsDir = Path.Combine(instanceDir, "mods");
        var overwriteDir = Path.Combine(instanceDir, "overwrite");
        var profileDir = Path.Combine(instanceDir, "profiles", "Default");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(overwriteDir);
        Directory.CreateDirectory(profileDir);

        // --- game/Data: implicit masters + one unlisted CC-style straggler ---
        File.WriteAllText(Path.Combine(dataDir, "Skyrim.esm"), "SKYRIM");
        File.WriteAllText(Path.Combine(dataDir, "Update.esm"), "UPDATE");
        File.WriteAllText(Path.Combine(dataDir, "Dawnguard.esm"), "DAWNGUARD"); // present on disk, deliberately NOT in plugins.txt
        // HearthFires.esm/Dragonborn.esm/CC entries deliberately absent — proves the "skip if missing" path doesn't crash.
        File.WriteAllText(Path.Combine(dataDir, "CustomCC.esl"), "CUSTOM-CC"); // not an implicit master, not in plugins.txt

        // --- mods/ ---
        var modHigh = Path.Combine(modsDir, "ModHigh");
        var modLow = Path.Combine(modsDir, "ModLow");
        var modDisabled = Path.Combine(modsDir, "ModDisabled");
        Directory.CreateDirectory(modHigh);
        Directory.CreateDirectory(modLow);
        Directory.CreateDirectory(modDisabled);

        File.WriteAllText(Path.Combine(modHigh, "Conflict.esp"), "HIGH");
        File.WriteAllText(Path.Combine(modLow, "Conflict.esp"), "LOW");
        File.WriteAllText(Path.Combine(modLow, "OverwriteWins.esp"), "MOD");
        File.WriteAllText(Path.Combine(modLow, "Solo.esp"), "SOLO");
        File.WriteAllText(Path.Combine(modDisabled, "ActiveButFromDisabledMod.esp"), "DISABLED-BUT-ACTIVE");
        File.WriteAllText(Path.Combine(modHigh, "NotActivated.esp"), "NEVER-ACTIVATED");

        Directory.CreateDirectory(Path.Combine(modHigh, "Meshes"));
        Directory.CreateDirectory(Path.Combine(modLow, "Meshes"));
        Directory.CreateDirectory(Path.Combine(modDisabled, "Meshes"));
        File.WriteAllText(Path.Combine(modHigh, "Meshes", "foo.nif"), "HIGH-MESH");
        File.WriteAllText(Path.Combine(modLow, "Meshes", "foo.nif"), "LOW-MESH"); // conflict: ModHigh must win
        File.WriteAllText(Path.Combine(modLow, "Meshes", "bar.nif"), "LOW-BAR"); // distinct path: must coexist
        File.WriteAllText(Path.Combine(modDisabled, "Meshes", "baz.nif"), "DISABLED-BAZ"); // disabled mod: must NOT appear in the VFS merge

        // --- overwrite/: always wins, even over the highest-priority mod ---
        File.WriteAllText(Path.Combine(overwriteDir, "OverwriteWins.esp"), "OVERWRITE");
        Directory.CreateDirectory(Path.Combine(overwriteDir, "Meshes"));
        File.WriteAllText(Path.Combine(overwriteDir, "Meshes", "foo.nif"), "OVERWRITE-MESH");

        // --- profiles/Default/modlist.txt: top = highest priority ---
        File.WriteAllLines(Path.Combine(profileDir, "modlist.txt"), new[]
        {
            "# comment line",
            "+ModHigh",
            "+ModLow",
            "-ModDisabled",
            "+SomeGroup_separator", // must be excluded regardless of its '+'/'-' state
        });

        // --- profiles/Default/plugins.txt: only '*'-prefixed lines are active ---
        File.WriteAllLines(Path.Combine(profileDir, "plugins.txt"), new[]
        {
            "# comment line",
            "*Conflict.esp",
            "*OverwriteWins.esp",
            "*Solo.esp",
            "*ActiveButFromDisabledMod.esp",
            "NotActivated.esp", // present but NOT starred -> inactive
        });

        // --- ModOrganizer.ini: gamePath/selected_profile wrapped in @ByteArray(...), as real MO2 writes them ---
        File.WriteAllLines(Path.Combine(instanceDir, "ModOrganizer.ini"), new[]
        {
            "[General]",
            $"gamePath=@ByteArray({gameDir.Replace('\\', '/')})",
            "selected_profile=@ByteArray(Default)",
        });

        return instanceDir;
    }

    [Fact]
    public void Read_ParsesGamePathAndProfileName_UnwrappingByteArray()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            // Written into the ini with forward slashes (matching real MO2's own style) — Read() doesn't normalize them.
            Assert.Equal(Path.Combine(root, "game").Replace('\\', '/'), instance.GamePath);
            Assert.Equal("Default", instance.ProfileName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Read_LoadOrder_ImplicitMastersFirstInFixedOrder_ThenUnlistedStraggler_ThenPluginsTxtOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            Assert.Equal(
                new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "CustomCC.esl", "Conflict.esp", "OverwriteWins.esp", "Solo.esp", "ActiveButFromDisabledMod.esp" },
                instance.LoadOrder.Select(p => p.FileName).ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Read_PluginListedButNeverStarredInPluginsTxt_NeverAppearsInLoadOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            Assert.DoesNotContain(instance.LoadOrder, p => p.FileName == "NotActivated.esp");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Read_HigherPriorityMod_WinsOverLowerPriorityModForSamePlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            var conflict = instance.LoadOrder.Single(p => p.FileName == "Conflict.esp");
            Assert.Equal("HIGH", File.ReadAllText(conflict.AbsolutePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Read_OverwriteFolder_WinsOverEvenTheHighestPriorityMod()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            var overwriteWins = instance.LoadOrder.Single(p => p.FileName == "OverwriteWins.esp");
            Assert.Equal("OVERWRITE", File.ReadAllText(overwriteWins.AbsolutePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Real MO2 edge case, explicitly handled per the class's own
    /// remarks: a plugin can be starred active in plugins.txt while its only
    /// physical copy sits in a mod folder the user has DISABLED (a common way
    /// to "force" a specific plugin active without enabling the whole mod's
    /// loose files). Path resolution must still find it.</summary>
    [Fact]
    public void Read_PluginActiveButOnlyPhysicallyPresentInADisabledMod_StillResolves()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            var resolved = instance.LoadOrder.Single(p => p.FileName == "ActiveButFromDisabledMod.esp");
            Assert.Equal("DISABLED-BUT-ACTIVE", File.ReadAllText(resolved.AbsolutePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Read_EnabledModPriorityHighFirst_ExcludesDisabledMods()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            Assert.Equal(new[] { "ModHigh", "ModLow" }, instance.EnabledModPriorityHighFirst);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void BuildVfsDirectoryMerge_OverwriteWinsOverMods_HigherPriorityModWinsOverLower_DistinctPathsCoexist_DisabledModExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var instance = Mo2InstanceReader.Read(instanceDir);

            var merged = Mo2InstanceReader.BuildVfsDirectoryMerge(instance, "Meshes");

            Assert.Equal("OVERWRITE-MESH", File.ReadAllText(merged["foo.nif"])); // overwrite beats every mod
            Assert.Equal("LOW-BAR", File.ReadAllText(merged["bar.nif"])); // distinct path, only ModLow has it
            Assert.False(merged.ContainsKey("baz.nif")); // ModDisabled's loose file must never be served
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void BuildVfsDirectoryMerge_WithoutOverwriteEntry_HigherPriorityModStillWinsConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            // Remove the overwrite copy so this isolates mod-vs-mod priority specifically,
            // independent of the (already-proven) overwrite-always-wins rule.
            File.Delete(Path.Combine(instanceDir, "overwrite", "Meshes", "foo.nif"));

            var instance = Mo2InstanceReader.Read(instanceDir);
            var merged = Mo2InstanceReader.BuildVfsDirectoryMerge(instance, "Meshes");

            Assert.Equal("HIGH-MESH", File.ReadAllText(merged["foo.nif"]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// v0.57.0: MO2's own "Paths" settings tab ([Settings] mod_directory etc. in
    /// ModOrganizer.ini) can redirect mods/profiles/overwrite away from the
    /// instance folder's default children — a case this reader deliberately
    /// doesn't parse from the ini (see the class doc comment), instead exposing
    /// three optional override parameters the caller supplies. This proves each
    /// override, independently, actually replaces (not supplements) its
    /// corresponding auto-derived default: relocate ONE of mods/profile/overwrite
    /// entirely outside the instance folder, leave the other two at their normal
    /// default location, and confirm Read() still resolves correctly when (and
    /// only when) the matching override is supplied.
    /// </summary>
    [Fact]
    public void Read_WithModsDirOverride_ResolvesPluginsFromRedirectedLocation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var redirectedModsDir = Path.Combine(root, "redirected_mods");
            Directory.Move(Path.Combine(instanceDir, "mods"), redirectedModsDir);

            // Without the override, the default <instanceDir>/mods no longer exists at
            // all -> Read() throws instead of silently resolving fewer plugins (v0.57.1:
            // a missing default is checked exactly like a missing explicit override,
            // never tolerated -- see Mo2InstanceConfigurationException's doc comment).
            Assert.Throws<Mo2InstanceConfigurationException>(() => Mo2InstanceReader.Read(instanceDir));

            var withOverride = Mo2InstanceReader.Read(instanceDir, modsDirOverride: redirectedModsDir);
            var solo = Assert.Single(withOverride.LoadOrder, p => p.FileName == "Solo.esp");
            Assert.Equal("SOLO", File.ReadAllText(solo.AbsolutePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Read_WithProfileDirOverride_UsesRedirectedProfileFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var redirectedProfileDir = Path.Combine(root, "redirected_profile");
            Directory.Move(Path.Combine(instanceDir, "profiles", "Default"), redirectedProfileDir);

            // Default <instanceDir>/profiles/Default no longer exists -> Read() without the override throws.
            // v0.57.1: now a clean Mo2InstanceConfigurationException (with a readable
            // message), not a raw DirectoryNotFoundException -- see that type's own
            // doc comment for why (a real user's unhandled-crash bug report).
            Assert.Throws<Mo2InstanceConfigurationException>(() => Mo2InstanceReader.Read(instanceDir));

            var withOverride = Mo2InstanceReader.Read(instanceDir, profileDirOverride: redirectedProfileDir);
            Assert.Contains(withOverride.LoadOrder, p => p.FileName == "Solo.esp");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Read_WithOverwriteDirOverride_RedirectedOverwriteStillWinsConflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var instanceDir = BuildInstance(root);
            var redirectedOverwriteDir = Path.Combine(root, "redirected_overwrite");
            Directory.Move(Path.Combine(instanceDir, "overwrite"), redirectedOverwriteDir);

            // Without the override, the default <instanceDir>/overwrite no longer exists
            // at all -> Read() throws (v0.57.1: same "missing default is an error too"
            // contract as the mods-dir case above).
            Assert.Throws<Mo2InstanceConfigurationException>(() => Mo2InstanceReader.Read(instanceDir));

            var withOverride = Mo2InstanceReader.Read(instanceDir, overwriteDirOverride: redirectedOverwriteDir);
            Assert.Equal("OVERWRITE", File.ReadAllText(
                withOverride.LoadOrder.Single(p => p.FileName == "OverwriteWins.esp").AbsolutePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
