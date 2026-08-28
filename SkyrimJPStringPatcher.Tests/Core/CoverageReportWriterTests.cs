using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// CoverageReportWriter.WriteTsv/ReadTsv — the per-plugin "how much is already
/// covered by an existing DSD vs. still untranslated" summary (coverage_by_plugin.tsv),
/// consumed downstream by PluginSummaryWriter. Real logic worth pinning: the
/// merge of coveredByPlugin (existing-DSD counts) with candidates (still
/// untranslated), the least-covered-first sort, the 0-total edge case's ratio
/// default, and the sample-preview truncation/join.
/// </summary>
public class CoverageReportWriterTests
{
    [Fact]
    public void WriteTsv_ThenReadTsv_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_coveragereport_{Guid.NewGuid():N}.tsv");
        try
        {
            var candidates = new List<Candidate>
            {
                new("SjptsMod.esp", "01000001", "WEAP FULL", "Sjpts Untranslated Sword"),
                new("SjptsMod.esp", "01000002", "WEAP FULL", "Sjpts Untranslated Bow"),
            };
            var coveredByPlugin = new Dictionary<string, (int Count, long Chars)> { ["SjptsMod.esp"] = (3, 30) };

            CoverageReportWriter.WriteTsv(path, candidates, coveredByPlugin);
            var rows = CoverageReportWriter.ReadTsv(path);

            var row = Assert.Single(rows);
            Assert.Equal("SjptsMod.esp", row.Plugin);
            Assert.Equal(5, row.TotalCount); // 3 translated + 2 untranslated
            Assert.Equal(3, row.TranslatedCount);
            Assert.Equal(2, row.UntranslatedCount);
            Assert.Equal(60.0, row.TranslatedRatio, precision: 1); // 3/5
            Assert.Equal(30, row.TranslatedChars);
            Assert.Contains("Sjpts Untranslated Sword", row.SampleUntranslated);
            Assert.Contains("Sjpts Untranslated Bow", row.SampleUntranslated);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteTsv_SortsLeastCoveredPluginFirst()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_coveragereport_{Guid.NewGuid():N}.tsv");
        try
        {
            var candidates = new List<Candidate>
            {
                new("SjptsWellCovered.esp", "01000001", "WEAP FULL", "Sjpts Untranslated A"),
                new("SjptsPoorlyCovered.esp", "02000001", "WEAP FULL", "Sjpts Untranslated B"),
            };
            var coveredByPlugin = new Dictionary<string, (int Count, long Chars)>
            {
                ["SjptsWellCovered.esp"] = (9, 90), // 9/10 = 90% translated
                ["SjptsPoorlyCovered.esp"] = (1, 10), // 1/2 = 50% translated
            };

            CoverageReportWriter.WriteTsv(path, candidates, coveredByPlugin);
            var rows = CoverageReportWriter.ReadTsv(path);

            Assert.Equal("SjptsPoorlyCovered.esp", rows[0].Plugin); // least covered first
            Assert.Equal("SjptsWellCovered.esp", rows[1].Plugin);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteTsv_PluginWithZeroTotalCandidates_DefaultsRatioTo100Percent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_coveragereport_{Guid.NewGuid():N}.tsv");
        try
        {
            // A plugin present in coveredByPlugin with a 0 count and no
            // candidates at all -- TotalCount ends up 0, which must not divide
            // by zero.
            var coveredByPlugin = new Dictionary<string, (int Count, long Chars)> { ["SjptsEmptyMod.esp"] = (0, 0) };

            CoverageReportWriter.WriteTsv(path, new List<Candidate>(), coveredByPlugin);
            var row = Assert.Single(CoverageReportWriter.ReadTsv(path));

            Assert.Equal(0, row.TotalCount);
            Assert.Equal(100.0, row.TranslatedRatio, precision: 1);
            Assert.Equal(100.0, row.TranslatedCharsRatio, precision: 1);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteTsv_SamplePreview_TruncatesLongTextAndCapsAtThreeEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_coveragereport_{Guid.NewGuid():N}.tsv");
        try
        {
            var longText = new string('x', 50); // exceeds the 40-char truncation length
            var candidates = new List<Candidate>
            {
                new("SjptsMod.esp", "01000001", "WEAP FULL", longText),
                new("SjptsMod.esp", "01000002", "WEAP FULL", "Sjpts Second"),
                new("SjptsMod.esp", "01000003", "WEAP FULL", "Sjpts Third"),
                new("SjptsMod.esp", "01000004", "WEAP FULL", "Sjpts Fourth (must not appear -- only 3 sampled)"),
            };

            CoverageReportWriter.WriteTsv(path, candidates, new Dictionary<string, (int, long)>());
            var row = Assert.Single(CoverageReportWriter.ReadTsv(path));

            Assert.Contains(new string('x', 40) + "…", row.SampleUntranslated);
            Assert.Contains("Sjpts Second", row.SampleUntranslated);
            Assert.Contains("Sjpts Third", row.SampleUntranslated);
            Assert.DoesNotContain("Fourth", row.SampleUntranslated);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ReadTsv_SkipsBlankAndMalformedLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sjpts_coveragereport_{Guid.NewGuid():N}.tsv");
        try
        {
            File.WriteAllLines(path,
            [
                "Plugin\tTotalCount\tTranslatedCount\tUntranslatedCount\tTranslatedRatio(%)\tTotalChars\tTranslatedChars\tUntranslatedChars\tTranslatedCharsRatio(%)\tSampleUntranslated",
                "",
                "TooFewColumns\t1\t1",
                "SjptsMod.esp\t2\t1\t1\t50.0\t20\t10\t10\t50.0\tsample",
            ]);

            var rows = CoverageReportWriter.ReadTsv(path);

            var row = Assert.Single(rows);
            Assert.Equal("SjptsMod.esp", row.Plugin);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
