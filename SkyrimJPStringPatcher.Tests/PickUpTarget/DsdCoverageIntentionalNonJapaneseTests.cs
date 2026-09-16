using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// 2026-09-17: DSDカバレッジ判定（PickUpTargetRunner）は、既存のDSDエントリの
/// 訳文（"string"）が日本語を含むかどうか（LanguageDetector.ContainsJapanese）
/// でのみ「カバー済み」を判定していたため、本ツール自身が意図的に非日本語の
/// まま確定させた値（TranslationLocalLlmNoJapanese・ModifiedByUser・
/// AutoCorpusOverride等、いずれもGenerateDsdFile/DsdJsonGenerator.csの設計上
/// 正当に非日本語値を許容する）を含むDSDが既に存在していても、再読込のたびに
/// 未翻訳候補として検出し続けてしまう不具合があった（実データで確認済み、
/// 管理リポジトリのtodo記録参照）。
///
/// 修正: DSDエントリのstatusが"SJPTS_"接頭辞を持つ場合（＝本ツール自身の
/// 出力）は、内容（日本語を含むか）を問わず無条件でカバー済みとして扱う。
/// 接頭辞を持たない場合（他modのDSD等）は、従来通りContainsJapaneseで判定
/// する——安全側のデフォルトとして変更しない。
/// </summary>
public class DsdCoverageIntentionalNonJapaneseTests
{
    private static (string Mo2Dir, string Root) BuildFakeMo2Instance(string plugin, string dsdJson)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdnonja_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var dsdDir = Path.Combine(modDir, "SKSE", "Plugins", "DynamicStringDistributor", plugin);
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(dsdDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        var modKey = ModKey.FromNameAndExtension(plugin);
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
        var weapon = mod.Weapons.AddNew();
        weapon.EditorID = "SjptsDsdNonJapaneseTarget";
        weapon.Name = "Sjpts Gilded Hammer";
        mod.WriteToBinary(Path.Combine(modDir, plugin));

        File.WriteAllText(Path.Combine(dsdDir, "ExistingPatch.json"), dsdJson);

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{plugin}\r\n");

        return (mo2Dir, root);
    }

    [Fact]
    public void Run_SjptsPrefixedNonJapaneseDsdEntry_IsTreatedAsCoveredNotCandidate()
    {
        const string plugin = "SjptsDsdNonJa1.esp";
        var dsdJson =
            """
            [
              {
                "editor_id": "",
                "form_id": "000800|SjptsDsdNonJa1.esp",
                "index": 0,
                "type": "WEAP FULL",
                "original": "Sjpts Gilded Hammer",
                "string": "Sjpts Gilded Hammer",
                "status": "SJPTS_TranslationLocalLlmNoJapanese"
              }
            ]
            """;
        var (mo2Dir, root) = BuildFakeMo2Instance(plugin, dsdJson);
        try
        {
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale: false);

            Assert.DoesNotContain(result.Candidates, c => c.CurrentText == "Sjpts Gilded Hammer");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>他mod（本ツール以外）のDSDが非日本語の値をそのまま出力している
    /// レアケース——安全側のデフォルトとして、従来通り未翻訳候補になるべき
    /// （本ツールの決定でない以上、勝手に「意図的」と判断してはいけない）。</summary>
    [Fact]
    public void Run_NonSjptsNonJapaneseDsdEntry_IsStillTreatedAsCandidate()
    {
        const string plugin = "SjptsDsdNonJa2.esp";
        var dsdJson =
            """
            [
              {
                "editor_id": "",
                "form_id": "000800|SjptsDsdNonJa2.esp",
                "index": 0,
                "type": "WEAP FULL",
                "original": "Sjpts Gilded Hammer",
                "string": "Sjpts Gilded Hammer",
                "status": "SomeOtherTranslationTool"
              }
            ]
            """;
        var (mo2Dir, root) = BuildFakeMo2Instance(plugin, dsdJson);
        try
        {
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale: false);

            Assert.Contains(result.Candidates, c => c.CurrentText == "Sjpts Gilded Hammer");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>statusフィールド自体が無い（欠損）既存DSDでも、上記と同じ
    /// 安全側のデフォルト（ContainsJapaneseで判定）にフォールバックする。</summary>
    [Fact]
    public void Run_MissingStatusFieldNonJapaneseDsdEntry_IsStillTreatedAsCandidate()
    {
        const string plugin = "SjptsDsdNonJa3.esp";
        var dsdJson =
            """
            [
              {
                "editor_id": "",
                "form_id": "000800|SjptsDsdNonJa3.esp",
                "index": 0,
                "type": "WEAP FULL",
                "original": "Sjpts Gilded Hammer",
                "string": "Sjpts Gilded Hammer"
              }
            ]
            """;
        var (mo2Dir, root) = BuildFakeMo2Instance(plugin, dsdJson);
        try
        {
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale: false);

            Assert.Contains(result.Candidates, c => c.CurrentText == "Sjpts Gilded Hammer");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
