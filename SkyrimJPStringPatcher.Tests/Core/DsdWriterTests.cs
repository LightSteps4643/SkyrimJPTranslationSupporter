using System.Text.Json;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// DsdWriter is the final artifact writer — its output is what the player's
/// game actually reads. Its two most consequential behaviors (per its own
/// remarks) are indirectly exercised by GenerateDsdFile/DsdJsonGeneratorTests
/// (which reads the JSON back out through DsdJsonGenerator.Run), but two
/// specific behaviors were never directly asserted anywhere: wiping the
/// output root before every run (so a stale file from a previous run can't
/// linger), and skipping a plugin folder entirely when it has zero entries.
/// </summary>
public class DsdWriterTests
{
    // v0.57.4: the output filename now stamps in the run timestamp (see
    // DsdWriter.cs's own remarks). Tests pass this fixed value so the
    // filename stays deterministic.
    private static readonly DateTime TestTimestamp = new(2026, 1, 1, 0, 0, 0);

    private static string ExpectedJsonPath(string outputRoot, string plugin) =>
        Path.Combine(outputRoot, "SKSE", "Plugins", "DynamicStringDistributor", plugin, "SkyrimJPStringPatcher_20260101000000.json");

    [Fact]
    public void WriteAll_OneEntry_WritesExpectedJsonAtThePluginSpecificPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdwriter_{Guid.NewGuid():N}");
        try
        {
            var entries = new Dictionary<string, List<DsdEntry>>
            {
                ["TestMod.esp"] = new()
                {
                    new DsdEntry { EditorId = "", FormId = "0x001~TestMod.esp", Index = 0, Type = "WEAP FULL", Original = "Steel Sword", String = "鋼の剣", Status = "Completed" },
                },
            };

            DsdWriter.WriteAll(root, entries, timestamp: TestTimestamp);

            var jsonPath = ExpectedJsonPath(root, "TestMod.esp");
            Assert.True(File.Exists(jsonPath));

            var written = JsonSerializer.Deserialize<List<DsdEntry>>(File.ReadAllText(jsonPath))!;
            var entry = Assert.Single(written);
            Assert.Equal("Steel Sword", entry.Original);
            Assert.Equal("鋼の剣", entry.String);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Japanese text must be written as literal UTF-8 characters, not
    /// \uXXXX-escaped — the JsonSerializerOptions explicitly opts into
    /// UnsafeRelaxedJsonEscaping for this. A regression here wouldn't break
    /// DSD (which can read \u-escapes fine), but would make the output file
    /// unreadable to a human reviewing it.</summary>
    [Fact]
    public void WriteAll_JapaneseText_IsNotUnicodeEscapedInTheRawFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdwriter_{Guid.NewGuid():N}");
        try
        {
            var entries = new Dictionary<string, List<DsdEntry>>
            {
                ["TestMod.esp"] = new() { new DsdEntry { FormId = "0x001~TestMod.esp", Type = "WEAP FULL", Original = "Steel Sword", String = "鋼の剣" } },
            };

            DsdWriter.WriteAll(root, entries, timestamp: TestTimestamp);

            var raw = File.ReadAllText(ExpectedJsonPath(root, "TestMod.esp"));
            Assert.Contains("鋼の剣", raw);
            Assert.DoesNotContain("\\u", raw);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A plugin whose entries list is empty must not get a folder at
    /// all — an empty DSD json is pointless output clutter, and the class's
    /// own code special-cases this with an explicit `if (entries.Count == 0)
    /// continue;`.</summary>
    [Fact]
    public void WriteAll_PluginWithZeroEntries_GetsNoFolderAtAll()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdwriter_{Guid.NewGuid():N}");
        try
        {
            var entries = new Dictionary<string, List<DsdEntry>>
            {
                ["EmptyMod.esp"] = new(),
                ["RealMod.esp"] = new() { new DsdEntry { FormId = "0x001~RealMod.esp", Type = "WEAP FULL", Original = "Steel Sword", String = "鋼の剣" } },
            };

            DsdWriter.WriteAll(root, entries, timestamp: TestTimestamp);

            Assert.False(Directory.Exists(Path.Combine(root, "SKSE", "Plugins", "DynamicStringDistributor", "EmptyMod.esp")));
            Assert.True(File.Exists(ExpectedJsonPath(root, "RealMod.esp")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Every run must produce a clean, reproducible result — a stale
    /// file left over from an earlier run (e.g. for a plugin no longer in this
    /// run's winning set at all) must not survive into the new output.</summary>
    [Fact]
    public void WriteAll_WipesPreexistingOutputRoot_StalePluginFromAnOlderRunDisappears()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdwriter_{Guid.NewGuid():N}");
        try
        {
            // Simulate a previous run's leftover output for a plugin that is NOT part of this run at all.
            var staleDir = Path.Combine(root, "SKSE", "Plugins", "DynamicStringDistributor", "NoLongerInstalled.esp");
            Directory.CreateDirectory(staleDir);
            File.WriteAllText(Path.Combine(staleDir, "SkyrimJPStringPatcher.json"), "[]");
            // Also plant an unrelated file directly under outputRoot to confirm the WHOLE tree is wiped, not just the DSD subtree.
            File.WriteAllText(Path.Combine(root, "leftover_marker.txt"), "should be gone");

            var entries = new Dictionary<string, List<DsdEntry>>
            {
                ["CurrentMod.esp"] = new() { new DsdEntry { FormId = "0x001~CurrentMod.esp", Type = "WEAP FULL", Original = "Steel Sword", String = "鋼の剣" } },
            };

            DsdWriter.WriteAll(root, entries, timestamp: TestTimestamp);

            Assert.False(Directory.Exists(staleDir));
            Assert.False(File.Exists(Path.Combine(root, "leftover_marker.txt")));
            Assert.True(File.Exists(ExpectedJsonPath(root, "CurrentMod.esp")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteAll_MultiplePlugins_EachGetsItsOwnFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdwriter_{Guid.NewGuid():N}");
        try
        {
            var entries = new Dictionary<string, List<DsdEntry>>
            {
                ["ModA.esp"] = new() { new DsdEntry { FormId = "0x001~ModA.esp", Type = "WEAP FULL", Original = "Sword A", String = "剣A" } },
                ["ModB.esp"] = new() { new DsdEntry { FormId = "0x001~ModB.esp", Type = "WEAP FULL", Original = "Sword B", String = "剣B" } },
            };

            DsdWriter.WriteAll(root, entries, timestamp: TestTimestamp);

            Assert.True(File.Exists(ExpectedJsonPath(root, "ModA.esp")));
            Assert.True(File.Exists(ExpectedJsonPath(root, "ModB.esp")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Black-box test for the actual real-world scenario that motivated the
    /// timestamped filename (v0.57.4, from a user walkthrough): translate 90/100
    /// of a plugin's candidates now, install as its own mod; later translate the
    /// remaining 10 (PickUpTarget's own DSD-coverage scan means round 2's own
    /// output only ever contains the 10 new ones — the 90 are never re-candidated),
    /// and install THAT output too. Both installs land in the identical MO2-VFS
    /// plugin folder (SKSE/Plugins/DynamicStringDistributor/&lt;plugin&gt;/) — this
    /// simulates that final combined folder directly (copying both runs' own
    /// output files into one directory, exactly what installing both mods into
    /// MO2 produces) and asserts the specific guarantees that matter:
    /// 1. The two runs' files do NOT have the same name (the actual root cause
    ///    of the old bug — a fixed name meant the second file replaced, rather
    ///    than joined, the first).
    /// 2. Once combined, entries from BOTH runs are present, undamaged — i.e.
    ///    installing the later batch does not erase the earlier one.
    /// This does not (and cannot, without the real SKSE plugin) verify DSD's own
    /// runtime merge — that behavior (any *.json in the folder is read and
    /// merged) is corroborated separately via Manager.cpp's own source, see
    /// DsdWriter.cs's remarks — this test verifies OUR side of the contract: that
    /// we hand DSD two files it CAN merge, not one file that already ate the other.
    /// </summary>
    [Fact]
    public void WriteAll_TwoRunsAtDifferentTimestamps_BothOutputsSurviveTogetherWhenInstalledIntoTheSamePluginFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdwriter_{Guid.NewGuid():N}");
        try
        {
            var round1Root = Path.Combine(root, "round1_out"); // e.g. "MOD1_translated"'s own staged output
            var round2Root = Path.Combine(root, "round2_out"); // e.g. a later "MOD1_translated_more"'s staged output
            var round1Timestamp = new DateTime(2026, 1, 1, 9, 0, 0); // first translation pass
            var round2Timestamp = new DateTime(2026, 3, 15, 14, 30, 0); // later pass, translating the rest

            DsdWriter.WriteAll(round1Root, new Dictionary<string, List<DsdEntry>>
            {
                ["MOD1.esp"] = new() { new DsdEntry { FormId = "0x001~MOD1.esp", Type = "WEAP FULL", Original = "Steel Sword", String = "鋼の剣" } },
            }, timestamp: round1Timestamp);

            DsdWriter.WriteAll(round2Root, new Dictionary<string, List<DsdEntry>>
            {
                ["MOD1.esp"] = new() { new DsdEntry { FormId = "0x002~MOD1.esp", Type = "WEAP FULL", Original = "Iron Dagger", String = "鉄の短剣" } },
            }, timestamp: round2Timestamp);

            var round1PluginDir = Path.Combine(round1Root, "SKSE", "Plugins", "DynamicStringDistributor", "MOD1.esp");
            var round2PluginDir = Path.Combine(round2Root, "SKSE", "Plugins", "DynamicStringDistributor", "MOD1.esp");
            var round1File = Directory.GetFiles(round1PluginDir).Single();
            var round2File = Directory.GetFiles(round2PluginDir).Single();

            // Guarantee 1: different names -- this is the entire point.
            Assert.NotEqual(Path.GetFileName(round1File), Path.GetFileName(round2File));

            // Simulate MO2 presenting both mods' contributions to the SAME plugin
            // folder at once (what actually happens once both are installed).
            var combinedPluginDir = Path.Combine(root, "combined_mo2_view", "MOD1.esp");
            Directory.CreateDirectory(combinedPluginDir);
            File.Copy(round1File, Path.Combine(combinedPluginDir, Path.GetFileName(round1File)));
            File.Copy(round2File, Path.Combine(combinedPluginDir, Path.GetFileName(round2File)));

            // Guarantee 2: both runs' entries are present, undamaged, once combined
            // -- neither install silently erased the other (the actual bug this
            // whole change fixes; with the OLD fixed filename, round2's copy would
            // have overwritten round1's file at this exact step).
            Assert.Equal(2, Directory.GetFiles(combinedPluginDir).Length);
            var combinedText = string.Join('\n', Directory.GetFiles(combinedPluginDir).Select(File.ReadAllText));
            Assert.Contains("鋼の剣", combinedText);
            Assert.Contains("鉄の短剣", combinedText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
