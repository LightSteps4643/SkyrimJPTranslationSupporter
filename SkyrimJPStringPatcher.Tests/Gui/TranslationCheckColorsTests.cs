using System.Drawing;
using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// 2026-09-18: 翻訳詳細ウィンドウ（TranslationDetailForm/InterfaceTextDetailForm）
/// で、TranslationCheck列の値に応じて行の背景色を赤〜白のグラデーションで
/// 変える機能（ユーザー要望）。「真っ赤だと見づらい」ため、最も深刻な
/// NoJapaneseでも薄めの赤に留める。空欄・AllJapanese（＝チェック対象外／
/// 問題なし）はどちらも白（着色なし）。
/// </summary>
public class TranslationCheckColorsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("AllJapanese")]
    public void NoIssueOrNotChecked_IsWhite(string translationCheck)
    {
        Assert.Equal(Color.White, TranslationCheckColors.BackColorFor(translationCheck));
    }

    [Fact]
    public void UnrecognizedValue_FallsBackToWhite()
    {
        // 将来の分類追加・想定外の値でも、真っ白に倒すのが安全側。
        Assert.Equal(Color.White, TranslationCheckColors.BackColorFor("SomeFutureCategory"));
    }

    [Fact]
    public void SeverityIncreasesMonotonically_TowardsRed()
    {
        var order = new[] { "AllJapanese", "ContainsTagAlphaNumeric", "ContainsPartsOfAlphaNumeric", "ContainsMostOfAlphaNumeric", "NoJapanese" };
        var colors = order.Select(TranslationCheckColors.BackColorFor).ToList();

        for (var i = 1; i < colors.Count; i++)
        {
            // 赤(R)は一定、緑・青が段階的に減っていく（＝白から赤へ近づく）。
            Assert.Equal(255, colors[i].R);
            Assert.True(colors[i].G < colors[i - 1].G, $"expected G to decrease from {order[i - 1]} to {order[i]}");
            Assert.Equal(colors[i].G, colors[i].B); // 常に G == B（赤単色方向のグラデーション）
        }
    }

    [Fact]
    public void MostSevere_IsNotPureRed()
    {
        // 「真っ赤だと見づらい」——最も深刻なNoJapaneseでも、G/Bが0になり
        // きらない、視認性を保った赤に留める。
        var color = TranslationCheckColors.BackColorFor("NoJapanese");
        Assert.True(color.G > 60, $"expected a readable (not pure) red, got G={color.G}");
    }
}
