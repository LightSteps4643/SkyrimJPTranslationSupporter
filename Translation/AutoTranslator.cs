using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <param name="Detail">v0.36.0: human-readable breakdown of HOW this was resolved
/// (which pieces, from which table) — populated only for step 2 (意味合成) and
/// step 3 (音訳分解), where the result is a COMPOSITION and "why" isn't obvious
/// from the whole-string result alone. Left "" for step 1 (コーパス完全一致,
/// verbatim precedent needs no explaining) — see PromptGenerator's per-plugin
/// detail log, DESIGN_HISTORY.md's v0.36.0 section for why this exists.</param>
public sealed record AutoTranslationResult(string Japanese, string Method, string Detail = "");

/// <summary>
/// Automatic, no-AI-chat-needed translation pipeline for Translation — steps ①③④ from
/// DESIGN_NOTES.md's "A・C向けの段階的フォールバック構成". Applied before generating
/// any AI-chat prompt, so only genuinely hard-to-resolve candidates (long/idiomatic
/// text) end up costing a round trip through a chat model.
///
/// ① コーパス完全一致 — このロードオーダー内で既に確立している訳語をそのまま再利用（最も信頼できる）
/// ③ コーパス由来の音訳辞書による単語構成 — 実際にゲーム内で使われた音訳（例: "Whiterun"→
///    "ホワイトラン"）を、このロードオーダーのコーパスから単語単位で抽出し、未知の候補語を
///    その既知の断片の組み合わせとして解決できる場合に採用する（<see cref="CorpusTransliterator"/>）
/// ④ コーパス由来の意味合成 — <see cref="CorpusMeaningTranslator"/>
///
/// v0.29.5: 「② 辞書一致（JMdict由来のAutoDict）」は撤去した。単一英単語のみ・曖昧性のない
/// 語のみという条件で絞ってあっても、"Ward"→"区"（行政区、防御呪文の意味ではない）、
/// "Batter"→"打者"（野球、Perk名の文脈ではない）、"Constitution"→"憲法"（法律、能力値の
/// 意味ではない）のような同綴異義語の誤訳が実データで見つかり、しかも自動確定の対象
/// （＝人がレビューせず訳文として確定する経路）に乗っていた。解決できる件数
/// （全体の0.7%程度）に対してこの誤訳リスクは見合わないと判断し、ユーザーと相談のうえ撤去。
/// 撤去した分は未解決のままAIチャット向けプロンプトの対象に戻る（サイレントな誤訳より安全）。
///
/// ①③④で解決できない候補は null を返し、Translationの従来どおりAIチャット向けプロンプトの
/// 対象として残る。当初、自前の発音ルールベース音訳エンジン（<see cref="Transliterator"/>）も
/// この自動確定パイプラインに含める予定だったが、実データで検証した結果（"Dead Passenger"→
/// "デアド・パッセングエル" 等）品質が不十分で、無条件に自動確定するとゲーム内の訳文の質を
/// 落とす懸念があった。そちらは自動確定せず、AIチャット向けプロンプト内の「参考（未確定・
/// 要判断）」ヒントとしてのみ提示する（<see cref="SuggestTransliteration"/>）。一方、③の
/// コーパス由来辞書は実在する正しい precedent の組み合わせでしかないため、自動確定して良い
/// という判断をしている。
///
/// v0.33.0: the "fedback" trust tier (this tool's own earlier, unverified output,
/// round-tripped back in via the now-removed CompletedTranslations) is gone —
/// see DESIGN_HISTORY.md's v0.33.0 section for why the reflux mechanism it
/// depended on was retired. Every corpus entry reaching this class now traces to
/// either Bethesda/DSD data, an xTranslator import, or the reference glossary —
/// all genuinely external evidence, so there is no longer a "this tool's own
/// earlier guess" tier to label separately.
/// </summary>
public sealed class AutoTranslator
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "in", "on", "at", "is", "are", "was", "were", "this", "that",
        "and", "or", "for", "with", "from", "to", "get", "what", "you", "your", "it", "its",
        "do", "does", "did", "not", "no", "yes", "if", "but", "so", "as", "by", "be", "been",
        "has", "have", "had",
    };

    /// <summary>Japanese plus the precedent's <see cref="SourceKind"/> — one of
    /// "vanilla" (this load order's own scanned EN/JA record pairs), "dsd" (an
    /// existing DSD translation patch), "imported" (an xTranslator import), or
    /// "reference" (Data/skyrim_taiyaku_reference.tsv). v0.38.0: on a genuine
    /// conflict (the same English key attested by more than one SourceKind), the
    /// entry with the best <see cref="SourceTier"/> wins, not merely whichever
    /// happened to be seen first in corpus list order — corrected after "dsd"
    /// was found silently lumped in with "vanilla" as an undifferentiated
    /// "official" tier (see DESIGN_HISTORY.md's v0.38.0 section), which let a
    /// community DSD patch's translation out-rank the tool's own scanned data.</summary>
    /// <summary>v0.49.2: <c>SeenAsNpcName</c> — has ANY corpus entry for this exact
    /// key ever carried an "NPC_" DsdType (regardless of which entry ultimately
    /// won the SourceTier competition above)? Independent of the winning
    /// (Japanese, SourceKind, Source), since the winner might come from a
    /// completely different, non-NPC context — see <see cref="TryTranslate"/>'s
    /// use of this flag for why that distinction matters (the "Courage" homograph:
    /// a vanilla spell/enchantment/magic-effect name that also collides with a
    /// mod-added dog's NPC_ FULL name).</summary>
    private readonly Dictionary<string, (string Japanese, string SourceKind, string Source, bool SeenAsNpcName)> _corpusExact;
    private readonly CorpusTransliterator _corpusTransliterator;
    private readonly CorpusMeaningTranslator _corpusMeaning;

    /// <summary>v0.49.1: per-stage opt-out for whole-candidate auto-resolution
    /// (②意味合成/③音訳分解). Deliberately does NOT gate whether the underlying
    /// tables get BUILT — <see cref="MeaningTable"/>/<see cref="TransliterationTable"/>
    /// stay available even when their whole-candidate step is off, since
    /// NameFallbackTranslator's own per-WORD chain (④) and the prompt.txt word
    /// hints both consult them independently of whether ② or ③ is allowed to
    /// settle a candidate outright. ①完全一致 has no such flag — see
    /// DESIGN_NOTES.md for why it's always on.</summary>
    private readonly bool _enableMeaning;
    private readonly bool _enableTransliteration;

    public CorpusMeaningTranslator MeaningTable => _corpusMeaning;
    public CorpusTransliterator TransliterationTable => _corpusTransliterator;

    /// <summary>
    /// v0.29.11: direct access to the ① exact-match table, exposed for
    /// <see cref="NameFallbackTranslator"/>'s per-WORD resolution — not just
    /// whole-candidate lookup. A word like "Alteration" is a clean single-word
    /// corpus/reference entry ("Alteration"→"変性") that this exact table
    /// already carries, but neither <see cref="CorpusMeaningTranslator"/> nor
    /// <see cref="CorpusTransliterator"/> can reach it: both mine vocabulary
    /// out of MULTI-word compounds, so a standalone single-word precedent with
    /// no compound to be mined from was simply invisible to a name that only
    /// contains it as ONE token among several ("Twilight Princess Book -
    /// Alteration, 45th Edition"). This method is that missing single-word
    /// lookup, reusing the same trust-tier data <see cref="TryTranslate"/>'s
    /// own ① step already trusts.
    /// </summary>
    /// <param name="recordType">v0.49.2: the WHOLE candidate's DSD type (not this
    /// individual word's), used for the same NPC_FULL homograph guard
    /// <see cref="TryTranslate"/> applies at ① — see that method's remarks for
    /// the "Courage" case this exists to catch. Pass "" (the default) to skip the
    /// guard, e.g. for callers that already know the word-level context is safe
    /// (there are none today; kept as an explicit opt-out rather than always
    /// required, since a caller resolving a WORD may not always have a
    /// meaningful whole-candidate type to hand).</param>
    public bool TryExactWord(string word, out string japanese, out string source, string recordType = "")
    {
        if (_corpusExact.TryGetValue(word.Trim(), out var hit) && LanguageDetector.ContainsJapanese(hit.Japanese)
            && !(recordType == "NPC_ FULL" && !hit.SeenAsNpcName))
        {
            japanese = hit.Japanese;
            source = $"{hit.SourceKind}:{hit.Source}";
            return true;
        }
        japanese = "";
        source = "";
        return false;
    }

    /// <summary>v0.41.0: manual, human-curated exclusion list
    /// (<c>Data/corpus_exact_exclusions.txt</c>) for the ①完全一致 dictionary —
    /// same idea as <see cref="CorpusTransliterator"/>'s own manual exclusions,
    /// but for THIS class's exact-string lookup instead. Exists for a word whose
    /// corpus precedent is a genuine, correct translation in ONE sense but a
    /// homograph in another that ①完全一致 has no way to tell apart — found via
    /// real data: "Fall"→"秋" (a GMST calendar-season string) reused for "The
    /// Fall of Winterhold" (the "downfall" sense), and "Hide"→"身隠し" (a
    /// Creation Club stealth spell) reused for "Stormcloak Hide Gauntlets" (the
    /// "leather" sense). No general fix exists — the only sound repair after the
    /// fact is a human excluding the specific word, exactly as
    /// transliteration_exclusions.txt already does for ③.</summary>
    private static readonly HashSet<string> ExactMatchExclusions = LoadExactMatchExclusions();

    private static HashSet<string> LoadExactMatchExclusions() => LoadWordList("corpus_exact_exclusions.txt");

    /// <summary>v0.44.2: manual exclusion list for ②意味合成's OWN head/modifier
    /// mining (<c>Data/meaning_mining_exclusions.txt</c>) — deliberately a
    /// SEPARATE list from <see cref="ExactMatchExclusions"/>, not a shared one.
    /// A word can need one exclusion without the other: "Armor"/"Light" are in
    /// ExactMatchExclusions because ①完全一致's single vanilla entry is wrong,
    /// but ②意味合成 independently mines the CORRECT meaning from other
    /// evidence ("Harbinger Armor"→"導き手の鎧") — excluding them here too
    /// would silently break that correct resolution. This list is only for a
    /// word where ②意味合成's OWN mining is what's wrong, found via real data:
    /// "Farm" learned "会話" (from 3 "Dialogue X Farm" internal-topic rows that
    /// happened to carry a "の" seam) instead of "農場"/"農園" (60+ correct
    /// rows); "Locks" learned "鍵師" (locksmith, a person) from lockpicking
    /// skill-tier names ("Novice/Master/Expert Locks").</summary>
    private static readonly HashSet<string> MeaningMiningExclusions = LoadWordList("meaning_mining_exclusions.txt");

    private static HashSet<string> LoadWordList(string fileName)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, "Data", fileName);
        if (!File.Exists(path)) return words;
        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            words.Add(trimmed);
        }
        return words;
    }

    /// <summary>v0.43.0: Japanese particles that only make grammatical sense
    /// attached to something else — a genuinely single-word gloss ("Cloak"→
    /// "マント") never contains one, so their presence in a single-WORD
    /// imported/dsd entry is evidence the community translator rendered more
    /// than just that word (see <see cref="AutoTranslator(IReadOnlyList{CorpusEntry})"/>
    /// for why this matters).</summary>
    private static readonly char[] Particles = { 'の', 'を', 'が', 'に', 'へ', 'と', 'で', 'は' };

    /// <summary>v0.44.0: the inverse of <see cref="ExactMatchExclusions"/> —
    /// human-curated corrections (<c>Data/phrase_overrides.tsv</c>) for an
    /// English string that resolves WRONG through every tier at once, so
    /// excluding it would only push it to AI-chat/manual review instead of
    /// fixing it. Found via real data: "Plate"→"皿" (a literal dinner plate,
    /// vanilla MISC FULL) is correctly excluded from ①完全一致
    /// (Data/corpus_exact_exclusions.txt), but ②意味合成 independently mines
    /// the SAME wrong sense from its own corpus evidence ("Silver Plate"→
    /// "銀の皿" teaches head "Plate"→"皿"), so "Bosmer Plate Armor" still came
    /// out "…の皿の鎧" via that separate path — excluding "Plate" everywhere
    /// would only make it unresolved. Overriding the specific compound instead
    /// ("Plate Armor"→"プレートの鎧", matching vanilla's own "Steel Plate
    /// Armor"→"スチールプレートの鎧" convention) lets <see cref="GroupKnownPhrases"/>
    /// (v0.39.0) find and use it as a whole span BEFORE either resolver ever
    /// reaches the single word "Plate". <see cref="SourceTier"/> ranks
    /// "override" above even vanilla for exactly this reason.</summary>
    private static readonly List<(string English, string Japanese)> PhraseOverrides = LoadPhraseOverrides();

    private static List<(string English, string Japanese)> LoadPhraseOverrides()
    {
        var overrides = new List<(string, string)>();
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "phrase_overrides.tsv");
        if (!File.Exists(path)) return overrides;
        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8).Skip(1)) // header row
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var english = parts[0].Trim();
            var japanese = parts[1].Trim();
            if (english.Length == 0 || japanese.Length == 0) continue;
            overrides.Add((english, japanese));
        }
        return overrides;
    }

    /// <param name="enableNameFallback">v0.52.0a: NOT stored — used only here,
    /// alongside <paramref name="enableMeaning"/>/<paramref name="enableTransliteration"/>,
    /// to decide whether <see cref="MeaningTable"/>/<see cref="TransliterationTable"/>
    /// are worth mining at all. All three gate whether ②③④ may settle a whole
    /// candidate, but NameFallbackTranslator's per-word chain (④) and prompt.txt's
    /// word hints both read these tables independently of any single flag — so the
    /// tables are only truly unused when every one of the three is off at once
    /// (e.g. the GUI's "scan" pass, which runs with all three disabled to get an
    /// ①-only baseline). That is the ONLY case skipped; any other combination
    /// still mines both tables in full, unchanged from before this parameter
    /// existed.</param>
    public AutoTranslator(IReadOnlyList<CorpusEntry> corpus, TraceLog? trace = null, bool enableMeaning = true, bool enableTransliteration = true, bool enableNameFallback = true)
    {
        _enableMeaning = enableMeaning;
        _enableTransliteration = enableTransliteration;
        var skipDerivedTables = !enableMeaning && !enableTransliteration && !enableNameFallback;

        trace?.Info($"CorpusTransliterator.Build start: corpus {corpus.Count} entries" + (skipDerivedTables ? " (skipped: 2/3/4 all disabled)" : ""));
        _corpusTransliterator = CorpusTransliterator.Build(corpus, trace, skipDerivedTables);
        trace?.Info($"CorpusTransliterator.Build done: {_corpusTransliterator.AllWords.Count()} words");

        // v0.44.2: a SEPARATE exclusion list from ①完全一致's — see
        // MeaningMiningExclusions' remarks for why reusing ExactMatchExclusions
        // here would be wrong (it would also block words like "Armor"/"Light"
        // that ①'s vanilla entry gets wrong but ②意味合成 independently gets
        // right from other evidence).
        trace?.Info($"CorpusMeaningTranslator.Build start: {MeaningMiningExclusions.Count} excluded words");
        _corpusMeaning = CorpusMeaningTranslator.Build(corpus, MeaningMiningExclusions, trace, skipDerivedTables);
        trace?.Info($"CorpusMeaningTranslator.Build done: head={_corpusMeaning.HeadCount} modifier={_corpusMeaning.ModifierCount}");

        trace?.Debug($"_corpusExact build start: {ExactMatchExclusions.Count} excluded words, {PhraseOverrides.Count} phrase overrides");
        _corpusExact = new Dictionary<string, (string, string, string, bool)>(StringComparer.Ordinal);
        foreach (var entry in corpus)
        {
            var key = entry.English.Trim();
            if (key.Length == 0 || !LanguageDetector.ContainsJapanese(entry.Japanese)) continue;
            if (ExactMatchExclusions.Contains(key)) continue;

            // v0.43.0: a single-WORD (no space) imported/dsd entry whose
            // Japanese contains a particle is very likely a community
            // translator's context-aware rendering of a short internal EDID,
            // not a literal word-for-word gloss — real cases found via full
            // review: "Druid"→"ドルイドの指" (Vokrii's own name for a specific
            // alchemy perk, "Druid's Finger") reused for an unrelated candidate
            // ("Stone Claw of the Druid") as if it just meant "druid";
            // "SkillPickpocket"→"スリのスキル書" (Book Covers Skyrim's cover
            // text for that skill's book) similarly isn't a translation of the
            // bare word "SkillPickpocket". Official data (vanilla/reference) is
            // NOT filtered here — that tier's failure mode is a genuine
            // homograph with no decoration at all ("Fall"→"秋", no particle),
            // which is what Data/corpus_exact_exclusions.txt is for instead.
            // Multi-word entries are unaffected — a whole phrase is much less
            // likely to have silently absorbed extra context, and restricting
            // those too was measured to cost far more coverage for little
            // additional safety (see DESIGN_NOTES.md's v0.43.0 section).
            if ((entry.SourceKind is "imported" or "dsd") && !key.Contains(' ') && entry.Japanese.IndexOfAny(Particles) >= 0)
                continue;

            // v0.49.2: has ANY entry for this key (win or lose the tier
            // competition below) ever carried an NPC_ DsdType? Tracked
            // independently of which entry's (Japanese, SourceKind, Source)
            // ultimately wins — see the field's own remarks and TryTranslate's
            // use of it (the "Courage" homograph guard).
            var isNpcSourced = entry.DsdType.StartsWith("NPC_", StringComparison.Ordinal);

            // v0.38.0: keep whichever entry has the best SourceTier, not merely
            // whichever was seen first — ties within the same tier still keep the
            // first one seen (deterministic, same as the old TryAdd behavior).
            if (_corpusExact.TryGetValue(key, out var existing))
            {
                var seenAsNpc = existing.SeenAsNpcName || isNpcSourced;
                if (SourceTier.Of(existing.SourceKind) <= SourceTier.Of(entry.SourceKind))
                {
                    if (seenAsNpc != existing.SeenAsNpcName)
                        _corpusExact[key] = (existing.Japanese, existing.SourceKind, existing.Source, seenAsNpc);
                    continue;
                }
                _corpusExact[key] = (entry.Japanese, entry.SourceKind, entry.Source, seenAsNpc);
            }
            else
            {
                _corpusExact[key] = (entry.Japanese, entry.SourceKind, entry.Source, isNpcSourced);
            }
        }

        // v0.44.0: applied LAST so a human correction always wins regardless of
        // corpus processing order — see PhraseOverrides' remarks. v0.49.2:
        // SeenAsNpcName=true unconditionally — a human explicitly curated this
        // exact string, so the NPC_FULL homograph guard (meant to catch
        // ACCIDENTAL corpus collisions) has no reason to second-guess it.
        foreach (var (english, japanese) in PhraseOverrides)
            _corpusExact[english] = (japanese, "override", "Data/phrase_overrides.tsv", true);
        trace?.Debug($"_corpusExact build done: {_corpusExact.Count} entries");
    }

    /// <param name="recordType">The candidate's DSD type, used to gate the
    /// meaning table (④) to the record types it was mined from. Pass "" to skip
    /// that step entirely.</param>
    public AutoTranslationResult? TryTranslate(string englishText, string recordType = "", TraceLog? trace = null)
    {
        var text = englishText.Trim();
        if (text.Length == 0) return null;

        // v0.49.2: NPC_FULL homograph guard. A key like "Courage" can be a
        // perfectly correct vanilla spell/enchantment/magic-effect name
        // (SourceKind "vanilla", DsdType "SPEL FULL"/"ENCH FULL"/"MGEF FULL",
        // Japanese "挑発") that ALSO happens to collide with a mod-added dog's
        // NPC_ FULL name — the same word, unrelated contexts. Unlike ②意味合成/
        // ④NameFallbackTranslator (which exclude NPC_ FULL from their whole
        // record type, since they compose/guess), ① is normally trusted
        // unconditionally because it's supposed to be ground truth — but ground
        // truth attested for one context is not ground truth for another. Only
        // reject the hit when this exact text has NEVER been attested as an
        // NPC_ name anywhere in the corpus (a real NPC name reused for another
        // NPC — e.g. two mods' NPCs sharing a name — still passes, since that
        // case DOES carry NPC_ provenance). Measured against real data: only 1
        // of 757 currently ①-resolved NPC_ FULL candidates in a 332-plugin load
        // order was affected (Courage itself) — see DESIGN_NOTES.md.
        if (_corpusExact.TryGetValue(text, out var corpusHit) && !(recordType == "NPC_ FULL" && !corpusHit.SeenAsNpcName))
        {
            // v0.38.0: "dsd" now gets its own method tag (AutoCorpusDsd) instead of
            // being silently folded into "AutoCorpus" (vanilla) — see _corpusExact's
            // remarks and DESIGN_HISTORY.md's v0.38.0 section.
            var method = corpusHit.SourceKind switch
            {
                "dsd" => "AutoCorpusDsd",
                "imported" => "AutoCorpusImported",
                "reference" => "AutoCorpusReferenceTaiyaku",
                "override" => "AutoCorpusOverride",
                _ => "AutoCorpus",
            };
            trace?.Trace($"Resolve \"{text}\": {method} -> \"{corpusHit.Japanese}\" [{corpusHit.SourceKind}:{corpusHit.Source}]");
            return new AutoTranslationResult(corpusHit.Japanese, method);
        }

        // ④ corpus-derived MEANING composition — placed ahead of the phonetic
        // paths because for gear a meaning translation ("鉄のブーツ") is what the
        // player expects, where transliteration ("アイアン・ブーツ") reads as a
        // foreign product name. Only applied to the record types the table was
        // mined from (see CorpusMeaningTranslator.AppliesToRecordType).
        if (_enableMeaning && CorpusMeaningTranslator.AppliesToRecordType(recordType)
            && _corpusMeaning.TryTranslate(text, _corpusTransliterator, out var meaningJa, out var viaTransliteration, out var meaningBreakdown))
        {
            // Labelled apart so the two kinds stay separable in the log and in
            // translations.tsv: a fully meaning-composed name and one whose
            // modifier was transliterated carry different amounts of evidence.
            var method = viaTransliteration ? "AutoCorpusMeaningTranslit" : "AutoCorpusMeaning";
            var modifierSrc = meaningBreakdown.ModifierSource.Length > 0 ? $"[{meaningBreakdown.ModifierSource}]" : "";
            var headSrc = meaningBreakdown.HeadSource.Length > 0 ? $"[{meaningBreakdown.HeadSource}]" : "";
            var detail = $"修飾語\"{meaningBreakdown.ModifierWord}\"→\"{meaningBreakdown.ModifierJapanese}\"({meaningBreakdown.ModifierVia}){modifierSrc} " +
                         $"+ 語尾\"{meaningBreakdown.HeadWord}\"→\"{meaningBreakdown.HeadJapanese}\"(意味){headSrc}";
            trace?.Trace($"Resolve \"{text}\": {method} -> \"{meaningJa}\" ({detail})");
            return new AutoTranslationResult(meaningJa, method, detail);
        }

        // v0.48.1: explicit type gate, same "*_FULL except NPC_ FULL" scope as
        // ②意味合成/④NameFallbackTranslator (CorpusMeaningTranslator.AppliesToRecordType).
        // Previously ③ was gated ONLY by the shape checks below (unspaced single
        // word / LooksLikeProperNounPhrase), with no restriction on record type at
        // all — harmless in every load order seen so far (description/dialogue/
        // journal text never happens to match the shape), but structurally a latent
        // gap: a short, punctuation-free fragment landing in a *_DESC or similar
        // sentence-shaped field could still trigger this path. Making the scope
        // explicit closes that gap without changing any observed behavior.
        if (_enableTransliteration && CorpusMeaningTranslator.AppliesToRecordType(recordType))
        {
            if (!text.Contains(' ') && !text.Contains('\t'))
            {
                // Single unspaced word: try to fully cover it end-to-end with known
                // corpus-precedent transliteration pieces (e.g. an unseen "Frostmere"
                // resolved as "Frost" + "mere" if both roots have shipped precedent).
                var word = new string(text.Where(char.IsLetter).ToArray());
                if (word.Length > 0)
                {
                    var decomposed = _corpusTransliterator.TryDecompose(word, out var wordPieces);
                    if (decomposed != null)
                    {
                        var detail = string.Join(" + ", wordPieces.Select(p =>
                            $"\"{p.Piece}\"→\"{p.Kana}\"" + (p.Source.Length > 0 ? $"[{p.Source}]" : "(derived: 複数語からの切り出し・単独の出典なし)")));
                        trace?.Trace($"Resolve \"{text}\": AutoCorpusTransliterate -> \"{decomposed}\" ({detail})");
                        return new AutoTranslationResult(decomposed, "AutoCorpusTransliterate", detail);
                    }
                }
            }
            else if (LooksLikeProperNounPhrase(text))
            {
                // Multi-word candidate (e.g. a person's name): resolve each word via the
                // corpus transliteration dictionary ONLY (deliberately not mixed with the
                // JMdict meaning dictionary — an earlier version did, and it produced
                // inconsistent results like "Ash Cloud" -> "灰・雲", katakana and kanji
                // joined with a middle dot, which reads as un-idiomatic Japanese; a
                // meaning-based translation needs real compositional grammar, not word
                // substitution). Only auto-apply if every word is covered by real
                // in-game transliteration precedent.
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pieces = new List<string>(words.Length);
                var wordDetails = new List<string>(words.Length);
                var allResolved = true;
                foreach (var w in words)
                {
                    var alpha = new string(w.Where(char.IsLetter).ToArray());
                    if (alpha.Length == 0) { allResolved = false; break; }

                    if (_corpusTransliterator.TryTranslateWord(alpha, out var kana, out var wordSource))
                    {
                        pieces.Add(kana);
                        wordDetails.Add($"\"{alpha}\"→\"{kana}\"" + (wordSource.Length > 0 ? $"[{wordSource}]" : ""));
                    }
                    else if (_corpusTransliterator.TryDecompose(alpha, out var subPieces) is { } decomposedKana)
                    {
                        pieces.Add(decomposedKana);
                        wordDetails.Add(subPieces.Count > 1
                            ? $"\"{alpha}\"→\"{decomposedKana}\"({string.Join("+", subPieces.Select(p => p.Piece))})"
                            : $"\"{alpha}\"→\"{decomposedKana}\"" + (subPieces.Count == 1 && subPieces[0].Source.Length > 0 ? $"[{subPieces[0].Source}]" : ""));
                    }
                    else { allResolved = false; break; }
                }

                if (allResolved && pieces.Count > 0)
                {
                    // v0.36.0: this path was built for person-name phrases (see remarks
                    // above), so the "・" it joins with is a stylistic choice for names —
                    // it can read oddly when what actually landed here is a race/material
                    // + item-type compound that failed ②意味合成 for lack of head support
                    // (see DESIGN_HISTORY.md's v0.36.0 section, "Dwarven Claymore" →
                    // "ドワーフ・クレイモア"). The detail string exists so that distinction
                    // is reviewable without re-deriving it by hand.
                    var detail = "複数語の固有名詞句として音訳（人名用の結合規則・意味合成は不成立）: " + string.Join(" ・ ", wordDetails);
                    var joined = string.Join("・", pieces);
                    trace?.Trace($"Resolve \"{text}\": AutoCorpusTransliterate (proper-noun phrase) -> \"{joined}\" ({detail})");
                    return new AutoTranslationResult(joined, "AutoCorpusTransliterate", detail);
                }
            }
        }

        trace?.Trace($"Resolve \"{text}\": unresolved (falls through to AI-chat / NameFallbackTranslator)");
        return null;
    }

    /// <summary>Best-effort transliteration draft for AI-chat prompt hints only —
    /// NOT auto-applied to the Japanese column (see class remarks for why). Returns
    /// null unless the text looks like a short proper-noun phrase.</summary>
    public static string? SuggestTransliteration(string englishText)
    {
        var text = englishText.Trim();
        return LooksLikeProperNounPhrase(text) ? Transliterator.TransliterateName(text) : null;
    }

    /// <summary>Heuristic gate for auto-transliteration: short (1-3 word), Title Case,
    /// no stop words, no sentence punctuation. Meant to admit names like "Rorikstead"
    /// or "Shady Sam" while rejecting phrases/sentences like "This is what you get!"
    /// (those are better left for the AI-chat pass, which can read tone/idiom).</summary>
    private static bool LooksLikeProperNounPhrase(string text)
    {
        if (text.IndexOfAny(new[] { '!', '?', '.', ',', ';', ':' }) >= 0) return false;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 3) return false;

        foreach (var w in words)
        {
            var alpha = new string(w.Where(char.IsLetter).ToArray());
            if (alpha.Length == 0) return false;
            if (StopWords.Contains(alpha)) return false;
            if (!char.IsUpper(alpha[0])) return false;
        }
        return true;
    }
}
