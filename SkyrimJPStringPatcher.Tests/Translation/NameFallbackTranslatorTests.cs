using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// NameFallbackTranslator (④, the lowest-confidence step): translates each
/// word of a name independently (①完全一致 → ②意味 → ③音訳 → curated
/// glossary) and chains the results with a small fixed set of connector
/// rules — no grammar, and all-or-nothing (a single word with no precedent
/// anywhere abandons the WHOLE candidate rather than emitting a half-English
/// hybrid).
///
/// Built from an empty CorpusMeaningTranslator/CorpusTransliterator (this
/// class's own mining logic is covered by CorpusMeaningTranslatorTests/
/// CorpusTransliteratorTests separately) plus a small hand-built corpus so
/// AutoTranslator.TryExactWord (word-level ①) can resolve each test word —
/// what's being exercised here is the CHAINING/connector logic, not word
/// resolution itself.
/// </summary>
public class NameFallbackTranslatorTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "NameFallbackTranslator");

    private static NameFallbackTranslator BuildFromFixture()
    {
        var corpus = CorpusIo.ReadTsv(Path.Combine(FixturesDir, "corpus.tsv"));
        var auto = new AutoTranslator(corpus);
        var meaning = CorpusMeaningTranslator.Build(Array.Empty<CorpusEntry>());
        var transliterator = CorpusTransliterator.Build(Array.Empty<CorpusEntry>());
        var glossary = EnJaDictionary.Load(Path.Combine(FixturesDir, "empty_glossary.tsv"));
        return NameFallbackTranslator.Build(glossary, auto, meaning, transliterator);
    }

    private static readonly ModGlossary NoModGlossary = ModGlossary.LoadFor("SjptsNameFallbackTest.esp");

    [Fact]
    public void TryTranslate_TwoResolvableWords_ChainsWithDefaultJoiner()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Steel Boots", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Equal("鋼のブーツ", result!.Japanese);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void TryTranslate_AndConnector_JoinsWithToInsteadOfDefault()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Shoes and Boots", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Equal("靴とブーツ", result!.Japanese);
        Assert.Empty(unresolved);
    }

    /// <summary>All-or-nothing (v0.30.0): "Zzyzxqoo" has no precedent anywhere,
    /// so the WHOLE candidate must fail — never a half-translated hybrid like
    /// "鋼のZzyzxqoo".</summary>
    [Fact]
    public void TryTranslate_OneUnresolvableWord_FailsTheWholeCandidate()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Steel Zzyzxqoo", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.Null(result);
        Assert.Contains("Zzyzxqoo", unresolved);
    }

    [Fact]
    public void TryTranslate_NpcFull_NeverAttempted()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Steel Boots", "NPC_ FULL", NoModGlossary, out var unresolved);

        Assert.Null(result);
        Assert.Empty(unresolved);
    }

    /// <summary>Data/name_glossary.tsv is the real, curated, tool-shipped
    /// glossary — TryResolveCore's last-resort source. Rather than hardcode
    /// which word it currently contains, this reads the file's own first
    /// usable entry at test time (same approach as AutoTranslatorTests'
    /// real-exclusion-list/phrase-override tests), so the test tracks the
    /// real curated data instead of a frozen snapshot of it.</summary>
    [Fact]
    public void TryTranslate_WordOnlyInRealNameGlossary_ResolvesViaGlossaryFallback()
    {
        var realGlossaryPath = Path.Combine(AppContext.BaseDirectory, "Data", "name_glossary.tsv");
        var (english, japanese) = ReadFirstGlossaryEntry(realGlossaryPath);

        var corpus = CorpusIo.ReadTsv(Path.Combine(FixturesDir, "corpus.tsv")); // "Boots" only — english is NOT in here
        var auto = new AutoTranslator(corpus);
        var meaning = CorpusMeaningTranslator.Build(Array.Empty<CorpusEntry>());
        var transliterator = CorpusTransliterator.Build(Array.Empty<CorpusEntry>());
        var realGlossary = EnJaDictionary.Load(realGlossaryPath);
        var nameFallback = NameFallbackTranslator.Build(realGlossary, auto, meaning, transliterator);

        // Data/name_glossary.tsv stores plain lowercase dictionary words
        // (EnJaDictionary's lookup is case-insensitive either way), but a
        // real name-field candidate is Title Case — NameFieldFilter.
        // LooksLikeNameField requires every word to carry an uppercase
        // letter, so the composed test name needs that shape too.
        var titleCased = char.ToUpperInvariant(english[0]) + english[1..];
        var result = nameFallback.TryTranslate($"{titleCased} Boots", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Equal($"{japanese}のブーツ", result!.Japanese);
        Assert.Empty(unresolved);
    }

    private static (string English, string Japanese) ReadFirstGlossaryEntry(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var english = line[..tab].Trim();
            var japanese = line[(tab + 1)..].Trim();
            // Needs to look like a single plain word (no space) to compose
            // cleanly as "<word> Boots" in this test's chain.
            if (english.Length > 0 && japanese.Length > 0 && !english.Contains(' '))
                return (english, japanese);
        }
        throw new InvalidOperationException($"No usable single-word entry found in {path}");
    }
}
