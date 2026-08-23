using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Meaning-level (not phonetic) translation of "&lt;Modifier&gt; &lt;Head&gt;" gear
/// names, with both halves mined from this load order's own corpus.
///
/// Why this exists: the automatic pipeline resolved only 12.8% of candidates
/// (2,452 of 19,203), yet **70.3% of short candidates consist entirely of words
/// that already appear somewhere in the corpus** (10,054 of 14,299). The
/// vocabulary is in hand; what was missing is the ALIGNMENT between an English
/// word and the Japanese fragment that renders it. <see cref="CorpusTransliterator"/>
/// only ever learned phonetic renderings (katakana), so "Iron" → "鉄" — a meaning
/// translation — had no way to be learned at all.
///
/// How the alignment is found — BY CONTRAST, never from a single pair. Splitting
/// one pair on "の" is unreliable ("Accept Sign" → "星座の変更" has no word-level
/// correspondence at that boundary). Instead:
///
///   1. HEAD: group entries by their English last word. "Amber Battleaxe" →
///      "琥珀の両手斧", "Steel Battleaxe" → "鋼鉄の両手斧", ... all share the
///      Japanese suffix "両手斧", so that is what "battleaxe" renders as. The
///      agreement of several independently-authored entries is the evidence.
///   2. MODIFIER: with the head's suffix known, whatever precedes it is the
///      modifier — "琥珀の" for "amber". Note it naturally carries the "の", which
///      is what makes composition a plain concatenation rather than a guess about
///      Japanese grammar.
///   3. CORROBORATION: a modifier is kept only when the SAME rendering is reached
///      through two or more DIFFERENT heads ("amber" must come out "琥珀の" from
///      Battleaxe AND from Bow AND from Dagger). This is the safeguard against
///      the failure mode v0.7.1 documented, where a single loose official phrase
///      ("Shield Charge Knockback" → "シールド・チャージ・ノックダウン") taught a
///      wrong word-level mapping. One pair can be idiosyncratic; the same
///      alignment reached independently several times cannot.
///
/// Scope is deliberately narrow for a first cut: mined from and applied to gear
/// name types only (ARMO/WEAP/AMMO FULL). Bethesda's naming is at its most
/// templated there, the head nouns are a closed set of a few dozen, and a wrong
/// composition costs an odd-looking item name rather than a broken quest or line
/// of dialogue. Widening to SPEL/MGEF/PERK is a later step, once the method has
/// been judged on real output.
/// </summary>
public sealed class CorpusMeaningTranslator
{
    /// <summary>A head noun needs this many distinct entries agreeing on its
    /// Japanese suffix before the suffix is trusted.</summary>
    private const int MinHeadSupport = 3;

    /// <summary>A modifier needs to be reached through this many DIFFERENT heads.
    /// Two is enough to rule out a one-off idiosyncratic phrase while still
    /// admitting the many materials that only appear on a couple of gear kinds.</summary>
    private const int MinModifierHeads = 2;

    /// <summary>The particle Skyrim's Japanese localization consistently puts
    /// between a material/qualifier and the gear it describes (鉄<b>の</b>ブーツ).
    /// Both halves are stored without it and it is re-inserted on composition.</summary>
    internal const string Joiner = "の";

