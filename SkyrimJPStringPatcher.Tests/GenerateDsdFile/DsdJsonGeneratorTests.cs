using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.GenerateDsdFile;

namespace SkyrimJPStringPatcher.Tests.GenerateDsdFile;

/// <summary>DsdJsonGenerator has no Mutagen dependency at all (Translation's
/// translations.tsv in, DSD json out), so — like CandidateIoTests — every case
/// here uses a checked-in fixture plus, where the exact serialized shape
/// matters, a checked-in golden-file JSON to diff against.</summary>
public class DsdJsonGeneratorTests
{
    private static string FixturePath(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures", "GenerateDsdFile" }.Concat(parts).ToArray());

    private static RunLog OpenTestLog(string root) => RunLog.Open(Path.Combine(root, "GenerateDsdFile"), "GenerateDsdFile");

    // v0.57.4: the output filename now stamps in the run timestamp (see
    // DsdWriter.cs's own remarks — lets successive incremental runs coexist
    // instead of shadowing each other in MO2's VFS). Tests pass this fixed
    // value so the filename stays deterministic and matches the checked-in
    // golden fixture below.
    private static readonly DateTime TestTimestamp = new(2026, 1, 1, 0, 0, 0);
    private const string TestOutputFileName = "SkyrimJPStringPatcher_20260101000000.json";

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    /// <summary>Fixtures/translations_basic.tsv exercises, in one pass: a normal
    /// translated row (正常系); a still-blank row, which is the normal
    /// work-in-progress state, not an error (準正常系); the AutoCorpusOverride
    /// exemption that deliberately keeps a non-Japanese value like "pts"
    /// (準正常系, a real historical special case); the same exemption for
    /// ModifiedByUser (準正常系 — a human's own deliberate edit via
    /// TranslationDetailForm, e.g. keeping a proper noun like "Bob" as-is,
    /// deserves the same trust as a curated override, not a second-guess); a
    /// translated row whose Japanese column isn't actually Japanese AND carries
    /// no resolution-method/ModifiedByUser tag at all (準正常系 — 2026-09-06: no
    /// longer excluded either, since the exclusion branch was removed entirely
    /// in favor of the same informational-note treatment ModifiedByUser/
    /// *NoJapanese already get; see Run_NonJapaneseAutoResolutionTags_
    /// ShouldBeIncludedWithNoteNotExcluded for the tag-by-tag coverage). This
    /// row's blank Notes still can't actually arise through this tool's own
    /// pipeline — WriteTranslationTemplate only ever fills the Japanese column
    /// together with a Notes tag — so it implies the .tsv was hand-edited
    /// outside the tool; it's kept here as a characterization of that
    /// "unknown/blank tag" edge case, not as an exclusion guard anymore. An
    /// unparseable FormId (異常系, a corrupted row) is still excluded, since
    /// that's a structurally different problem (can't be written to a plugin
    /// folder at all, not a content-trust question). A non-zero Index
    /// (正常系 — DSD's indexed types, e.g. a quest objective) confirms it
    /// passes through untouched, not silently reset to 0. Two different
    /// winning plugins split the output across two files.</summary>
    [Fact]
    public void Run_BasicFixture_WritesExpectedEntriesPerPlugin_SkipsBlankAndInvalidRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outDir = Path.Combine(root, "out");
            using var log = OpenTestLog(root);

            DsdJsonGenerator.Run(FixturePath("translations_basic.tsv"), outDir, log, outputTimestamp: TestTimestamp);

            var expectedRoot = FixturePath("expected_output");
            var actualRoot = outDir;
            var expectedFiles = Directory.GetFiles(expectedRoot, "*", SearchOption.AllDirectories);
            Assert.Equal(2, expectedFiles.Length); // Skyrim.esm + TestMod.esp — guards the fixture itself against silent drift

            foreach (var expectedFile in expectedFiles)
            {
                var relative = Path.GetRelativePath(expectedRoot, expectedFile);
                var actualFile = Path.Combine(actualRoot, relative);
                Assert.True(File.Exists(actualFile), $"Expected output file missing: {relative}");
                // Content equality, not byte equality — checked-in line endings
                // depend on git's checkout settings (no .gitattributes pins them
                // here), and that's not what this test is meant to catch.
                Assert.Equal(Normalize(File.ReadAllText(expectedFile)), Normalize(File.ReadAllText(actualFile)));
            }

            // And nothing EXTRA was written beyond the two expected plugin files
            // (e.g. a third folder for the excluded rows would be a real bug).
            var actualFiles = Directory.GetFiles(actualRoot, "*", SearchOption.AllDirectories);
            Assert.Equal(2, actualFiles.Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// 2026-09-12: issue #6 — the GUI's "DSDファイル生成" button always processed
    /// the ENTIRE Translation/out_temp tree, ignoring which plugins were checked
    /// in the DataGridView (unlike Interface翻訳's "output", which already
    /// scopes to selection via --mods-file). This is a pure behavior/black-box
    /// test — like the fixture test above, it drives DsdJsonGenerator.Run
    /// end-to-end on the SAME checked-in fixture (translations_basic.tsv, two
    /// winning plugins: Skyrim.esm and TestMod.esp) and asserts only on the
    /// output files, with no reference to internals — but this time passing a
    /// pluginFilter restricted to "TestMod.esp" and confirming Skyrim.esm's
    /// output is entirely absent while TestMod.esp's is unaffected. Deliberately
    /// confirmed red against today's (pre-fix) code — Run had no such parameter
    /// at all before this fix.
    /// </summary>
    [Fact]
    public void Run_WithPluginFilter_OnlyWritesOutputForTheFilteredPlugins()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_filter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outDir = Path.Combine(root, "out");
            using var log = OpenTestLog(root);

            DsdJsonGenerator.Run(FixturePath("translations_basic.tsv"), outDir, log,
                outputTimestamp: TestTimestamp, pluginFilter: new[] { "TestMod.esp" });

            var actualFiles = Directory.Exists(outDir)
                ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

            Assert.Contains(actualFiles, f => f.Contains("TestMod.esp"));
            Assert.DoesNotContain(actualFiles, f => f.Contains("Skyrim.esm"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.56.0: a ModifiedByUser row whose translation doesn't contain
    /// Japanese (Fixtures/translations_basic.tsv's "Bob" row) must be INCLUDED
    /// as-is (already covered by the golden-file check above) AND get a logged
    /// NOTE — distinct from the [warn]+exclude path used for untrusted rows —
    /// so a person can spot-check it later without the tool silently dropping
    /// or silently accepting it without any trace.
    ///
    /// v0.55.2: found via real usage — a user who ran generatedsdfile.exe
    /// directly never opens generatedsdfile.log, so a log-file-only note went
    /// completely unnoticed. Promoted to a console [warn] line too, so it's
    /// visible without opening the log.
    ///
    /// 2026-09-06: the exclusion branch was removed entirely, so the fixture's
    /// other non-Japanese row (00011111:TestMod.esp, blank Notes) now ALSO
    /// gets this same informational-note treatment instead of being excluded
    /// — hence 2 notes, 0 exclusions, where this test previously expected
    /// 1 and 1.</summary>
    [Fact]
    public void Run_ModifiedByUserNonJapanese_LogsANoteInsteadOfExcluding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var log = OpenTestLog(root);

            var originalError = Console.Error;
            var capturedError = new StringWriter();
            Console.SetError(capturedError);
            try
            {
                DsdJsonGenerator.Run(FixturePath("translations_basic.tsv"), Path.Combine(root, "out"), log, outputTimestamp: TestTimestamp);
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.Equal(2, log.DetailCount(
                "情報: 訳文に日本語が含まれていないが、そのまま出力する（意図的な可能性があるため除外しない）",
                "Note: a translation doesn't contain Japanese — included as-is (not excluded, since this may be intentional)"));
            Assert.Contains("[warn] '00099999:TestMod.esp' translation doesn't look like Japanese — keeping as-is (not excluded): \"Bob\"",
                capturedError.ToString());
            Assert.Contains("[warn] '00011111:TestMod.esp' translation doesn't look like Japanese — keeping as-is (not excluded): \"NotJapaneseOops\"",
                capturedError.ToString());

            // The exclusion branch no longer exists — no non-Japanese row goes
            // through it anymore, regardless of Notes tag.
            Assert.Equal(0, log.DetailCount(
                "除外: Japanese列に日本語が含まれていない（訳し忘れ・貼り付けミスの可能性）",
                "Excluded: the Japanese column doesn't contain Japanese (possibly a missed translation or a paste mistake)"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.58.5: PromptGenerator.ApplyLlmStep now tags a candidate whose
    /// LLM response parsed fine but whose translation contains no Japanese with
    /// a dedicated "&lt;method&gt;NoJapanese" Notes value (e.g.
    /// "TranslationLocalLlmNoJapanese") instead of silently discarding it or
    /// accepting it as an ordinary success — see that method's own remarks for
    /// why (a real example: vanilla Skyrim's own untranslated "arcane script"
    /// spell-tome flavor text). DsdJsonGenerator must treat ANY Notes value
    /// ending in "NoJapanese" the same way it already treats ModifiedByUser —
    /// included as-is with an informational note, not excluded with a [warn] —
    /// since this may be genuinely untranslatable content, not a mistake.
    /// Uses its own self-contained fixture (not translations_basic.tsv) so it
    /// doesn't disturb that fixture's golden-file JSON diff used elsewhere.</summary>
    [Fact]
    public void Run_LlmNoJapaneseTag_IsIncludedAsIsWithANote_NotExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inputPath = Path.Combine(root, "translations.tsv");
            File.WriteAllText(inputPath,
                "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\n" +
                "000ABCDE:TestMod.esp\tTestMod.esp\tBOOK CNAM\tSCRAMBLED GIBBERISH TEXT\tSCRAMBLED GIBBERISH TEXT\tTranslationLocalLlmNoJapanese\t0\t\n");

            var outDir = Path.Combine(root, "out");
            using var log = OpenTestLog(root);

            var originalError = Console.Error;
            var capturedError = new StringWriter();
            Console.SetError(capturedError);
            try
            {
                DsdJsonGenerator.Run(inputPath, outDir, log, outputTimestamp: TestTimestamp);
            }
            finally
            {
                Console.SetError(originalError);
            }

            // Included as-is (not excluded) -- the DSD json for TestMod.esp must exist.
            var dsdPath = Path.Combine(outDir, "SKSE", "Plugins", "DynamicStringDistributor", "TestMod.esp", TestOutputFileName);
            Assert.True(File.Exists(dsdPath));
            Assert.Contains("SCRAMBLED GIBBERISH TEXT", File.ReadAllText(dsdPath));

            // Noted (info), not the "excluded" [warn]/log category.
            Assert.Equal(1, log.DetailCount(
                "情報: 訳文に日本語が含まれていないが、そのまま出力する（意図的な可能性があるため除外しない）",
                "Note: a translation doesn't contain Japanese — included as-is (not excluded, since this may be intentional)"));
            Assert.Equal(0, log.DetailCount(
                "除外: Japanese列に日本語が含まれていない（訳し忘れ・貼り付けミスの可能性）",
                "Excluded: the Japanese column doesn't contain Japanese (possibly a missed translation or a paste mistake)"));
            Assert.Contains("[warn] '000ABCDE:TestMod.esp' translation doesn't look like Japanese — keeping as-is (not excluded)", capturedError.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-06 TODO investigation (management repo's
    /// todo/active.md): ①②③④ (and AutoCrossModPrecedent, once implemented) are
    /// ALL either structurally incapable of returning partial non-Japanese
    /// output (②③④ — see design/translation_resolution_chain.md's all-or-
    /// nothing invariant) or, like AutoCorpusOverride, back themselves with
    /// human-authored external translation data (AutoCorpus/AutoCorpusDsd/
    /// AutoCorpusImported/AutoCorpusReferenceTaiyaku — real DSD files,
    /// xTranslator imports, a bilingual reference table) where a legitimately
    /// non-Japanese value is exactly as plausible as it is for
    /// AutoCorpusOverride's curated "pts"->"pts". Current behavior instead
    /// EXCLUDES all of these from DSD output (silently dropping a real,
    /// intentional entry) whenever the sole non-Japanese-safe channel — a typo
    /// in a human-edited glossary (Data/mod_glossary/*.tsv,
    /// Data/name_glossary.tsv) — happens to fire. This test documents that
    /// CURRENT (undesired) behavior as a RED characterization: every one of
    /// these tags should end up INCLUDED with an informational note (the same
    /// treatment ModifiedByUser/*NoJapanese already get), not excluded. It is
    /// expected to FAIL until DsdJsonGenerator's exclusion branch is removed
    /// in favor of widening the info-note branch's condition.
    /// AutoCorpusOverride itself (row 9 in the fixture) is included as a
    /// regression guard: it must stay fully silent — no exclusion AND no info
    /// log — exactly as before, since that curated-exemption behavior isn't
    /// changing.</summary>
    [Fact]
    public void Run_NonJapaneseAutoResolutionTags_ShouldBeIncludedWithNoteNotExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outDir = Path.Combine(root, "out");
            using var log = OpenTestLog(root);

            var originalError = Console.Error;
            var capturedError = new StringWriter();
            Console.SetError(capturedError);
            try
            {
                DsdJsonGenerator.Run(FixturePath("translations_nonjapanese_autotags.tsv"), outDir, log, outputTimestamp: TestTimestamp);
            }
            finally
            {
                Console.SetError(originalError);
            }

            var dsdPath = Path.Combine(outDir, "SKSE", "Plugins", "DynamicStringDistributor", "TagsMod.esp", TestOutputFileName);
            Assert.True(File.Exists(dsdPath), "TagsMod.esp's DSD output should exist even though every candidate row is non-Japanese.");
            var json = File.ReadAllText(dsdPath);

            var taggedNonJapaneseValues = new[]
            {
                "Iron Dagger Corpus",       // AutoCorpus
                "Steel Shield Dsd",         // AutoCorpusDsd
                "Fireball Imported",        // AutoCorpusImported
                "Ancient Tome Reference",   // AutoCorpusReferenceTaiyaku
                "Frost Breath Meaning",     // AutoCorpusMeaning
                "Silver Ingot Translit",    // AutoCorpusTransliterate
                "John Namefallback",        // TranslationNameFallback
                "Dragon Quest Precedent",   // AutoCrossModPrecedent
            };
            foreach (var value in taggedNonJapaneseValues)
                Assert.Contains(value, json);

            // AutoCorpusOverride's "pts" must also still be included (unchanged).
            Assert.Contains("pts", json);

            // None of the 8 auto-resolution-tagged rows should have gone through
            // the exclusion path.
            Assert.Equal(0, log.DetailCount(
                "除外: Japanese列に日本語が含まれていない（訳し忘れ・貼り付けミスの可能性）",
                "Excluded: the Japanese column doesn't contain Japanese (possibly a missed translation or a paste mistake)"));

            // Each should instead carry the same informational note ModifiedByUser/*NoJapanese get.
            Assert.Equal(8, log.DetailCount(
                "情報: 訳文に日本語が含まれていないが、そのまま出力する（意図的な可能性があるため除外しない）",
                "Note: a translation doesn't contain Japanese — included as-is (not excluded, since this may be intentional)"));

            // AutoCorpusOverride stays fully silent — no [warn] line for "pts" at all.
            Assert.DoesNotContain("'00000009:TagsMod.esp'", capturedError.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>ResolveInputFiles accepts a DIRECTORY too, recursively finding
    /// every "translations.tsv" — the shape Translation/out_temp/&lt;plugin&gt;/
    /// actually has, as opposed to a single merged file.</summary>
    [Fact]
    public void Run_DirectoryInput_MergesEveryNestedTranslationsTsv()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outDir = Path.Combine(root, "out");
            using var log = OpenTestLog(root);

            DsdJsonGenerator.Run(FixturePath("directory_input"), outDir, log, outputTimestamp: TestTimestamp);

            var dsdRoot = Path.Combine(outDir, "SKSE", "Plugins", "DynamicStringDistributor");
            Assert.True(File.Exists(Path.Combine(dsdRoot, "PluginA.esp", TestOutputFileName)));
            Assert.True(File.Exists(Path.Combine(dsdRoot, "PluginB.esp", TestOutputFileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>DsdJsonGenerator has no dedup logic — if the SAME (FormId,
    /// RecordType) shows up in two different translations.tsv files under a
    /// directory input (Fixtures/duplicate_input/FolderA and FolderB both
    /// translate 00000005:DupPlugin.esp's WEAP FULL, differently), both rows
    /// are written to the plugin's output JSON as separate entries. This
    /// documents that current (permissive) behavior rather than asserting it's
    /// the "correct" one — there is no real-world path that produces two
    /// translations.tsv files for the same plugin under Translation/out_temp,
    /// so this is a characterization test for an edge case, not a guard
    /// against a known bug.</summary>
    [Fact]
    public void Run_DuplicateFormIdAcrossFiles_WritesBothEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outDir = Path.Combine(root, "out");
            using var log = OpenTestLog(root);

            DsdJsonGenerator.Run(FixturePath("duplicate_input"), outDir, log, outputTimestamp: TestTimestamp);

            var jsonPath = Path.Combine(outDir, "SKSE", "Plugins", "DynamicStringDistributor", "DupPlugin.esp", TestOutputFileName);
            var json = File.ReadAllText(jsonPath);
            Assert.Contains("重複剣（旧）", json);
            Assert.Contains("重複剣（新）", json);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Run_NonexistentInputPath_ThrowsFileNotFoundException()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var log = OpenTestLog(root);
            var missingPath = Path.Combine(root, "does_not_exist.tsv");

            Assert.Throws<FileNotFoundException>(() => DsdJsonGenerator.Run(missingPath, Path.Combine(root, "out"), log));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
