using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SJPTS_InterfaceText;

/// <summary>
/// 2026-09-18: ESP側の`ModPhraseGlossary`（MOD特有語句の検出・
/// mod_glossary.tsv生成）をInterface翻訳側にも移植する際の橋渡し。
///
/// ESP側はPickUpTargetがロード順全体を1回スキャンして`ctx.AllCandidates`
/// という「グローバルな母集団」を自然に用意できるが、Interfaceの`detect`は
/// MODごとに個別の`interface_translations.tsv`を書き出すだけで、集約済みの
/// 「全体コーパス」に相当するものがそもそも存在しない（2026-09-18ユーザー
/// 指摘・検討）。各MODの出力が共通のワークディレクトリ配下に兄弟フォルダ
/// として存在することを利用し、<see cref="BuildGlobalFrequency"/>でその場を
/// 走査して代用する。
///
/// また、Interface翻訳には①〜④のコーパスベース自動解決という概念が
/// そもそも存在しない（全面的にLLM任せ）ため、`ModPhraseGlossary.
/// DetectCandidatePhrases`が要求する「既に解決済みかどうか」の判定には、
/// 空のコーパスで構築した`AutoTranslator`（何も解決しない）を渡す——
/// 検出ロジック自体（TF-IDFランキング・固有度計算除外単語リスト・
/// マークアップ除去等）はテキストのみに依存する汎用実装なので、そのまま
/// 機能する。
/// </summary>
public static class InterfaceModPhraseGlossarySupport
{
    /// <summary>workDir直下の各MODフォルダに既に`detect`/`translate`が
    /// 書き出した`interface_translations.tsv`を走査し、その英文（English列）
    /// をESP側の`ctx.AllCandidates`に相当する「ロード順全体」の母集団として
    /// 使う。呼び出し元（Program.csのRunTranslate）は、複数MODを1プロセスで
    /// 処理する場合でもこれを1回だけ構築し、使い回すこと——毎回MODごとに
    /// 再計算すると、ESP側で修正済みの「6倍速度低下」と同じ問題を
    /// 再発させてしまう。</summary>
    public static ModPhraseGlossary.GlobalNgramFrequency BuildGlobalFrequency(string workDir)
    {
        var allTexts = new List<string>();
        if (Directory.Exists(workDir))
        {
            foreach (var modDir in Directory.GetDirectories(workDir))
            {
                var tsvPath = Path.Combine(modDir, "interface_translations.tsv");
                if (!File.Exists(tsvPath)) continue;
                allTexts.AddRange(InterfaceTranslationsTsv.Read(tsvPath).Select(r => r.English));
            }
        }
        return ModPhraseGlossary.GlobalNgramFrequency.Build(allTexts);
    }

    /// <summary>このMOD自身の候補（<paramref name="localTexts"/>）を、事前に
    /// 構築済みの母集団と照らし合わせて検出し、mod_glossary.tsvへ書き出す。
    /// ESP側のWritePluginFilesWithDirが（LLM呼び出しの有無に関わらず）常に
    /// この処理を行うのと同じく、呼び出し元（RunTranslateOne）でも
    /// pending件数によらず毎回呼ぶこと。</summary>
    public static void WriteModGlossary(string modWorkDir, string modDisplayName, IReadOnlyList<string> localTexts, ModPhraseGlossary.GlobalNgramFrequency globalFrequency)
    {
        var auto = new AutoTranslator(Array.Empty<CorpusEntry>());
        var detected = ModPhraseGlossary.DetectCandidatePhrases(localTexts, globalFrequency, auto);
        ModPhraseGlossary.WriteTemplate(modWorkDir, modDisplayName, detected);
    }
}
