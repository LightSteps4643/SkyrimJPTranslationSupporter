using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTargetRunner.BuildCandidates' TEXT-based exclusion chain (as opposed
/// to ExclusionClassificationTests' RECORD-FLAG-based exclusions — MGEF
/// HideInUI, ARMO/WEAP NonPlayable, PERK Hidden). Coverage showed these 9
/// NonTranslatableText-driven checks were never exercised THROUGH the actual
/// runner pipeline — each predicate's own logic is unit-tested directly
/// (NonTranslatableTextTests, 60 cases), but whether PickUpTargetRunner
/// actually calls them, in the right order, and correctly skips the
/// candidate was untested.
///
/// Fixtures/PickUpTarget/TextExclusionTest.esp bundles one WEAP FULL record
/// per exclusion reason (markup/icon-glyph, asset path, internal identifier,
/// audio template name, "do not delete" note, internal fx name, dev temp
/// marker, placeholder token, non-word acronym) plus one QUST FULL record
/// for the QUST-FULL-scoped version-tracking check, plus one normal WEAP
/// that must survive as a candidate — each text was deliberately chosen so
/// it trips ONLY its own intended check, not an earlier one in the sequential
/// exclusion chain (verified by hand against BuildCandidates' actual order).
/// </summary>
public class TextExclusionClassificationTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "TextExclusionTest.esp"),
            Path.Combine(modDir, "TextExclusionTest.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*TextExclusionTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_TextExclusionFixture_ExcludesEachForItsOwnReason_NormalSurvives()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_textexclusion_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var candidateTexts = result.Candidates.Select(c => c.CurrentText).ToHashSet();

            Assert.Contains("Normal Test Sword", candidateTexts);

            Assert.DoesNotContain("<font face=\"Iconographia\">G</Font>", candidateTexts);
            Assert.DoesNotContain("Meshes\\Weapons\\Test.nif", candidateTexts);
            Assert.DoesNotContain("TestInternalIdentifierNoSpace", candidateTexts);
            Assert.DoesNotContain("AudioTemplate Test", candidateTexts);
            Assert.DoesNotContain("Do Not Delete Test Weapon", candidateTexts);
            Assert.DoesNotContain("Retroactive fixes for 4.2.1", candidateTexts);
            Assert.DoesNotContain("Test Effect fx", candidateTexts);
            Assert.DoesNotContain("TEMP Test Weapon", candidateTexts);
            Assert.DoesNotContain("xxx", candidateTexts);
            Assert.DoesNotContain("YMMP", candidateTexts);

            Assert.Single(result.Candidates);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
