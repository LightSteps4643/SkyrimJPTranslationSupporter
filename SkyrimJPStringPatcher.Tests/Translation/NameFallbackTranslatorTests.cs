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

    /// <summary>A leading "[Tag]" brand marker (a mod's own item-list prefix)
    /// is not part of the name — set aside before resolution and re-attached
    /// verbatim in front of the result.</summary>
    [Fact]
    public void TryTranslate_LeadingBracketedTag_IsSetAsideAndReattachedVerbatim()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("[E] Steel Boots", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Equal("[E] 鋼のブーツ", result!.Japanese);
        Assert.Empty(unresolved);
    }

    /// <summary>"AAA of CCC" moves the post-"of" part to the front — "Boots of
    /// Steel" must resolve identically to "Steel Boots".</summary>
    [Fact]
    public void TryTranslate_OfPhrase_MovesThePostOfPartToTheFront()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Boots of Steel", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Equal("鋼のブーツ", result!.Japanese);
        Assert.Empty(unresolved);
    }

    /// <summary>v0.40.0: "No X"/"Not X" has no slot in either connector rule
    /// (の/と both assume mutual modification, not negation) — resolves as
    /// one "（Xなし）" annotation instead.</summary>
    [Fact]
    public void TryTranslate_NoXNegation_RendersAsAParentheticalNashiAnnotation()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Steel Boots No Shoes", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Contains("（靴なし）", result!.Japanese);
        Assert.Empty(unresolved);
    }

    /// <summary>A hyphenated compound within a single token ("Dai-Katana")
    /// is split and each half resolved independently, then concatenated with
    /// NO separator — a hyphen binds tighter than a word boundary, so a
    /// "の" there would misrepresent it as two separately-modified nouns.</summary>
    [Fact]
    public void TryTranslate_HyphenatedCompoundWord_ResolvesBothHalvesConcatenatedWithNoSeparator()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Steel-Boots", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Equal("鋼ブーツ", result!.Japanese);
        Assert.Empty(unresolved);
    }

    /// <summary>A token that IS a parenthetical annotation start-to-finish
    /// ("(Steel)") is unwrapped, its inside resolved, and re-wrapped in
    /// parentheses — found via real data: "Bishop Belt (Brown)" left
    /// "(Brown)" untranslated even though "brown" was already known, because
    /// the parentheses made the token as a whole a literal miss.</summary>
    [Fact]
    public void TryTranslate_ParentheticalToken_UnwrapsResolvesAndRewraps()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Boots (Steel)", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Contains("(鋼)", result!.Japanese);
        Assert.Empty(unresolved);
    }

    /// <summary>v0.39.0: a known MULTI-WORD corpus phrase is matched as a
    /// whole before falling back to per-word resolution — the exact real
    /// motivating case from the class's own remarks: "Heavy" alone is a bound
    /// stem ("重", only reads naturally fused into a compound), but the
    /// corpus's own "Heavy Armor"→"重装" precedent is used directly instead
    /// of gluing "重" and a separately-resolved "Armor" with the default "の"
    /// (which would have produced the ungrammatical "重の鎧").</summary>
    [Fact]
    public void TryTranslate_KnownTwoWordCorpusPhrase_ResolvesAsOneUnitNotWordByWord()
    {
        var nameFallback = BuildFromFixture();

        var result = nameFallback.TryTranslate("Heavy Armor", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Equal("重装", result!.Japanese);
        Assert.Empty(unresolved);
    }

    /// <summary>v0.40.0/v0.40.1: a trailing run of color words (from the real,
    /// curated Data/color_words.txt — read dynamically here rather than
    /// assuming a specific word) is wrapped as one "（色）" annotation instead
    /// of joined with "の", which would read as a false possessive ("…の
    /// ホワイト" = "the tinsel's white"). Built with its own tiny in-memory
    /// corpus (not the shared fixture) so this test supplies a translation
    /// for whichever word the real color list actually starts with.</summary>
    [Fact]
    public void TryTranslate_TrailingColorWord_WrapsAsAParentheticalAnnotation()
    {
        var colorWordsPath = Path.Combine(AppContext.BaseDirectory, "Data", "color_words.txt");
        var colorWord = File.ReadLines(colorWordsPath).Select(l => l.Trim())
            .First(l => l.Length > 0 && !l.StartsWith('#'));

        var corpus = new List<CorpusEntry>
        {
            new("Steel", "鋼", "Fixture.esp", "vanilla", "WEAP FULL"),
            new("Boots", "ブーツ", "Fixture.esp", "vanilla", "ARMO FULL"),
            new(colorWord, "色見本", "Fixture.esp", "vanilla", "ARMO FULL"),
        };
        var auto = new AutoTranslator(corpus);
        var meaning = CorpusMeaningTranslator.Build(Array.Empty<CorpusEntry>());
        var transliterator = CorpusTransliterator.Build(Array.Empty<CorpusEntry>());
        var glossary = EnJaDictionary.Load(Path.Combine(FixturesDir, "empty_glossary.tsv"));
        var nameFallback = NameFallbackTranslator.Build(glossary, auto, meaning, transliterator);

        var result = nameFallback.TryTranslate($"Steel Boots {colorWord}", "ARMO FULL", NoModGlossary, out var unresolved);

        Assert.NotNull(result);
        Assert.Contains("（色見本）", result!.Japanese);
        Assert.Empty(unresolved);
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
