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
    [Fact]
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
    [Fact]
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

    // ===== Additional black-box patterns (2026-08-29), each written from a
    // real-world modding story and independent of the planned implementation
    // -- they check the pipeline's overall OUTPUT (translations.tsv), not any
    // internal data structure, exactly like the blind test above. =====

    /// <summary>Builds an MO2 instance from an ordered list of (folder, esp)
    /// mods already present in Fixtures/Integration/, plus an optional extra
    /// setup callback (e.g. to drop a DSD coverage file into a mod's folder).
    /// Shared by every pattern test below so each test method only needs to
    /// state its own mod list and load order.</summary>
    private static string BuildMo2Instance(string root, (string Folder, string Esp)[] modsInLoadOrder, Action<string, (string Folder, string Esp)[]>? extraSetup = null)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
        foreach (var (folder, esp) in modsInLoadOrder)
        {
            var modDir = Path.Combine(mo2Dir, "mods", folder);
            Directory.CreateDirectory(modDir);
            File.Copy(Path.Combine(fixturesDir, esp), Path.Combine(modDir, esp));

            // A genuinely localized mod (UsingLocalization=true) ships its
            // strings as LOOSE Strings/* files, not embedded in the .esp --
            // copy them over too when this fixture has them
            // (Fixtures/Integration/<EspNameWithoutExtension>Strings/*).
            var sourceStringsDir = Path.Combine(fixturesDir, Path.GetFileNameWithoutExtension(esp) + "Strings");
            if (Directory.Exists(sourceStringsDir))
            {
                var destStringsDir = Path.Combine(modDir, "Strings");
                Directory.CreateDirectory(destStringsDir);
                foreach (var file in Directory.EnumerateFiles(sourceStringsDir))
                    File.Copy(file, Path.Combine(destStringsDir, Path.GetFileName(file)));
            }
        }

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), string.Join("\r\n", modsInLoadOrder.Select(m => "+" + m.Folder).Reverse()) + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Join("\r\n", modsInLoadOrder.Select(m => "*" + m.Esp)) + "\r\n");

        extraSetup?.Invoke(mo2Dir, modsInLoadOrder);
        return mo2Dir;
    }

    /// <summary>Runs the real PickUpTarget -> Translation handoff (PromptGenerator.RunOne
    /// for just the given target plugin) and returns the resulting
    /// translations.tsv content, keyed by unescaped EnglishText.</summary>
    private static Dictionary<string, (string Japanese, string Notes)> RunPipeline(string mo2Dir, string root, string targetPlugin)
    {
        using var pickUpTargetLog = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");
        var result = PickUpTargetRunner.Run(mo2Dir, pickUpTargetLog);

        var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
        Directory.CreateDirectory(pickUpTargetOutDir);
        var candidatesTsvPath = Path.Combine(pickUpTargetOutDir, "candidates.tsv");
        var corpusTsvPath = Path.Combine(pickUpTargetOutDir, "corpus.tsv");
        CandidateIo.WriteTsv(candidatesTsvPath, result.Candidates);
        CorpusIo.WriteTsv(corpusTsvPath, result.Corpus);

        var translationOutDir = Path.Combine(root, "Translation", "out_temp");
        var importDir = Path.Combine(root, "Translation", "import");
        using (var translationLog = RunLog.Open(Path.Combine(root, "Translation"), "Translation"))
        {
            PromptGenerator.RunOne(candidatesTsvPath, corpusTsvPath, importDir, targetPlugin, translationOutDir, translationLog);
        }

        var translationsTsvPath = Path.Combine(translationOutDir, Path.GetFileNameWithoutExtension(targetPlugin), "translations.tsv");
        return File.ReadAllLines(translationsTsvPath).Skip(1)
            .Select(l => l.Split('\t'))
            .Where(parts => parts.Length >= 6)
            .ToDictionary(parts => TsvEscaping.Unescape(parts[3]), parts => (Japanese: TsvEscaping.Unescape(parts[4]), Notes: TsvEscaping.Unescape(parts[5])));
    }

    /// <summary>Pattern A (vanilla generalization): a genuinely localized
    /// (dual-language) mod stands in for vanilla Skyrim.esm's own official
    /// Japanese localization -- an unrelated mod (a UI/rebalance mod that
    /// never intended to touch translation) re-saves the SAME record,
    /// carrying the English text back in as a side effect.
    ///
    /// Given: VanillaEquivMod.esp defines a WEAP with BOTH English and
    /// Japanese in the same FULL field (UsingLocalization=true, mirroring
    /// how Skyrim.esm itself ships). UnrelatedRebalanceMod.esp masters it and
    /// re-saves the SAME record with the identical English text, becoming the
    /// winner.
    /// When: PickUpTarget -> Translation is run.
    /// Then: the record should not stay untranslated/mistranslated.
    ///
    /// CONFIRMED (2026-08-29, empirically, not assumed): unlike the other
    /// patterns, this one ALREADY PASSES with today's code -- a genuinely
    /// localized mod's own dual-language field already feeds the EXISTING
    /// same-mod corpus harvesting (Consider()'s hasEnglish && hasJapanese
    /// branch) regardless of whether that mod ends up winning the chain, and
    /// the override's English text here is byte-identical. So "vanilla
    /// generalization" is NOT actually a gap the new cross-mod feature needs
    /// to cover -- it's already handled, as long as the source is genuinely
    /// localized (dual-language in one field) rather than a separate
    /// non-localized JP-only patch mod (which is what patterns B/C/D and the
    /// blind test are about). First attempt at this fixture omitted copying
    /// VanillaEquivMod.esp's loose Strings/* files into the MO2 instance,
    /// which made it fail for an unrelated reason (the localized text
    /// couldn't be read at all) -- fixed by extending BuildMo2Instance to
    /// copy a per-mod Strings/ folder when the fixture has one.</summary>
    [Fact]
    public void Run_ThenTranslate_PatternA_VanillaLikeSourceOverriddenByUnrelatedMod()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_pattern_a_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMo2Instance(root,
            [
                ("VanillaEquivModFolder", "VanillaEquivMod.esp"),
                ("UnrelatedRebalanceModFolder", "UnrelatedRebalanceMod.esp"),
            ]);
            var lines = RunPipeline(mo2Dir, root, "UnrelatedRebalanceMod.esp");

            Assert.True(lines.ContainsKey("Sjpts Frostmere Blade"));
            Assert.Equal("フロストミアの刃", lines["Sjpts Frostmere Blade"].Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Pattern B (stale precedent, applied but flagged -- REVISED
    /// 2026-08-29): a later mod repurposes the same FormKey for a
    /// MEANINGFULLY DIFFERENT item, not just a reformatting.
    ///
    /// Given: OriginalItemMod.esp defines "Sjpts Ancient Warblade".
    /// OriginalItemModJapanesePatch.esp masters it, translates it to
    /// "古の戦刃". RenameMod.esp masters OriginalItemMod.esp and overrides the
    /// SAME record to a DIFFERENT English name, "Sjpts Cursed Battleaxe"
    /// (simulating a mod that repurposes the FormKey for a different weapon
    /// entirely), and wins.
    /// When: PickUpTarget -> Translation is run.
    /// Then (REVISED per user decision, 2026-08-29): this tool does not judge
    /// whether a given translation is objectively correct for the current
    /// text -- that mirrors scenario⑤'s existing stale-DSD handling exactly
    /// (a stale DSD translation is still APPLIED, just logged for review,
    /// never withheld). So "古の戦刃" SHOULD be applied here too, with a
    /// warning surfaced through the log (not by withholding the translation
    /// or by encoding confidence into a second Notes tag -- see class remarks
    /// on the "single Notes tag" decision).
    ///
    /// CORRECTION (2026-08-29): the FIRST version of this test asserted the
    /// OPPOSITE ("must NOT be applied") and passed only because nothing
    /// currently links these two strings -- that assertion was written more
    /// strictly than the design already agreed at the time (apply-with-
    /// warning, matching scenario⑤), a test-authoring gap rather than an
    /// actual design conflict. Marked Skip like patterns C/D: this is now a
    /// genuine TDD-red placeholder (confirmed to fail against today's code,
    /// since nothing resolves it yet), not a currently-passing safety net.</summary>
    [Fact]
    public void Run_ThenTranslate_PatternB_MeaningfullyDifferentOverrideAppliesStaleTranslationWithWarning()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_pattern_b_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMo2Instance(root,
            [
                ("OriginalItemModFolder", "OriginalItemMod.esp"),
                ("OriginalItemModJapanesePatchFolder", "OriginalItemModJapanesePatch.esp"),
                ("RenameModFolder", "RenameMod.esp"),
            ]);
            var lines = RunPipeline(mo2Dir, root, "RenameMod.esp");

            Assert.True(lines.ContainsKey("Sjpts Cursed Battleaxe"));
            // The stale precedent IS applied -- this tool doesn't adjudicate
            // whether it's still the objectively correct translation. The
            // warning itself is surfaced via the log, not asserted here.
            Assert.Equal("古の戦刃", lines["Sjpts Cursed Battleaxe"].Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Pattern C (trivial reformatting, corpus-only would miss it): a
    /// later mod re-saves the record for an unrelated reason and the English
    /// text comes back with a single trailing space -- a byte-for-byte corpus
    /// match would fail, but the record identity (FormKey/type/index) is
    /// unchanged.
    ///
    /// Given: ReformatOriginalMod.esp defines "Sjpts Moonlit Rapier".
    /// ReformatJapanesePatch.esp masters it, translates it to "月光の刺剣".
    /// TrivialRebalanceMod.esp masters ReformatOriginalMod.esp and overrides
    /// the SAME record with "Sjpts Moonlit Rapier " (note the trailing
    /// space -- a CK/xEdit re-save artifact), and wins.
    /// When: PickUpTarget -> Translation is run.
    /// Then: "月光の刺剣" should still be applied -- this is clearly the same
    /// item, just with an incidental formatting difference in the carried-
    /// forward English text.
    ///
    /// Confirmed RED (2026-08-29): stays unresolved today, exactly as
    /// predicted (the trailing space breaks corpus's exact-text match).</summary>
    [Fact]
    public void Run_ThenTranslate_PatternC_TrivialReformattingStillResolvesViaRecordIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_pattern_c_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMo2Instance(root,
            [
                ("ReformatOriginalModFolder", "ReformatOriginalMod.esp"),
                ("ReformatJapanesePatchFolder", "ReformatJapanesePatch.esp"),
                ("TrivialRebalanceModFolder", "TrivialRebalanceMod.esp"),
            ]);
            var lines = RunPipeline(mo2Dir, root, "TrivialRebalanceMod.esp");

            Assert.True(lines.ContainsKey("Sjpts Moonlit Rapier "));
            Assert.Equal("月光の刺剣", lines["Sjpts Moonlit Rapier "].Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Pattern D (multiple JP patches with different content, the
    /// revised/closest-to-winner one should be preferred): two competing
    /// Japanese translation patches exist in the same load order (an old one
    /// and a later revision), then an unrelated mod overrides back to
    /// English.
    ///
    /// Given: MultiPatchMod.esp defines "Sjpts Emberfall Axe".
    /// MultiPatchJapanesePatchOld.esp masters it, translates it to "旧訳・
    /// 灰塵の斧" (an old/deprecated translation). MultiPatchJapanesePatchRevised.esp
    /// masters the OLD patch (loads after it) and re-translates the SAME
    /// record to "エンバーフォールの斧" (the revised, more current
    /// translation). MultiPatchQuestMod.esp masters MultiPatchMod.esp and
    /// overrides back to English, and wins.
    /// When: PickUpTarget -> Translation is run.
    /// Then: the REVISED translation ("エンバーフォールの斧") should be
    /// recovered, not the old one -- the nearest-to-winner precedent in the
    /// chain is the most current/intentional one.
    ///
    /// Confirmed RED (2026-08-29): stays unresolved today, exactly as
    /// predicted (no corpus entry links "Sjpts Emberfall Axe" to either
    /// Japanese candidate).</summary>
    [Fact]
    public void Run_ThenTranslate_PatternD_RevisedTranslationClosestToWinnerIsPreferredOverOlderOne()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_pattern_d_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMo2Instance(root,
            [
                ("MultiPatchModFolder", "MultiPatchMod.esp"),
                ("MultiPatchJapanesePatchOldFolder", "MultiPatchJapanesePatchOld.esp"),
                ("MultiPatchJapanesePatchRevisedFolder", "MultiPatchJapanesePatchRevised.esp"),
                ("MultiPatchQuestModFolder", "MultiPatchQuestMod.esp"),
            ]);
            var lines = RunPipeline(mo2Dir, root, "MultiPatchQuestMod.esp");

            Assert.True(lines.ContainsKey("Sjpts Emberfall Axe"));
            Assert.Equal("エンバーフォールの斧", lines["Sjpts Emberfall Axe"].Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Pattern B' (direct-translation / "2-file" structure, per user
    /// clarification 2026-08-29): xTranslator's MORE common real-world usage
    /// is not producing a SEPARATE translation-patch plugin (patterns
    /// A-E/the blind test all used that "3-file" shape) -- it's editing the
    /// ORIGINAL plugin IN PLACE and redistributing it under the SAME
    /// filename, so only ONE file for that mod ever exists in the user's load
    /// order, and it directly contains Japanese. The equally-common mirror
    /// image: a mod ORIGINALLY authored in Japanese by its own creator (no
    /// translation involved at all) gets extended by an overseas user's
    /// expansion mod. Both stories produce the IDENTICAL structural shape
    /// tested here, so one fixture covers both.
    ///
    /// Given: JpOriginalMod.esp directly defines a WEAP whose ONLY text is
    /// Japanese ("彼岸の剣") -- no separate English-only file ever existed for
    /// this mod, so there is NO reference English text anywhere in the chain
    /// to compare against for staleness. ForeignExpansionMod.esp masters it
    /// and overrides the SAME record to a MEANINGFULLY DIFFERENT English name
    /// ("Sjpts Foreign Cursed Sword", not just a translation of "彼岸の剣"),
    /// and wins.
    /// When: PickUpTarget -> Translation is run.
    /// Then (REVISED per user decision, 2026-08-29 -- same as Pattern B):
    /// "彼岸の剣" SHOULD be applied, with a warning. This case is exactly WHY
    /// that decision was needed -- with no reference text to compare against
    /// at all, the tool has no way to distinguish "same item, just
    /// reformatted" from "repurposed FormKey for a different item" (compare
    /// Pattern D', which needs the identical mechanism to actually recover a
    /// revised translation). Trying to withhold application only when
    /// "meaningfully different" is undecidable from text alone -- so the
    /// tool applies uniformly and lets the log's warning carry the nuance,
    /// exactly like the case where a reference EXISTS and is stale (Pattern
    /// B).
    ///
    /// CORRECTION (2026-08-29): the first version of this test asserted the
    /// opposite and passed only because nothing currently links these two
    /// strings -- see Pattern B's remarks for the identical correction.</summary>
    [Fact]
    public void Run_ThenTranslate_PatternBPrime_DirectlyTranslatedModMeaningfullyOverriddenAppliesStaleTranslationWithWarning()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_pattern_b_prime_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMo2Instance(root,
            [
                ("JpOriginalModFolder", "JpOriginalMod.esp"),
                ("ForeignExpansionModFolder", "ForeignExpansionMod.esp"),
            ]);
            var lines = RunPipeline(mo2Dir, root, "ForeignExpansionMod.esp");

            Assert.True(lines.ContainsKey("Sjpts Foreign Cursed Sword"));
            Assert.Equal("彼岸の剣", lines["Sjpts Foreign Cursed Sword"].Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Pattern D' (direct-translation / "2-file" structure): the
    /// SAME structural point as B' above, but for the tie-break rule instead
    /// of the staleness safeguard -- the original Japanese author (or a
    /// community translator editing in place) releases a wording revision of
    /// their own mod, still directly in Japanese (not a separate "patch"
    /// file), before a foreign expansion mod overrides back to English.
    ///
    /// Given: JpOriginalMod2.esp directly defines "たそがれの刃" (v1 wording).
    /// JpOriginalMod2Revised.esp masters it and re-translates the SAME
    /// record to "黄昏の刃" (v2 wording, the author's own revision, still
    /// directly in Japanese). ForeignExpansionMod2.esp masters
    /// JpOriginalMod2.esp and overrides back to English ("Sjpts Twilight
    /// Edge"), and wins.
    /// When: PickUpTarget -> Translation is run.
    /// Then: the REVISED wording ("黄昏の刃") should be recovered, not the
    /// original v1 ("たそがれの刃") -- same tie-break rule as Pattern D,
    /// verified under the more common single-file translation structure.
    ///
    /// Confirmed RED (2026-08-29): stays unresolved today, exactly as
    /// predicted (no corpus entry links "Sjpts Twilight Edge" to either
    /// Japanese wording).</summary>
    [Fact]
    public void Run_ThenTranslate_PatternDPrime_DirectlyTranslatedModRevisedWordingIsPreferredOverOriginalWording()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_pattern_d_prime_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMo2Instance(root,
            [
                ("JpOriginalMod2Folder", "JpOriginalMod2.esp"),
                ("JpOriginalMod2RevisedFolder", "JpOriginalMod2Revised.esp"),
                ("ForeignExpansionMod2Folder", "ForeignExpansionMod2.esp"),
            ]);
            var lines = RunPipeline(mo2Dir, root, "ForeignExpansionMod2.esp");

            Assert.True(lines.ContainsKey("Sjpts Twilight Edge"));
            Assert.Equal("黄昏の刃", lines["Sjpts Twilight Edge"].Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Pattern E (already covered by existing DSD -- the cross-mod
    /// machinery must never even engage): a record has BOTH a cross-mod
    /// Japanese precedent in its chain AND an existing DSD translation
    /// patch that already covers it.
    ///
    /// Given: DsdCoveredMod.esp defines "Sjpts Gilded Hammer".
    /// DsdCoveredJapanesePatch.esp masters it, translates it to "金箔の槌"
    /// (a cross-mod precedent, same shape as the other patterns).
    /// DsdCoveredQuestMod.esp masters DsdCoveredMod.esp, overrides back to
    /// English "Sjpts Gilded Hammer", and wins. An EXISTING DSD coverage file
    /// (Fixtures/Integration/DsdCoveredModDsd/ExistingCommunityPatch.json)
    /// already targets this exact (FormID, WEAP FULL, index) with a
    /// DIFFERENT, independently-authored Japanese string, "金箔のハンマー".
    /// When: PickUpTarget -> Translation is run.
    /// Then: the record should not become a translation candidate at all
    /// (already covered by DSD, exactly like scenario④) -- the cross-mod
    /// precedent ("金箔の槌") must never surface or compete with the DSD
    /// translation.</summary>
    [Fact]
    public void Run_ThenTranslate_PatternE_ExistingDsdCoverageTakesPrecedenceOverCrossModPrecedent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_pattern_e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMo2Instance(root,
            [
                ("DsdCoveredModFolder", "DsdCoveredMod.esp"),
                ("DsdCoveredJapanesePatchFolder", "DsdCoveredJapanesePatch.esp"),
                ("DsdCoveredQuestModFolder", "DsdCoveredQuestMod.esp"),
            ], (mo2Dir, mods) =>
            {
                var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
                var questModDir = Path.Combine(mo2Dir, "mods", "DsdCoveredQuestModFolder");
                var dsdDir = Path.Combine(questModDir, "SKSE", "Plugins", "DynamicStringDistributor", "DsdCoveredQuestMod.esp");
                Directory.CreateDirectory(dsdDir);
                File.Copy(
                    Path.Combine(fixturesDir, "DsdCoveredModDsd", "ExistingCommunityPatch.json"),
                    Path.Combine(dsdDir, "ExistingCommunityPatch.json"));
            });

            using var pickUpTargetLog = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");
            var result = PickUpTargetRunner.Run(mo2Dir, pickUpTargetLog);

            Assert.DoesNotContain(result.Candidates, c => c.CurrentText == "Sjpts Gilded Hammer");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
