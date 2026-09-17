namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// 2026-09-18: 翻訳詳細ウィンドウ（TranslationDetailForm/InterfaceTextDetailForm）
/// で、CLI側が書き出すTranslationCheck列（⑤⑥のLLM応答にのみ設定される、
/// 半角英数字の混入度合いによる5段階の機械分類——Translation/
/// TranslationQualityChecker.csの remarks 参照）に応じて行の背景色を決める
/// （ユーザー要望）。GUIはCore/Translationへの参照を持たない設計
/// （design/gui_architecture.md）のため、この2フォームで共有できるよう
/// GUIプロジェクト内に独立して置く。
///
/// 白（問題なし・未チェック）から赤（最も深刻）へのグラデーション。
/// 「真っ赤だと見づらい」というユーザー指摘を踏まえ、最も深刻な
/// NoJapaneseでも彩度を抑えた読みやすい赤に留める。
/// </summary>
public static class TranslationCheckColors
{
    // 深刻度の低い順。ここに無い値（空欄・"AllJapanese"・未知の値）は
    // severity=0（白）扱いになる——安全側のフォールバック。
    private static readonly string[] SeverityOrder =
    [
        "ContainsTagAlphaNumeric",
        "ContainsPartsOfAlphaNumeric",
        "ContainsMostOfAlphaNumeric",
        "NoJapanese",
    ];

    public static Color BackColorFor(string translationCheck)
    {
        var index = Array.IndexOf(SeverityOrder, translationCheck);
        if (index < 0) return Color.White;

        var severity = index + 1; // 1..SeverityOrder.Length
        var t = (double)severity / SeverityOrder.Length;
        var channel = (int)Math.Round(255 - 115 * t); // 255(白) -> 140(読みやすい赤)
        return Color.FromArgb(255, channel, channel);
    }
}
