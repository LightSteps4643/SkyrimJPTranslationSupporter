using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// 2026-09-17: FindCrossModPrecedent（PickUpTargetRunner.cs）は、地の文の
/// 大文字小文字差を意図的に無視する仕様（v0.56.2、「the Jarl」vs「the jarl」等
/// の実データに基づく判断）だが、この許容範囲がタグの中身（&lt;Alias=QuestGiver&gt;
/// 等のスクリプト変数参照）にもそのまま適用されてしまっていた不具合の修正
/// （実データで&lt;Alias=QuestGiver&gt;と&lt;Alias=Questgiver&gt;の食い違いを複数件
/// 確認済み）。ゲームエンジンがこの種のタグ名を大文字小文字を区別して解決
/// しているかどうか公式情報源から確証を得られなかったため、安全側に倒し、
/// タグの中身は大文字小文字を含めて完全一致を要求するようにした。
///
/// IsConfirmedCrossModTextMatchへ切り出し、ChainValue等の複雑な型を介さず
/// 文字列だけで直接テストできるようにしている（LlmBatchTranslationEngine.
/// HasMatchingTagStructureと同じ考え方）。
/// </summary>
public class CrossModPrecedentTagMatchTests
{
    [Fact]
    public void PlainTextCaseOnlyDifference_StillMatches()
    {
        // 既存仕様（v0.56.2）の維持確認——地の文の大文字小文字は引き続き無視する。
        Assert.True(PickUpTargetRunner.IsConfirmedCrossModTextMatch("the Jarl of Whiterun", "the jarl of whiterun"));
    }

    [Fact]
    public void TagCaseOnlyDifference_DoesNotMatch()
    {
        // 実データで見つかった不具合の再現——タグの中身の大文字小文字違いは
        // 一致とみなさない。
        Assert.False(PickUpTargetRunner.IsConfirmedCrossModTextMatch(
            "Speak to <Alias=QuestGiver> about the bounty.",
            "Speak to <Alias=Questgiver> about the bounty."));
    }

    [Fact]
    public void IdenticalTextWithTags_Matches()
    {
        Assert.True(PickUpTargetRunner.IsConfirmedCrossModTextMatch(
            "Speak to <Alias=QuestGiver> about the bounty.",
            "Speak to <Alias=QuestGiver> about the bounty."));
    }

    [Fact]
    public void IdenticalTagsButPlainTextCaseDiffers_StillMatches()
    {
        // タグは完全一致・地の文だけ大文字小文字が違う、という組み合わせの確認。
        Assert.True(PickUpTargetRunner.IsConfirmedCrossModTextMatch(
            "Speak to <Alias=QuestGiver> About The Bounty.",
            "speak to <Alias=QuestGiver> about the bounty."));
    }

    [Fact]
    public void DifferentTagContentEntirely_DoesNotMatch()
    {
        Assert.False(PickUpTargetRunner.IsConfirmedCrossModTextMatch(
            "You gain <mag> for <dur> seconds.", "You gain <mag> for <15> seconds."));
    }

    [Fact]
    public void NoTagsEitherSide_PlainTextComparisonUnaffected()
    {
        Assert.True(PickUpTargetRunner.IsConfirmedCrossModTextMatch("Steel Sword", "STEEL SWORD"));
        Assert.False(PickUpTargetRunner.IsConfirmedCrossModTextMatch("Steel Sword", "Iron Sword"));
    }
}
