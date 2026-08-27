using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Integration;

/// <summary>
/// PickUpTarget + Translation working together, not just each stage in
/// isolation: a real Skyrim modding scenario where two mods edit the SAME
/// FormKey — one (lower priority) already carries a Japanese FULL text
/// (e.g. hand-translated via xTranslator at the ESP level, no DSD file
/// involved at all), the other (higher priority, e.g. a compatibility/quest
/// patch) overrides the same field back to English, unintentionally.
///
/// Fixtures/Integration/TestXMod1.esp: defines WEAP "SJPTSTestXWeapon",
/// FULL = "テストXの剣" (written with a Japanese-targeted encoding, the same
/// way a real xTranslator-edited non-localized plugin stores it).
/// Fixtures/Integration/TestXMod2.esp: masters TestXMod1.esp, overrides the
/// SAME FormKey's WEAP FULL to "Sword of Test X" (plain English).
///
/// This documents the INTENDED end-to-end behavior: PickUpTarget should
/// recognize that some OTHER contributor to this exact (FormKey, type, index)
/// already had Japanese text, harvest it as corpus precedent, and Translation
/// (AutoTranslator's ①完全一致) should then resolve the winning English
/// text back to that Japanese automatically — no DSD file, no AI call needed.
/// </summary>
public class PickUpTargetTranslationCrossModTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var mod1Dir = Path.Combine(mo2Dir, "mods", "Mod1Folder");
        var mod2Dir = Path.Combine(mo2Dir, "mods", "Mod2Folder");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(mod1Dir);
        Directory.CreateDirectory(mod2Dir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
        File.Copy(Path.Combine(fixturesDir, "TestXMod1.esp"), Path.Combine(mod1Dir, "TestXMod1.esp"));
        File.Copy(Path.Combine(fixturesDir, "TestXMod2.esp"), Path.Combine(mod2Dir, "TestXMod2.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        // modlist.txt priority order doesn't matter for THIS scenario (neither
        // mod ships loose Strings/DSD files this test cares about) — only
        // plugins.txt's load order matters, since that decides which mod's
        // WEAP FULL contribution is the chain's LAST (winning) entry.
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+Mod2Folder\r\n+Mod1Folder\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*TestXMod1.esp\r\n*TestXMod2.esp\r\n");

        return mo2Dir;
    }

    /// <summary>Currently RED (TDD): PickUpTarget's corpus building only pairs
    /// English/Japanese text found on the SAME mod's SAME field (see
    /// PickUpTargetRunner.cs's Consider() local function) — it does not yet
    /// look across a (FormKey, type, index) chain's OTHER contributors for a
    /// non-winning Japanese entry. Skipped (not deleted) so the suite stays
    /// green until that PickUpTarget-side fix is designed and implemented;
    /// remove the Skip then.</summary>
    [Fact(Skip = "TDD placeholder for a not-yet-implemented PickUpTarget feature (cross-mod corpus harvesting) — see the class remarks")]
    public void Run_ThenTranslate_RecoversJapaneseFromANonWinningModsContribution()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_crossmod_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            // The winning (Mod2, higher load-order priority) text is what
            // shows up as the candidate needing translation.
            var candidate = Assert.Single(result.Candidates, c => c.RecordType == "WEAP FULL");
            Assert.Equal("Sword of Test X", candidate.CurrentText);
            Assert.Equal("TestXMod2.esp", candidate.WinningPlugin);

            // Mod1's Japanese contribution to the SAME (FormKey, type, index)
            // should have been harvested as corpus precedent, even though
            // Mod1 didn't win the field.
            Assert.Contains(result.Corpus, e => e.English == "Sword of Test X" && e.Japanese == "テストXの剣");

            // And Translation's own ①完全一致 should resolve it automatically
            // from that corpus — the actual end-to-end payoff.
            var autoTranslator = new AutoTranslator(result.Corpus);
            var resolved = autoTranslator.TryTranslate(candidate.CurrentText, candidate.RecordType);
            Assert.NotNull(resolved);
            Assert.Equal("テストXの剣", resolved!.Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
