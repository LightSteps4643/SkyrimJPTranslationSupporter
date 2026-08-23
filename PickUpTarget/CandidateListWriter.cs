using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.PickUpTarget;

/// <summary>Writes the human-readable review copy of PickUpTarget's output (grouped by winning plugin).</summary>
public static class CandidateListWriter
{
    public static void WriteText(string path, PickUpTargetResult result)
    {
        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        writer.WriteLine("# Skyrim JP翻訳候補リスト (PickUpTarget)");
        writer.WriteLine($"# プロファイル: {result.ProfileName}");
        writer.WriteLine($"# 生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"# 対象候補数: {result.Candidates.Count}");
        writer.WriteLine("# 対象範囲: FULL(名前)のみ。DESC/会話文等は今後拡張予定");
        writer.WriteLine();

        foreach (var group in result.Candidates.GroupBy(c => c.WinningPlugin).OrderByDescending(g => g.Count()))
        {
            writer.WriteLine($"## {group.Key} ({group.Count()}件)");
            foreach (var c in group.OrderBy(x => x.FormId))
                writer.WriteLine($"  {c.FormId}\t[{c.RecordType}]\t{c.CurrentText}");
            writer.WriteLine();
        }

        if (result.StaleReviewLog.Count > 0)
        {
            writer.WriteLine("## 要レビュー（DSD翻訳は適用されているが、原文が変わった可能性あり）");
            foreach (var line in result.StaleReviewLog)
                writer.WriteLine($"  {line}");
        }
    }
}
