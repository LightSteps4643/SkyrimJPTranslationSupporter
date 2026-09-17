using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// 2026-09-17: ModPhraseGlossary — the ⑤/⑥-facing sibling of ModGlossary
/// (Data/mod_glossary/*.tsv, ④-only). Lives alongside translations.tsv in
/// each plugin's own Translation output folder (mod_glossary.tsv), detects
/// phrases (1-5 words) that recur within one mod's own candidates far more
/// than across the whole load order (TF-IDF-style: local occurrence count ×
/// rarity elsewhere) and are not already resolved via ①②③, and — once a
/// person fills in the Japanese column — feeds those filled rows into issue
/// #4's "c" (same-mod hint) pool at SJPTS_ModifiedByUser trust tier.
///
/// Real-data validation (Light Greatswords.esp/Cosplay Pack Gala.esp/
/// Nodachi.esp/Soul Hunter Armor.esp, recorded in the management repo's
/// todo/active.md) found this exact two-stage design (TF-IDF ranking, then a
/// whole-phrase — not per-word — ①②③ resolution check on the top-ranked
/// candidates) correctly surfaces the genuinely mod-specific term in all 4
/// real mods while correctly excluding an already-vanilla-resolved phrase
/// ("Soul Cairn") that merely happened to recur often.
/// </summary>
public class ModPhraseGlossaryTests
{
    private static AutoTranslator BuildAutoTranslator(params CorpusEntry[] corpus) =>
        new(corpus.ToList(), null, enableMeaning: false, enableTransliteration: false, enableNameFallback: false);

    [Fact]
    public void DetectCandidatePhrases_LocallyFrequentButGloballyRare_IsDetected()
    {
        // "Light Greatsword" appears 3x within this one mod's own candidates and
        // nowhere else in the load order (allTexts here IS pluginTexts) — exactly
        // the real Light Greatswords.esp pattern.
        var pluginTexts = new List<string>
        {
            "Daedric Light Greatsword", "Dwarven Light Greatsword", "Ebony Light Greatsword", "Warden",
        };
        var auto = BuildAutoTranslator(); // empty corpus: nothing resolves via (1)(2)(3)

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto);

