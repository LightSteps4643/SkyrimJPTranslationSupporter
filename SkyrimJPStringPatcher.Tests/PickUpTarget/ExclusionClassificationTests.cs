using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTarget's ①除外判定 ("not player-facing") breadth coverage (area C):
/// MGEF HideInUI, ARMO/WEAP NonPlayable, PERK Hidden/non-Playable, and an
/// Ability spell whose every effect is HideInUI. Each check is paired with a
/// NORMAL sibling record of the same type that should NOT be excluded — the
/// real risk here isn't "does exclusion fire at all" but "does it fire only
/// for the record it's supposed to," so a broken condition that excludes
/// everything (or nothing) of a given type is exactly what this test would
/// catch.
///
/// Fixtures/PickUpTarget/ExclusionTest.esp bundles all 5 excluded + 5 normal
/// pairs (MGEF hidden/visible, ARMO non-playable/playable, WEAP non-playable/
/// playable, PERK hidden/not-playable/normal, Ability-spell-all-hidden/normal
/// spell) into one plugin.
/// </summary>
public class ExclusionClassificationTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "ExclusionTest.esp"),
            Path.Combine(modDir, "ExclusionTest.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*ExclusionTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_ExclusionFixture_ExcludesOnlyTheFlaggedRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_exclusion_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var candidateTexts = result.Candidates.Select(c => c.CurrentText).ToHashSet();

            // The 5 "normal" siblings must survive as candidates.
            Assert.Contains("Visible Effect Name", candidateTexts);
            Assert.Contains("Playable Armor Name", candidateTexts);
            Assert.Contains("Playable Weapon Name", candidateTexts);
            Assert.Contains("Normal Perk Name", candidateTexts);
            Assert.Contains("Normal Spell Name", candidateTexts);

            // The 6 flagged records must NOT become candidates.
            Assert.DoesNotContain("Hidden Effect Name", candidateTexts);
            Assert.DoesNotContain("NonPlayable Armor Name", candidateTexts);
            Assert.DoesNotContain("NonPlayable Weapon Name", candidateTexts);
            Assert.DoesNotContain("Hidden Perk Name", candidateTexts);
            Assert.DoesNotContain("NotPlayable Perk Name", candidateTexts);
            Assert.DoesNotContain("AllHidden Ability Name", candidateTexts);

            Assert.Equal(5, result.Candidates.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
