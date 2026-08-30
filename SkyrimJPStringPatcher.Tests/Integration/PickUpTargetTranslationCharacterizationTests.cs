using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Integration;

/// <summary>
/// A characterization (golden-snapshot) test: a black-box regression net for
/// changes that touch the shared PickUpTarget/Translation pipeline broadly
/// (e.g. the planned cross-mod translation-precedent feature). Unlike the
/// scenario tests in PickUpTargetToTranslationScenarioTests.cs and
/// PickUpTargetTranslationCrossModTests.cs -- which each assert a SPECIFIC,
/// hand-picked expectation -- this test asserts the ENTIRE resolved output
/// (every candidate's method tag and Japanese text, across every plugin in
/// a combined load order exercising ①corpus/②意味合成/③音訳分解/④NameFallback
/// and the not-yet-implemented cross-mod precedent scenarios at once) against
/// a checked-in golden file, byte for byte. Any change anywhere in the
/// pipeline that alters a result NOT explicitly covered by another test's
/// assertions will still show up here as a diff.
///
/// Written 2026-08-29 as a BEFORE snapshot -- i.e. this captures CURRENT
/// (pre-cross-mod-precedent-feature) behavior on purpose, so that once that
/// feature is implemented, re-running this test will show EXACTLY which
/// rows changed and how, for deliberate human review before updating
/// Golden.txt:
/// - A diff on a "vanilla"-tier ①コーパス完全一致 row is suspicious by
///   default (that tier is expected to be fully deterministic given the same
///   input) and should be investigated before accepting.
/// - A diff on a ②意味合成/③音訳分解/④NameFallback row is expected to be
///   possible (these are inherently best-effort automatic guesses) and
///   should be accepted only after confirming the NEW result is actually
///   higher quality, not just different.
///
/// Combines every Integration/PickUpTarget fixture mod already built this
/// session that has no cross-fixture vocabulary collision (each was
/// deliberately designed with invented/unique vocabulary specifically to
/// avoid colliding with Data/'s real glossary content OR with each other):
/// TestXMod1/TestXMod2 (2-mod cross-mod), FamousMod/FamousModJapanesePatch/
/// BigQuestMod (3-mod cross-mod, the blind test), PriorityModBase/
/// PriorityModPatch (plain load-order override, no Japanese anywhere --
/// proves the new logic must NOT misfire on an all-English chain),
/// SjptsVanillaLikeSource/SjptsUnrelatedMod (①vanilla-tier corpus),
/// SjptsMeaningSource/SjptsMeaningTarget (②意味合成+③音訳分解),
/// SjptsGlossaryTarget (④NameFallback via mod-glossary),
/// SjptsUnresolvableTarget (stays unresolved by every method).
/// </summary>
public class PickUpTargetTranslationCharacterizationTests
{
    private static string BuildCombinedMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration");

        // (modFolderName, espFileName, hasOwnSubfolderWithStrings)
        var mods = new (string Folder, string Esp, bool HasSubfolder)[]
        {
            ("TestXMod1Folder", "TestXMod1.esp", false),
            ("TestXMod2Folder", "TestXMod2.esp", false),
            ("FamousModFolder", "FamousMod.esp", false),
            ("FamousModJapanesePatchFolder", "FamousModJapanesePatch.esp", false),
            ("BigQuestModFolder", "BigQuestMod.esp", false),
            ("PriorityModBaseFolder", "PriorityModBase.esp", false),
            ("PriorityModPatchFolder", "PriorityModPatch.esp", false),
            ("SjptsVanillaLikeSourceFolder", "SjptsVanillaLikeSource.esp", true),
            ("SjptsUnrelatedModFolder", "SjptsUnrelatedMod.esp", true),
            ("SjptsMeaningSourceFolder", "SjptsMeaningSource.esp", true),
            ("SjptsMeaningTargetFolder", "SjptsMeaningTarget.esp", true),
            ("SjptsGlossaryTargetFolder", "SjptsGlossaryTarget.esp", true),
            ("SjptsUnresolvableTargetFolder", "SjptsUnresolvableTarget.esp", true),
        };

