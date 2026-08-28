using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// PluginSummaryWriter.Write — combines PickUpTarget's coverage_by_plugin.tsv
/// (existing-DSD coverage) with Translation's own AutoTranslator resolution
/// counts into the per-plugin "where does every plugin stand" report. Real
/// logic worth pinning: the untranslated-count/-chars subtraction, the ratio
/// computation, a plugin present in coverage but absent from
/// autoResolveByPlugin (must default to 0 auto-resolved, not throw), and the
/// three differently-ordered blocks (alphabetical / most-untranslated-records
/// / most-untranslated-chars).
/// </summary>
public class PluginSummaryWriterTests
{
    private static string WriteCoverageFixture(string dir, IReadOnlyList<Candidate> candidates, IReadOnlyDictionary<string, (int, long)> coveredByPlugin)
    {
        var path = Path.Combine(dir, "coverage_by_plugin.tsv");
        CoverageReportWriter.WriteTsv(path, candidates, coveredByPlugin);
        return dir;
    }

    [Fact]
    public void Write_CombinesCoverageAndAutoResolved_ComputesUntranslatedCountsAndRatios()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_pluginsummary_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 10 total candidates for SjptsMod.esp: 4 already covered by an
            // existing DSD, 3 auto-resolved by AutoTranslator, 3 left untranslated.
            var candidates = Enumerable.Range(1, 6)
                .Select(i => new Candidate("SjptsMod.esp", $"0100000{i}", "WEAP FULL", $"Sjpts Untranslated {i}"))
                .ToList(); // 6 "still in candidates.tsv" rows -- 3 of these get resolved by AutoTranslator below
            var coveredByPlugin = new Dictionary<string, (int, long)> { ["SjptsMod.esp"] = (4, 40) };
            WriteCoverageFixture(root, candidates, coveredByPlugin);

            var autoResolveByPlugin = new List<(string Plugin, int Count, int AutoResolved, long AutoResolvedChars, long RemainingChars, List<string> SampleRemaining)>
            {
                ("SjptsMod.esp", 6, 3, 30, 30, []),
            };

            var summaryPath = Path.Combine(root, "plugin_summary.txt");
            PluginSummaryWriter.Write(summaryPath, root, autoResolveByPlugin);
            var text = File.ReadAllText(summaryPath);

            // Total 10 (4 covered + 6 in candidates.tsv), translated 4+3=7, untranslated 3.
            Assert.Contains("SjptsMod.esp\t10件\t4件\t3件\t70.0%\t3件\t30.0%", text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Write_PluginInCoverageButMissingFromAutoResolveList_TreatsAutoResolvedAsZero()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_pluginsummary_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var candidates = new List<Candidate> { new("SjptsOrphan.esp", "01000001", "WEAP FULL", "Sjpts Untranslated") };
            WriteCoverageFixture(root, candidates, new Dictionary<string, (int, long)> { ["SjptsOrphan.esp"] = (0, 0) });

            var summaryPath = Path.Combine(root, "plugin_summary.txt");
            // No entry for "SjptsOrphan.esp" in autoResolveByPlugin at all.
            PluginSummaryWriter.Write(summaryPath, root, []);
            var text = File.ReadAllText(summaryPath);

            Assert.Contains("SjptsOrphan.esp\t1件\t0件\t0件\t0.0%\t1件\t100.0%", text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Write_NoCoverageFileYet_WritesEmptyReportWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_pluginsummary_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var summaryPath = Path.Combine(root, "plugin_summary.txt");

            PluginSummaryWriter.Write(summaryPath, root, []); // coverage_by_plugin.tsv does not exist in root

            Assert.True(File.Exists(summaryPath));
            Assert.Contains("対象プラグイン: 0件", File.ReadAllText(summaryPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Write_RankingBlocks_OrderByDescendingUntranslatedCountAndChars()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_pluginsummary_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // "Big" has more untranslated RECORDS but fewer untranslated CHARS
            // than "Wordy" -- so the two ranking blocks must actually disagree
            // on ordering, proving each sorts by its own metric.
            var candidates = new List<Candidate>
            {
                new("SjptsBig.esp", "01000001", "WEAP FULL", "AA"),
                new("SjptsBig.esp", "01000002", "WEAP FULL", "BB"),
                new("SjptsBig.esp", "01000003", "WEAP FULL", "CC"),
                new("SjptsWordy.esp", "02000001", "WEAP FULL", new string('z', 100)),
            };
            WriteCoverageFixture(root, candidates, new Dictionary<string, (int, long)>());

            var summaryPath = Path.Combine(root, "plugin_summary.txt");
            PluginSummaryWriter.Write(summaryPath, root, []);
            var text = File.ReadAllText(summaryPath);

            var recordBlockStart = text.IndexOf("⑤未翻訳件数が多い順", StringComparison.Ordinal);
            var charBlockStart = text.IndexOf("⑪未翻訳文字数が多い順", StringComparison.Ordinal);
            var recordBlock = text[recordBlockStart..charBlockStart];
            var charBlock = text[charBlockStart..];

            // By untranslated RECORD count: Big (3 candidates) before Wordy (1).
            Assert.True(recordBlock.IndexOf("SjptsBig.esp", StringComparison.Ordinal) < recordBlock.IndexOf("SjptsWordy.esp", StringComparison.Ordinal));
            // By untranslated CHAR count: Wordy (100 chars) before Big (6 chars) -- reversed.
            Assert.True(charBlock.IndexOf("SjptsWordy.esp", StringComparison.Ordinal) < charBlock.IndexOf("SjptsBig.esp", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
