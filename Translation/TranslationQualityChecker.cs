using System.Text.RegularExpressions;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// 2026-09-18: 翻訳結果を機械的に判定できる範囲で分類し、translations.tsvの
/// 新規列"TranslationCheck"へ記録する（ユーザー要望）。「自然な訳かどうか」
/// のような踏み込んだ評価はできないが、半角英数字の混入度合いから、
/// レビューが必要そうな翻訳を機械的に見分けられるようにする。
///
/// Notes列（`SJPTS_TranslationLocalLlm`等、どの手法で解決したか）とは意図
/// して分離している——Notes列は複数箇所（DsdJsonGeneratorのstatus欄への
/// 直接コピー、SameModTrustTierの完全一致照合）で既知の値との厳密な一致を
/// 前提にしており、そこに品質情報を混ぜると壊れるため（2026-09-17検討）。
/// なお⑤/⑥のNotes列には既に手法名+"NoJapanese"という接尾辞の仕組みがあり、
/// これは今回も残す（①〜④には対応する仕組みが元々無かったため、この新規列
/// が全手法共通で"NoJapanese"相当を記録できるようになる、という位置づけ）。
///
/// 5段階の分類、重複時は後者ほど優先する（より深刻な方を採用——ユーザー
/// 判断）:
/// AllJapanese &lt; ContainsTagAlphaNumeric &lt; ContainsPartsOfAlphaNumeric
/// &lt; ContainsMostOfAlphaNumeric &lt; NoJapanese
///
/// 「タグ」は&lt;[^&gt;]*&gt;（issue #11の
/// <see cref="LlmBatchTranslationEngine.HasMatchingTagStructure"/>と同一
/// パターン）に限定する。プロンプトの固定指示文が明示的に保持を指示して
/// いるのはこの山括弧タグだけで、$Key形式のInterface\Translationsキー
/// 参照は、候補全体がそれだけで構成される場合はそもそも
/// PickUpTarget側で除外されLLMに到達せず、文中に部分的に埋め込まれた
/// 場合の保持指示も存在しないため、タグとして扱わない。
///
/// 「半分以上」の判定は正確に50%である必要はなく（ユーザー確認済み）、
/// 実装しやすさを優先して文字数（.NETの`string.Length`、過去の実測で
/// UTF-8バイト数とのズレが問題になった教訓を踏まえ、必ず文字数ベースで
/// 統一する）に対する半角英数字の割合で判定する。
/// </summary>
public static class TranslationQualityChecker
{
    private static readonly Regex TagPattern = new(@"<[^>]*>", RegexOptions.Compiled);

    private static bool IsHalfWidthAlphaNumeric(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    public static string Classify(string japanese)
    {
        if (string.IsNullOrEmpty(japanese) || !LanguageDetector.ContainsJapanese(japanese))
            return "NoJapanese";

        var totalAlphaNumeric = japanese.Count(IsHalfWidthAlphaNumeric);
        if (totalAlphaNumeric == 0) return "AllJapanese";

        var ratio = (double)totalAlphaNumeric / japanese.Length;
        if (ratio >= 0.5) return "ContainsMostOfAlphaNumeric";

        var withoutTags = TagPattern.Replace(japanese, "");
        var alphaNumericOutsideTags = withoutTags.Count(IsHalfWidthAlphaNumeric);
        return alphaNumericOutsideTags > 0 ? "ContainsPartsOfAlphaNumeric" : "ContainsTagAlphaNumeric";
    }
}
