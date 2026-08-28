using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// CandidateListWriter.WriteText — the human-readable review copy of
/// PickUpTarget's output. Low priority (output-only, no downstream stage
/// reads it back), but it has real grouping/ordering logic worth pinning:
/// plugin groups sorted by descending candidate count, candidates within a
/// group sorted by FormId, and the stale-review section appearing only when
/// there is something to review.
/// </summary>
public class CandidateListWriterTests
{
    private static PickUpTargetResult MakeResult(List<Candidate> candidates, List<string>? staleReviewLog = null) =>
        new("TestProfile", candidates, [], staleReviewLog ?? [], new Dictionary<string, (int, long)>(), []);

    [Fact]
    public void WriteText_GroupsByWinningPlugin_OrderedByDescendingCandidateCount()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_candidatelist_{Guid.NewGuid():N}.txt");
        try
        {
            var result = MakeResult(
            [
                new Candidate("SmallMod.esp", "01000001", "WEAP FULL", "Small Mod Sword"),
                new Candidate("BigMod.esp", "02000001", "WEAP FULL", "Big Mod Sword A"),
                new Candidate("BigMod.esp", "02000002", "WEAP FULL", "Big Mod Sword B"),
            ]);

            CandidateListWriter.WriteText(path, result);
            var text = File.ReadAllText(path);

            var bigIndex = text.IndexOf("## BigMod.esp", StringComparison.Ordinal);
            var smallIndex = text.IndexOf("## SmallMod.esp", StringComparison.Ordinal);
            Assert.True(bigIndex >= 0 && smallIndex >= 0);
            Assert.True(bigIndex < smallIndex); // 2-candidate group listed before the 1-candidate group
            Assert.Contains("## BigMod.esp (2件)", text);
            Assert.Contains("## SmallMod.esp (1件)", text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteText_WithinAGroup_OrdersByFormId()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_candidatelist_{Guid.NewGuid():N}.txt");
        try
        {
            var result = MakeResult(
            [
                new Candidate("SomeMod.esp", "02000002", "WEAP FULL", "Second By FormId"),
                new Candidate("SomeMod.esp", "01000001", "WEAP FULL", "First By FormId"),
            ]);

            CandidateListWriter.WriteText(path, result);
            var text = File.ReadAllText(path);

            var firstIndex = text.IndexOf("First By FormId", StringComparison.Ordinal);
            var secondIndex = text.IndexOf("Second By FormId", StringComparison.Ordinal);
            Assert.True(firstIndex >= 0 && secondIndex >= 0 && firstIndex < secondIndex);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteText_NoStaleReviewEntries_OmitsTheStaleReviewSection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_candidatelist_{Guid.NewGuid():N}.txt");
        try
        {
            var result = MakeResult([new Candidate("SomeMod.esp", "01000001", "WEAP FULL", "Some Sword")]);

            CandidateListWriter.WriteText(path, result);
            var text = File.ReadAllText(path);

            Assert.DoesNotContain("要レビュー", text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteText_WithStaleReviewEntries_AppendsTheStaleReviewSection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_candidatelist_{Guid.NewGuid():N}.txt");
        try
        {
            var result = MakeResult(
                [new Candidate("SomeMod.esp", "01000001", "WEAP FULL", "Some Sword")],
                staleReviewLog: ["[SomeMod.esp] 01000002 [WEAP FULL] original text changed since translation"]);

            CandidateListWriter.WriteText(path, result);
            var text = File.ReadAllText(path);

            Assert.Contains("要レビュー", text);
            Assert.Contains("original text changed since translation", text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
