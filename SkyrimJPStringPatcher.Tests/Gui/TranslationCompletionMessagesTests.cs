using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// 2026-09-13: プラグイン翻訳・Interface翻訳で共通化した「未翻訳が残って
/// いる場合」の警告文言。設定不備の決めつけ（旧: "設定に問題がある可能性が
/// あります"）ではなく、複数の原因を中立に例示する文言へ統一したことを
/// ロックする——将来どちらかの画面だけ独自の文言に戻ってしまう回帰を防ぐ。
/// </summary>
public class TranslationCompletionMessagesTests
{
    [Fact]
    public void IncompleteBody_DoesNotAssumeConfigurationIsBroken()
    {
        Assert.DoesNotContain("設定に問題がある可能性があります", TranslationCompletionMessages.IncompleteBody);
    }

    [Fact]
    public void IncompleteBody_MentionsOutputTokenLimitAsAPossibleCause()
    {
        Assert.Contains("出力トークン数の上限で打ち切られた", TranslationCompletionMessages.IncompleteBody);
    }

    [Fact]
    public void IncompleteBody_SuggestsRetryingAndCheckingTheLog()
    {
        Assert.Contains("複数回行うと解決することもあります", TranslationCompletionMessages.IncompleteBody);
        Assert.Contains("ログ", TranslationCompletionMessages.IncompleteBody);
    }
}
