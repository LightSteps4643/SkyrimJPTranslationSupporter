using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTarget/ExtraTranslatableFields.cs's normal-case ("面"/breadth)
/// coverage: a hand-written type switch mapping 15 record types (16 counting
/// Book's two fields) to their DSD type string. Each case shares the same
/// trivial shape (yield the DsdType + the field), so the real risk isn't the
/// mechanism (already covered by known issue 21.'s SafeForEach/PickUpTargetRunner
/// tests) but a wrong DsdType string or wrong field mapped for one specific
/// record type — exactly what this test catches.
///
/// Fixtures/PickUpTarget/ExtraFieldsTest.esp bundles one record of each type
/// into a single plugin (amortizing ESP-construction cost across all of them,
/// rather than one fixture per type) — built the same way as known issue 21.'s
/// fixture (Mutagen itself, via a throwaway generator script not kept in the
/// repo). Text values are realistic display sentences with spaces — an
/// EditorID-style value with no spaces (the first attempt used e.g.
/// "ArmoDescText") trips PickUpTargetRunner's own
/// NonTranslatableText.LooksLikeInternalIdentifier heuristic and gets
/// excluded, which is itself a useful thing this test setup surfaced.
/// </summary>
public class ExtraTranslatableFieldsTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "ExtraFieldsTest.esp"),
            Path.Combine(modDir, "ExtraFieldsTest.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*ExtraFieldsTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_ExtraFieldsFixture_ProducesEveryRecordTypesCandidateWithTheRightDsdType()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_extrafields_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var expected = new (string DsdType, string Text)[]
            {
                ("ACTI RNAM", "Open the Chest"),
                ("FLOR RNAM", "Pick the Flower"),
                ("LSCR DESC", "A weathered old altar stands in the ruins."),
                ("MGEF DNAM", "Deals frost damage over time."),
                ("WOOP TNAM", "Fire Breath"),
                ("BOOK DESC", "A dusty old tome."),
                ("BOOK CNAM", "Once upon a time, in the land of Skyrim..."),
                ("AMMO DESC", "A finely crafted arrow."),
                ("ARMO DESC", "Sturdy armor forged from iron."),
                ("WEAP DESC", "A simple but reliable blade."),
                ("SPEL DESC", "Summons a ward of protective magic."),
                ("SCRL DESC", "A scroll bearing a powerful enchantment."),
                ("SHOU DESC", "Calls forth the power of the ancient dragons."),
                ("RACE DESC", "A proud and ancient people of the north."),
                ("MESG DESC", "Are you sure you want to proceed?"),
                ("PERK DESC", "Grants greater skill with one-handed weapons."),
                ("NPC_ SHRT", "The Wanderer"),
            };

            Assert.Equal(expected.Length, result.Candidates.Count);
            foreach (var (dsdType, text) in expected)
            {
                var candidate = Assert.Single(result.Candidates, c => c.RecordType == dsdType);
                Assert.Equal(text, candidate.CurrentText);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
