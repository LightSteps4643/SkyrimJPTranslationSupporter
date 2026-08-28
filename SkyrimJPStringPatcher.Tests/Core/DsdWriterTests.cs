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
    private static string ExpectedJsonPath(string outputRoot, string plugin) =>
        Path.Combine(outputRoot, "SKSE", "Plugins", "DynamicStringDistributor", plugin, "SkyrimJPStringPatcher.json");

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

            DsdWriter.WriteAll(root, entries);

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

            DsdWriter.WriteAll(root, entries);

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

            DsdWriter.WriteAll(root, entries);

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

            DsdWriter.WriteAll(root, entries);

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

            DsdWriter.WriteAll(root, entries);

            Assert.True(File.Exists(ExpectedJsonPath(root, "ModA.esp")));
            Assert.True(File.Exists(ExpectedJsonPath(root, "ModB.esp")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
