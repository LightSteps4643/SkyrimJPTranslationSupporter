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
    /// no resolution-method/ModifiedByUser tag at all (異常系). Note this last
    /// case can't actually arise through this tool's own pipeline — WriteTranslationTemplate
    /// only ever fills the Japanese column together with a Notes tag (the
    /// resolution method, or ModifiedByUser) — so a blank-Notes row with
    /// Japanese text implies the .tsv was hand-edited outside the tool
    /// entirely; that's exactly the untrusted case this check is a safety net
    /// for. An unparseable FormId (異常系, a corrupted row) is also covered.
    /// A non-zero Index (正常系 — DSD's indexed types, e.g. a quest objective)
    /// confirms it passes through untouched, not silently reset to 0.
    /// Two different winning plugins split the output across two files.</summary>
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
    /// visible without opening the log.</summary>
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

            Assert.Equal(1, log.DetailCount(
                "情報: 手動編集の訳文に日本語が含まれていないが、そのまま出力する（意図的な可能性があるため除外しない）",
                "Note: a manually-edited translation doesn't contain Japanese — included as-is (not excluded, since this may be intentional)"));
            Assert.Contains("[warn] '00099999:TestMod.esp' manually-edited translation doesn't look like Japanese — keeping as-is (not excluded): \"Bob\"",
                capturedError.ToString());

            // The untrusted (no-tag) non-Japanese row must still go through the
            // real exclusion path, not get swept into this same note category.
            Assert.Equal(1, log.DetailCount(
                "除外: Japanese列に日本語が含まれていない（訳し忘れ・貼り付けミスの可能性）",
                "Excluded: the Japanese column doesn't contain Japanese (possibly a missed translation or a paste mistake)"));
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
