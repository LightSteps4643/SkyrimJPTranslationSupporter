using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTarget's DSD coverage matching + --include-stale breadth coverage
/// (area D): PickUpTargetRunner.BuildCandidates compares the load order's
/// CURRENT winning text against what an EXISTING (community-authored, no DSD
/// file this tool wrote) DSD json recorded as the original text it was
/// translated against, for the same (FormKey, type, index).
///
/// Fixtures/PickUpTarget/StaleTest.esp defines 3 WEAP records; Fixtures/
/// PickUpTarget/StaleTestDsd/ExistingCommunityPatch.json is placed under the
/// fake MO2 instance's SKSE/Plugins/DynamicStringDistributor/StaleTest.esp/
/// folder (a real DSD json's gating folder — its filename doesn't matter,
/// DSD merges every *.json under a plugin folder) to simulate a pre-existing
/// translation patch already installed:
/// - "Iron Blade Updated" (FormKey 000800): the DSD entry's recorded
///   original text MATCHES the current text exactly -> fully covered, never
///   becomes a candidate, not flagged stale, regardless of --include-stale.
/// - "Steel Blade New" (FormKey 000801): the DSD entry's recorded original
///   is "Steel Blade Old" -> a later mod update changed the text but the old
///   translation keeps applying (DSD matches by FormID alone). Default
///   (no --include-stale): stays covered/excluded (just flagged for review).
///   With --include-stale: re-included as a candidate carrying
///   StaleOriginal/StaleTranslation.
/// - "Bronze Blade" (FormKey 000802): no DSD entry at all -> an ordinary new
///   candidate either way.
/// </summary>
public class DsdCoverageAndStaleTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var dsdDir = Path.Combine(modDir, "SKSE", "Plugins", "DynamicStringDistributor", "StaleTest.esp");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(dsdDir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget");
        File.Copy(Path.Combine(fixturesDir, "StaleTest.esp"), Path.Combine(modDir, "StaleTest.esp"));
        File.Copy(
            Path.Combine(fixturesDir, "StaleTestDsd", "ExistingCommunityPatch.json"),
            Path.Combine(dsdDir, "ExistingCommunityPatch.json"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*StaleTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_WithoutIncludeStale_StaysCoveredAndDoesNotBecomeACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_stale_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale: false);

            var texts = result.Candidates.Select(c => c.CurrentText).ToHashSet();
            Assert.DoesNotContain("Iron Blade Updated", texts); // matched coverage: fully covered
            Assert.DoesNotContain("Steel Blade New", texts);    // stale coverage: still excluded by default
            Assert.Contains("Bronze Blade", texts);             // no coverage at all: ordinary candidate
            Assert.Single(result.Candidates);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Run_WithIncludeStale_ReincludesOnlyTheStaleOneWithItsOldTranslationAttached()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_stale_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale: true);

            Assert.DoesNotContain(result.Candidates, c => c.CurrentText == "Iron Blade Updated"); // still fully covered, exact match
            Assert.Contains(result.Candidates, c => c.CurrentText == "Bronze Blade"); // unaffected by the flag

            var stale = Assert.Single(result.Candidates, c => c.CurrentText == "Steel Blade New");
            Assert.Equal("Steel Blade Old", stale.StaleOriginal);
            Assert.Equal("古い鋼の剣", stale.StaleTranslation);

            Assert.Equal(2, result.Candidates.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
