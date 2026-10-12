using System.Text;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.SkyPatcher;

/// <summary>The `skypatcher-pickup` command: reads the MO2 instance, scans its SkyPatcher .ini files and
/// writes the candidates (every name row, translatable or not) plus a plain-text list of what was left
/// out of translation and why. The self-audit — every row in exactly one category — goes into the run log.</summary>
public static class SkyPatcherPickUpRunner
{
    public static SkyPatcherScanResult Run(
        string mo2Dir, string outDir, RunLog log,
        string? modsDirOverride = null, string? profileDirOverride = null, string? overwriteDirOverride = null)
    {
        var instance = Mo2InstanceReader.Read(mo2Dir, modsDirOverride, profileDirOverride, overwriteDirOverride);
        var result = SkyPatcherScanner.Scan(instance);

        Directory.CreateDirectory(outDir);
        var candidatesPath = Path.Combine(outDir, "candidates.tsv");
        CandidateIo.WriteTsv(candidatesPath, result.Candidates);
        var excludedPath = Path.Combine(outDir, "excluded_candidates.txt");
        File.WriteAllText(excludedPath, ExcludedList(result.Candidates), new UTF8Encoding(true));

        LogSummary(log, result.Stats, candidatesPath, excludedPath);
        return result;
    }

    /// <summary>Rows that can take effect but are not offered for translation, grouped by reason. (Inactive
    /// rows are not listed here: they are visible, greyed out, in the detail window.)</summary>
    internal static string ExcludedList(IEnumerable<Candidate> candidates)
    {
        var text = new StringBuilder();
        text.AppendLine("SkyPatcher: translation対象外の行（効く行のうち、訳せない・訳すと壊れる文字列）");
        text.AppendLine("# 理由 | 提供MOD | 元の.ini:行 | 種別 | 文字列");
        foreach (var group in candidates.Where(c => c.Inactive.Length == 0 && c.Excluded.Length > 0).GroupBy(c => c.Excluded).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine();
            text.AppendLine($"[{group.Key}] {group.Count()}件");
            foreach (var c in group)
                text.AppendLine($"{c.Excluded} | {c.WinningPlugin} | {c.SourcePath}:{c.SourceLine} | {c.RecordType} | {c.CurrentText}");
        }
        return text.ToString();
    }

    private static void LogSummary(RunLog log, SkyPatcherScanStats stats, string candidatesPath, string excludedPath)
    {
        log.Section("SkyPatcherの.ini走査", "SkyPatcher .ini scan");
        log.Line($"読み込んだ.iniファイル: {stats.FilesRead}件", $"Loose .ini files read: {stats.FilesRead}");
        log.Line($"名前の行（fullName/shortName）: {stats.TotalRows}件", $"Name rows (fullName/shortName): {stats.TotalRows}");

        log.Section("自己監査（全行が、次のどれか1つに分類される）", "Self-audit (every row falls into exactly one category)");
        log.Line($"翻訳対象: {stats.Translatable}件", $"To translate: {stats.Translatable}");
        foreach (var (reason, count) in stats.Excluded.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            log.Line($"対象外（{reason}）: {count}件", $"Not offered ({reason}): {count}");
        foreach (var (reason, count) in stats.Inactive.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            log.Line($"効かない行（{reason}）: {count}件", $"Cannot take effect ({reason}): {count}");

        var classified = stats.Translatable + stats.Excluded.Values.Sum() + stats.Inactive.Values.Sum();
        log.Line($"分類できなかった行: {stats.TotalRows - classified}件", $"Unclassified rows: {stats.TotalRows - classified}");

        log.Section("出力ファイル", "Output files");
        log.Line(candidatesPath, candidatesPath);
        log.Line(excludedPath, excludedPath);

        Console.WriteLine($"SkyPatcher rows: {stats.TotalRows} (to translate {stats.Translatable}, not offered {stats.Excluded.Values.Sum()}, cannot take effect {stats.Inactive.Values.Sum()})");
        Console.WriteLine($"Wrote: {candidatesPath}");
        Console.WriteLine($"Wrote: {excludedPath}");
    }
}
