using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// CorpusTransliterator (③音訳分解) — Pass 0 (seed) of its class remarks:
/// entries whose Japanese is a single unsegmented katakana block, appearing
/// VERBATIM in the corpus, are trusted outright as "official" precedent.
/// This first wave covers that seed pass plus multi-piece composition built
/// from it — the iterative multi-segment ("・"-joined) bootstrap growth pass
/// is NOT covered here (constructing a fixture that reliably exercises it,
/// without accidentally tripping the class's own several safety heuristics,
/// is materially harder and deserves its own pass).
///
/// Fixtures/Translation/CorpusTransliterator/corpus.tsv: "Frost"→"フロスト"
/// and "Fall"→"フォール", each a single katakana block attested verbatim —
/// textbook Pass 0 seed entries.
/// </summary>
public class CorpusTransliteratorTests
{
    private static CorpusTransliterator BuildFromFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "CorpusTransliterator", "corpus.tsv");
        return CorpusTransliterator.Build(CorpusIo.ReadTsv(path));
    }

    [Fact]
    public void TryTranslateWord_SeedEntry_ResolvesVerbatim()
    {
        var transliterator = BuildFromFixture();

        var resolved = transliterator.TryTranslateWord("Frost", out var katakana, out var source);

        Assert.True(resolved);
        Assert.Equal("フロスト", katakana);
        Assert.Contains("Fixture.esp", source);
    }

    /// <summary>"Frostfall" itself never appears in the corpus — only its two
    /// pieces do — so a successful decomposition proves genuine word-level
    /// composition, not a lookup.</summary>
    [Fact]
    public void TryDecompose_UnseenWord_ComposesFromTwoKnownPieces()
    {
        var transliterator = BuildFromFixture();

        var result = transliterator.TryDecompose("Frostfall", out var pieces);

        Assert.Equal("フロストフォール", result);
        Assert.Equal(2, pieces.Count);
        // Piece casing follows the INPUT word's own casing at that position
        // (here "Frostfall"'s literal "Frost"+"fall"), not the corpus key's —
        // the lookup itself is case-insensitive.
        Assert.Equal(("Frost", "フロスト"), (pieces[0].Piece, pieces[0].Kana));
        Assert.Equal(("fall", "フォール"), (pieces[1].Piece, pieces[1].Kana));
    }

    /// <summary>TryTranslateWord is a WHOLE-word/phrase lookup answered only
    /// from verbatim-attested corpus pairs — "Frostfall" was never attested
    /// as its own pair (only decomposable from pieces), so this must fail
    /// even though TryDecompose above succeeds for the same word. Guards
    /// against the two methods' contracts being conflated.</summary>
    [Fact]
    public void TryTranslateWord_WordOnlyReachableByDecomposition_Fails()
    {
        var transliterator = BuildFromFixture();

        var resolved = transliterator.TryTranslateWord("Frostfall", out _, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryTranslateWord_CompletelyUnknownWord_Fails()
    {
        var transliterator = BuildFromFixture();

        var resolved = transliterator.TryTranslateWord("Zzyzxqoo", out _, out _);

        Assert.False(resolved);
    }
}
