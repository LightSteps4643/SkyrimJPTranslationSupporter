using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTarget/NestedTranslatableFields.cs's breadth coverage (area B) —
/// unlike ExtraTranslatableFields' flat one-field-per-type mapping, these are
/// nested list/indexed structures (QUST objectives/log entries, INFO
/// responses, MESG buttons, PERK effects) or a non-FormID identity (GMST's
/// EditorID match), where the INDEX arithmetic itself (not just which field
/// maps to which DsdType) is the real risk.
///
/// Fixtures/PickUpTarget/NestedFieldsTest.esp bundles one QUST (one
/// objective + one stage with one log entry), one INFO (prompt + one
/// response), one MESG (one button), one REGN, one GMST (string variant),
/// and one PERK (one SetText entry-point effect) into a single plugin.
///
/// PERK EPF2/EPFD's ButtonLabel arm is NOT covered here: in practice, setting
/// PerkEntryPointSetText/SelectText's ButtonLabel and writing+re-reading the
/// plugin through Mutagen came back empty regardless of what was set (a
/// Mutagen-side gap encountered while building this fixture, not something
/// to work around) — and NestedTranslatableFields.cs's own remarks already
/// note that arm is "extremely rare in real data (2 records...)". Only the
/// Text (verbText) arm, which round-trips correctly, is covered.
/// </summary>
public class NestedTranslatableFieldsTests
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
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "NestedFieldsTest.esp"),
            Path.Combine(modDir, "NestedFieldsTest.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*NestedFieldsTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_NestedFieldsFixture_ProducesEveryNestedTypesCandidateWithTheRightDsdTypeAndIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_nestedfields_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var expected = new (string DsdType, int Index, string Text, string EditorId)[]
            {
                // QUST NNAM: index is the objective's own Index (arbitrary,
                // not sequential from 0) — set to 10 to prove it's not silently
                // renumbered.
                ("QUST NNAM", 10, "Find the ancient sword", ""),
                // QUST CNAM: index = stage.Index * 1000 + position-within-stage
                // (20 * 1000 + 0).
                ("QUST CNAM", 20000, "I have found the ancient sword.", ""),
                // INFO RNAM (the prompt) is always index 0.
                ("INFO RNAM", 0, "What brings you here?", ""),
                // INFO NAM1: index is the response's own ResponseNumber (set to 1).
                ("INFO NAM1", 1, "I am just passing through.", ""),
                ("MESG ITXT", 0, "Accept the quest", ""),
                ("REGN RDMP", 0, "The Ancient Forest", ""),
                // GMST DATA carries its EditorID (EditorID-matched, not FormID).
                ("GMST DATA", 0, "Press any key to continue", "sSJPTSGmst"),
                // PERK EPF2/EPFD: the Text (verbText) arm, effectIndex=0 -> index 1.
                ("PERK EPF2", 1, "You tear the heart from your fallen foe.", ""),
                ("PERK EPFD", 1, "You tear the heart from your fallen foe.", ""),
            };

            Assert.Equal(expected.Length, result.Candidates.Count);
            foreach (var (dsdType, index, text, editorId) in expected)
            {
                var candidate = Assert.Single(result.Candidates, c => c.RecordType == dsdType);
                Assert.Equal(index, candidate.Index);
                Assert.Equal(text, candidate.CurrentText);
                Assert.Equal(editorId, candidate.EditorId);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
