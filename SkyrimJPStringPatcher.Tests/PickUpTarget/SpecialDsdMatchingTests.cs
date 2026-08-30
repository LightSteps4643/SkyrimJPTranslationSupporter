using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTargetRunner.BuildCandidates' two special DSD coverage-matching
/// strategies (DsdTypeMatching.GetStrategy, consumed at PickUpTargetRunner.cs
/// around line 599) were previously untested — DsdCoverageAndStaleTests.cs
/// only exercises the DEFAULT ByFormIdIndex path (ordinary WEAP FULL). These
/// two exist specifically because the default strategy would be WRONG for
/// them:
///
/// - GMST DATA (ByEditorId): GMST FormIDs are documented as unstable across
///   game versions, so DSD matches by EditorID instead. A regression here
///   (e.g. silently falling back to FormID matching) would mean "already
///   translated" GMST settings get silently re-flagged as untranslated after
///   a game update changed their FormID — or the reverse.
/// - QUST CNAM (ByOriginalText, DSD's kRuntimeLegacy exception): matches by
///   the literal original text across every log entry on the quest, NOT by
///   index at all — this tool's own index is just internal bookkeeping.
///
/// Fixtures/PickUpTarget/SpecialMatchingTest.esp defines 2 GMST (string)
/// records and 1 QUST with 2 stages (1 log entry each). Fixtures/PickUpTarget/
/// SpecialMatchingTestDsd/ExistingCommunityPatch.json simulates a pre-existing
/// community DSD patch with 3 entries, each deliberately probing ONE axis:
/// - GMST "sTestGmstCorrectEditorWrongForm": DSD entry has the CORRECT
///   editor_id but a WRONG form_id (000999, not this GMST's real 000800) ->
///   must still be recognized as covered, proving EditorID alone drives the
///   match.
/// - GMST "sTestGmstFormIdOnlyNoEditor": DSD entry has the CORRECT form_id
///   (000801) but NO editor_id at all -> must NOT be recognized as covered,
///   proving GMST DATA never falls back to FormID matching.
/// - QUST CNAM "First quest log message.": DSD entry has the CORRECT form_id
///   but a WRONG index (99999, not this entry's real 10000) -> must still be
///   recognized as covered, proving original-text content alone drives the
///   match. The quest's OTHER log entry ("Second quest log message.", no
///   matching DSD original text) remains an ordinary candidate.
/// </summary>
public class SpecialDsdMatchingTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var dsdDir = Path.Combine(modDir, "SKSE", "Plugins", "DynamicStringDistributor", "SpecialMatchingTest.esp");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(dsdDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget");
        File.Copy(Path.Combine(fixturesDir, "SpecialMatchingTest.esp"), Path.Combine(modDir, "SpecialMatchingTest.esp"));
        File.Copy(
            Path.Combine(fixturesDir, "SpecialMatchingTestDsd", "ExistingCommunityPatch.json"),
            Path.Combine(dsdDir, "ExistingCommunityPatch.json"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*SpecialMatchingTest.esp\r\n");

        return mo2Dir;
    }

    private static PickUpTargetResult RunFixture(string root)
    {
        var mo2Dir = BuildFakeMo2Instance(root);
        using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");
        return PickUpTargetRunner.Run(mo2Dir, log);
    }

    [Fact]
    public void Gmst_MatchedByEditorIdDespiteWrongFormId_IsNotACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_specialmatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = RunFixture(root);

            Assert.DoesNotContain(result.Candidates, c => c.CurrentText == "Correct Editor Match Setting");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Gmst_CoverageWithCorrectFormIdButNoEditorId_NeverMatches_StaysACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_specialmatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = RunFixture(root);

            var candidate = Assert.Single(result.Candidates, c => c.RecordType == "GMST DATA");
            Assert.Equal("Form Id Only Setting", candidate.CurrentText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void QuestCnam_MatchedByOriginalTextDespiteWrongIndex_IsNotACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_specialmatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = RunFixture(root);

            Assert.DoesNotContain(result.Candidates, c => c.CurrentText == "First quest log message.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void QuestCnam_LogEntryWithNoMatchingCoverageText_StaysACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_specialmatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = RunFixture(root);

            var candidate = Assert.Single(result.Candidates, c => c.RecordType == "QUST CNAM");
            Assert.Equal("Second quest log message.", candidate.CurrentText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>3 candidates total: the 2 GMST/QUST-CNAM ones this class is
    /// actually about, plus an incidental "QUST FULL" candidate for the
    /// quest's own Name ("Test Quest") — unrelated to this class's matching
    /// logic (QUST FULL uses the default ByFormIdIndex strategy, unaffected
    /// by any DSD coverage entry in this fixture), but real Quest records
    /// always carry a translatable FULL, so it's present here too.</summary>
    [Fact]
    public void Run_ExactlyThreeCandidatesSurvive_TwoFromThisClassScopePlusTheIncidentalQuestFull()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_specialmatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = RunFixture(root);

            Assert.Equal(3, result.Candidates.Count);
            Assert.Contains(result.Candidates, c => c.RecordType == "QUST FULL" && c.CurrentText == "Test Quest");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
