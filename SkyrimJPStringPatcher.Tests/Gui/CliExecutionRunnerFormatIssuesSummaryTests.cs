using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>FormatIssuesSummary was previously duplicated byte-for-byte in
/// both MainForm.cs and InterfaceTextPanel.cs; consolidated into
/// CliExecutionRunner (2026-09-12) as a public static method, so it can be
/// tested directly instead of via reflection.</summary>
public class CliExecutionRunnerFormatIssuesSummaryTests
{
    [Fact]
    public void FormatIssuesSummary_AllFieldsNonzero_ListsAllWithJapaneseLabels()
    {
        var result = CliExecutionRunner.FormatIssuesSummary(
            "##SJPTS_ISSUES## plugins=2 fields=3 fail_open=1 context_only=4");

        Assert.Equal(
            "・スキップされたプラグイン: 2件\n" +
            "・スキップされたレコード/フィールド: 3件\n" +
            "・除外判定に失敗し、安全側に倒して含めた候補: 1件\n" +
            "・文脈情報のみ抽出できなかった候補（翻訳への影響なし）: 4件",
            result);
    }

    [Fact]
    public void FormatIssuesSummary_ZeroFieldsOmitted()
    {
        var result = CliExecutionRunner.FormatIssuesSummary(
            "##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0");

        Assert.Equal("・スキップされたレコード/フィールド: 1件", result);
    }

    [Fact]
    public void FormatIssuesSummary_AllZero_ReturnsEmptyString()
    {
        var result = CliExecutionRunner.FormatIssuesSummary(
            "##SJPTS_ISSUES## plugins=0 fields=0 fail_open=0 context_only=0");

        Assert.Equal("", result);
    }

    [Fact]
    public void FormatIssuesSummary_UnknownToken_IsIgnored()
    {
        var result = CliExecutionRunner.FormatIssuesSummary(
            "##SJPTS_ISSUES## plugins=1 unknown_key=5");

        Assert.Equal("・スキップされたプラグイン: 1件", result);
    }
}
