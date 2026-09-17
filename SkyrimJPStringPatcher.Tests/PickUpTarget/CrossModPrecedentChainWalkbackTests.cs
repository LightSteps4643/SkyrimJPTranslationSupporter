using Mutagen.Bethesda.Plugins;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// 2026-09-17: FindCrossModPrecedent（PickUpTargetRunner.cs）は、比較対象の
/// 英文（reference）を探す際、日本語を持つ寄与MODのすぐ1つ手前
/// （chain[i-1]）だけを確認し、それが日本語だった場合はそこで諦めていた。
/// 翻訳パッチが2つ以上連続するチェーン（例: 旧翻訳パッチ→新翻訳パッチ）では、
/// 本来もっと手前（バニラ・オリジナル）まで遡れば見つかるはずの英文を見逃し、
/// (a) 本来検証できるはずの一致を「要確認」のまま埋もれさせ、
/// (b) より深刻に、本来「確定した不一致」として弾くべきケース（レコードが
/// 実は別物に転用されている）まで、検証不能として素通しで適用してしまう、
/// という2つの問題があった。ユーザー指摘により、英文（非日本語）が見つかる
/// まで際限なく遡る設計に修正した。
///
/// あわせて、「チェーンの先頭まで遡っても英文の参照が一切ない」（＝この
/// ツールから見てそのレコードは最初からMODによって日本語で導入された）
/// ケースを、NeedsReview（要確認）とは別の専用フラグ IsChainOrigin として
/// 明示的に区別する（2026-09-17ユーザー指摘）。この場合、レコード自身への
/// 直接適用は妥当（同じレコードなので）だが、
/// AddCrossModPrecedentToCorpusによる汎用コーパス登録（英文が一致する
/// 他の無関係な候補にまで波及する）は見送るべきだからである——このMODの
/// 日本語が、勝者MODが持つ具体的な英文の正しい訳であるという保証が無いため。
///
/// IsConfirmedCrossModTextMatchと同じ理由で、FindCrossModPrecedent／
/// AddCrossModPrecedentToCorpus自体もChainValue/ChainKeyという単純な
/// タプル型だけを介して直接テストできるようpublicにしている。
/// </summary>
public class CrossModPrecedentChainWalkbackTests
{
    private static ModKey Mod(string name) => ModKey.FromNameAndExtension(name);

    private static List<(ModKey Source, string Text, string EditorId, string Context, string? DualLanguageEnglishText)> Chain(
        params (string ModName, string Text, string? DualLanguageEnglishText)[] entries) =>
        entries.Select(e => (Mod(e.ModName), e.Text, "SjptsItem", "", e.DualLanguageEnglishText)).ToList();

    [Fact]
    public void MultipleConsecutiveJapaneseContributors_WalksBackToGenuineEnglishReference_ConfirmsMatch()
    {
        // vanilla(EN) -> 旧翻訳パッチ(JP) -> 新翻訳パッチ(JP) -> 無関係な再保存(EN, 同一文言, 勝者)
        var chain = Chain(
            ("Original.esp", "Sjpts Same Item", null),
            ("JpPatchOld.esp", "旧訳", null),
            ("JpPatchNew.esp", "新訳", null),
            ("UnrelatedResave.esp", "Sjpts Same Item", null));

        var (japanese, needsReview, isChainOrigin, skippedMismatch) = PickUpTargetRunner.FindCrossModPrecedent(chain, "Sjpts Same Item");

        Assert.Equal("新訳", japanese);
        Assert.False(needsReview); // 修正前は旧訳が日本語のため参照を見つけられず、trueのまま適用されていた
        Assert.False(isChainOrigin);
        Assert.Null(skippedMismatch);
    }

