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

            DsdJsonGenerator.Run(FixturePath("translations_basic.tsv"), outDir, log);

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

            DsdJsonGenerator.Run(FixturePath("directory_input"), outDir, log);

            var dsdRoot = Path.Combine(outDir, "SKSE", "Plugins", "DynamicStringDistributor");
            Assert.True(File.Exists(Path.Combine(dsdRoot, "PluginA.esp", "SkyrimJPStringPatcher.json")));
            Assert.True(File.Exists(Path.Combine(dsdRoot, "PluginB.esp", "SkyrimJPStringPatcher.json")));
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