    /// <summary>
    /// v0.28.0: widened from a fixed 3-type set (`ARMO/WEAP/AMMO FULL`) to every
    /// name-type field (any DSD type ending in " FULL") — both as mining SOURCE
    /// and as the gate <see cref="AutoTranslator"/> uses to decide where to try
    /// composition. A single-word modifier like "Ancient" is common across
    /// QUST/LOCTN FULL etc., all of which carry huge amounts of existing
    /// (vanilla/DSD/community) precedent — restricting mining to gear types alone
    /// starved the modifier vocabulary of exactly the everyday adjectives that
    /// would otherwise resolve immediately.
    ///
    /// v0.29.10: `NPC_ FULL` explicitly excluded. It is the one "*_FULL" type
    /// whose content is near-uniformly PERSON/CREATURE proper nouns, not
    /// descriptive gear names — meaning composition treats a real word as its
    /// dictionary sense regardless of role, which is exactly wrong for a name.
    /// Found via real data: a mod's dogs literally named "Silver"/"Steel"/
    /// "Arrow"/"Shadow" came out as "銀"/"鋼鉄"/"矢"/"影" (the material/object
    /// senses) instead of being left for transliteration or human judgment.
    /// This mirrors a rule <see cref="AutoTranslator.LooksLikeProperNounPhrase"/>
    /// already enforces for its OWN multi-word path (person names: corpus
    /// transliteration ONLY, never mixed with meaning) — that path just never
    /// reaches single words like "Silver", which meaning composition otherwise
    /// intercepts first. Excluding NPC_ FULL here removes it from both mining
    /// (so pet names can't pollute the learned modifier/head vocabulary either)
    /// and from <see cref="NameFallbackTranslator"/>'s word-chain fallback,
    /// which mixes meaning and transliteration per word and would reproduce the
    /// same problem. AutoTranslator's own transliteration-only steps (③) are
    /// unaffected — they don't gate on this method — so an NPC_ FULL name with
    /// real transliteration precedent still resolves; the difference is only
    /// that a real word can no longer win on meaning ahead of that.
    /// </summary>
    public static bool AppliesToRecordType(string recordType) =>
        recordType.EndsWith(" FULL", StringComparison.Ordinal) && recordType != "NPC_ FULL";

    /// <summary>v0.37.0: each learned head/modifier carries a
    /// <see cref="SourceSummary"/> string built from every corroborating corpus
    /// entry's (SourceKind, Source) — see <see cref="BuildHeadLexicon"/>/
    /// <see cref="BuildModifierLexicon"/>. v0.38.0: also the best
    /// <see cref="SourceTier"/> among those entries, so a rendering corroborated
    /// only by dsd/imported (community) data can be recognized as such and
    /// weighed against a competing vanilla/reference precedent elsewhere —
    /// see <see cref="TryTranslate"/>.</summary>
    private readonly Dictionary<string, (string Japanese, string SourceSummary, int Tier)> _heads;
    private readonly Dictionary<string, (string Japanese, string SourceSummary, int Tier)> _modifiers;

    private CorpusMeaningTranslator(
        Dictionary<string, (string Japanese, string SourceSummary, int Tier)> heads,
        Dictionary<string, (string Japanese, string SourceSummary, int Tier)> modifiers)
    {
        _heads = heads;
        _modifiers = modifiers;
    }

    public int HeadCount => _heads.Count;
    public int ModifierCount => _modifiers.Count;

    public IEnumerable<(string English, string Japanese, string Kind, string Source)> AllEntries =>
        _heads.Select(kv => (kv.Key, kv.Value.Japanese, "head", kv.Value.SourceSummary))
            .Concat(_modifiers.Select(kv => (kv.Key, kv.Value.Japanese, "modifier", kv.Value.SourceSummary)));

    /// <param name="wordExclusions">v0.44.2: the same manual exclusion list
    /// AutoTranslator uses for ①完全一致 (<c>Data/corpus_exact_exclusions.txt</c>),
    /// also applied here to head/modifier mining — see BuildHeadLexicon/
    /// BuildModifierLexicon's remarks for why a word flagged as an unreliable
    /// standalone translation shouldn't be trusted in EITHER role either. Pass
    /// an empty set to mine unrestricted (used by tests/tools that don't care).</param>
    /// <param name="skip">v0.52.0a: when true, returns an empty table without
    /// mining the corpus at all — see <see cref="CorpusTransliterator.Build"/>'s
    /// matching parameter for why.</param>
    public static CorpusMeaningTranslator Build(IReadOnlyList<CorpusEntry> corpus, IReadOnlySet<string>? wordExclusions = null, Core.TraceLog? trace = null, bool skip = false)
    {
        if (skip) return new CorpusMeaningTranslator(new(), new());

        wordExclusions ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = corpus
            .Where(e => AppliesToRecordType(e.DsdType))
            .Select(e => (English: e.English.Trim(), Japanese: e.Japanese.Trim(), e.Source, e.SourceKind))
            .Where(p => IsMineable(p.English, p.Japanese))
            .Distinct()
            .ToList();
        trace?.Debug($"Meaning-mining input extraction: corpus {corpus.Count} -> mineable {pairs.Count} entries (excluded words: {wordExclusions.Count})");

        var heads = BuildHeadLexicon(pairs, wordExclusions);
        trace?.Debug($"HeadLexicon build done: {heads.Count} entries");
        if (trace != null) foreach (var (head, entry) in heads) trace.Trace($"Head learned: \"{head}\" -> \"{entry.Japanese}\" (tier={entry.Tier}) [{entry.SourceSummary}]");
        var modifiers = BuildModifierLexicon(pairs, heads, wordExclusions);
        trace?.Debug($"ModifierLexicon build done: {modifiers.Count} entries");
        if (trace != null) foreach (var (modifier, entry) in modifiers) trace.Trace($"Modifier learned: \"{modifier}\" -> \"{entry.Japanese}\" (tier={entry.Tier}) [{entry.SourceSummary}]");
        return new CorpusMeaningTranslator(heads, modifiers);
    }

