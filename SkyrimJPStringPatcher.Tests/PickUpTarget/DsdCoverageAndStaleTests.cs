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
///   original text MATCHES the current text exactly -> fully covered, not
///   flagged stale, regardless of --include-stale. 2026-09-18: still becomes
///   a candidate (real-data finding — excluding it entirely made a fully-
///   covered plugin vanish from the GUI grid with no way to select it for
///   re-translation), but pre-resolved via DsdCoveredJapanese/DsdCoveredNotes
///   rather than left for ①〜⑥ to work out.
/// - "Steel Blade New" (FormKey 000801): the DSD entry's recorded original
///   is "Steel Blade Old" -> a later mod update changed the text but the old
///   translation keeps applying (DSD matches by FormID alone). Default
///   (no --include-stale): stays covered (pre-resolved, same as above, just
///   flagged for review). With --include-stale: re-included as an ordinary
///   (unresolved) candidate carrying StaleOriginal/StaleTranslation instead.
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
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget");
        File.Copy(Path.Combine(fixturesDir, "StaleTest.esp"), Path.Combine(modDir, "StaleTest.esp"));
        File.Copy(
            Path.Combine(fixturesDir, "StaleTestDsd", "ExistingCommunityPatch.json"),
            Path.Combine(dsdDir, "ExistingCommunityPatch.json"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*StaleTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_WithoutIncludeStale_CoveredRecordsBecomePreResolvedCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_stale_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale: false);

            // matched coverage: fully covered, but still present — pre-resolved,
            // not left for ①〜⑥ (real-data finding: excluding it entirely made a
            // fully-covered plugin vanish from the GUI grid).
            var ironBlade = Assert.Single(result.Candidates, c => c.CurrentText == "Iron Blade Updated");
            Assert.Equal("更新された鉄の剣", ironBlade.DsdCoveredJapanese);
            Assert.Equal("SJPTS_AutoCorpusDsd", ironBlade.DsdCoveredNotes);

            // stale coverage (default, no --include-stale): also pre-resolved with
            // the OLD translation, same as a non-stale covered record — DSD itself
            // keeps applying it regardless, so it isn't "untranslated" either.
            var steelBlade = Assert.Single(result.Candidates, c => c.CurrentText == "Steel Blade New");
            Assert.Equal("古い鋼の剣", steelBlade.DsdCoveredJapanese);
            Assert.Equal("SJPTS_AutoCorpusDsd", steelBlade.DsdCoveredNotes);

            // no coverage at all: an ordinary, still-unresolved candidate.
            var bronzeBlade = Assert.Single(result.Candidates, c => c.CurrentText == "Bronze Blade");
            Assert.Equal("", bronzeBlade.DsdCoveredJapanese);

            Assert.Equal(3, result.Candidates.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Run_WithIncludeStale_ReincludesOnlyTheStaleOneAsAnOrdinaryCandidateWithItsOldTranslationAttached()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_stale_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale: true);

            // still fully covered, exact match — pre-resolved either way.
            var ironBlade = Assert.Single(result.Candidates, c => c.CurrentText == "Iron Blade Updated");
            Assert.Equal("更新された鉄の剣", ironBlade.DsdCoveredJapanese);

            Assert.Contains(result.Candidates, c => c.CurrentText == "Bronze Blade"); // unaffected by the flag

            // --include-stale re-opens the STALE one as an ordinary (unresolved)
            // candidate instead of pre-resolving it — DsdCovered* stays empty,
            // StaleOriginal/StaleTranslation carry the old translation instead.
            var stale = Assert.Single(result.Candidates, c => c.CurrentText == "Steel Blade New");
            Assert.Equal("Steel Blade Old", stale.StaleOriginal);
            Assert.Equal("古い鋼の剣", stale.StaleTranslation);
            Assert.Equal("", stale.DsdCoveredJapanese);

            Assert.Equal(3, result.Candidates.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
