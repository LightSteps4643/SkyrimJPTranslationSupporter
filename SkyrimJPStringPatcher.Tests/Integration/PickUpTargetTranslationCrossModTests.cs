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

    /// <summary>Blind test (user's own real-world story, 2026-08-29), written
    /// and verified independently of how the cross-mod harvesting fix will
    /// actually be implemented — it checks the end-to-end translations.tsv
    /// output a real user would see, not any internal data structure.
    ///
    /// Given: a famous English mod ("FamousMod.esp") is translated into
    /// Japanese by the community via xTranslator, producing a translation
    /// patch ("FamousModJapanesePatch.esp") that masters FamousMod.esp and
    /// overrides the same WEAP's FULL to Japanese. It loads right after
    /// FamousMod.esp. Later, a large English quest mod ("BigQuestMod.esp")
    /// that requires FamousMod.esp as a master is installed with EVEN HIGHER
    /// priority — it re-saves the same record for reasons unrelated to
    /// translation (a common real-world occurrence: CK/xEdit carries every
    /// field of a touched record along, even ones the author never intended
    /// to change), carrying the original English FULL text back in.
    ///
    /// When: PickUpTarget -> Translation is run (load order: FamousMod.esp ->
    /// FamousModJapanesePatch.esp -> BigQuestMod.esp, so BigQuestMod.esp's
    /// English text wins).
    ///
    /// Then: the record must NOT be left untranslated in translations.tsv —
    /// the Japanese translation patch's text should be applied automatically
    /// (same idea as re-translating a vanilla record: the tool already knows
    /// the answer from elsewhere in the load order), with no new DSD file and
    /// no AI call.
    ///
    /// Currently RED (2026-08-29): confirmed to fail, but NOT by leaving the
    /// row blank as might be assumed -- ④NameFallbackTranslator's own
    /// word-by-word glossary fallback (Notes="TranslationNameFallback")
    /// silently steps in and produces "テスト X 剣" (grammatically wrong,
    /// no corpus backing) instead. This is a worse outcome than a blank row:
    /// it LOOKS translated and would not be flagged for review, silently
    /// discarding the translation patch's actual (and correct) "テストXの剣"
    /// that already exists earlier in the same load order.</summary>
    [Fact(Skip = "TDD placeholder (blind test) for the same not-yet-implemented cross-mod corpus harvesting feature as Run_ThenTranslate_RecoversJapaneseFromANonWinningModsContribution — see the class remarks")]
    public void Run_ThenTranslate_BlindTest_JapaneseTranslationPatchSurvivesALaterUnrelatedEnglishOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_crossmod_blind_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = Path.Combine(root, "mo2");
            var famousModDir = Path.Combine(mo2Dir, "mods", "FamousModFolder");
            var jpPatchDir = Path.Combine(mo2Dir, "mods", "FamousModJapanesePatchFolder");
            var questModDir = Path.Combine(mo2Dir, "mods", "BigQuestModFolder");
            var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
            Directory.CreateDirectory(famousModDir);
            Directory.CreateDirectory(jpPatchDir);
            Directory.CreateDirectory(questModDir);
            Directory.CreateDirectory(profileDir);

            var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
            File.Copy(Path.Combine(fixturesDir, "FamousMod.esp"), Path.Combine(famousModDir, "FamousMod.esp"));
            File.Copy(Path.Combine(fixturesDir, "FamousModJapanesePatch.esp"), Path.Combine(jpPatchDir, "FamousModJapanesePatch.esp"));
            File.Copy(Path.Combine(fixturesDir, "BigQuestMod.esp"), Path.Combine(questModDir, "BigQuestMod.esp"));

            File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
                "[General]\r\n" +
                $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
                "selected_profile=@ByteArray(Default)\r\n");
            File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+BigQuestModFolder\r\n+FamousModJapanesePatchFolder\r\n+FamousModFolder\r\n");
            File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*FamousMod.esp\r\n*FamousModJapanesePatch.esp\r\n*BigQuestMod.esp\r\n");

            using var pickUpTargetLog = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");
            var result = PickUpTargetRunner.Run(mo2Dir, pickUpTargetLog);

            var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
            Directory.CreateDirectory(pickUpTargetOutDir);
            var candidatesTsvPath = Path.Combine(pickUpTargetOutDir, "candidates.tsv");
            var corpusTsvPath = Path.Combine(pickUpTargetOutDir, "corpus.tsv");
            CandidateIo.WriteTsv(candidatesTsvPath, result.Candidates);
            CorpusIo.WriteTsv(corpusTsvPath, result.Corpus);

            var translationOutDir = Path.Combine(root, "Translation", "out_temp");
            var importDir = Path.Combine(root, "Translation", "import"); // no xTranslator import used in this scenario
            using (var translationLog = RunLog.Open(Path.Combine(root, "Translation"), "Translation"))
            {
                PromptGenerator.RunOne(candidatesTsvPath, corpusTsvPath, importDir, "BigQuestMod.esp", translationOutDir, translationLog);
            }

            var translationsTsvPath = Path.Combine(translationOutDir, "BigQuestMod", "translations.tsv");
            var lines = File.ReadAllLines(translationsTsvPath).Skip(1)
                .Select(l => l.Split('\t'))
                .Where(parts => parts.Length >= 6)
                .ToDictionary(parts => TsvEscaping.Unescape(parts[3]), parts => (Japanese: TsvEscaping.Unescape(parts[4]), Notes: TsvEscaping.Unescape(parts[5])));

            Assert.True(lines.ContainsKey("Sword of Test X"), "BigQuestMod.esp's winning English text should appear as the row to check.");
            Assert.Equal("テストXの剣", lines["Sword of Test X"].Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