        foreach (var (folder, esp, ownSubfolder) in mods)
        {
            var modDir = Path.Combine(mo2Dir, "mods", folder);
            Directory.CreateDirectory(modDir);
            // "Own subfolder" mods (all Sjpts* fixtures) live at
            // Fixtures/Integration/<EspNameWithoutExtension>/<Esp> -- some of
            // those also carry their own Strings/ subfolder, some don't.
            var sourceDir = ownSubfolder ? Path.Combine(fixturesDir, Path.GetFileNameWithoutExtension(esp)) : fixturesDir;
            File.Copy(Path.Combine(sourceDir, esp), Path.Combine(modDir, esp));
            var sourceStringsDir = Path.Combine(sourceDir, "Strings");
            if (Directory.Exists(sourceStringsDir))
            {
                var destStringsDir = Path.Combine(modDir, "Strings");
                Directory.CreateDirectory(destStringsDir);
                foreach (var file in Directory.EnumerateFiles(sourceStringsDir))
                    File.Copy(file, Path.Combine(destStringsDir, Path.GetFileName(file)));
            }
        }

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), string.Join("\r\n", mods.Select(m => "+" + m.Folder).Reverse()) + "\r\n");
        // Load order: TestXMod1 -> TestXMod2 -> FamousMod -> FamousModJapanesePatch ->
        // BigQuestMod -> PriorityModBase -> PriorityModPatch -> the independent
        // Sjpts* mods (order among these doesn't matter, none share a FormKey).
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Join("\r\n", mods.Select(m => "*" + m.Esp)) + "\r\n");

        return mo2Dir;
    }

    private static void SeedModGlossary(string plugin, string english, string japanese)
    {
        var path = Path.Combine(ModGlossary.DirectoryPath, Path.GetFileNameWithoutExtension(plugin) + ".tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{english}\t{japanese}\n");
    }

    private static string PluginFolderName(string plugin) => Path.GetFileNameWithoutExtension(plugin);

    /// <summary>Reads every plugin's translations.tsv under outputDir and
    /// flattens them into one deterministic, human-reviewable text block:
    /// grouped by resolution method (Notes column) so a diff immediately
    /// shows WHICH tier of confidence changed, then sorted by plugin and
    /// English text within each group.</summary>
    private static string CaptureSnapshot(string translationOutDir)
    {
        var rows = new List<(string Method, string Plugin, string English, string Japanese)>();
        foreach (var pluginDir in Directory.EnumerateDirectories(translationOutDir))
        {
            var tsvPath = Path.Combine(pluginDir, "translations.tsv");
            if (!File.Exists(tsvPath)) continue;
            var pluginFolderName = Path.GetFileName(pluginDir);
            foreach (var line in File.ReadAllLines(tsvPath).Skip(1))
            {
                if (line.Length == 0) continue;
                var parts = line.Split('\t');
                if (parts.Length < 6) continue;
                var english = TsvEscaping.Unescape(parts[3]);
                var japanese = TsvEscaping.Unescape(parts[4]);
                var notes = TsvEscaping.Unescape(parts[5]);
                var method = string.IsNullOrEmpty(notes) ? "(未解決 unresolved)" : notes;
                rows.Add((method, pluginFolderName, english, japanese));
            }
        }

        var sb = new System.Text.StringBuilder();
        foreach (var group in rows.GroupBy(r => r.Method).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"=== {group.Key} ===");
            foreach (var row in group.OrderBy(r => r.Plugin, StringComparer.Ordinal).ThenBy(r => r.English, StringComparer.Ordinal))
                sb.AppendLine($"[{row.Plugin}] \"{row.English}\" -> \"{row.Japanese}\"");
        }
        return sb.ToString();
    }

    [Fact]
    public void Run_ThenTranslate_FullCombinedLoadOrder_MatchesGoldenSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_characterization_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root); // isolates ModGlossary.DirectoryPath (CWD-relative) to this test
            SeedModGlossary("SjptsGlossaryTarget.esp", "Vrenn", "ヴレン");
            var mo2Dir = BuildCombinedMo2Instance(root);
            using var pickUpTargetLog = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");
            var result = PickUpTargetRunner.Run(mo2Dir, pickUpTargetLog);

            var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
            Directory.CreateDirectory(pickUpTargetOutDir);
            var candidatesTsvPath = Path.Combine(pickUpTargetOutDir, "candidates.tsv");
            var corpusTsvPath = Path.Combine(pickUpTargetOutDir, "corpus.tsv");
            CandidateIo.WriteTsv(candidatesTsvPath, result.Candidates);
            CorpusIo.WriteTsv(corpusTsvPath, result.Corpus);

            var translationOutDir = Path.Combine(root, "Translation", "out_temp");
            var importDir = Path.Combine(root, "Translation", "import");
            using (var translationLog = RunLog.Open(Path.Combine(root, "Translation"), "Translation"))
            {
                PromptGenerator.RunAll(candidatesTsvPath, corpusTsvPath, importDir, translationOutDir, translationLog);
            }

            var actual = CaptureSnapshot(translationOutDir);

            var goldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Integration", "Characterization", "golden_snapshot.txt");
            var golden = File.Exists(goldenPath) ? File.ReadAllText(goldenPath) : "";

            Assert.Equal(golden, actual);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
