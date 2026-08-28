using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Integration;

/// <summary>
/// Desired-behavior scenarios for the real PickUpTarget -> Translation handoff
/// (2026-08-28, agreed with the user as Given/前提条件・When/操作・Then/期待する
/// 結果 — see DESIGN_NOTES.md for the full list). Unlike the rest of this test
/// suite, these are NOT organized around one class's own logic — each test
/// models one realistic MO2 scenario end-to-end (PickUpTargetRunner.Run ->
/// candidates.tsv/corpus.tsv -> PromptGenerator.RunOne, exactly like Program.cs's
/// own pickuptarget/translation subcommands wire them together) and checks the
/// resulting translations.tsv, which is the one shared checkpoint every
/// scenario naturally converges on.
///
/// Scenarios ①④⑤ (this file) all reuse the existing
/// Fixtures/PickUpTarget/StaleTest.esp + StaleTestDsd/ExistingCommunityPatch.json
/// fixture (already established by DsdCoverageAndStaleTests, which verifies the
/// PickUpTarget-only half of this same behavior) — 3 WEAP records:
/// - "Iron Blade Updated" (000800): existing DSD's "original" matches exactly
///   -> fully covered, never a candidate at all (④).
/// - "Steel Blade New" (000801): existing DSD's "original" is "Steel Blade Old"
///   -> stale (⑤).
/// - "Bronze Blade" (000802): no DSD entry -> an ordinary new candidate (①).
/// </summary>
public class PickUpTargetToTranslationScenarioTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var dsdDir = Path.Combine(modDir, "SKSE", "Plugins", "DynamicStringDistributor", "StaleTest.esp");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(dsdDir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget");
        File.Copy(Path.Combine(fixturesDir, "StaleTest.esp"), Path.Combine(modDir, "StaleTest.esp"));
        File.Copy(
            Path.Combine(fixturesDir, "StaleTestDsd", "ExistingCommunityPatch.json"),
            Path.Combine(dsdDir, "ExistingCommunityPatch.json"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*StaleTest.esp\r\n");

        return mo2Dir;
    }

    /// <summary>Runs the real PickUpTarget -> Translation handoff exactly like
    /// Program.cs's own "pickuptarget" then "translation" subcommands do:
    /// PickUpTargetRunner.Run's result is written to candidates.tsv/corpus.tsv
    /// via the same CandidateIo/CorpusIo this tool's CLI itself uses, then fed
    /// into PromptGenerator.RunOne exactly as-is — no shortcuts that would let
    /// a real serialization-boundary bug slip past.</summary>
    private static (PickUpTargetResult PickUpTargetResult, Dictionary<string, (string Japanese, string Notes)> Translations, string PromptText) RunPickUpTargetThenTranslation(
        string mo2Dir, string root, string targetPlugin, bool includeStale = false, bool discardUserEdits = false, TranslationStageOptions? stageOptions = null,
        (string Plugin, string RecordType, string Source, string Dest)[]? xTranslatorImports = null)
    {
        var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
        Directory.CreateDirectory(pickUpTargetOutDir);
        PickUpTargetResult pickUpTargetResult;
        using (var pickUpTargetLog = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget"))
        {
            pickUpTargetResult = PickUpTargetRunner.Run(mo2Dir, pickUpTargetLog, includeStale);
        }
        var candidatesTsvPath = Path.Combine(pickUpTargetOutDir, "candidates.tsv");
        var corpusTsvPath = Path.Combine(pickUpTargetOutDir, "corpus.tsv");
        CandidateIo.WriteTsv(candidatesTsvPath, pickUpTargetResult.Candidates);
        CorpusIo.WriteTsv(corpusTsvPath, pickUpTargetResult.Corpus);

        var translationOutDir = Path.Combine(root, "Translation", "out_temp");
        var importDir = Path.Combine(root, "Translation", "import"); // deliberately not created unless xTranslatorImports is given -> "no import" (XTranslatorImporter tolerates this)
        if (xTranslatorImports is { Length: > 0 })
        {
            Directory.CreateDirectory(importDir);
            foreach (var group in xTranslatorImports.GroupBy(e => e.Plugin))
            {
                var strings = string.Join("\n", group.Select(e =>
                    $"    <String>\n      <REC>{e.RecordType.Replace(' ', ':')}</REC>\n      <Source>{e.Source}</Source>\n      <Dest>{e.Dest}</Dest>\n    </String>"));
                var xml = $"<SSTXMLRessources>\n  <Params>\n    <Addon>{group.Key}</Addon>\n  </Params>\n  <Content>\n{strings}\n  </Content>\n</SSTXMLRessources>";
                File.WriteAllText(Path.Combine(importDir, $"{PluginFolderName(group.Key)}.xml"), xml);
            }
        }
        using (var translationLog = RunLog.Open(Path.Combine(root, "Translation"), "Translation"))
        {
            PromptGenerator.RunOne(candidatesTsvPath, corpusTsvPath, importDir, targetPlugin, translationOutDir, translationLog,
                discardUserEdits: discardUserEdits, stageOptions: stageOptions);
        }

        var pluginDir = Path.Combine(translationOutDir, PluginFolderName(targetPlugin));
        var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
        var promptPath = Path.Combine(pluginDir, "prompt.txt");
        var promptText = File.Exists(promptPath) ? File.ReadAllText(promptPath) : "";
        return (pickUpTargetResult, translations, promptText);
    }

    // Mirrors PromptGenerator.MakeSafeFolderName (internal, not visible to this
    // test project) -- for a plain ".esp" filename with no invalid filename
    // characters, this is just the extension stripped.
    private static string PluginFolderName(string plugin) => Path.GetFileNameWithoutExtension(plugin);

    private static Dictionary<string, (string Japanese, string Notes)> ReadTranslationsTemplate(string path)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 6) continue;
            result[TsvEscaping.Unescape(parts[3])] = (TsvEscaping.Unescape(parts[4]), TsvEscaping.Unescape(parts[5]));
        }
        return result;
    }

    /// <summary>① 原文英語のレコードが翻訳対象として出力される。
    /// 実施イメージ: 新しく導入した武器MODの剣が英語名のまま、他のMODやDSDには
    /// 一切関与されていない、最も単純なケース（"Bronze Blade"、DSDカバーなし）。</summary>
    [Fact]
    public void PlainEnglishRecord_WithNoDsdCoverage_BecomesATranslationCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_plain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            var (_, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "StaleTest.esp");

            Assert.True(translations.ContainsKey("Bronze Blade"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>④ 既存DSDカバー済みのレコードは翻訳対象にならない。
    /// 実施イメージ: 有名な武器MOD向けに有志配布のDSD翻訳パッチを導入済みで、
    /// 再スキャンしてもカバー済みの武器名は翻訳対象に出てこない
    /// （"Iron Blade Updated"、DSDのoriginalが現在の原文と完全一致）。</summary>
    [Fact]
    public void RecordAlreadyCoveredByExistingDsd_NeverBecomesATranslationCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_covered_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            var (_, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "StaleTest.esp");

            Assert.False(translations.ContainsKey("Iron Blade Updated"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>⑤ staleな既存DSD訳（既定、--include-stale無し）は翻訳対象にならない。
    /// `--include-stale`ありのケース（既存の旧原文/旧訳がprompt.txtに引き継がれる
    /// 挙動）は、現状GUIから一切到達できない（CLI直接利用者のみの機能、
    /// SkyrimJPStringPatcherGui配下を検索して該当ゼロを確認済み）ため、
    /// Integration観点では対象外とした（ユーザー判断、2026-08-28）。その挙動自体
    /// は既存のPickUpTarget単体テスト（DsdCoverageAndStaleTests）で検証済み。
    /// 実施イメージ: 翻訳パッチが古いバージョンのMOD向けで、MOD側の更新で武器名の
    /// 英語表記が変わった（"Steel Blade Old"→"Steel Blade New"）。DSD自体はFormID
    /// 一致で適用され続けるが、既定ではこのツールは静かに見過ごす。</summary>
    [Fact]
    public void StaleExistingDsdTranslation_WithoutIncludeStale_DoesNotBecomeACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_stale_off_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            var (_, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "StaleTest.esp", includeStale: false);

            Assert.False(translations.ContainsKey("Steel Blade New"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildPriorityOverrideMo2Instance(string root, bool withDsdCoverageForWinner = false)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var baseDir = Path.Combine(mo2Dir, "mods", "PriorityModBaseFolder");
        var patchDir = Path.Combine(mo2Dir, "mods", "PriorityModPatchFolder");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(patchDir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
        File.Copy(Path.Combine(fixturesDir, "PriorityModBase.esp"), Path.Combine(baseDir, "PriorityModBase.esp"));
        File.Copy(Path.Combine(fixturesDir, "PriorityModPatch.esp"), Path.Combine(patchDir, "PriorityModPatch.esp"));

        if (withDsdCoverageForWinner)
        {
            // A real DSD json's FormID always names the DEFINING plugin (the
            // record's own master, PriorityModBase.esp) regardless of which
            // plugin's own DSD folder it's placed under -- matches how DSD
            // itself resolves FormIDs. Placed under the WINNING plugin's own
            // folder (PriorityModPatch.esp), modeling a patch author who ships
            // a translation specifically for their own patched value.
            var dsdDir = Path.Combine(patchDir, "SKSE", "Plugins", "DynamicStringDistributor", "PriorityModPatch.esp");
            Directory.CreateDirectory(dsdDir);
            File.Copy(
                Path.Combine(fixturesDir, "PriorityModPatchDsd", "ExistingCommunityPatch.json"),
                Path.Combine(dsdDir, "ExistingCommunityPatch.json"));
        }

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+PriorityModPatchFolder\r\n+PriorityModBaseFolder\r\n");
        // Base loads first, patch loads last (wins) -- plugins.txt order is what
        // actually decides the winner, not modlist.txt's priority order (which
        // only matters for loose-file VFS merging, unused by this scenario).
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*PriorityModBase.esp\r\n*PriorityModPatch.esp\r\n");

        return mo2Dir;
    }

    /// <summary>② 複数MODが同じレコードを上書きしている場合、ロードオーダー上の
    /// 勝者MODの英文だけが翻訳対象になる。
    /// 実施イメージ: ベースの防具MOD「ArmorPack.esp」が「Iron Guard」という名前を
    /// 定義し、それを上書きする互換パッチMOD「ArmorPack Patch.esp」が同じ防具の
    /// 名前を「Iron Guardian」に変更。パッチが後に読まれるため、翻訳対象になるのは
    /// 「Iron Guardian」の方だけ。</summary>
    [Fact]
    public void MultipleModsOverrideTheSameRecord_OnlyTheLoadOrderWinnersTextBecomesACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_priority_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildPriorityOverrideMo2Instance(root);
            var (pickUpTargetResult, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "PriorityModPatch.esp");

            // The winner's text is the only one that ever becomes a candidate at
            // all -- the loser's text ("Iron Guard") never even reaches
            // candidates.tsv, since PickUpTarget only ever considers each
            // (FormKey, type, index) chain's WINNING entry.
            var candidate = Assert.Single(pickUpTargetResult.Candidates, c => c.RecordType == "WEAP FULL");
            Assert.Equal("Iron Guardian", candidate.CurrentText);
            Assert.Equal("PriorityModPatch.esp", candidate.WinningPlugin);

            Assert.True(translations.ContainsKey("Iron Guardian"));
            Assert.False(translations.ContainsKey("Iron Guard"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>②＋④の組み合わせ: 上書きされた勝者テキストが既存DSDでカバーされて
    /// いる場合も、翻訳対象にならない。現行仕様では「上書きの有無」はDSDカバー
    /// 判定に影響しない（判定は常に勝者テキストに対して行われる）はずだが、将来
    /// 何らかの変更で崩れる可能性があるため、②単体・④単体とは別に明示的に
    /// 固定しておく。
    /// 実施イメージ: 互換パッチMODが上書きした後の防具名に対して、有志が
    /// DSD翻訳パッチを配布している（パッチMOD自体を対象にした翻訳）。</summary>
    [Fact]
    public void OverriddenWinnerAlreadyCoveredByExistingDsd_NeverBecomesATranslationCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_priority_covered_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildPriorityOverrideMo2Instance(root, withDsdCoverageForWinner: true);
            var (pickUpTargetResult, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "PriorityModPatch.esp");

            Assert.DoesNotContain(pickUpTargetResult.Candidates, c => c.RecordType == "WEAP FULL");
            Assert.False(translations.ContainsKey("Iron Guardian"));
            Assert.False(translations.ContainsKey("Iron Guard"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildVanillaHarvestMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var sourceDir = Path.Combine(mo2Dir, "mods", "SourceFolder");
        var sourceStringsDir = Path.Combine(sourceDir, "Strings");
        var targetDir = Path.Combine(mo2Dir, "mods", "TargetFolder");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(sourceStringsDir);
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
        File.Copy(Path.Combine(fixturesDir, "SjptsVanillaLikeSource", "SjptsVanillaLikeSource.esp"), Path.Combine(sourceDir, "SjptsVanillaLikeSource.esp"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fixturesDir, "SjptsVanillaLikeSource", "Strings")))
            File.Copy(file, Path.Combine(sourceStringsDir, Path.GetFileName(file)));
        File.Copy(Path.Combine(fixturesDir, "SjptsUnrelatedMod", "SjptsUnrelatedMod.esp"), Path.Combine(targetDir, "SjptsUnrelatedMod.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TargetFolder\r\n+SourceFolder\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*SjptsVanillaLikeSource.esp\r\n*SjptsUnrelatedMod.esp\r\n");

        return mo2Dir;
    }

    /// <summary>③ バニラ相当のローカライズ済みMODの訳文が、無関係な別MODの同名
    /// アイテムに使われる（このツールが解決したい主要ケースそのもの）。
    /// 実施イメージ: バニラ本体の翻訳済みアイテム名（例: 鉄の剣）が、全く無関係な
    /// 別MODで偶然同じ英語名のアイテムとして再利用され、それがそのまま自動解決に
    /// 使われる。
    /// Fixtures/Integration/SjptsVanillaLikeSource.esp: ローカライズ済み
    /// （UsingLocalization=true）、"Sjpts Ornate Blade"→"装飾の刃" を内蔵。
    /// Fixtures/Integration/SjptsUnrelatedMod.esp: 非ローカライズ、全く同じ英語名
    /// "Sjpts Ornate Blade" を持つ別アイテム（命名の衝突）。</summary>
    [Fact]
    public void VanillaLikeLocalizedTranslation_ResolvesAnUnrelatedModsIdenticallyNamedItem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_vanillaharvest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildVanillaHarvestMo2Instance(root);
            var (pickUpTargetResult, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "SjptsUnrelatedMod.esp");

            // The source's own item is already Japanese -- never a candidate.
            Assert.DoesNotContain(pickUpTargetResult.Candidates, c => c.CurrentText == "装飾の刃" || c.WinningPlugin == "SjptsVanillaLikeSource.esp");

            // The harvested pair reached the corpus.
            Assert.Contains(pickUpTargetResult.Corpus, e => e.English == "Sjpts Ornate Blade" && e.Japanese == "装飾の刃");

            // The unrelated mod's identically-named item got auto-resolved from it.
            Assert.True(translations.ContainsKey("Sjpts Ornate Blade"));
            var (japanese, notes) = translations["Sjpts Ornate Blade"];
            Assert.Equal("装飾の刃", japanese);
            Assert.StartsWith("AutoCorpus", notes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>⑥ xTranslatorインポート済み文字列が実候補を完全一致で自動解決する。
    /// 実施イメージ: 翻訳者が手作業でxTranslatorを使い、あるMODの候補をまとめて
    /// 訳し、その結果をXMLとして`Translation/import/`に配置した。GUIから見ても
    /// CLIオプションではなくファイル配置だけで機能するため、到達可能な操作。
    /// "Bronze Blade"（`StaleTest.esp`、DSDカバーなし、①の4条件を満たす）を対象に、
    /// 全く同じ英語テキストのxTranslator XMLを用意する。</summary>
    [Fact]
    public void XTranslatorImportedTranslation_ResolvesTheMatchingRealCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_xtimport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            var (_, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "StaleTest.esp",
                xTranslatorImports: [("StaleTest.esp", "WEAP FULL", "Bronze Blade", "青銅の刃")]);

            Assert.True(translations.ContainsKey("Bronze Blade"));
            var (japanese, notes) = translations["Bronze Blade"];
            Assert.Equal("青銅の刃", japanese);
            Assert.Equal("AutoCorpusImported", notes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>⑥の裏付け: xTranslatorインポートに、既にDSD/他MODで日本語化済みの
    /// レコードと同じ英語テキストのエントリが（古い/紛れ込んだ等の理由で）存在
    /// していても、そのレコードは翻訳対象として復活しない。除外判定は
    /// PickUpTarget段階（インポートを一切読む前）で完結しているため、インポート
    /// データの存在に影響されないことを確認する頑健性のテスト。
    /// 実施イメージ: 昔作ったxTranslator訳のXMLをそのまま`Translation/import/`に
    /// 置き続けているが、その後そのMODの一部が公式に翻訳されたDSDパッチに置き
    /// 換わった。古いインポートファイルを消し忘れていても、既にカバー済みの
    /// レコードが二重に翻訳対象へ復活してはいけない。</summary>
    [Fact]
    public void XTranslatorImportForAnAlreadyDsdCoveredRecord_DoesNotResurrectItAsACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_xtimport_stale_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            // "Iron Blade Updated" is already covered by the existing DSD patch
            // (scenario ④) -- an xTranslator entry for the exact same text is a
            // leftover/stray import that must have no effect on it.
            var (_, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "StaleTest.esp",
                xTranslatorImports: [("StaleTest.esp", "WEAP FULL", "Iron Blade Updated", "紛れ込んだ古いインポート訳")]);

            Assert.False(translations.ContainsKey("Iron Blade Updated"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildGlossaryTargetMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
        File.Copy(Path.Combine(fixturesDir, "SjptsGlossaryTarget", "SjptsGlossaryTarget.esp"), Path.Combine(modDir, "SjptsGlossaryTarget.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*SjptsGlossaryTarget.esp\r\n");

        return mo2Dir;
    }

    /// <summary>Overwrites (not merges) this plugin's mod-scoped glossary with
    /// one filled row -- matches PromptGeneratorTests' own helper, so a test
    /// can force ④NameFallbackTranslator resolution deterministically without
    /// depending on ModGlossary.WriteTemplate's own merge/regeneration timing.</summary>
    private static void SeedModGlossary(string plugin, string english, string japanese)
    {
        var path = Path.Combine(ModGlossary.DirectoryPath, Path.GetFileNameWithoutExtension(plugin) + ".tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{english}\t{japanese}\n");
    }

    /// <summary>⑦ MOD別グロッサリーが実候補の自動翻訳に反映される（④
    /// NameFallbackTranslator経由）。②意味合成・③音訳分解は別項目⑨で扱う。
    /// 実施イメージ: 独自造語が何度も出てくるMODの専用グロッサリーに訳語を
    /// 記入し、対応する候補が解決される。
    /// 今回新たに検証した差分: 同機能はPromptGeneratorTestsで合成candidates.tsv
    /// 経由では検証済みだが、PickUpTarget由来の実Mutagenレコードからこの経路まで
    /// 正しく流れ着くかは未検証だった。</summary>
    [Fact]
    public void ModScopedGlossary_ResolvesARealCandidateViaNameFallback()
    {
        const string plugin = "SjptsGlossaryTarget.esp";
        SeedModGlossary(plugin, "Vrenn", "ヴレン");
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_modglossary_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildGlossaryTargetMo2Instance(root);
            var (_, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, plugin);

            Assert.True(translations.ContainsKey("Vrenn"));
            var (japanese, notes) = translations["Vrenn"];
            Assert.Equal("ヴレン", japanese);
            Assert.Equal("TranslationNameFallback", notes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>⑧a 汎用名前グロッサリー（Data/name_glossary.tsv）が実候補の
    /// 自動翻訳に反映される（④NameFallbackTranslator経由、MOD別グロッサリー
    /// ⑦とは別の、全MOD共通のグロッサリー源が正しく配線されているかを確認）。
    /// ⑧b（参照用語集）は③⑥と構造的に同じコーパス追加パターンのため対象外
    /// （ユーザー確認済み）。
    /// ハードコード回避のため、実データ（Data/name_glossary.tsv、テスト
    /// プロジェクト自身のビルド出力コピー＝実リポジトリと同一内容）の1件目を
    /// 実行時に読み取り、その英語テキストをMutagenでその場限りのMODに書き込む
    /// （固定フィクスチャではなく、常に現在のData/の中身と一致させるため）。</summary>
    [Fact]
    public void GlobalNameGlossary_ResolvesARealCandidateViaNameFallback()
    {
        var glossaryPath = Path.Combine(AppContext.BaseDirectory, "Data", "name_glossary.tsv");
        var firstLine = File.ReadLines(glossaryPath, System.Text.Encoding.UTF8).First(l => l.Length > 0 && l.Contains('\t'));
        var tab = firstLine.IndexOf('\t');
        // Data/name_glossary.tsv stores its dictionary KEY in lowercase (lookup
        // is case-insensitive), but a real Bethesda display name is always Title
        // Case -- NameFieldFilter.LooksLikeNameField requires every word to
        // contain an uppercase letter, specifically to reject sentence-like
        // internal notes. Capitalize to match how this word would actually
        // appear as a real candidate.
        var english = char.ToUpperInvariant(firstLine[0]) + firstLine[1..tab];
        var japanese = firstLine[(tab + 1)..];

        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_nameglossary_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        try
        {
            const string plugin = "SjptsNameGlossaryTarget.esp";
            var modKey = ModKey.FromNameAndExtension(plugin);
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var weapon = mod.Weapons.AddNew();
            weapon.EditorID = "SjptsNameGlossaryWord";
            weapon.Name = english;
            mod.WriteToBinary(Path.Combine(modDir, plugin));

            File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
                "[General]\r\n" +
                $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
                "selected_profile=@ByteArray(Default)\r\n");
            File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
            File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{plugin}\r\n");

            var (_, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, plugin);

            Assert.True(translations.ContainsKey(english));
            var (resolvedJapanese, notes) = translations[english];
            Assert.Equal(japanese, resolvedJapanese);
            Assert.Equal("TranslationNameFallback", notes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildMeaningTranslitMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var sourceDir = Path.Combine(mo2Dir, "mods", "SourceFolder");
        var sourceStringsDir = Path.Combine(sourceDir, "Strings");
        var targetDir = Path.Combine(mo2Dir, "mods", "TargetFolder");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(sourceStringsDir);
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
        File.Copy(Path.Combine(fixturesDir, "SjptsMeaningSource", "SjptsMeaningSource.esp"), Path.Combine(sourceDir, "SjptsMeaningSource.esp"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fixturesDir, "SjptsMeaningSource", "Strings")))
            File.Copy(file, Path.Combine(sourceStringsDir, Path.GetFileName(file)));
        File.Copy(Path.Combine(fixturesDir, "SjptsMeaningTarget", "SjptsMeaningTarget.esp"), Path.Combine(targetDir, "SjptsMeaningTarget.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TargetFolder\r\n+SourceFolder\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*SjptsMeaningSource.esp\r\n*SjptsMeaningTarget.esp\r\n");

        return mo2Dir;
    }

    /// <summary>⑨ 意味合成/音訳分解で解決できる実候補。コーパスはテスト用に
    /// 直接注入するのではなく、実際にPickUpTargetを実行することで実Mutagen
    /// レコードから収穫される（ユーザー確認済み——実際の動作に近い、価値の
    /// 高いテスト）。
    /// Fixtures/Integration/SjptsMeaningSource.esp: バニラ相当のローカライズ
    /// 済みMOD。Blade/Buckleの2つのHEADに共通する3つのmodifier（Glimmeroot/
    /// Ashfall/Windrose）＋Ringという別HEADに3つの異なるmodifierを持たせ、
    /// CorpusMeaningTranslatorの学習閾値（MinHeadSupport=3・MinModifierHeads=2）
    /// を満たす。音訳分解用に独立した2単語（Nemra/Skol）も追加。
    /// Fixtures/Integration/SjptsMeaningTarget.esp: 無関係な非ローカライズMOD。
    /// "Glimmeroot Ring"（新しいmodifier+head組み合わせ、コーパスに無い）と
    /// "Nemraskol"（未知の複合語だが構成要素が既知）を持つ。
    /// 実施イメージ: バニラ本体に「Steel Sword→鋼の剣」等、複数の対訳が存在し、
    /// 別MODの「Steel Mace」（コーパスに無い新組み合わせ）が意味合成で自動解決
    /// される。</summary>
    [Fact]
    public void MeaningCompositionAndTransliterationDecomposition_ResolveRealCandidatesFromHarvestedCorpus()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_meaningtranslit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildMeaningTranslitMo2Instance(root);
            var (pickUpTargetResult, translations, _) = RunPickUpTargetThenTranslation(mo2Dir, root, "SjptsMeaningTarget.esp");

            // The corpus was genuinely harvested from the source mod's own
            // bilingual fields, not injected -- confirms the ③-style harvest
            // mechanism actually produced enough precedent pairs.
            Assert.Contains(pickUpTargetResult.Corpus, e => e.English == "Glimmeroot Blade" && e.Japanese == "きらめきの刃");
            Assert.Contains(pickUpTargetResult.Corpus, e => e.English == "Nemra" && e.Japanese == "ネムラ");

            Assert.True(translations.ContainsKey("Glimmeroot Ring"));
            var (meaningJapanese, meaningNotes) = translations["Glimmeroot Ring"];
            Assert.Equal("きらめきの指輪", meaningJapanese);
            Assert.Equal("AutoCorpusMeaning", meaningNotes);

            Assert.True(translations.ContainsKey("Nemraskol"));
            var (translitJapanese, translitNotes) = translations["Nemraskol"];
            Assert.Equal("ネムラスコル", translitJapanese);
            Assert.Equal("AutoCorpusTransliterate", translitNotes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildUnresolvableTargetMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");
        File.Copy(Path.Combine(fixturesDir, "SjptsUnresolvableTarget", "SjptsUnresolvableTarget.esp"), Path.Combine(modDir, "SjptsUnresolvableTarget.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*SjptsUnresolvableTarget.esp\r\n");

        return mo2Dir;
    }

    /// <summary>⑩ どの自動手法でも解決できない実候補が、未解決のまま`prompt.txt`
    /// に正しく列挙される。①とは目的が異なる別テスト——①は「候補になること」
    /// 自体、⑩は「解決手段が尽きたときの受け皿（prompt.txt）が正しく機能する
    /// こと」を検証する。
    /// 実施イメージ: 完全に新規の造語（そのMOD作者の独自ネーミング）で、
    /// コーパスにもグロッサリーにも手がかりが一切ない候補。
    /// 当初①の"Bronze Blade"を再利用しようとしたが、実データ
    /// （Data/name_glossary.tsv等、BuildContextが無条件でマージする）経由で
    /// 偶然「青銅の刀剣」に自動解決されてしまうことが判明——PromptGeneratorTests
    /// が警告していた「実在しそうな武器名は実データと衝突しうる」リスクが
    /// 実際に顕在化した例。①の目的には影響しないが⑩には不適切なため、明確に
    /// 架空の語（Sjpts Quorvenith Blazrukk）を使った専用フィクスチャに
    /// 差し替えた。</summary>
    [Fact]
    public void CandidateUnresolvableByAnyAutomaticMethod_StaysUnresolved_AndIsListedInThePrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_scenario_unresolved_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildUnresolvableTargetMo2Instance(root);
            var (_, translations, promptText) = RunPickUpTargetThenTranslation(mo2Dir, root, "SjptsUnresolvableTarget.esp");

            const string text = "Sjpts Quorvenith Blazrukk";
            Assert.True(translations.ContainsKey(text));
            var (japanese, notes) = translations[text];
            Assert.Equal("", japanese);
            Assert.Equal("", notes);

            Assert.Contains($"Target: \"{text}\"", promptText);
            Assert.Contains("Type:", promptText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