    /// <summary>Only plain Title Case names qualify as mining input — anything
    /// carrying markup, aliases, or punctuation is a sentence or a templated
    /// runtime string, not a gear/item name.</summary>
    private static bool IsMineable(string english, string japanese)
    {
        if (english.Length == 0 || japanese.Length == 0) return false;
        if (english.Length > 40) return false;
        if (english.IndexOfAny(new[] { '<', '>', '%', '[', ']', '(', ')', '"', ',', '.', '!', '?', ':', ';' }) >= 0) return false;
        if (!LanguageDetector.ContainsJapanese(japanese)) return false;
        if (japanese.IndexOfAny(new[] { '\n', '\t', '<', '>', '%' }) >= 0) return false;
        if (!NameFieldFilter.LooksLikeNameField(english)) return false;

        var words = english.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!words.All(w => w.All(c => char.IsAsciiLetter(c) || c == '\'' || c == '-'))) return false;

        return TrySplitHeadModifier(english, out _, out _);
    }

    /// <summary>
    /// v0.28.0: splits an English name into (head, modifier phrase) — the single
    /// piece of parsing every mining/lookup step in this class shares, so the two
    /// name shapes stay in exact agreement.
    ///
    /// Two shapes are recognized, both composing to "&lt;modifier&gt;の&lt;head&gt;"
    /// (Skyrim's own convention either way): "Steel Plate Boots" (modifier =
    /// everything but the last word, head = the last word), and "Blade of Woe"
    /// (head = the part before " of ", modifier = the part after it, with a
    /// leading article stripped). The "of" shape requires the head part to be a
    /// SINGLE word — "Champions of the Realm" has no single-word head, so it
    /// falls through to the last-word rule instead of guessing wrong.
    ///
    /// The part after " of " is capped at 2 words (post-article), same as the
    /// modifier cap on the other shape below — found by real data to matter: an
    /// uncapped version happily "split" candidates like "Blade of Woe Kill
    /// Reward" (a quest's internal FULL, built by appending "Kill Reward" to the
    /// weapon's own name) into head="Blade", modifier="Woe Kill Reward", which
    /// composed into unreadable nonsense ("Woe キル Rewardの刃"). A real "X of Y"
    /// item name's Y is almost always 1-2 words; anything longer is far more
    /// likely a templated compound like this than a genuine long modifier.
    /// </summary>
    internal static bool TrySplitHeadModifier(string english, out string head, out string modifierPhrase)
    {
        var trimmed = english.Trim();

        // Neither shape below has a slot for a coordinating conjunction or a
        // leading negation — both break the "single descriptive modifier" premise
        // the whole class rests on. Found by real data: "Shoes and Boots" (a
        // coordinate PAIR, not "Shoes" modifying "Boots") composed into "靴
        // andのブーツ", and "No Fall Damage" (negates "Fall Damage", doesn't
        // belong to a "No") composed into "否 Fallのダメージ" — both fluent-looking
        // garbage rather than a merely-incomplete partial translation, which is
        // why they're rejected outright instead of left to the per-word fallback.
        if (trimmed.Contains(" and ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(" or ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("No ", StringComparison.Ordinal)
            || trimmed.StartsWith("Not ", StringComparison.Ordinal))
        {
            head = modifierPhrase = "";
            return false;
        }

        var ofIdx = trimmed.IndexOf(" of ", StringComparison.Ordinal);
        if (ofIdx > 0)
        {
            var headPart = trimmed[..ofIdx].Trim();
            var modPart = trimmed[(ofIdx + 4)..].Trim();
            foreach (var article in new[] { "the ", "a ", "an " })
                if (modPart.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { modPart = modPart[article.Length..].Trim(); break; }

            var modWordCount = modPart.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (!headPart.Contains(' ') && headPart.Length > 0 && modPart.Length > 0 && modWordCount <= 2)
            {
                head = headPart;
                modifierPhrase = modPart;
                return true;
            }
        }

        // Two or three words: the last is the head, everything before it is one
        // modifier phrase ("Steel Plate" for "Steel Plate Boots"). Allowing three
        // materially widens the mining input, since a great many gear names carry
        // a two-word material or qualifier.
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 3) { head = modifierPhrase = ""; return false; }
        head = words[^1];
        modifierPhrase = string.Join(' ', words[..^1]);
        return true;
    }

    /// <summary>v0.42.0: the longest trailing run of katakana characters in
    /// <paramref name="japanese"/> — the boundary signal <see cref="BuildHeadLexicon"/>
    /// uses for an entry with no "の" seam. Requires at least 2 characters and a
    /// non-empty remainder before it (so a whole string that's ENTIRELY katakana
    /// — a plain transliterated name, not a "modifier+head" compound — never
    /// qualifies; that shape belongs to <see cref="CorpusTransliterator"/>, not
    /// here). Same character range <see cref="CorpusTransliterator.IsPureKatakana"/>
    /// uses, duplicated locally rather than shared — it's a two-line range check,
    /// not worth threading a dependency between the two classes for.</summary>
    private static string TrailingKatakanaRun(string japanese)
    {
        var end = japanese.Length;
        var start = end;
        while (start > 0 && IsKatakanaChar(japanese[start - 1])) start--;
        if (start == 0) return ""; // whole string is katakana — no modifier prefix, not a compound
        var run = japanese[start..end];
        return run.Length >= 2 ? run : "";
    }

    private static bool IsKatakanaChar(char c) => c is 'ー' or '・' || (c is >= '゠' and <= 'ヿ');

    /// <summary>
    /// Longest common Japanese suffix across the entries sharing an English last
    /// word, then normalized so that EVERY head is stored WITHOUT its joining
    /// particle and every supporting entry is confirmed to use "の" at that seam.
    ///
    /// The normalization is what makes composition safe. Mined raw, the particle
    /// lands inconsistently — "Battleaxe" came out "の両手斧" (particle included,
    /// because every supporting entry had it) while "Armor" came out "鎧" (one
    /// outlier without the particle shortened the common suffix). Composing
    /// across that inconsistency produces "琥珀鎧" instead of "琥珀の鎧". Storing
    /// the bare head and re-inserting "の" at composition time removes the
    /// possibility entirely.
    ///
    /// Requiring "の" before the head on EVERY supporting entry also discards
    /// heads that are really just the tail of a compound word rather than a
    /// separable noun — "Bear" had mined as "グマ" (from ヒグマ), which would have
    /// composed into nonsense.
    /// </summary>
    private static Dictionary<string, (string Japanese, string SourceSummary, int Tier)> BuildHeadLexicon(
        List<(string English, string Japanese, string Source, string SourceKind)> pairs,
        IReadOnlySet<string> wordExclusions)
    {
        var byHead = new Dictionary<string, List<(string Japanese, string Source, string SourceKind, string PrecedingWord)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (english, japanese, source, sourceKind) in pairs)
        {
            if (!TrySplitHeadModifier(english, out var head, out var modifierPhrase)) continue;
            // v0.44.2: a head flagged as an unreliable single-word translation
            // never gets mined, regardless of how many "votes" it gathers — real
            // case: "Farm" learned "会話" from 3 "Dialogue X Farm" (internal
            // topic-name template) rows that happen to carry a "の" seam, while
            // 60+ genuine "___ Farm"→"...農場/農園" rows (correct, but a direct
            // "の"-less compound) were invisible to the seam-based path before
            // this fix even landed — same root cause as v0.42.0's "Quest" bug,
            // just on a kanji-ending head the katakana-run fallback can't reach.
            if (wordExclusions.Contains(head)) continue;
            if (!byHead.TryGetValue(head, out var list)) byHead[head] = list = new();
            // v0.42.0: the modifier's OWN last word, kept alongside each entry so
            // BuildHeadLexicon's katakana-run fallback (below) can require it to
            // vary across voters — see that fallback's remarks for why.
            var precedingWord = modifierPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } mw ? mw[^1] : modifierPhrase;
            list.Add((japanese, source, sourceKind, precedingWord));
        }

        var heads = new Dictionary<string, (string, string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (head, entries) in byHead)
        {
            // Distinct by Japanese text (keeping one representative source per
            // distinct rendering) — same "3 different item names must agree"
            // requirement as before v0.37.0, now just retaining who said each one.
            var distinct = entries
                .GroupBy(e => e.Japanese, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (distinct.Count < MinHeadSupport) continue;

            // Take the text after the LAST "の" from each entry and keep whichever
            // reading the most entries agree on. A longest-common-suffix would have
            // been destroyed by a single outlier (one "Wooden Sword" → "木刀", with
            // no particle at all, is enough to erase a head that fifty other
            // entries agree on); counting votes ignores such entries instead.
            //
            // v0.42.0: an entry with NO "の" at all used to be discarded outright —
            // found by real data to be a serious blind spot, not a rare edge case.
            // A head whose Japanese is a direct-compound loanword ("会話クエスト",
            // "雇用クエスト" — no "の" before "クエスト", which is completely
            // ordinary Japanese compounding) was INVISIBLE to this method entirely,
            // while a small minority of "の"-joined outliers for the SAME head
            // ("エルマスへの親切クエスト" ×3, from a single reused Dragonborn.esm
            // quest-naming template) had the reading slot to themselves and won by
            // default — "Quest" learned "親切クエスト" as its head, despite being
            // outnumbered roughly 5:1 by the correct "クエスト" evidence the "の"
            // requirement was silently throwing away. Fixed by giving those
            // no-particle entries a SECOND way to vote: the longest TRAILING RUN
            // OF KATAKANA characters — a loanword head rendered in katakana forms
            // a contiguous katakana run at the very end of the string regardless
            // of whether a particle precedes it, so this is a real boundary
            // signal, not a guess (unlike the rejected longest-common-suffix
            // approach, which computed one answer across ALL entries at once and
            // so WAS destroyed by a single outlier — this instead adds each
            // entry's own candidate into the SAME vote-counted pool as the
            // "の"-based readings, so an outlier can only ever cast one vote,
            // exactly like every other reading here). Kanji-ending heads ("鎧",
            // "兜") still rely solely on the "の" marker — kanji compounds have no
            // equivalent script-based boundary to detect this way.
            var votes = new Dictionary<string, List<(string Source, string SourceKind, string PrecedingWord, bool ViaKatakanaRun)>>(StringComparer.Ordinal);
            foreach (var (japanese, source, sourceKind, precedingWord) in distinct)
            {
                var seam = japanese.LastIndexOf(Joiner, StringComparison.Ordinal);
                var viaKatakanaRun = seam <= 0;
                var reading = !viaKatakanaRun ? japanese[(seam + Joiner.Length)..] : TrailingKatakanaRun(japanese);
                if (reading.Length == 0) continue;
                if (!votes.TryGetValue(reading, out var voters)) votes[reading] = voters = new();
                voters.Add((source, sourceKind, precedingWord, viaKatakanaRun));
            }

            if (votes.Count == 0) continue;

            // v0.38.0: tier decides which READING is trusted when readings
            // disagree — a handful of dsd/imported votes must never outvote a
            // single vanilla/reference vote just because there are more of them.
            // But when readings AGREE, tier draws no distinction: a dsd/imported
            // vote for the SAME reading a vanilla vote already supports is real
            // corroborating evidence, not noise, so it must still count toward
            // the threshold — discarding it (an earlier version of this fix did)
            // only wastes genuine agreement for no safety benefit.
            var bestTier = votes.Values.SelectMany(v => v).Select(v => SourceTier.Of(v.SourceKind)).Min();
            var eligible = votes
                .Where(kv => kv.Value.Any(v => SourceTier.Of(v.SourceKind) == bestTier))
                .ToList();
            if (eligible.Count == 0) continue;

            var best = eligible.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key.Length).First();
            if (best.Value.Count < MinHeadSupport) continue;

            // v0.42.0: a reading reached ENTIRELY via the katakana-run fallback
            // (no voter had a real "の" seam) needs one more check the "の"-anchored
            // case doesn't: that its voters don't all share the same immediately-
            // preceding English word. Real case found: "Addled/Frostbitten/
            // Weakened Effect Timer" all mined the SAME trailing run "エフェクト
            // タイマー" for head "Timer" — 3 distinct whole strings, satisfying
            // MinHeadSupport by count, but every one of them has "Effect" as the
            // word immediately before "Timer", so this is one template varying
            // its FRONT, not 3 independent confirmations of what "Timer" means;
            // the run had silently swallowed "Effect" (also katakana, no boundary
            // between them) into what should have been just "タイマー". Requiring
            // the preceding word to vary is the same corroboration principle
            // BuildModifierLexicon already applies from the other direction
            // (a modifier needs several DIFFERENT heads); here a katakana-run
            // head reading needs several different modifiers.
            if (best.Value.All(v => v.ViaKatakanaRun))
            {
                var distinctPrecedingWords = best.Value.Select(v => v.PrecedingWord).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (distinctPrecedingWords < MinHeadSupport) continue;
            }

            heads[head] = (best.Key, SourceSummary.Summarize(best.Value.Select(v => (v.SourceKind, v.Source))), bestTier);
        }
        return heads;
    }

    /// <summary>What precedes a known head suffix is that entry's modifier. Kept
    /// only when several different heads independently produce the same rendering.</summary>
    private static Dictionary<string, (string Japanese, string SourceSummary, int Tier)> BuildModifierLexicon(
        List<(string English, string Japanese, string Source, string SourceKind)> pairs,
        Dictionary<string, (string Japanese, string SourceSummary, int Tier)> heads,
        IReadOnlySet<string> wordExclusions)
    {
        // modifier -> rendering -> head -> one representative (source, sourceKind)
        // for that head (a head can only vote once per modifier either way, so one
        // representative per head is all corroboration needs).
        var observed = new Dictionary<string, Dictionary<string, Dictionary<string, (string Source, string SourceKind)>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (english, japanese, source, sourceKind) in pairs)
        {
            if (!TrySplitHeadModifier(english, out var head, out var modifier)) continue;
            if (!heads.TryGetValue(head, out var headEntry)) continue;
            // v0.44.2: same exclusion as BuildHeadLexicon, applied to the
            // MODIFIER role too — a word unreliable enough to distrust as a
            // standalone translation shouldn't be trusted as a modifier either.
            if (wordExclusions.Contains(modifier)) continue;

            // v0.44.2: previously required the head's OWN suffix to be preceded
            // by "の" specifically ("スチールの鎧") — an entry where the modifier
            // combines with the head as a direct compound instead ("炎ダメージ",
            // no "の") was silently discarded, the same blind spot v0.42.0 fixed
            // on the head-mining side. Confirmed by real data to be common, not
            // rare: 744 corpus rows matched this exact shape (Fire/Frost/Poison/
            // Animal/... Damage, Levelled/Brelyna's... Spell, and more).
            if (!japanese.EndsWith(headEntry.Japanese, StringComparison.Ordinal)) continue;

            var rendering = japanese[..^headEntry.Japanese.Length];
            var endedWithJoiner = rendering.EndsWith(Joiner, StringComparison.Ordinal);
            if (endedWithJoiner)
            {
                rendering = rendering[..^Joiner.Length]; // existing safe path — an internal "の" elsewhere in what's left is fine ("ドラゴンの骨")
            }
            else
            {
                // v0.44.2 safety net: a no-separator rendering that still
                // CONTAINS "の" somewhere in the middle is not a clean direct
                // compound — it means a QUALIFIER belonging to the head got
                // swept in, not the modifier's own translation. Real case:
                // "Nordic Shield" → "ノルドの刻印盾" (a Dragonborn.esm oddity —
                // this one shield's official name bakes in "刻印"/"carved" even
                // though the English doesn't say "Carved"). Stripping only the
                // head "盾" leaves "ノルドの刻印" — "刻印" modifies THIS head
                // specifically, not "Nordic" in general, and voting it in as
                // "Nordic"→"ノルドの刻印" corrupted the modifier once enough
                // votes existed to compete with (though not yet outright beat)
                // the correct "Nordic"→"ノルド". Genuine no-separator compounds
                // ("炎"+"ダメージ", "永久"+"効果") never contain "の" at all —
                // only a truncated multi-clause phrase does.
                if (rendering.Contains(Joiner, StringComparison.Ordinal)) continue;
            }
            if (rendering.Length == 0) continue;

            if (!observed.TryGetValue(modifier, out var byRendering))
                observed[modifier] = byRendering = new(StringComparer.Ordinal);
            if (!byRendering.TryGetValue(rendering, out var headsSeen))
                byRendering[rendering] = headsSeen = new(StringComparer.OrdinalIgnoreCase);
            headsSeen[head] = (source, sourceKind);
        }

        var modifiers = new Dictionary<string, (string, string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modifier, byRendering) in observed)
        {
            // v0.38.0: same principle as BuildHeadLexicon — tier decides which
            // RENDERING is eligible to win (a rendering needs at least one vote at
            // this modifier's best tier to even be considered), but once a
            // rendering is eligible, EVERY head that voted for it counts toward
            // the corroboration threshold, tier notwithstanding. A dsd/imported
            // head agreeing with a vanilla/reference head on the same rendering is
            // corroboration, not noise — only a rendering with no best-tier
            // support at all is the thing this exists to reject.
            var bestTier = byRendering.Values.SelectMany(hs => hs.Values).Select(v => SourceTier.Of(v.SourceKind)).DefaultIfEmpty(int.MaxValue).Min();
            var eligible = byRendering
                .Where(kv => kv.Value.Values.Any(h => SourceTier.Of(h.SourceKind) == bestTier))
                .ToList();

            // Ambiguous modifiers (the same English rendered differently depending
            // on the head) are dropped outright rather than resolved by majority —
            // picking a winner is exactly how a plausible-looking wrong mapping
            // gets in.
            var corroborated = eligible.Where(kv => kv.Value.Count >= MinModifierHeads).ToList();
            if (corroborated.Count != 1) continue;
            var provenance = corroborated[0].Value.Values.Select(v => (v.SourceKind, v.Source));
            modifiers[modifier] = (corroborated[0].Key, SourceSummary.Summarize(provenance), bestTier);
        }
        return modifiers;
    }


    /// <summary>
    /// Composes "&lt;Modifier&gt; &lt;Head&gt;" when both halves are known. Both are
    /// stored bare and rejoined with the particle that the mining step confirmed on
    /// every supporting entry, so the seam is reproduced rather than guessed at.
    ///
    /// v0.14.0: when the modifier has no MEANING translation, fall back to its
    /// TRANSLITERATION. Measured on real data, the binding constraint was never the
    /// head — 631 gear candidates had a known head and failed only because the
    /// modifier was unknown — and the modifiers that are missing are overwhelmingly
    /// proper nouns (Akaviri, Aetherial, Dwemer), which want transliterating, not
    /// translating, anyway. "Akaviri Sword" then composes as アカヴィリ + の + 剣,
    /// which is exactly how Skyrim's own localization renders that shape of name.
    ///
    /// The fallback goes through <see cref="CorpusTransliterator.TryTranslateWord"/>,
    /// which answers only for entries the corpus itself attests (official or
    /// sentence-witnessed) — a "derived" slice still cannot supply the modifier, so
    /// the v0.7.1 rule holds through this path too.
    /// </summary>
    /// <param name="usedTransliteration">True when the modifier came from the
    /// transliteration table rather than the meaning table, so the caller can label
    /// the result distinctly and keep the two reviewable apart.</param>
    /// <param name="breakdown">v0.36.0: the head/modifier pieces actually used, for
    /// per-candidate review logging (see PromptGenerator's per-plugin detail log) —
    /// tracing a composed result back to "why" otherwise requires cross-referencing
    /// derived_meaning_dict.tsv/derived_transliteration_dict.tsv and corpus.tsv by
    /// hand, which is exactly what this exists to avoid.</param>
    public bool TryTranslate(string englishText, CorpusTransliterator? transliterator, out string japanese,
        out bool usedTransliteration, out MeaningBreakdown breakdown)
    {
        japanese = "";
        usedTransliteration = false;
        breakdown = default;

        if (!TrySplitHeadModifier(englishText, out var headWord, out var modifierKey)) return false;
        if (!_heads.TryGetValue(headWord, out var head)) return false;

        // v0.38.0: previously this only fell back to the transliteration table
        // when _modifiers had NO entry at all for modifierKey — so a modifier
        // learned entirely from dsd/imported (community) precedent silently won
        // over a vanilla/reference precedent sitting in the transliteration table
        // under the same word, because the code stopped looking the moment ANY
        // answer was found. Real case: "Cloak of Hevnoraak" — _modifiers["Hevnoraak"]
        // resolved from an xTranslator import (SPERG-SSE.esp) to the wrong
        // "ヘブラノーク", while the transliteration table separately held the
        // correct vanilla "Hevnoraak"→"ヘブノラーク" (corpus.tsv, Skyrim.esm) — and
        // was never even consulted. Now both are looked up and compared by
        // SourceTier; the modifier table wins only when it is not backed by a
        // strictly worse tier than the transliteration table's answer.
        var haveModifierEntry = _modifiers.TryGetValue(modifierKey, out var modifierEntry);
        string translitJapanese = "", translitSource = "";
        var translitTier = SourceTier.Of("");
        var haveTranslit = transliterator != null
            && transliterator.TryTranslateWordWithTier(modifierKey, out translitJapanese, out translitSource, out translitTier);

        string modifier, modifierSource;
        var modifierVia = "意味";
        if (haveModifierEntry && (!haveTranslit || modifierEntry.Tier <= translitTier))
        {
            modifier = modifierEntry.Japanese;
            modifierSource = modifierEntry.SourceSummary;
        }
        else if (haveTranslit)
        {
            modifier = translitJapanese;
            modifierSource = translitSource;
            usedTransliteration = true;
            modifierVia = "音訳";
        }
        else
        {
            return false;
        }

        japanese = modifier + Joiner + head.Japanese;
        breakdown = new MeaningBreakdown(modifierKey, modifier, modifierVia, modifierSource, headWord, head.Japanese, head.SourceSummary);
        return true;
    }

    /// <summary>v0.36.0: which modifier/head pieces a composed translation used, and
    /// (for the modifier) whether it came from the meaning table or fell back to
    /// transliteration — see <see cref="TryTranslate"/>. v0.37.0: each side also
    /// carries its <see cref="SourceSummary"/> — which corpus entries (plugin +
    /// vanilla/dsd/imported/reference) corroborated it.</summary>
    public readonly record struct MeaningBreakdown(
        string ModifierWord, string ModifierJapanese, string ModifierVia, string ModifierSource,
        string HeadWord, string HeadJapanese, string HeadSource);

    /// <summary>v0.28.0: single-word head lookup, exposed for
    /// <see cref="NameFallbackTranslator"/> — that class needs the head resolved
    /// on its own (it decomposes the MODIFIER word-by-word, but still requires a
    /// known head; see its remarks for why).</summary>
    public bool TryTranslateHead(string head, out string japanese, out string source)
    {
        if (_heads.TryGetValue(head, out var entry)) { japanese = entry.Japanese; source = entry.SourceSummary; return true; }
        japanese = ""; source = ""; return false;
    }

    /// <summary>Tier-aware counterpart to <see cref="TryTranslateWord"/>, used
    /// internally by <see cref="TryTranslate"/> to compare against the
    /// transliteration table's answer for the same word.</summary>
    internal bool TryTranslateWordWithTier(string word, out string japanese, out string source, out int tier)
    {
        if (_modifiers.TryGetValue(word, out var m)) { japanese = m.Japanese; source = m.SourceSummary; tier = m.Tier; return true; }
        if (_heads.TryGetValue(word, out var h)) { japanese = h.Japanese; source = h.SourceSummary; tier = h.Tier; return true; }
        japanese = ""; source = ""; tier = SourceTier.Of(""); return false;
    }

    /// <summary>v0.28.0: single-word lookup across BOTH lexicons (a word this
    /// table has ever seen as a modifier OR as a head), exposed for
    /// <see cref="NameFallbackTranslator"/>'s per-word modifier decomposition.
    /// Checking modifiers first is not a meaningful distinction here — a word's
    /// role in a name isn't decided by which lexicon happened to record it —
    /// it just needs to try both.</summary>
    public bool TryTranslateWord(string word, out string japanese, out string source)
    {
        if (_modifiers.TryGetValue(word, out var m)) { japanese = m.Japanese; source = m.SourceSummary; return true; }
        if (_heads.TryGetValue(word, out var h)) { japanese = h.Japanese; source = h.SourceSummary; return true; }
        japanese = ""; source = ""; return false;
    }
}
