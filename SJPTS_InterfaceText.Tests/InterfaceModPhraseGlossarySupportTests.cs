using SJPTS_InterfaceText;
using SkyrimJPStringPatcher.Translation;

namespace SJPTS_InterfaceText.Tests;

/// <summary>
/// 2026-09-18: Interface翻訳側にModPhraseGlossary（MOD特有語句の検出・
/// mod_glossary.tsv生成）を移植する際の課題への対応。ESP側は
/// PickUpTargetがロード順全体を1回スキャンして`ctx.AllCandidates`という
/// 「グローバルな母集団」を自然に用意できるが、Interfaceの`detect`はMOD
/// ごとに個別の`interface_translations.tsv`を書き出すだけで、集約済みの
/// 「全体コーパス」に相当するものが存在しない。共通のワークディレクトリ
/// （InterfaceTextWorkDir）配下に各MODの出力が兄弟フォルダとして存在する
/// ことを利用し、その場で走査して母集団を構築する。
/// </summary>
public class InterfaceModPhraseGlossarySupportTests
{
    [Fact]
    public void BuildGlobalFrequency_ScansAllSiblingModFolders()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"sjpts_glossary_global_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            InterfaceTranslationsTsv.Write(Path.Combine(workDir, "ModA", "interface_translations.tsv"),
                [new("$1", "Sjpts Frostwind Blade", "刃", true), new("$2", "Sjpts Frostwind Blade Alt", "刃2", true)]);
            InterfaceTranslationsTsv.Write(Path.Combine(workDir, "ModB", "interface_translations.tsv"),
                [new("$3", "Sjpts Frostwind Blade", "刃", true)]);

            var frequency = InterfaceModPhraseGlossarySupport.BuildGlobalFrequency(workDir);

            Assert.Equal(3, frequency.TotalDocs); // ModAの2行 + ModBの1行
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void BuildGlobalFrequency_NonexistentWorkDir_ReturnsEmptyFrequency()
    {
        var frequency = InterfaceModPhraseGlossarySupport.BuildGlobalFrequency(
            Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}"));

        Assert.Equal(0, frequency.TotalDocs);
    }

    /// <summary>実データで検証したのと同じ形（3つの候補が"Windrune Blade"を
    /// 共有する）——ESP側PromptGeneratorTestsの同名テストのInterface版。</summary>
    [Fact]
    public void WriteModGlossary_DetectsRecurringPhraseAndWritesFile()
    {
        var modWorkDir = Path.Combine(Path.GetTempPath(), $"sjpts_glossary_write_{Guid.NewGuid():N}");
        try
        {
            var localTexts = new List<string>
            {
                "Sjpts Daedric Windrune Blade", "Sjpts Dwarven Windrune Blade", "Sjpts Ebony Windrune Blade",
            };
            var globalFrequency = ModPhraseGlossary.GlobalNgramFrequency.Build(localTexts);

            InterfaceModPhraseGlossarySupport.WriteModGlossary(modWorkDir, "SjptsInterfaceGlossaryMod", localTexts, globalFrequency);

            var glossaryPath = ModPhraseGlossary.PathFor(modWorkDir);
            Assert.True(File.Exists(glossaryPath));
            var lines = File.ReadAllLines(glossaryPath).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.Contains(lines, l => l.StartsWith("Windrune Blade\t"));
        }
        finally
        {
            try { Directory.Delete(modWorkDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
