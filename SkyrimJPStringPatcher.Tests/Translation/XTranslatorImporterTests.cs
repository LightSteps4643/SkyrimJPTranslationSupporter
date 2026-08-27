using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// XTranslatorImporter parses xTranslator's SSTXMLRessources export format and
/// folds it into the corpus as "imported" precedent. A past real incident
/// (v0.33.0's motivation) had a missed step cut the auto-resolved count in
/// half, so the matching key ((RecordType, EnglishText)), the Japanese-Dest
/// validity check, the BOOK DESC/CNAM swap, and the newest-file-wins merge
/// order are all covered here directly against synthetic XML — there is no
/// real sample XML checked into this repo to reference instead.
/// </summary>
public class XTranslatorImporterTests
{
    private static string BuildXml(string plugin, params (string Rec, string Source, string Dest)[] entries)
    {
        var strings = string.Join("\n", entries.Select(e =>
            $"    <String>\n      <REC>{e.Rec}</REC>\n      <Source>{e.Source}</Source>\n      <Dest>{e.Dest}</Dest>\n    </String>"));
        return $"<SSTXMLRessources>\n  <Params>\n    <Addon>{plugin}</Addon>\n  </Params>\n  <Content>\n{strings}\n  </Content>\n</SSTXMLRessources>";
    }

    private static RunLog OpenTestLog(string root) => RunLog.Open(Path.Combine(root, "Translation"), "Translation");

    [Fact]
    public void Import_MatchesCandidateByRecordTypeAndText_AddsAsImportedCorpusEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            File.WriteAllText(Path.Combine(importDir, "TestMod.xml"),
                BuildXml("TestMod.esp", ("WEAP:FULL", "Steel Sword", "鋼の剣")));

            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "WEAP FULL", "Steel Sword") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            var entry = Assert.Single(result);
            Assert.Equal(("Steel Sword", "鋼の剣", "TestMod.esp", "imported", "WEAP FULL"),
                (entry.English, entry.Japanese, entry.Source, entry.SourceKind, entry.DsdType));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Import_EntryNotMatchingAnyCandidate_IsSkippedSilently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            File.WriteAllText(Path.Combine(importDir, "TestMod.xml"),
                BuildXml("TestMod.esp", ("WEAP:FULL", "Some Other Weapon", "何か別の武器")));

            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "WEAP FULL", "Steel Sword") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            Assert.Empty(result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Import_DestNotJapanese_IsExcludedEvenIfItMatchesACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            // Dest is still English — a likely paste mistake or leftover untranslated text.
            File.WriteAllText(Path.Combine(importDir, "TestMod.xml"),
                BuildXml("TestMod.esp", ("WEAP:FULL", "Steel Sword", "Steel Sword")));

            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "WEAP FULL", "Steel Sword") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            Assert.Empty(result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>xTranslator's "BOOK:DESC" is this tool's "BOOK CNAM" and vice
    /// versa — a real mislabel discovered importing Book Covers Skyrim.esp
    /// (see the class's own remarks on SwapBookDescCnam).</summary>
    [Fact]
    public void Import_BookDescRecordType_SwapsToBookCnamToMatchThisToolsCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            File.WriteAllText(Path.Combine(importDir, "TestMod.xml"),
                BuildXml("TestMod.esp", ("BOOK:DESC", "A short blurb.", "短い説明文。")));

            // This tool's own candidate list calls the same field "BOOK CNAM" (see PickUpTarget/ExtraTranslatableFields.cs).
            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "BOOK CNAM", "A short blurb.") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            var entry = Assert.Single(result);
            Assert.Equal("BOOK CNAM", entry.DsdType);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.24.0: files are processed oldest-first, so a later (newer)
    /// file's entry for the same key overwrites an earlier one — the theory
    /// being a more recently published community translation supersedes an
    /// older one for the same mod.</summary>
    [Fact]
    public void Import_TwoFilesTranslateTheSameKey_NewerFileByLastWriteTimeWins()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            var olderPath = Path.Combine(importDir, "a_older.xml");
            var newerPath = Path.Combine(importDir, "b_newer.xml");
            File.WriteAllText(olderPath, BuildXml("TestMod.esp", ("WEAP:FULL", "Steel Sword", "旧訳・鋼の剣")));
            File.WriteAllText(newerPath, BuildXml("TestMod.esp", ("WEAP:FULL", "Steel Sword", "新訳・鋼の剣")));
            // Filenames alone don't determine processing order (real download names like "(1)"/"(2)"
            // don't reliably encode recency) — LastWriteTimeUtc does, so set it explicitly here
            // rather than relying on filesystem creation order, which this test cannot control.
            File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);

            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "WEAP FULL", "Steel Sword") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            var entry = Assert.Single(result);
            Assert.Equal("新訳・鋼の剣", entry.Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Import_ImportDirDoesNotExist_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var importDir = Path.Combine(root, "does_not_exist");
            var candidates = new List<Candidate>();
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            Assert.Empty(result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Import_ImportDirExistsButHasNoXmlFiles_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            File.WriteAllText(Path.Combine(importDir, "readme.txt"), "not xml");
            var candidates = new List<Candidate>();
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            Assert.Empty(result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A malformed input file must never crash the whole batch import.</summary>
    [Fact]
    public void Import_MalformedXml_SkippedGracefully_OtherFilesStillProcessed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            File.WriteAllText(Path.Combine(importDir, "broken.xml"), "<SSTXMLRessources><Content>not closed");
            File.WriteAllText(Path.Combine(importDir, "good.xml"),
                BuildXml("TestMod.esp", ("WEAP:FULL", "Steel Sword", "鋼の剣")));

            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "WEAP FULL", "Steel Sword") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            var entry = Assert.Single(result);
            Assert.Equal("鋼の剣", entry.Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Import_XmlMissingParamsAddon_SkippedGracefully_NotARecognizableFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            File.WriteAllText(Path.Combine(importDir, "no_addon.xml"),
                "<SSTXMLRessources><Content><String><REC>WEAP:FULL</REC><Source>Steel Sword</Source><Dest>鋼の剣</Dest></String></Content></SSTXMLRessources>");

            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "WEAP FULL", "Steel Sword") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            Assert.Empty(result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.26.0: a zip extracted straight into the import folder routinely
    /// puts the actual XML one or more subfolders down (alongside a readme/license
    /// the *.xml-only scan simply never matches) — the search must be recursive.</summary>
    [Fact]
    public void Import_XmlInsideSubfolder_IsFoundRecursively()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_xtimport_{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        var subDir = Path.Combine(importDir, "TestMod_v1.2", "SSTXMLRessources");
        Directory.CreateDirectory(subDir);
        try
        {
            File.WriteAllText(Path.Combine(subDir, "TestMod.xml"),
                BuildXml("TestMod.esp", ("WEAP:FULL", "Steel Sword", "鋼の剣")));

            var candidates = new List<Candidate> { new("TestMod.esp", "0x001", "WEAP FULL", "Steel Sword") };
            using var log = OpenTestLog(root);

            var result = XTranslatorImporter.Load(importDir, candidates, log);

            Assert.Single(result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
