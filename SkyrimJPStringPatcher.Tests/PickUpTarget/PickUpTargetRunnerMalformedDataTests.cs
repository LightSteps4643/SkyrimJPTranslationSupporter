using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// Reproduces DESIGN_NOTES.md known issue 21 end-to-end: PickUpTargetRunner.Run
/// against a real (tiny, synthetic) MO2 instance whose one plugin contains a
/// PERK record with a deliberately corrupted entry-point effect — the exact
/// condition that used to crash the whole CLI process with exit code
/// -532462766 (0xE0434352, an unhandled MalformedDataException from Mutagen).
///
/// Fixtures/MalformedPerkTest.esp is checked in rather than built at test time:
/// it was generated once (see the comment on BuildFakeMo2Instance for how) by
/// writing a valid PERK entry-point effect via Mutagen itself and then flipping
/// a single byte (the EPFT parameter-type flag, Float=1 -> LString=7) — the same
/// class of corruption the real bug report hit, confirmed to raise the exact
/// same exception message: "PerkEntryPointModifyValue did not have expected
/// parameter type flag: LString".
/// </summary>
public class PickUpTargetRunnerMalformedDataTests
{
    /// <summary>Builds a minimal-but-real MO2 instance directory around the
    /// checked-in malformed .esp: ModOrganizer.ini + one profile (modlist.txt/
    /// plugins.txt) + one mod folder holding the plugin. gamePath deliberately
    /// points at a nonexistent folder — Mo2InstanceReader.AddImplicitMasters
    /// only ever probes it with File.Exists/Directory.Exists, so a missing game
    /// install just means "no implicit masters," which this test doesn't need
    /// (the malformed PERK record carries no external references).</summary>
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "MalformedPerkTest.esp"),
            Path.Combine(modDir, "MalformedPerkTest.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*MalformedPerkTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_MalformedPerkEntryPoint_DoesNotThrow_SkipsOnlyTheBrokenField_ReportsTheIssue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            PickUpTargetResult result;
            try
            {
                // The pre-fix behavior was an unhandled exception here, crashing
                // the whole process. If a future change regresses SafeForEach or
                // one of PickUpTargetRunner's per-step try/catch blocks, THIS is
                // the line that starts throwing again.
                result = PickUpTargetRunner.Run(mo2Dir, log);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            var stdout = capturedOut.ToString();

            // The record's Name/FULL field does not go through the malformed
            // Effects list at all, so it must survive as a normal candidate —
            // this is the whole point of the known issue 21 fix being
            // field-grained rather than record-grained.
            var perkFull = Assert.Single(result.Candidates, c => c.RecordType == "PERK FULL");
            Assert.Equal("Test Perk", perkFull.CurrentText);

            // The entry-point effect itself is unreadable, so no PERK
            // EPFD/EPF2 candidate should have been produced from it.
            Assert.DoesNotContain(result.Candidates, c => c.RecordType is "PERK EPFD" or "PERK EPF2");

            // The GUI's MessageBox notification (known issue 21) is driven
            // entirely by these two stdout marker lines — assert on them
            // directly rather than on RunLog's human-readable prose, which is
            // free to reword without breaking this contract.
            Assert.Contains("##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0", stdout);
            Assert.Contains("##SJPTS_ISSUES_PLUGINS## MalformedPerkTest.esp", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A DIFFERENT failure point from the test above: the whole
    /// plugin fails to OPEN at all (OpenMods' own try/catch, PickUpTargetRunner.cs
    /// ~line 204-218) — e.g. a file that isn't valid Mutagen binary data in the
    /// first place, as opposed to a structurally-valid ESP with one corrupted
    /// field. Coverage showed this catch block was never exercised. A garbage
    /// text file with a ".esp" extension is enough to trigger Mutagen's own
    /// parse failure — no byte-flipping trick needed, unlike the PERK case
    /// above (which specifically needed a STRUCTURALLY VALID file with one
    /// deliberately-wrong flag).</summary>
    [Fact]
    public void Run_PluginFailsToOpenEntirely_SkipsOnlyThatPlugin_OtherPluginsStillProcessed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_openfail_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(root, "mo2");
        var goodModDir = Path.Combine(mo2Dir, "mods", "GoodMod");
        var badModDir = Path.Combine(mo2Dir, "mods", "BadMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(goodModDir);
        Directory.CreateDirectory(badModDir);
        Directory.CreateDirectory(profileDir);
        try
        {
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "StaleTest.esp"),
                Path.Combine(goodModDir, "StaleTest.esp"));
            File.WriteAllText(Path.Combine(badModDir, "BadMod.esp"), "this is not a valid Mutagen ESP binary file at all");

            File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
                "[General]\r\n" +
                $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
                "selected_profile=@ByteArray(Default)\r\n");
            File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+GoodMod\r\n+BadMod\r\n");
            File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*StaleTest.esp\r\n*BadMod.esp\r\n");

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

            // GoodMod's own candidates must still appear — one broken plugin
            // must never take down the whole run.
            Assert.Contains(result.Candidates, c => c.CurrentText == "Bronze Blade");

            Assert.Contains("##SJPTS_ISSUES## plugins=1 fields=0 fail_open=0 context_only=0", stdout);
            Assert.Contains("##SJPTS_ISSUES_PLUGINS## BadMod.esp", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
