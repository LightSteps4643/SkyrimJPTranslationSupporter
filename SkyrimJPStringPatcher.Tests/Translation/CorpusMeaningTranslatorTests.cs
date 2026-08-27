using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// CorpusMeaningTranslator (②意味合成) — mines "&lt;Modifier&gt; &lt;Head&gt;" gear
/// names from corroborating corpus evidence (a head needs 3+ distinct entries
/// agreeing on its Japanese suffix; a modifier needs to reach the SAME
/// rendering through 2+ DIFFERENT heads) and composes NEW pairs from what it
/// learned. This class takes a corpus list directly (no Data/ file reads of
/// its own), so the fixture below is a small hand-built corpus, not real
/// game data — the point isn't "is this the officially correct translation,"
/// it's "does the corroboration/composition mechanism work."
///
/// Fixtures/Translation/CorpusMeaningTranslator/corpus.tsv is built so heads
/// (Sword/Battleaxe/Boots) each get exactly the minimum 3 supporting entries,
/// and modifiers (Amber/Steel/Iron) each corroborate through exactly the
/// minimum 2 different heads (Sword+Battleaxe) — while Gold/Silver/Bronze
/// only ever modify ONE head (Boots) each, so they deliberately fall short
/// of corroboration and must NOT be learned as modifiers.
/// </summary>
public class CorpusMeaningTranslatorTests
{
    private static CorpusMeaningTranslator BuildFromFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "CorpusMeaningTranslator", "corpus.tsv");
        return CorpusMeaningTranslator.Build(CorpusIo.ReadTsv(path));
    }

    [Fact]
    public void TryTranslate_ComposesAPairNeverSeenVerbatimInTheCorpus()
    {
        var meaning = BuildFromFixture();

        // "Amber Boots" appears nowhere in the fixture corpus — this proves
        // real composition (modifier "Amber" learned from Sword/Battleaxe,
        // head "Boots" learned from Gold/Silver/Bronze), not parroting a row.
        var resolved = meaning.TryTranslate("Amber Boots", transliterator: null, out var japanese, out var usedTransliteration, out var breakdown);

        Assert.True(resolved);
        Assert.Equal("琥珀のブーツ", japanese);
        Assert.False(usedTransliteration);
        Assert.Equal("Amber", breakdown.ModifierWord);
        Assert.Equal("Boots", breakdown.HeadWord);
    }

    /// <summary>Gold/Silver/Bronze each only ever modify ONE head (Boots) in
    /// the fixture — one short of the 2-different-heads corroboration
    /// requirement — so they must never be learned as modifiers, even though
    /// their head (Boots) itself is perfectly well-supported.</summary>
    [Fact]
    public void TryTranslate_UncorroboratedModifier_IsNotLearned()
    {
        var meaning = BuildFromFixture();

        var resolved = meaning.TryTranslate("Gold Sword", transliterator: null, out _, out _, out _);

        Assert.False(resolved);
    }

    /// <summary>A head needs 3+ distinct supporting entries. This fixture's
    /// Dagger doesn't exist at all, so composing anything with "Dagger" as
    /// the head must fail regardless of how well-supported the modifier is.</summary>
    [Fact]
    public void TryTranslate_UnknownHead_Fails()
    {
        var meaning = BuildFromFixture();

        var resolved = meaning.TryTranslate("Amber Dagger", transliterator: null, out _, out _, out _);

        Assert.False(resolved);
    }

    [Theory]
    [InlineData("NPC_ FULL", false)]
    [InlineData("WEAP FULL", true)]
    [InlineData("ARMO FULL", true)]
    public void AppliesToRecordType_ExcludesOnlyNpcFull(string recordType, bool expected)
    {
        Assert.Equal(expected, CorpusMeaningTranslator.AppliesToRecordType(recordType));
    }
}