    [Fact]
    public void MultipleConsecutiveJapaneseContributors_GenuineMismatchFurtherBack_IsCorrectlyBlocked()
    {
        // vanilla(EN "Item A") -> 旧翻訳パッチ(JP) -> 新翻訳パッチ(JP) -> 別物に転用するMOD(EN "Item B", 勝者)
        var chain = Chain(
            ("Original.esp", "Sjpts Original Item A", null),
            ("JpPatchOld.esp", "古い翻訳A", null),
            ("JpPatchNew.esp", "新しい翻訳A", null),
            ("RepurposeMod.esp", "Sjpts Repurposed Item B", null));

        var (japanese, needsReview, isChainOrigin, skippedMismatch) = PickUpTargetRunner.FindCrossModPrecedent(chain, "Sjpts Repurposed Item B");

        // 修正前は「1つ手前(旧訳)が日本語」で諦めてreference=null、要確認扱いで
        // 「新しい翻訳A」がそのまま適用されてしまっていた（本来は不一致で弾くべき）。
        Assert.Null(japanese);
        Assert.False(needsReview);
        Assert.False(isChainOrigin);
        Assert.NotNull(skippedMismatch);
        Assert.Equal("新しい翻訳A", skippedMismatch!.Value.Japanese);
        Assert.Equal("Sjpts Original Item A", skippedMismatch.Value.Reference);
    }

    [Fact]
    public void NoEarlierNonJapaneseContributorAnywhereInChain_IsChainOrigin_AppliedWithReviewFlag()
    {
        // MODが最初から日本語で導入したレコード（このツールから見て原文の英語が一切存在しない）。
        var chain = Chain(
            ("JpOriginalMod.esp", "彼岸の剣", null),
            ("ForeignExpansionMod.esp", "Sjpts Foreign Cursed Sword", null));

        var (japanese, needsReview, isChainOrigin, skippedMismatch) = PickUpTargetRunner.FindCrossModPrecedent(chain, "Sjpts Foreign Cursed Sword");

        Assert.Equal("彼岸の剣", japanese);
        Assert.True(needsReview);
        Assert.True(isChainOrigin);
        Assert.Null(skippedMismatch);
    }

    [Fact]
    public void ConsecutiveJapaneseAllTheWayToChainStart_IsChainOrigin()
    {
        // 連続する日本語提供MODが、どこまで遡ってもバニラ等の英文に辿り着かない。
        var chain = Chain(
            ("JpPatchOld.esp", "旧訳", null),
            ("JpPatchNew.esp", "新訳", null),
            ("UnrelatedResave.esp", "Sjpts Something", null));

        var (japanese, needsReview, isChainOrigin, skippedMismatch) = PickUpTargetRunner.FindCrossModPrecedent(chain, "Sjpts Something");

        Assert.Equal("新訳", japanese);
        Assert.True(needsReview);
        Assert.True(isChainOrigin);
        Assert.Null(skippedMismatch);
    }

    [Fact]
    public void AddCrossModPrecedentToCorpus_ChainOriginCase_IsNotRegisteredAsGenericCorpusPrecedent()
    {
        var chain = Chain(
            ("JpOriginalMod.esp", "彼岸の剣", null),
            ("ForeignExpansionMod.esp", "Sjpts Foreign Cursed Sword", null));
        var chains = new Dictionary<(FormKey FormKey, string DsdType, int Index), List<(ModKey Source, string Text, string EditorId, string Context, string? DualLanguageEnglishText)>>
        {
            [(FormKey.Factory("000800:JpOriginalMod.esp"), "WEAP FULL", 0)] = chain,
        };
        var corpus = new List<CorpusEntry>();

        PickUpTargetRunner.AddCrossModPrecedentToCorpus(chains, corpus);

        // このMODの日本語が「Sjpts Foreign Cursed Sword」という具体的な英文の
        // 正しい訳である保証が無いため、他の候補にまで波及するコーパス登録は
        // 見送るべき（レコード自身への直接適用とは別の話）。
        Assert.DoesNotContain(corpus, e => e.English == "Sjpts Foreign Cursed Sword");
    }

    [Fact]
    public void AddCrossModPrecedentToCorpus_ConfirmedMatch_IsStillRegisteredAsGenericCorpusPrecedent()
    {
        var chain = Chain(
            ("Original.esp", "Sjpts Same Item", null),
            ("JpPatch.esp", "訳語", null),
            ("UnrelatedResave.esp", "Sjpts Same Item", null));
        var chains = new Dictionary<(FormKey FormKey, string DsdType, int Index), List<(ModKey Source, string Text, string EditorId, string Context, string? DualLanguageEnglishText)>>
        {
            [(FormKey.Factory("000800:Original.esp"), "WEAP FULL", 0)] = chain,
        };
        var corpus = new List<CorpusEntry>();

        PickUpTargetRunner.AddCrossModPrecedentToCorpus(chains, corpus);

        Assert.Contains(corpus, e => e.English == "Sjpts Same Item" && e.Japanese == "訳語");
    }
}
