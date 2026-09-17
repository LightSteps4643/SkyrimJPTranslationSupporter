using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// 2026-09-18: 翻訳結果の品質を機械的に分類する仕組み（新規列
/// "TranslationCheck"、translations.tsv）。「自然かどうか」のような踏み込んだ
/// 評価はできないが、半角英数字の混入度合いから機械的に判定できる範囲を
/// 分類する（ユーザー要望）。
///
/// 分類は5段階、重複時は後者ほど優先（より深刻な方を採用する——ユーザー
/// 判断）:
/// AllJapanese &lt; ContainsTagAlphaNumeric &lt; ContainsPartsOfAlphaNumeric
/// &lt; ContainsMostOfAlphaNumeric &lt; NoJapanese
///
/// 「タグ」の定義は、issue #11の`LlmBatchTranslationEngine.HasMatchingTagStructure`
/// と同じ`&lt;[^&gt;]*&gt;`パターンに限定する——プロンプトの固定指示文
/// （`LlmBatchInstruction`）が明示的に「そのまま保持しろ」と教えているのは
/// この山括弧タグ（`&lt;mag&gt;`等）と内部用の複数行マーカーだけであり、
/// `$Key`形式のInterface\Translationsキー参照については、候補全体が
/// それだけで構成される場合はそもそもPickUpTarget側で候補から除外される
/// （`NonTranslatableText.LooksLikeInterfaceTranslationKeyReference`）ため
/// LLMに送られることが無く、文中に部分的に埋め込まれた場合の保持指示も
/// 存在しないため、「意図して保持されたタグ」とは扱わない。
/// </summary>
public class TranslationQualityCheckerTests
{
    [Fact]
    public void PureJapanese_IsAllJapanese()
    {
        Assert.Equal("AllJapanese", TranslationQualityChecker.Classify("軽大剣"));
    }

    [Fact]
    public void EmptyString_IsNoJapanese()
    {
        Assert.Equal("NoJapanese", TranslationQualityChecker.Classify(""));
    }

    [Fact]
    public void PureEnglish_IsNoJapanese()
    {
        Assert.Equal("NoJapanese", TranslationQualityChecker.Classify("Light Greatsword"));
    }

    [Fact]
    public void JapaneseWithLegitimateAngleBracketTag_IsContainsTagAlphaNumeric()
    {
        // <mag>のような、プロンプトの指示で保持を明示されている山括弧タグ。
        Assert.Equal("ContainsTagAlphaNumeric", TranslationQualityChecker.Classify("<mag>のダメージを与える"));
    }

    [Fact]
    public void JapaneseWithStrayEnglishWordOutsideAnyTag_IsContainsPartsOfAlphaNumeric()
    {
        // タグではない、モデルが訳し忘れた単語が混じっているケース。
        Assert.Equal("ContainsPartsOfAlphaNumeric", TranslationQualityChecker.Classify("これはSwordを持つ立派な剣です"));
    }

    [Fact]
    public void DollarKeyEmbeddedInRunningText_IsTreatedAsStrayAlphaNumericNotATag()
    {
        // $Keyはプロンプト側で保持を指示していないため、タグ扱いしない。
        Assert.Equal("ContainsPartsOfAlphaNumeric", TranslationQualityChecker.Classify("続けるには$ActivateKeyを押してください"));
    }

    [Fact]
    public void MostlyEnglishWithSomeJapanese_IsContainsMostOfAlphaNumeric()
    {
        Assert.Equal("ContainsMostOfAlphaNumeric", TranslationQualityChecker.Classify("This is 剣"));
    }

    [Fact]
    public void TagAlphaNumericAndStrayAlphaNumericBothPresentButUnderHalf_PrefersMoreSevereClassification()
    {
        // タグ内(<mag>)とタグ外(Sword)の両方に半角英数字が混じるが、全体の
        // 半分未満——より深刻なContainsPartsOfAlphaNumericが優先される。
        Assert.Equal("ContainsPartsOfAlphaNumeric", TranslationQualityChecker.Classify("<mag>のSwordがダメージを与えるアイテムです"));
    }

    [Fact]
    public void MostlyEnglishEvenWithLegitimateTag_PrefersMostSevereClassification()
    {
        Assert.Equal("ContainsMostOfAlphaNumeric", TranslationQualityChecker.Classify("<mag> damage to 敵"));
    }
}
