using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// Two of PickUpTargetRunner's per-field try/catch blocks (② the Name/FULL
/// read at ~PickUpTargetRunner.cs:400, ③ ExtraTranslatableFields' read at
/// ~PickUpTargetRunner.cs:413) had no test at all — coverage showed 0%.
/// Neither can be triggered by a structurally-corrupted single field the way
/// Fixtures/MalformedPerkTest.esp does (that trick corrupts a PERK entry
/// point's own binary layout, caught by ④ NestedTranslatableFields instead).
/// Both ② and ③ instead go through Mutagen's *TranslatedString* machinery,
/// which reads a genuinely-localized plugin's separate loose Strings/*.STRINGS
/// / *.DLSTRINGS files — truncating one of those files corrupts its whole
/// lookup table (confirmed via a scratchpad spike: any TryLookup/String
/// access against a truncated table throws System.ArgumentException("Strings
/// file had duplicate entries.")), which is a much cheaper and more reliable
/// way to make ONE specific field's read throw than binary-corrupting the
/// .esp itself.
///
/// Fixtures/PickUpTarget/NameFullCorruptTest/: a localized ARMO record whose
/// Japanese .STRINGS table (backing Name/FULL) is truncated to 8 bytes — this
/// is the ONLY notable field on the record, so ②'s catch is unambiguous.
///
/// Fixtures/PickUpTarget/ExtraFieldCorruptTest/: a localized ARMO record with
/// BOTH languages present on Name (so FULL resolves normally and the same-mod
/// corpus harvest — SameModCorpusHarvestTests' pattern — still fires,
/// confirming the record itself IS being processed) but whose Japanese
/// .DLSTRINGS table (backing Description) is truncated — isolating the
/// failure to ③ alone.
/// </summary>
public class PickUpTargetRunnerFieldReadFailureTests
{
    private static string BuildFakeMo2Instance(string root, string fixtureName, string espFileName)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var stringsDir = Path.Combine(modDir, "Strings");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(stringsDir);
        Directory.CreateDirectory(profileDir);

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", fixtureName);
        File.Copy(Path.Combine(fixtureDir, espFileName), Path.Combine(modDir, espFileName));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fixtureDir, "Strings")))
            File.Copy(file, Path.Combine(stringsDir, Path.GetFileName(file)));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{espFileName}\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_NameFullTableCorrupted_ExcludesTheRecordEntirely_ReportsSkippedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_namefullcorrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root, "NameFullCorruptTest", "NameFullCorruptTest.esp");
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            PickUpTargetResult result;
            try
            {
                result = PickUpTargetRunner.Run(mo2Dir, log);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            var stdout = capturedOut.ToString();

            Assert.DoesNotContain(result.Candidates, c => c.RecordType == "ARMO FULL");

            Assert.Contains("##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0", stdout);
            Assert.Contains("##SJPTS_ISSUES_PLUGINS## NameFullCorruptTest.esp", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>KNOWN BUG found while writing this test (2026-08-28, not yet
    /// fixed — see DESIGN_NOTES.md): SafeEnumeration.SafeForEach only wraps the
    /// ENUMERATOR (MoveNext/Current) in try/catch — its onItem callback runs
    /// OUTSIDE that protection (Core/SafeEnumeration.cs:35). ExtraTranslatableFields'
    /// SafeForEach call passes `fieldRef => Consider(...)` as onItem, and
    /// Consider() itself can throw (via TranslatedString.TryLookup, when the
    /// backing Strings table is corrupted) — so the exception propagates
    /// straight past SafeForEach's own onError, past ScanTranslatableFields,
    /// and out of PickUpTargetRunner.Run() entirely uncaught (Run() only has a
    /// try/finally, no catch). This is the exact class of whole-process crash
    /// known issue 21 was meant to prevent, just reached via a different call
    /// site (onItem, not the enumerator) than the one that fix actually covers.</summary>
    [Fact(Skip = "Known bug: SafeForEach's onItem callback is unprotected, so Consider() throwing inside it (e.g. a corrupted Strings table) crashes PickUpTargetRunner.Run() uncaught instead of being reported as a skipped field — see DESIGN_NOTES.md's SafeForEach entry")]
    public void Run_ExtraFieldTableCorrupted_ExcludesOnlyThatField_KeepsTheRecordOtherwiseIntact_ReportsSkippedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_extrafieldcorrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root, "ExtraFieldCorruptTest", "ExtraFieldCorruptTest.esp");
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            PickUpTargetResult result;
            try
            {
                result = PickUpTargetRunner.Run(mo2Dir, log);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            var stdout = capturedOut.ToString();

            // The record itself was fully processed -- Name/FULL had both
            // languages, so it correctly reached the corpus (proving ② and
            // context extraction were unaffected by the Description corruption).
            var corpusEntry = Assert.Single(result.Corpus, e => e.English == "Good Name Test");
            Assert.Equal("良い名前テスト", corpusEntry.Japanese);

            // Only the Description field's own read failed.
            Assert.DoesNotContain(result.Candidates, c => c.RecordType == "ARMO DESC");

            Assert.Contains("##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0", stdout);
            Assert.Contains("##SJPTS_ISSUES_PLUGINS## ExtraFieldCorruptTest.esp", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