        Assert.Contains(detected, d => d.Phrase == "Light Greatsword");
        Assert.Equal(3, detected.Single(d => d.Phrase == "Light Greatsword").Count);
        Assert.True(detected.Single(d => d.Phrase == "Light Greatsword").Score > 0);
    }

    [Fact]
    public void DetectCandidatePhrases_FrequentEverywhereInLoadOrder_IsNotDetected()
    {
        // "of the" recurs locally too, but it is EQUALLY common across the whole
        // load order (globalDocFreq keeps pace with local) — low IDF, must not
        // be surfaced as if it were mod-specific.
        var pluginTexts = new List<string> { "Blade of the Ancients", "Shield of the Ancients", "Ring of the Ancients" };
        var allTexts = new List<string>();
        for (var i = 0; i < 50; i++) allTexts.Add($"Something of the Thing{i}");
        allTexts.AddRange(pluginTexts);
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, allTexts, auto);

        Assert.DoesNotContain(detected, d => d.Phrase == "of the");
    }

    [Fact]
    public void DetectCandidatePhrases_AlreadyResolvedViaCorpus_IsExcluded()
    {
        // "Soul Cairn"-style case: locally frequent AND globally rare (so it
        // would rank highly on TF-IDF alone), but already resolved as a WHOLE
        // phrase via the corpus — must be excluded even though the ranking
        // alone can't tell it apart from a genuine gap.
        var pluginTexts = new List<string> { "Conjure Soul Cairn", "Soul Cairn Guardian", "Escape the Soul Cairn" };
        var auto = BuildAutoTranslator(new CorpusEntry("Soul Cairn", "ソウル・ケルン", "Skyrim.esm", "vanilla", ""));

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto);

        Assert.DoesNotContain(detected, d => d.Phrase == "Soul Cairn");
    }

    [Fact]
    public void DetectCandidatePhrases_WordsIndividuallyResolvedButCombinationIsNot_StillDetected()
    {
        // The exact trap a per-word decomposition check would fall into: "Light"
        // and "Greatsword" both resolve fine alone, but the COMBINATION doesn't
        // — checking the phrase as one whole unit (not decomposed) is required
        // to still catch this.
        var pluginTexts = new List<string> { "Light Greatsword", "Light Greatsword", "Warden" };
        var auto = BuildAutoTranslator(
            new CorpusEntry("Light", "発光", "Skyrim.esm", "vanilla", ""),
            new CorpusEntry("Greatsword", "グレートソード", "Skyrim.esm", "vanilla", ""));

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto);

        Assert.Contains(detected, d => d.Phrase == "Light Greatsword");
    }

    /// <summary>2026-09-17 real-data finding (Light Greatswords.esp): simple
    /// function words ("the", "A", "to", "by", "of") recur across a mod's own
    /// candidates just like any content word, so without a filter they show up
    /// in the ranking as if they were meaningful. A dedicated stopword list
    /// (deliberately NOT a reuse of <see cref="CorpusSimilarityIndex.StopWords"/>
    /// — see <see cref="ModPhraseGlossary"/>'s own remarks for why) drops any
    /// n-gram made entirely of such words.</summary>
    [Fact]
    public void DetectCandidatePhrases_PureFunctionWordNgram_IsExcluded()
    {
        // Each candidate anchors a DIFFERENT distinctive 2-gram ("Windrune
        // Blade" / "Frostbound Shield" / "Ember Ring") so no single longer
        // n-gram's containment check can accidentally mask "the"/"a"/"of" by
        // coincidence — the only thing that can exclude them is the stopword
        // filter itself.
        var pluginTexts = new List<string>
        {
            "A Windrune Blade of the Depths", "A Frostbound Shield of the Depths", "A Ember Ring of the Depths",
        };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase == "the");
        Assert.DoesNotContain(detected, d => d.Phrase == "A");
        Assert.DoesNotContain(detected, d => d.Phrase == "of");
        Assert.DoesNotContain(detected, d => d.Phrase == "a");
    }

    /// <summary>2026-09-17 real-data finding: a function word capitalized only
    /// because it happens to sit at the start of an item name ("The", "And")
    /// still needs to be excluded — the stopword check must be
    /// case-INsensitive, not rely on capitalization alone to spot noise.</summary>
    [Fact]
    public void DetectCandidatePhrases_CapitalizedFunctionWordAtStart_IsExcluded()
    {
        var pluginTexts = new List<string>
        {
            "The Windrune Blade", "The Frostbound Shield", "The Ember Ring",
        };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase == "The");
    }

    /// <summary>2026-09-17 real-data finding: an ordinary lowercase descriptive
    /// word from flavor text ("wielded") is not a stopword, but it is also not
    /// the kind of proper-noun-like term this glossary exists to surface — the
    /// capitalization requirement (not the stopword list) is what excludes it.</summary>
    [Fact]
    public void DetectCandidatePhrases_LowercaseDescriptiveWord_IsExcludedByCapitalizationRule()
    {
        var pluginTexts = new List<string>
        {
            "A blade wielded by knights", "A shield wielded by knights", "A ring wielded by knights",
        };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase == "wielded");
    }

    [Fact]
    public void DetectCandidatePhrases_BelowMinimumLocalOccurrences_IsIgnored()
    {
        var pluginTexts = new List<string> { "Unique Blade Of Woe", "Warden" }; // each n-gram appears only once
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto);

        Assert.Empty(detected);
    }

    /// <summary>2026-09-17 real-data finding (Twilight Princess Armor.esp): an
    /// ordinal like "45th" must stay ONE word. The old letters-only regex
    /// treated the digits as an invisible separator, leaving the bare suffix
    /// "th" behind as if it were its own word (e.g. "Book - Alteration, 45th
    /// Edition" produced the nonsense n-gram "Book Alteration th"). Only a
    /// word made ENTIRELY of digits should be dropped — one that merely
    /// contains digits alongside letters must survive intact.</summary>
    [Fact]
    public void DetectCandidatePhrases_OrdinalNumberSuffix_StaysIntactAsOneWord()
    {
        var pluginTexts = new List<string>
        {
            "Ancient Blade 45th Edition", "Ancient Blade 45th Edition", "Ancient Blade 45th Edition",
        };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase.Split(' ').Contains("th"));
        Assert.Contains(detected, d => d.Phrase.Contains("45th"));
    }

    /// <summary>A word made entirely of digits ("100") carries no translatable
    /// meaning of its own and must not appear as a word in any detected
    /// phrase — distinct from the ordinal case above, where digits are only
    /// PART of an otherwise-meaningful word.</summary>
    [Fact]
    public void DetectCandidatePhrases_PureNumericWord_IsExcludedFromPhrases()
    {
        var pluginTexts = new List<string> { "Level 100 Warhammer", "Level 100 Warhammer", "Level 100 Warhammer" };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase.Split(' ').Any(w => w.Length > 0 && w.All(char.IsDigit)));
    }

    /// <summary>2026-09-17 real-data finding (MysticismMagic.esp/Book Covers
    /// Skyrim.esp): book/note candidate text embeds Skyrim's own book-markup
    /// tags verbatim (e.g. <c>&lt;font face='$HandwrittenFont'&gt;&lt;p
    /// align='center'&gt;</c>) — these are processing-only, never actually
    /// shown to the player, and recur across every book/tome in a mod just
    /// like a real template, so without stripping them first they dominate
    /// the frequency ranking (observed: score 1403, ranked #1, ahead of the
    /// genuine "Spell Tome" candidate). Must be removed before n-gram
    /// extraction, not merely down-ranked.</summary>
    [Fact]
    public void DetectCandidatePhrases_HtmlLikeMarkupTag_IsStrippedBeforeExtraction()
    {
        var pluginTexts = new List<string>
        {
            "<font face='$HandwrittenFont'><font size='40'><p align='center'>\nSpell Tome Alpha",
            "<font face='$HandwrittenFont'><font size='40'><p align='center'>\nSpell Tome Beta",
            "<font face='$HandwrittenFont'><font size='40'><p align='center'>\nSpell Tome Gamma",
        };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase.Contains("font", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(detected, d => d.Phrase.Contains("align", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detected, d => d.Phrase == "Spell Tome");
    }

    /// <summary>Same real-data finding, the other recurring processing-only
    /// marker found in book text (Book Covers Skyrim.esp): the literal
    /// <c>[pagebreak]</c> token that splits book pages internally.</summary>
    [Fact]
    public void DetectCandidatePhrases_PagebreakMarker_IsStrippedBeforeExtraction()
    {
        var pluginTexts = new List<string>
        {
            "[pagebreak]\nAncient Tome Alpha", "[pagebreak]\nAncient Tome Beta", "[pagebreak]\nAncient Tome Gamma",
        };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase.Contains("pagebreak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detected, d => d.Phrase == "Ancient Tome");
    }

    /// <summary>Same finding: a literal escaped-newline sequence (two
    /// characters, backslash+n — not an actual line break) also shows up
    /// verbatim in book text and must not survive as part of a word.</summary>
    [Fact]
    public void DetectCandidatePhrases_EscapedNewlineSequence_IsStrippedBeforeExtraction()
    {
        var pluginTexts = new List<string>
        {
            "Ancient Tome\\nAlpha", "Ancient Tome\\nBeta", "Ancient Tome\\nGamma",
        };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.Contains(detected, d => d.Phrase == "Ancient Tome");
    }

    /// <summary>A real MOD-authored bracket tag ("[E]", used by the ELLE armor
    /// series as a variant marker prefixed to every item name) must NOT be
    /// stripped by the markup-removal added for [pagebreak] — only the
    /// specific known processing-only tokens are targeted, not every bracketed
    /// string.</summary>
    [Fact]
    public void DetectCandidatePhrases_RealBracketTagPrefix_IsNotStripped()
    {
        var pluginTexts = new List<string> { "[E] Chaos Boots", "[E] Chaos Gloves", "[E] Chaos Collar" };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.Contains(detected, d => d.Phrase.Contains("Chaos"));
    }

    /// <summary>2026-09-17 real-data finding (Book Covers Skyrim.esp):
    /// long-form book prose is full of sentences starting with "If", so
    /// without it in the exclusion word list it surfaces as high-scoring
    /// single-word noise (observed: 77 occurrences, score 453.9, ranked
    /// #7) purely because it happens to be capitalized at each sentence
    /// start.</summary>
    [Fact]
    public void DetectCandidatePhrases_IfWord_IsExcludedAsScoringExclusionWord()
    {
        var pluginTexts = new List<string> { "If found, return it.", "If lost, report it.", "If broken, replace it." };
        var auto = BuildAutoTranslator();

        var detected = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, pluginTexts, auto, maxResults: 20);

        Assert.DoesNotContain(detected, d => d.Phrase == "If");
    }

    /// <summary>2026-09-17 real-data finding: `--all`（183プラグイン）実行で
    /// グローバルなn-gram文書頻度表を毎回ゼロから構築していたため、実測で
    /// 約6倍（10秒→62秒）に遅くなっていた。ロードオーダー全体からの構築は
    /// 実行につき1回で済むはずなので、<see cref="ModPhraseGlossary.GlobalNgramFrequency"/>
    /// として事前計算・使い回せるようにする。この事前計算版オーバーロードは、
    /// 都度計算する既存オーバーロードとまったく同じ結果を返す必要がある。</summary>
    [Fact]
    public void DetectCandidatePhrases_WithPrecomputedGlobalFrequency_MatchesPlainOverload()
    {
        var pluginTexts = new List<string> { "Daedric Light Greatsword", "Dwarven Light Greatsword", "Ebony Light Greatsword", "Warden" };
        var allTexts = pluginTexts;
        var auto = BuildAutoTranslator();

        var viaPlainOverload = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, allTexts, auto);
        var globalFrequency = ModPhraseGlossary.GlobalNgramFrequency.Build(allTexts);
        var viaPrecomputed = ModPhraseGlossary.DetectCandidatePhrases(pluginTexts, globalFrequency, auto);

        Assert.Equal(viaPlainOverload, viaPrecomputed);
    }

    /// <summary>The whole point of precomputing: the SAME <see
    /// cref="ModPhraseGlossary.GlobalNgramFrequency"/> instance must be usable
    /// across several different plugins' own candidate sets (simulating
    /// several plugins processed in one run) without rebuilding it, and still
    /// give each plugin its own correct, independent result.</summary>
    [Fact]
    public void GlobalNgramFrequency_ReusedAcrossMultiplePlugins_EachGetsCorrectIndependentResult()
    {
        var pluginATexts = new List<string> { "Daedric Light Greatsword", "Dwarven Light Greatsword", "Ebony Light Greatsword" };
        var pluginBTexts = new List<string> { "Ancient Blade 45th Edition", "Ancient Blade 45th Edition", "Ancient Blade 45th Edition" };
        var allTexts = pluginATexts.Concat(pluginBTexts).ToList();
        var auto = BuildAutoTranslator();

        var globalFrequency = ModPhraseGlossary.GlobalNgramFrequency.Build(allTexts);
        var detectedA = ModPhraseGlossary.DetectCandidatePhrases(pluginATexts, globalFrequency, auto, maxResults: 20);
        var detectedB = ModPhraseGlossary.DetectCandidatePhrases(pluginBTexts, globalFrequency, auto, maxResults: 20);

        Assert.Contains(detectedA, d => d.Phrase == "Light Greatsword");
        Assert.DoesNotContain(detectedA, d => d.Phrase.Contains("45th"));
        Assert.Contains(detectedB, d => d.Phrase.Contains("45th"));
        Assert.DoesNotContain(detectedB, d => d.Phrase == "Light Greatsword");
    }

    /// <summary>2026-09-18 real-data crash (Interface翻訳「VioLens - A Killmove
    /// Mod SE」): 検出結果に大文字小文字違いだけの語句（"Hotkey"と"HotKey"）が
    /// 両方含まれると、内部の「既に検出済みか」判定用の`ToDictionary`
    /// （<see cref="StringComparer.OrdinalIgnoreCase"/>）が重複キー例外を投げ、
    /// detect自体が丸ごとクラッシュしていた（GUIが強制終了）。
    ///
    /// "Hotkey"と"HotKey"が本当に同じ語句かどうかは判断できない（例:
    /// "OriginalModWords"と"Originalmodwords"のように、大文字小文字が違う
    /// だけでも別物である可能性を否定できない）ため、どちらかを統合・破棄する
    /// のではなく、両方を別々の行として書き出せるようにする——最終的な判断は
    /// 人間（GUIでこのファイルを見るユーザー）に委ねる。</summary>
    [Fact]
    public void WriteTemplate_PhrasesDifferingOnlyByCase_DoesNotThrow_BothWrittenAsSeparateRows()
    {
        var pluginDir = Path.Combine(Path.GetTempPath(), $"sjpts_modphrase_{Guid.NewGuid():N}");
        try
        {
            ModPhraseGlossary.WriteTemplate(pluginDir, "SjptsCaseVariantTestMod",
                [new ModPhraseGlossary.DetectedPhrase("Hotkey", 2, 3.0), new ModPhraseGlossary.DetectedPhrase("HotKey", 2, 3.0)]);

            var lines = File.ReadAllLines(ModPhraseGlossary.PathFor(pluginDir)).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.Contains(lines, l => l.StartsWith("Hotkey\t"));
            Assert.Contains(lines, l => l.StartsWith("HotKey\t"));
        }
        finally
        {
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void WriteTemplate_NewFile_ListsDetectedPhrasesWithCountAndScore()
    {
        var pluginDir = Path.Combine(Path.GetTempPath(), $"sjpts_modphrase_{Guid.NewGuid():N}");
        try
        {
            ModPhraseGlossary.WriteTemplate(pluginDir, "SjptsPhraseTestMod.esp",
                [new ModPhraseGlossary.DetectedPhrase("Light Greatsword", 15, 42.3), new ModPhraseGlossary.DetectedPhrase("Cosplay Gala", 4, 9.1)]);

            var lines = File.ReadAllLines(ModPhraseGlossary.PathFor(pluginDir)).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.Contains("Light Greatsword\t\t15\t42.3", lines);
            Assert.Contains("Cosplay Gala\t\t4\t9.1", lines);

            var filled = ModPhraseGlossary.LoadFilled(pluginDir);
            Assert.Empty(filled); // nothing filled in yet
        }
        finally
        {
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Score-descending ordering is part of the contract (per user
    /// request 2026-09-17): the higher-scoring phrase must appear first.</summary>
    [Fact]
    public void WriteTemplate_NewFile_OrdersByScoreDescending()
    {
        var pluginDir = Path.Combine(Path.GetTempPath(), $"sjpts_modphrase_{Guid.NewGuid():N}");
        try
        {
            ModPhraseGlossary.WriteTemplate(pluginDir, "SjptsPhraseTestMod.esp",
                [new ModPhraseGlossary.DetectedPhrase("Low Score", 2, 5.0), new ModPhraseGlossary.DetectedPhrase("High Score", 10, 30.0)]);

            var lines = File.ReadAllLines(ModPhraseGlossary.PathFor(pluginDir)).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.True(lines.FindIndex(l => l.StartsWith("High Score\t")) < lines.FindIndex(l => l.StartsWith("Low Score\t")));
        }
        finally
        {
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The core "never destroys human work" contract, mirrored from
    /// ModGlossary: a filled-in row survives regeneration verbatim, while
    /// Count/Score are refreshed to the latest detection each time.</summary>
    [Fact]
    public void WriteTemplate_Regenerated_PreservesFilledJapanese_RefreshesCountAndScore()
    {
        var pluginDir = Path.Combine(Path.GetTempPath(), $"sjpts_modphrase_{Guid.NewGuid():N}");
        try
        {
            ModPhraseGlossary.WriteTemplate(pluginDir, "SjptsPhraseTestMod.esp",
                [new ModPhraseGlossary.DetectedPhrase("Light Greatsword", 3, 10.0)]);

            // A person fills it in by hand.
            var path = ModPhraseGlossary.PathFor(pluginDir);
            var content = File.ReadAllText(path).Replace("Light Greatsword\t\t3\t10", "Light Greatsword\t軽大剣\t3\t10");
            File.WriteAllText(path, content);

            // Regenerate: same phrase now with a higher count/score, plus a newly-detected one.
            ModPhraseGlossary.WriteTemplate(pluginDir, "SjptsPhraseTestMod.esp",
                [new ModPhraseGlossary.DetectedPhrase("Light Greatsword", 6, 22.5), new ModPhraseGlossary.DetectedPhrase("New Phrase", 2, 5.0)]);

            var filled = ModPhraseGlossary.LoadFilled(pluginDir);
            Assert.Equal("軽大剣", filled["Light Greatsword"]); // Japanese preserved verbatim

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.Contains("Light Greatsword\t軽大剣\t6\t22.5", lines); // Count/Score refreshed
            Assert.Contains("New Phrase\t\t2\t5", lines);
        }
        finally
        {
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Mirrors ModGlossary's retirement contract: a phrase no longer
    /// detected is NOT dropped (that would silently discard a person's
    /// translation work) — it is retained with Count/Score zeroed out.</summary>
    [Fact]
    public void WriteTemplate_PhraseNoLongerDetected_RetainsFilledEntryWithZeroedCountAndScore()
    {
        var pluginDir = Path.Combine(Path.GetTempPath(), $"sjpts_modphrase_{Guid.NewGuid():N}");
        try
        {
            ModPhraseGlossary.WriteTemplate(pluginDir, "SjptsPhraseTestMod.esp",
                [new ModPhraseGlossary.DetectedPhrase("Light Greatsword", 3, 10.0)]);

            var path = ModPhraseGlossary.PathFor(pluginDir);
            var content = File.ReadAllText(path).Replace("Light Greatsword\t\t3\t10", "Light Greatsword\t軽大剣\t3\t10");
            File.WriteAllText(path, content);

            // Regenerate: "Light Greatsword" no longer detected at all.
            ModPhraseGlossary.WriteTemplate(pluginDir, "SjptsPhraseTestMod.esp", []);

            var filled = ModPhraseGlossary.LoadFilled(pluginDir);
            Assert.Equal("軽大剣", filled["Light Greatsword"]); // still preserved

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.Contains("Light Greatsword\t軽大剣\t0\t0", lines);
        }
        finally
        {
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void LoadFilled_NoFile_ReturnsEmpty()
    {
        var pluginDir = Path.Combine(Path.GetTempPath(), $"sjpts_modphrase_nofile_{Guid.NewGuid():N}");
        Assert.Empty(ModPhraseGlossary.LoadFilled(pluginDir));
    }
}
