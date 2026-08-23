using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// v0.21.0: the single "where does every plugin stand" table checked after
/// every `translation --all` run, combining PickUpTarget's coverage_by_plugin.tsv
/// (existing-DSD coverage) with Translation's own AutoTranslator resolution —
/// ahead of any future GUI (see DESIGN_NOTES.md's "開発フェーズの方針": this is
/// exactly the kind of per-plugin progress view planned for the eventual GUI,
/// done as a plain tab-separated text file for now).
///
/// v0.20.0's version of this file grouped plugins into a judgment (「既存訳を
/// 探すべき」等) computed from a fixed threshold — the user pushed back: the
/// judgment is theirs to make, not the tool's, and a fixed threshold hid real
/// cases (Book Covers Skyrim.esp: few CANDIDATES, but each is a whole novel,
/// so it landed in "self-translate is faster" despite an enormous character
/// count). This version drops any judgment/threshold entirely and just lays
/// out the twelve raw numbers per plugin (record-count and character-count
/// views of total/existing-translated/AutoTranslator-resolved/untranslated),
/// with units on every value since a human reads this directly. Presented
/// three times — alphabetical (the base list) plus two rankings (by
/// untranslated record ratio, by untranslated character ratio) — because a
/// text file can't be resorted like the eventual GUI's table will be.
/// </summary>
public static class PluginSummaryWriter
{
    private sealed record PluginStats(
        string Plugin,
        int TotalCount, int ExistingTranslatedCount, int AutoResolvedCount, double TranslatedRecordRatio, int UntranslatedCount, double UntranslatedRecordRatio,
        long TotalChars, long ExistingTranslatedChars, long AutoResolvedChars, double TranslatedCharRatio, long UntranslatedChars, double UntranslatedCharRatio);

    public static void Write(string path, string pickUpTargetOutputDir,
        IReadOnlyList<(string Plugin, int Count, int AutoResolved, long AutoResolvedChars, long RemainingChars, List<string> SampleRemaining)> autoResolveByPlugin)
    {
        var coveragePath = Path.Combine(pickUpTargetOutputDir, "coverage_by_plugin.tsv");
        var coverage = File.Exists(coveragePath) ? CoverageReportWriter.ReadTsv(coveragePath) : new List<CoverageReportWriter.CoverageRow>();
        var autoByPlugin = autoResolveByPlugin.ToDictionary(a => a.Plugin, StringComparer.OrdinalIgnoreCase);

        var rows = new List<PluginStats>();
        foreach (var row in coverage)
        {
            autoByPlugin.TryGetValue(row.Plugin, out var auto);
            var autoResolvedCount = auto.AutoResolved;
            var autoResolvedChars = auto.AutoResolvedChars;

            var untranslatedCount = row.TotalCount - row.TranslatedCount - autoResolvedCount;
            var untranslatedChars = row.TotalChars - row.TranslatedChars - autoResolvedChars;

            rows.Add(new PluginStats(
                row.Plugin,
                row.TotalCount, row.TranslatedCount, autoResolvedCount,
                row.TotalCount == 0 ? 100.0 : 100.0 * (row.TranslatedCount + autoResolvedCount) / row.TotalCount,
                untranslatedCount,
                row.TotalCount == 0 ? 0.0 : 100.0 * untranslatedCount / row.TotalCount,
                row.TotalChars, row.TranslatedChars, autoResolvedChars,
                row.TotalChars == 0 ? 100.0 : 100.0 * (row.TranslatedChars + autoResolvedChars) / row.TotalChars,
                untranslatedChars,
                row.TotalChars == 0 ? 0.0 : 100.0 * untranslatedChars / row.TotalChars));
        }

        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine("# MODごとの翻訳状況一覧");
        w.WriteLine($"# 生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"# 対象プラグイン: {rows.Count}件");
        w.WriteLine("# ①全体件数 ②既存訳済み件数(既存DSD) ③AutoTranslator解決件数 ④翻訳済み割合(件数) ⑤未翻訳件数 ⑥未翻訳割合(件数)");
        w.WriteLine("# ⑦全体文字数 ⑧既存訳済み文字数(既存DSD) ⑨AutoTranslator解決文字数 ⑩翻訳済み割合(文字数) ⑪未翻訳文字数 ⑫未翻訳割合(文字数)");
        w.WriteLine("#（①⑦は非表示・ゲーム中に表示されない文字列を除いた、実際にプレイヤーの目に触れる文字列のみ）");
        w.WriteLine();

        WriteBlock(w, "MOD名 アルファベット順（基準リスト）", rows.OrderBy(r => r.Plugin, StringComparer.OrdinalIgnoreCase));
        w.WriteLine();
        // 割合（⑥⑫）ではなく実数（⑤⑪）でランキング — 割合順だと総件数1件のMODが
        // 「未翻訳率100%」で最上位に来てしまい、実際の作業量の大小を反映しないため。
        WriteBlock(w, "⑤未翻訳件数が多い順", rows.OrderByDescending(r => r.UntranslatedCount));
        w.WriteLine();
        WriteBlock(w, "⑪未翻訳文字数が多い順", rows.OrderByDescending(r => r.UntranslatedChars));
    }

    private static void WriteBlock(StreamWriter w, string title, IEnumerable<PluginStats> rows)
    {
        w.WriteLine($"## {title}");
        w.WriteLine(string.Join('\t', "Plugin", "①全体", "②既存訳済み", "③自動解決", "④翻訳済み割合", "⑤未翻訳", "⑥未翻訳割合",
            "⑦全体文字数", "⑧既存訳済み文字数", "⑨自動解決文字数", "⑩翻訳済み割合", "⑪未翻訳文字数", "⑫未翻訳割合"));
        foreach (var r in rows)
        {
            w.WriteLine(string.Join('\t', r.Plugin,
                $"{r.TotalCount}件", $"{r.ExistingTranslatedCount}件", $"{r.AutoResolvedCount}件", $"{r.TranslatedRecordRatio:F1}%",
                $"{r.UntranslatedCount}件", $"{r.UntranslatedRecordRatio:F1}%",
                $"{r.TotalChars}字", $"{r.ExistingTranslatedChars}字", $"{r.AutoResolvedChars}字", $"{r.TranslatedCharRatio:F1}%",
                $"{r.UntranslatedChars}字", $"{r.UntranslatedCharRatio:F1}%"));
        }
    }
}
