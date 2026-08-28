using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// ModGlossary's TSV round-trip and merge-never-overwrite contract
/// (Data/mod_glossary/&lt;safe plugin name&gt;.tsv, one file per plugin).
/// PromptGeneratorTests already confirmed Directory.GetCurrentDirectory()
/// resolves to the TEST PROJECT's own build-output copy of Data/ during
/// `dotnet test`, not the repository's real tracked Data/ folder, so writing
/// here (via ModGlossary.WriteTemplate, exactly like the real pipeline does)
/// cannot pollute real curated data. Each test uses its own unique,
/// obviously-fictional plugin name and deletes its file afterward.
/// </summary>
public class ModGlossaryTests
{
    private static string PathFor(string plugin) =>
        Path.Combine(ModGlossary.DirectoryPath, Path.GetFileNameWithoutExtension(plugin) + ".tsv");

    [Fact]
    public void LoadFor_NoFile_ReturnsEmpty()
    {
        var glossary = ModGlossary.LoadFor("SjptsGlossaryNoSuchPlugin.esp");

        Assert.Equal(0, glossary.FilledCount);
        Assert.False(glossary.TryTranslateWord("Anything", out _));
    }

    [Fact]
    public void LoadFor_ParsesEntries_SkipsCommentsBlankLinesAndUnfilledJapaneseColumn()
    {
        const string plugin = "SjptsGlossaryLoadTest.esp";
        var path = PathFor(plugin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllLines(path,
            [
                "# a comment line, must be skipped",
                "",
                "Fawnia\tフォニア",
                "Unfilled\t", // blank Japanese column -- must behave as no row at all
                "TagOnly\t=",
                "TooFewColumns",
            ]);

            var glossary = ModGlossary.LoadFor(plugin);

            Assert.Equal(2, glossary.FilledCount); // Fawnia + TagOnly only
            Assert.True(glossary.TryTranslateWord("Fawnia", out var jp));
            Assert.Equal("フォニア", jp);
            Assert.False(glossary.TryTranslateWord("Unfilled", out _));
            Assert.False(glossary.TryTranslateWord("TooFewColumns", out _));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void IsPassThrough_TrueOnlyForThePassThroughMarker()
    {
        const string plugin = "SjptsGlossaryPassThroughTest.esp";
        var path = PathFor(plugin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllLines(path,
            [
                "SMP\t=",
                "Fawnia\tフォニア",
            ]);

            var glossary = ModGlossary.LoadFor(plugin);

            Assert.True(glossary.IsPassThrough("SMP"));
            Assert.False(glossary.IsPassThrough("Fawnia"));
            Assert.False(glossary.IsPassThrough("NeverSeenWord"));

            // A pass-through row's translation is the English token itself.
            Assert.True(glossary.TryTranslateWord("SMP", out var smp));
            Assert.Equal("SMP", smp);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void TryTranslateWord_IsCaseInsensitive()
    {
        const string plugin = "SjptsGlossaryCaseTest.esp";
        var path = PathFor(plugin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllLines(path, ["Fawnia\tフォニア"]);

            var glossary = ModGlossary.LoadFor(plugin);

            Assert.True(glossary.TryTranslateWord("fawnia", out var jp));
            Assert.Equal("フォニア", jp);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteTemplate_NewFile_OrdersByBlockedCountDescending_LeavesJapaneseColumnBlank()
    {
        const string plugin = "SjptsGlossaryWriteNewTest.esp";
        var path = PathFor(plugin);
        try
        {
            ModGlossary.WriteTemplate(plugin,
            [
                new ModGlossary.Blocker("Rare", 2, "Rare Helm"),
                new ModGlossary.Blocker("Common", 10, "Common Boots"),
            ]);

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').ToList();
            // Most-blocking word first.
            Assert.Equal("Common\t\t10\tCommon Boots", lines[0]);
            Assert.Equal("Rare\t\t2\tRare Helm", lines[1]);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The core "never destroys human work" contract: a previously
    /// filled-in Japanese value survives regeneration verbatim, and a word
    /// that no longer blocks anything is RETAINED (not dropped) with its
    /// count zeroed, rather than losing the person's decision.</summary>
    [Fact]
    public void WriteTemplate_Regenerated_PreservesFilledValues_RetiresStaleWordsInsteadOfDroppingThem()
    {
        const string plugin = "SjptsGlossaryWriteMergeTest.esp";
        var path = PathFor(plugin);
        try
        {
            ModGlossary.WriteTemplate(plugin,
            [
                new ModGlossary.Blocker("Fawnia", 5, "Fawnia Robe"),
                new ModGlossary.Blocker("StillBlocking", 1, "StillBlocking Ring"),
            ]);

            // A person fills in "Fawnia" by hand.
            var glossary = ModGlossary.LoadFor(plugin);
            Assert.False(glossary.TryTranslateWord("Fawnia", out _)); // unfilled template row, not yet a real entry
            var content = File.ReadAllText(path).Replace("Fawnia\t\t5\tFawnia Robe", "Fawnia\tフォニア\t5\tFawnia Robe");
            File.WriteAllText(path, content);

            // Regenerate: "Fawnia" no longer blocks anything (retired), a new word appears.
            ModGlossary.WriteTemplate(plugin,
            [
                new ModGlossary.Blocker("StillBlocking", 3, "StillBlocking Ring"),
                new ModGlossary.Blocker("NewWord", 7, "NewWord Amulet"),
            ]);

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.Equal("NewWord\t\t7\tNewWord Amulet", lines[0]); // still-blocking, most first
            Assert.Equal("StillBlocking\t\t3\tStillBlocking Ring", lines[1]);
            Assert.Equal("Fawnia\tフォニア\t0\t", lines[2]); // retired, but the filled value survives

            var reloaded = ModGlossary.LoadFor(plugin);
            Assert.True(reloaded.TryTranslateWord("Fawnia", out var jp));
            Assert.Equal("フォニア", jp);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
