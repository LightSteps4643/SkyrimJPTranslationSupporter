using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// RunLog itself had no dedicated tests before v0.58.5 (only exercised
/// indirectly through other stages' own log-content assertions). Added
/// alongside <see cref="RunLog.WriteExclusionsFile"/> — a PickUpTarget-only
/// feature that pulls just the "除外:"/"Excluded:"-prefixed <see cref="RunLog.Detail"/>
/// categories out into their own standalone file (the same content
/// <see cref="RunLog.Dispose"/> writes into the main stage log, just easier
/// to find on its own — a user request: exclusions get buried among the
/// other statistics in the full log).
/// </summary>
public class RunLogTests
{
    [Fact]
    public void WriteExclusionsFile_OnlyIncludesCategoriesStartingWithExcluded_NotOtherDetailCategories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_runlog_{Guid.NewGuid():N}");
        var exclusionsPath = Path.Combine(root, "excluded_candidates.txt");
        try
        {
            using (var log = RunLog.Open(root, "PickUpTarget"))
            {
                log.Detail("除外: マークアップ/アイコングリフ（翻訳すると表示が壊れる）", "Excluded: markup/icon-glyph", "[ACTI RNAM] <font face=\"Iconographia\">G</Font>");
                log.Detail("除外: 文字列全体がアセットパス（訳すとパスが壊れる）", "Excluded: entire string is an asset path", "Effects\\FXEmptyObject.nif");
                // NOT an exclusion -- a diagnostic note about a context field, unrelated
                // to whether the candidate itself was excluded. Must not leak in.
                log.Detail("文脈抽出のみ失敗（候補・翻訳への影響なし）", "Context extraction only failed", "000801:Sjpts.esp");

                log.WriteExclusionsFile(exclusionsPath);
            }

            Assert.True(File.Exists(exclusionsPath));
            var content = File.ReadAllText(exclusionsPath);
            Assert.Contains("マークアップ/アイコングリフ", content);
            Assert.Contains("アセットパス", content);
            Assert.DoesNotContain("文脈抽出のみ失敗", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Identical items collapse to one line with a ×N occurrence count
    /// (same dedup RunLog.Dispose's own full report uses) — this is what keeps
    /// a category like "676 icon-glyph exclusions" readable as a handful of
    /// distinct lines instead of 676 near-duplicate ones.</summary>
    [Fact]
    public void WriteExclusionsFile_CollapsesIdenticalItemsWithAnOccurrenceCount()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_runlog_{Guid.NewGuid():N}");
        var exclusionsPath = Path.Combine(root, "excluded_candidates.txt");
        try
        {
            using (var log = RunLog.Open(root, "PickUpTarget"))
            {
                for (var i = 0; i < 3; i++)
                    log.Detail("除外: マークアップ/アイコングリフ（翻訳すると表示が壊れる）", "Excluded: markup/icon-glyph", "[ACTI RNAM] <font face=\"Iconographia\">G</Font>");

                log.WriteExclusionsFile(exclusionsPath);
            }

            var content = File.ReadAllText(exclusionsPath);
            Assert.Contains("×3", content);
            // The header count reflects the raw total, the dedup detail the
            // distinct count -- both should be visible.
            Assert.Contains("3件（うち異なる内容 1種類）", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>WriteExclusionsFile is scoped to ONE RunLog instance -- since
    /// PickUpTargetRunner shares a single RunLog across the WHOLE load order
    /// (not one per plugin), this is already a single file covering every
    /// plugin's exclusions, not something that needs per-plugin merging.</summary>
    [Fact]
    public void WriteExclusionsFile_AggregatesAcrossMultiplePluginsIntoOneFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_runlog_{Guid.NewGuid():N}");
        var exclusionsPath = Path.Combine(root, "excluded_candidates.txt");
        try
        {
            using (var log = RunLog.Open(root, "PickUpTarget"))
            {
                log.Detail("除外: プレースホルダー文字列", "Excluded: placeholder string", "[SjptsPluginA.esp] xxx");
                log.Detail("除外: プレースホルダー文字列", "Excluded: placeholder string", "[SjptsPluginB.esp] todo");

                log.WriteExclusionsFile(exclusionsPath);
            }

            var content = File.ReadAllText(exclusionsPath);
            Assert.Contains("SjptsPluginA.esp", content);
            Assert.Contains("SjptsPluginB.esp", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteExclusionsFile_WithNoExclusions_StillWritesAValidEmptyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_runlog_{Guid.NewGuid():N}");
        var exclusionsPath = Path.Combine(root, "excluded_candidates.txt");
        try
        {
            using (var log = RunLog.Open(root, "PickUpTarget"))
            {
                log.Detail("文脈抽出のみ失敗（候補・翻訳への影響なし）", "Context extraction only failed", "000801:Sjpts.esp");
                log.WriteExclusionsFile(exclusionsPath);
            }

            Assert.True(File.Exists(exclusionsPath));
            Assert.DoesNotContain("文脈抽出のみ失敗", File.ReadAllText(exclusionsPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
