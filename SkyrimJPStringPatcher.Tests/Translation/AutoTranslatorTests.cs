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
/// override) exercise the REAL Data/corpus_exact_exclusions.txt and
/// Data/phrase_overrides.tsv shipped with the tool (AutoTranslator loads
/// these unconditionally, static, from a fixed path — there's no way to
/// substitute a test-only list) — but deliberately do NOT hardcode which
/// specific word/phrase those files currently contain. Each reads the file's
/// actual first usable entry at test time and builds its assertions around
/// THAT, so the test tracks the real curated data instead of a frozen
/// snapshot of it that a future edit could silently invalidate.
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

    /// <summary>Depends on Data/corpus_exact_exclusions.txt existing and
    /// containing at least one word — but NOT on which specific word that is.
    /// Rather than hardcode a word that could silently go stale if someone
    /// edits the curated file later, this reads the file's actual first entry
    /// at test time and builds a matching corpus hit for exactly that word, so
    /// the test tracks the real file's content instead of a frozen
    /// assumption about it.</summary>
    [Fact]
    public void TryTranslate_WordInRealExclusionList_ReturnsNullDespiteCorpusHit()
    {
        var excludedWord = ReadFirstNonEmptyLine(Path.Combine(AppContext.BaseDirectory, "Data", "corpus_exact_exclusions.txt"));
        var corpus = new List<CorpusEntry> { new(excludedWord, "テスト用の訳文", "test", "vanilla", "") };

        var result = new AutoTranslator(corpus).TryTranslate(excludedWord);

        Assert.Null(result);
    }

    /// <summary>Same real-data-but-not-a-specific-value approach as the
    /// exclusion-list test above: reads Data/phrase_overrides.tsv's actual
    /// first entry at test time, rather than hardcoding "pts"→"pt". Uses an
    /// EMPTY corpus (no fixture support at all) to prove PhraseOverrides load
    /// into _corpusExact unconditionally, and confirms it bypasses the NPC_
    /// FULL homograph guard (SeenAsNpcName=true unconditionally for a
    /// human-curated override) — a curated correction should never be
    /// second-guessed the way an accidental corpus collision is.</summary>
    [Fact]
    public void TryTranslate_RealPhraseOverride_AlwaysWinsAndBypassesNpcGuard()
    {
        var (english, japanese) = ReadFirstPhraseOverride(Path.Combine(AppContext.BaseDirectory, "Data", "phrase_overrides.tsv"));
        var translator = new AutoTranslator(Array.Empty<CorpusEntry>());

        var result = translator.TryTranslate(english);
        Assert.NotNull(result);
        Assert.Equal(japanese, result!.Japanese);
        Assert.Equal("AutoCorpusOverride", result.Method);

        var npcResult = translator.TryTranslate(english, "NPC_ FULL");
        Assert.NotNull(npcResult);
        Assert.Equal("AutoCorpusOverride", npcResult!.Method);
    }

    private static string ReadFirstNonEmptyLine(string path) =>
        File.ReadLines(path).Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#'));

    private static (string English, string Japanese) ReadFirstPhraseOverride(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1)) // header row
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var english = parts[0].Trim();
            var japanese = parts[1].Trim();
            if (english.Length > 0 && japanese.Length > 0) return (english, japanese);
        }
        throw new InvalidOperationException($"No usable override row found in {path}");
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

    /// <summary>_corpusExact's SourceTier merge (v0.38.0): when a WORSE-tier
    /// entry for a key is seen first and a BETTER-tier entry for the same key
    /// arrives later, the later (better) entry must REPLACE it — not just the
    /// "existing already wins" branch the "Iron Dagger" test above exercises
    /// (there, vanilla is seen FIRST, so the tier check never needs to
    /// actually replace anything). Fixtures/Translation/corpus_basic.tsv lists
    /// "Sjpts Tier Test Alpha" dsd-first, vanilla-second for exactly this.</summary>
    [Fact]
    public void TryTranslate_WorseTierSeenFirst_BetterTierSeenLater_ReplacesIt()
    {
        var result = BuildFromFixture().TryTranslate("Sjpts Tier Test Alpha", "WEAP FULL");

        Assert.NotNull(result);
        Assert.Equal("鍵A(vanilla)", result!.Japanese);
        Assert.Equal("AutoCorpus", result.Method);
    }

    /// <summary>SeenAsNpcName is tracked INDEPENDENTLY of which entry's text
    /// wins the SourceTier competition (v0.49.2's Courage guard depends on
    /// this): "Sjpts Tier Test Beta" is attested first by a non-NPC vanilla
    /// entry (which wins the text) and second by a WORSE-tier dsd entry whose
    /// DsdType starts with "NPC_" — the vanilla text keeps winning, but the
    /// NPC_ homograph guard must still let an NPC_ FULL candidate through,
    /// proving the flag itself got updated on the already-winning entry.</summary>
    [Fact]
    public void TryTranslate_NpcSourcedEntryLosesTheTextButStillFlipsTheSeenAsNpcFlag()
    {
        var translator = BuildFromFixture();

        var ordinaryResult = translator.TryTranslate("Sjpts Tier Test Beta", "WEAP FULL");
        Assert.NotNull(ordinaryResult);
        Assert.Equal("鍵B", ordinaryResult!.Japanese); // vanilla's text still wins

        var npcResult = translator.TryTranslate("Sjpts Tier Test Beta", "NPC_ FULL");
        Assert.NotNull(npcResult); // guard does NOT reject — flag was updated despite losing the tier competition
        Assert.Equal("鍵B", npcResult!.Japanese);
    }

    /// <summary>④意味合成 (CorpusMeaningTranslator) reached THROUGH
    /// AutoTranslator.TryTranslate itself — CorpusMeaningTranslatorTests
    /// covers the class directly, but AutoTranslator's own wiring into it
    /// (the method tag, the record-type gate) was never exercised. Reuses
    /// that class's own fixture (Amber/Steel/Iron × Sword/Battleaxe, Gold/
    /// Silver/Bronze × Boots) rather than duplicating it.</summary>
    [Fact]
    public void TryTranslate_MeaningComposition_ReachedThroughAutoTranslatorItself()
    {
        var corpus = CorpusIo.ReadTsv(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "CorpusMeaningTranslator", "corpus.tsv"));
        var translator = new AutoTranslator(corpus);

        var result = translator.TryTranslate("Amber Boots", "ARMO FULL");

        Assert.NotNull(result);
        Assert.Equal("AutoCorpusMeaning", result!.Method);
        Assert.NotEmpty(result.Detail);
    }

    /// <summary>③音訳分解 (CorpusTransliterator.TryDecompose) reached THROUGH
    /// AutoTranslator for a single unspaced word ("Frostfall", never itself in
    /// the corpus) composed from two known transliterated pieces. Reuses
    /// CorpusTransliteratorTests' own fixture ("Frost"→"フロスト",
    /// "Fall"→"フォール").</summary>
    [Fact]
    public void TryTranslate_SingleWordTransliterationDecomposition_ReachedThroughAutoTranslatorItself()
    {
        var corpus = CorpusIo.ReadTsv(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "CorpusTransliterator", "corpus.tsv"));
        var translator = new AutoTranslator(corpus);

        var result = translator.TryTranslate("Frostfall", "WEAP FULL");

        Assert.NotNull(result);
        Assert.Equal("フロストフォール", result!.Japanese);
        Assert.Equal("AutoCorpusTransliterate", result.Method);
    }

    /// <summary>The OTHER ③ path: a 2-3 word Title Case phrase with no
    /// corpus precedent as a whole ("proper noun phrase" heuristic), resolved
    /// by transliterating each word independently via corpus precedent and
    /// joining with "・" — deliberately distinct formatting from the
    /// single-word decomposition above (no separator) to keep the two kinds
    /// of evidence visually distinguishable.</summary>
    [Fact]
    public void TryTranslate_MultiWordProperNounPhrase_JoinsPerWordTransliterationsWithMiddleDot()
    {
        var corpus = CorpusIo.ReadTsv(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "CorpusTransliterator", "corpus.tsv"));
        var translator = new AutoTranslator(corpus);

        var result = translator.TryTranslate("Frost Fall", "WEAP FULL");

        Assert.NotNull(result);
        Assert.Equal("フロスト・フォール", result!.Japanese);
        Assert.Equal("AutoCorpusTransliterate", result.Method);
    }
}
