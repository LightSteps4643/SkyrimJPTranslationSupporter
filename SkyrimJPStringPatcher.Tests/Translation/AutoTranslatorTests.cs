using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// AutoTranslator.TryTranslate's ①コーパス完全一致 path — the highest-value,
/// most historically bug-prone part of the auto-resolution pipeline (real
/// mistranslation incidents: "Hevnoraak"→"ヘブラノーク" (v0.38.0, SourceTier),
/// "Courage" NPC/spell homograph (v0.49.2), "Druid"→"ドルイドの指" (v0.43.0)).
/// ②意味合成/③音訳分解 (CorpusMeaningTranslator/CorpusTransliterator) are out
/// of scope here — their table-building is more involved and deserves its own
/// pass.
///
/// Fixtures/Translation/corpus_basic.tsv uses real vanilla Skyrim EN/JA pairs
/// where possible (stable, environment-independent test data — not verifying
/// "is this the officially correct translation," just using realistic
/// content), constructed via CorpusIo.ReadTsv exactly like the real
/// PickUpTarget -> Translation handoff. Two tests (exclusion list, phrase
/// override) depend on the REAL Data/corpus_exact_exclusions.txt and
/// Data/phrase_overrides.tsv shipped with the tool (AutoTranslator loads
/// these unconditionally, static, from a fixed path — there's no way to
/// substitute a test-only list) — each says so explicitly, so a future edit
/// to those curated files that breaks the test is diagnosable at a glance.
/// </summary>
public class AutoTranslatorTests
{
    private static AutoTranslator BuildFromFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "corpus_basic.tsv");
        var corpus = CorpusIo.ReadTsv(path);
        return new AutoTranslator(corpus);
    }

    [Fact]
    public void TryTranslate_VanillaExactMatch_ResolvesWithAutoCorpusMethod()
    {
        var result = BuildFromFixture().TryTranslate("Iron Sword", "WEAP FULL");

        Assert.NotNull(result);
        Assert.Equal("鉄の剣", result!.Japanese);
        Assert.Equal("AutoCorpus", result.Method);
    }

    [Theory]
    [InlineData("Ebony Sword", "黒檀の剣", "AutoCorpusDsd")]
    [InlineData("Glass Sword", "ガラスの剣", "AutoCorpusImported")]
    [InlineData("Steel Sword", "鋼の剣", "AutoCorpusReferenceTaiyaku")]
    public void TryTranslate_TagsMethodBySourceKind(string english, string expectedJapanese, string expectedMethod)
    {
        var result = BuildFromFixture().TryTranslate(english, "WEAP FULL");

        Assert.NotNull(result);
        Assert.Equal(expectedJapanese, result!.Japanese);
        Assert.Equal(expectedMethod, result.Method);
    }

    /// <summary>Fixture has "Iron Dagger" attested by BOTH vanilla (鉄の短剣)
    /// and dsd (鉄のダガー) — vanilla's SourceTier (0) beats dsd's (2), so its
    /// translation wins regardless of which row the corpus lists first. This
    /// is the exact mechanism that fixed the real "Hevnoraak"→"ヘブラノーク"
    /// mistranslation (v0.38.0): a community DSD patch must never silently
    /// outrank Bethesda's own shipped localization.</summary>
    [Fact]
    public void TryTranslate_PrefersHigherSourceTier_WhenSameEnglishKeyHasMultipleSources()
    {
        var result = BuildFromFixture().TryTranslate("Iron Dagger", "WEAP FULL");

        Assert.NotNull(result);
        Assert.Equal("鉄の短剣", result!.Japanese);
        Assert.Equal("AutoCorpus", result.Method);
    }

    /// <summary>The real v0.49.2 incident, reproduced directly: "Courage" is a
    /// genuine vanilla spell/magic-effect name ("挑発") that also happens to
    /// collide with a mod-added NPC's name in other load orders. ① must resolve
    /// it normally for its own attested context (MGEF FULL) but refuse to reuse
    /// it for an NPC_ FULL candidate this exact text has never been attested
    /// as, since a spell name is not evidence for what to call an NPC.</summary>
    [Fact]
    public void TryTranslate_NpcFullHomographGuard_RejectsUnattestedNpcReuse()
    {
        var translator = BuildFromFixture();

        var spellResult = translator.TryTranslate("Courage", "MGEF FULL");
        Assert.NotNull(spellResult);
        Assert.Equal("挑発", spellResult!.Japanese);

        var npcResult = translator.TryTranslate("Courage", "NPC_ FULL");
        Assert.Null(npcResult);
    }

    /// <summary>Depends on Data/corpus_exact_exclusions.txt actually containing
    /// "Fall" today (verified: line 14, as of this writing) — a human-curated
    /// exclusion for the "Fall"→"秋" (calendar season) vs. "The Fall of
    /// Winterhold" (downfall) homograph. If this test starts failing, check
    /// whether "Fall" was removed from that file before assuming a code
    /// regression.</summary>
    [Fact]
    public void TryTranslate_WordInRealExclusionList_ReturnsNullDespiteCorpusHit()
    {
        var result = BuildFromFixture().TryTranslate("Fall", "GMST DATA");

        Assert.Null(result);
    }

    /// <summary>Depends on Data/phrase_overrides.tsv actually containing
    /// "pts"→"pt" today (verified: line 27, as of this writing). Deliberately
    /// NOT added to corpus_basic.tsv — PhraseOverrides load unconditionally
    /// into _corpusExact regardless of what the corpus itself contains, so
    /// this proves the override applies even with zero corpus support. Also
    /// confirms it bypasses the NPC_ FULL homograph guard (SeenAsNpcName=true
    /// unconditionally for a human-curated override) — a curated correction
    /// should never be second-guessed the way an accidental corpus collision
    /// is.</summary>
    [Fact]
    public void TryTranslate_RealPhraseOverride_AlwaysWinsAndBypassesNpcGuard()
    {
        var translator = BuildFromFixture();

        var result = translator.TryTranslate("pts", "GMST DATA");
        Assert.NotNull(result);
        Assert.Equal("pt", result!.Japanese);
        Assert.Equal("AutoCorpusOverride", result.Method);

        var npcResult = translator.TryTranslate("pts", "NPC_ FULL");
        Assert.NotNull(npcResult);
        Assert.Equal("AutoCorpusOverride", npcResult!.Method);
    }

    /// <summary>The real v0.43.0 incident: a single-WORD imported/dsd entry
    /// whose Japanese contains a grammatical particle ("の") is very likely a
    /// community translator's context-aware rendering of a short internal
    /// EDID/name, not a literal word-for-word gloss ("Druid"→"ドルイドの指" was
    /// Vokrii's own name for a specific perk, "Druid's Finger" — not a
    /// translation of the bare word "Druid"). Such entries never make it into
    /// _corpusExact at all.</summary>
    [Fact]
    public void TryTranslate_SingleWordImportedEntryWithParticle_NeverEntersExactMatchTable()
    {
        var result = BuildFromFixture().TryTranslate("Druid", "MISC FULL");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryTranslate_EmptyOrWhitespace_ReturnsNull(string input)
    {
        var result = BuildFromFixture().TryTranslate(input);

        Assert.Null(result);
    }
}
