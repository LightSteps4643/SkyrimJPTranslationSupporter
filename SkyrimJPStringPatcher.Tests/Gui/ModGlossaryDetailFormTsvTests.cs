using System.Reflection;
using SkyrimJPStringPatcherGui;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// 2026-09-18: ModGlossaryDetailForm（mod_glossary.tsvをGUIから編集する新規
/// 画面、ユーザー要望）のReadTsv/WriteTsvは、TranslationDetailForm/
/// InterfaceTextDetailFormと同じ理由でprivate staticなヘルパーとして
/// 直接reflectionでテストする。
///
/// Translation/ModPhraseGlossary.cs（CLI側の本家）はEnglish/Japanese列を
/// TsvEscapingで一切エスケープしていない（n-gramは正規表現由来のため
/// タブ・改行を含み得ず、Japanese列にユーザーが手でタブ等を入力する
/// 極端なケースは元々CLI側でも未対応）——GUI側もこれに合わせ、あえて
/// エスケープしない（互換性を優先）。
/// </summary>
public class ModGlossaryDetailFormTsvTests
{
    private static List<ModGlossaryRow> InvokeReadTsv(string path) =>
        (List<ModGlossaryRow>)typeof(ModGlossaryDetailForm)
            .GetMethod("ReadTsv", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path])!;

    private static void InvokeWriteTsv(string path, string plugin, IReadOnlyList<ModGlossaryRow> rows) =>
        typeof(ModGlossaryDetailForm)
            .GetMethod("WriteTsv", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path, plugin, rows]);

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_modglossary_tsv_{Guid.NewGuid():N}.tsv");
        try
        {
            var rows = new List<ModGlossaryRow>
            {
                new("Light Greatsword", "軽大剣", "15", "42.3"),
                new("Cosplay Gala", "", "4", "9.1"),
            };
            InvokeWriteTsv(path, "SjptsTestMod.esp", rows);

            var read = InvokeReadTsv(path);

            Assert.Equal(rows, read);
        }
        finally { File.Delete(path); }
    }

    /// <summary>コメント行（#で始まる説明書き）はReadTsvで読み飛ばされ、
    /// WriteTsvで常に本家（ModPhraseGlossary.WriteTemplate）と同じ固定の
    /// 説明文が書き出される。</summary>
    [Fact]
    public void WriteTsv_WritesTheSameCommentHeaderAsModPhraseGlossary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_modglossary_tsv_{Guid.NewGuid():N}.tsv");
        try
        {
            InvokeWriteTsv(path, "SjptsTestMod.esp", []);

            var lines = File.ReadAllLines(path);
            Assert.Contains("# SjptsTestMod.esp — このMODだけに効く語彙集（⑤ローカルLLM・⑥生成AI翻訳へのヒントとして使われます）", lines);
            Assert.Contains("# English\tJapanese\tCount\tScore", lines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_SkipsCommentLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_modglossary_tsv_{Guid.NewGuid():N}.tsv");
        try
        {
            File.WriteAllLines(path,
            [
                "# SjptsTestMod.esp — このMODだけに効く語彙集",
                "#",
                "# English\tJapanese\tCount\tScore",
                "Light Greatsword\t軽大剣\t15\t42.3",
            ]);

            var read = InvokeReadTsv(path);

            Assert.Single(read);
            Assert.Equal(new ModGlossaryRow("Light Greatsword", "軽大剣", "15", "42.3"), read[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_NonexistentFile_ReturnsEmptyList()
    {
        var result = InvokeReadTsv(Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.tsv"));
        Assert.Empty(result);
    }
}
