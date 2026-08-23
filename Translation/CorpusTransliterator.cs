using System.Text;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// A word/phrase-level English→katakana transliteration dictionary mined directly
/// from this load order's own corpus — e.g. "Whiterun"→"ホワイトラン", "Gray-Mane"→
/// "グレイ・メーン", "Urag gro-Shub"→"ウラッグ・グロ・シューブ" — rather than a
/// hand-written phonetic engine (see <see cref="Transliterator"/> for why that
/// approach proved unreliable). Every entry here is a REAL precedent that shipped
/// in Skyrim's actual localization, so it's trustworthy enough to auto-apply,
/// unlike a guessed transliteration.
///
/// Built with an ITERATIVE BOOTSTRAP, "Japanese-first" (this is the design the
/// user proposed after reviewing an earlier, purely positional version):
///
///   Pass 0 (seed): only entries whose Japanese is a SINGLE unsegmented katakana
///   block (no "・" at all) are trusted outright — there's no internal ambiguity
///   to resolve, so the whole English phrase pairs directly with the whole
///   Japanese block.
///
///   Pass N (grow): for every remaining multi-segment entry ("Urag gro-Shub
///   Services" → "ウラッグ・グロ・シューブ・サービス"), search for ONE already-known
///   phrase's katakana value appearing as a literal contiguous run within this
///   entry's "・" segments — e.g. once "Services"→"サービス" is known, it's found
///   at the end, and both the matching English span and Japanese segment are
///   removed. Whatever is left before/after that removed chunk is a smaller,
///   cleaner block; if a block's word count and segment count match exactly and
///   every leftover segment is pure katakana, the whole block resolves in one
///   shot (both word-by-word AND as every contiguous multi-word span within it,
///   so "Urag gro-Shub" is captured as its own reusable unit, not just "Urag"
///   and "gro-Shub" independently). Newly learned pairs feed back into the next
///   pass, so the dictionary grows until a pass makes no further progress.
///
/// This is safer than positional-only matching (the very first version wrongly
/// paired "Acolyte"→"ドラゴン" in "Acolyte Dragon Priest FX"→"ドラゴン・プリースト
/// 侍者FX" because it just zipped English words to Japanese segments by index):
/// resolution here only ever commits when counts EXACTLY match on a given block,
/// same invariant as before, but the known-chunk removal makes many more real
/// entries reach that exact-match state without introducing new guesswork.
/// </summary>
public sealed class CorpusTransliterator
{
    private const int MinPieceLength = 3;
    /// <summary>Cap on the bootstrap's growth passes. The loop already stops at a
    /// fixed point, so this only matters when the dictionary is still growing —
    /// the "thorough" profile raises it so a release run reaches that fixed point
    /// rather than being cut short for the sake of a fast re-run.</summary>
    private static int MaxIterations => TuningProfile.Current.TransliteratorMaxIterations;

    private readonly Dictionary<string, string> _words;

    /// <summary>
    /// Entries whose English→Japanese pair exists VERBATIM in the corpus — i.e.
    /// some record really does carry that whole English string with that whole
    /// Japanese string. Everything else in <see cref="_words"/> was produced by
    /// this class's own 1:1 alignment of a longer phrase against its "・"-separated
    /// Japanese, and is therefore an INFERENCE of ours, not something the official
    /// localization ever asserted.
    ///
    /// The distinction was forced by a real mistranslation (v0.7.1). Skyrim.esm
    /// ships "Shield Charge Knockback" → "シールド・チャージ・ノックダウン". Aligning
    /// 3 English words against 3 katakana segments is structurally correct and
    /// yields "Knockback" → "ノックダウン" — but Bethesda's translator rendered that
    /// last component loosely, so the piece we extracted is wrong even though the
    /// whole phrase is right. The tool then auto-confirmed a candidate literally
    /// named "Knockback" as "ノックダウン" (knockback and knockdown are different
    /// effects in game). The whole phrase is official; the slice out of it is ours.
    ///
    /// Note this is NOT covered by the existing lowercase-usage safety net
    /// (追記6): that net checks whether a word is ever used lowercase in the
    /// corpus, and it removed the CORRECT entry ("Knockdown", which does appear
    /// in the sentence "An area effect knockdown with...") while keeping the
    /// wrong one ("Knockback", never seen lowercase here).
    /// </summary>
    /// <summary>v0.37.0: english -> SourceSummary of every corpus entry that
    /// verbatim-attested this pair (was a HashSet before; now also answers
    /// "which plugin/kind said so"). v0.38.0: also carries the best
    /// <see cref="SourceTier"/> among those attesting entries, so
    /// <see cref="CorpusMeaningTranslator"/> can compare this table's trust
    /// against its own modifier-table entry for the same word.</summary>
    private readonly Dictionary<string, (string SourceSummary, int Tier)> _official;

    /// <summary>
    /// Entries recovered by <see cref="SentenceAlignmentMiner"/> from running text
    /// rather than from name fields — "Bosmer" → "ボズマー", which the name-based
    /// passes could never see because Skyrim names that race "Wood Elf" and uses
    /// "Bosmer" only in prose.
    ///
    /// These ARE allowed to stand alone, unlike <see cref="WordOrigin.Derived"/>
    /// slices, because the miner keeps only pairs the corpus independently
    /// exhibits in an entry of its own (see its KeepVouchedFor). That witness is
    /// the same class of evidence that makes an entry "official" — Skyrim's own
    /// shipped Japanese showing the pair — reached by a different route. The
    /// distinction from official is kept only so the origin stays visible in the
    /// dumped table.
    /// </summary>
    private readonly Dictionary<string, (string SourceSummary, int Tier)> _sentenceAligned;

    private CorpusTransliterator(Dictionary<string, string> words, Dictionary<string, (string SourceSummary, int Tier)> official, Dictionary<string, (string SourceSummary, int Tier)> sentenceAligned)
    {
        _words = words;
        _official = official;
        _sentenceAligned = sentenceAligned;
    }

    /// <param name="skip">v0.52.0a: when true, returns an empty table without
    /// mining the corpus at all — for callers (AutoTranslator's ②③④ all
    /// disabled together, e.g. the GUI's "scan" pass) where nothing will ever
    /// consult this table, so the ~8-9s corpus-wide mining pass on a 128k-entry
    /// corpus is pure waste. See AutoTranslator's constructor remarks.</param>
    public static CorpusTransliterator Build(IReadOnlyList<CorpusEntry> corpus, Core.TraceLog? trace = null, bool skip = false)
    {
        if (skip) return new CorpusTransliterator(new(), new(), new());

        var meaningEvidence = BuildMeaningCounterEvidence(corpus);
        var manualExclusions = LoadManualExclusions();
        trace?.Debug($"Transliteration mining start: corpus {corpus.Count}, meaning-conflict exclusions {meaningEvidence.Count}, manual exclusions {manualExclusions.Count}");
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Commit(string eng, string jpn)
        {
            if (string.IsNullOrEmpty(eng) || string.IsNullOrEmpty(jpn)) return;
            if (meaningEvidence.Contains(eng)) return;
            if (manualExclusions.Contains(eng)) return;
            if (!IsPlainPhrase(eng)) return;
            if (dict.TryAdd(eng, jpn)) trace?.Trace($"Learned: \"{eng}\" -> \"{jpn}\"");
        }

        // Pass 0: unambiguous single-block seeds.
        foreach (var entry in corpus)
        {
            var eng = entry.English.Trim();
            var jpn = entry.Japanese.Trim();
            if (eng.Length < 2 || jpn.Length == 0) continue;
            if (!LooksLikeNameField(eng)) continue;
            if (jpn.Contains('・')) continue;
            if (IsPureKatakana(jpn))
                Commit(eng, jpn);
        }
        trace?.Trace($"Pass 0 (single-katakana-block seeds) done: {dict.Count} entries");

        // Pass "の": Bethesda's other extremely common naming template, "<Head> of
        // <Possessor>" — e.g. "Eye of Magnus" → "マグナスの目". Unlike the "・"
        // pattern (independently transliterated parts glued together), here the
        // Possessor is transliterated but the Head is translated by MEANING and
        // joined with Japanese's own possessive particle "の", not "・" — so this
        // never enters the "・"-based passes above/below at all (found by the user
        // asking why "Magnus" wasn't in the dictionary despite ~24 corpus
        // occurrences). The grammar is reliable in both directions: whatever
        // English comes after "of" is the possessor, and whatever Japanese comes
        // before "の" is the possessor, so pairing them is safe as long as the
        // Japanese prefix is confirmed pure katakana (if it's kanji instead, e.g.
        // "Ring of Nullification" → "無力化の指輪", the Possessor was itself
        // meaning-translated, not transliterated, and this pass correctly leaves
        // it alone).
        foreach (var entry in corpus)
        {
            var eng = entry.English.Trim();
            var jpn = entry.Japanese.Trim();
            if (eng.Length < 2 || jpn.Length == 0) continue;
            if (!LooksLikeNameField(eng)) continue;

            // Require "of" to be exactly the SECOND word ("<Head> of <Possessor>",
            // head is a single word) — found necessary after inspection turned up
            // "Daedric Shield of Nullification" → "デイドラの盾(無力化)": the katakana
            // before "の" is "デイドラ" (Daedric — the actual FIRST word, unrelated to
            // the "of" split point at all, since the real head here is the
            // two-word "Daedric Shield"), which blind pairing incorrectly matched
            // against "Nullification" (whatever follows "of") instead. Every
            // single-word-head case inspected ("Eye of Magnus", "Ring of
            // Hircine", "Staff of Magnus", ...) behaves correctly; only
            // multi-word heads produced this failure mode, so restricting to
            // single-word heads avoids it entirely rather than trying to guess.
            var tokens = eng.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3 || !tokens[1].Equals("of", StringComparison.OrdinalIgnoreCase)) continue;
            var possessorEng = string.Join(' ', tokens.Skip(2));

            var noIndex = jpn.IndexOf('の');
            if (noIndex <= 0) continue;
            var possessorJpn = jpn[..noIndex];

            ResolveBlock(Tokenize(possessorEng), possessorJpn.Split('・').ToList(), Commit);
        }
        trace?.Trace($"Pass 'no' ('X of Y' shape) done: cumulative {dict.Count} entries");

        // Iterative growth passes.
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var before = dict.Count;

            foreach (var entry in corpus)
            {
                var eng = entry.English.Trim();
                var jpn = entry.Japanese.Trim();
                if (eng.Length < 2 || jpn.Length == 0 || !jpn.Contains('・')) continue;
                if (!LooksLikeNameField(eng)) continue;
                if (dict.ContainsKey(eng)) continue; // already fully resolved as a whole phrase

                var fine = Tokenize(eng);
                var segments = jpn.Split('・').ToList();
                if (fine.Count == 0 || segments.Count == 0) continue;

                // Try to find ONE already-known phrase inside this entry (longest span first).
                var removed = false;
                for (var len = fine.Count; len >= 1 && !removed; len--)
                {
                    for (var start = 0; start + len <= fine.Count && !removed; start++)
                    {
                        var span = Reconstruct(fine, start, start + len);
                        if (!dict.TryGetValue(span, out var knownKatakana)) continue;

                        var knownParts = knownKatakana.Split('・');
                        var foundAt = FindSubsequence(segments, knownParts);
                        if (foundAt < 0) continue;

                        ResolveBlock(fine.GetRange(0, start), segments.GetRange(0, foundAt), Commit);
                        ResolveBlock(
                            fine.GetRange(start + len, fine.Count - start - len),
                            segments.GetRange(foundAt + knownParts.Length, segments.Count - foundAt - knownParts.Length),
                            Commit);
                        removed = true;
                    }
                }

                if (!removed)
                    ResolveBlock(fine, segments, Commit); // no known chunk found — resolve the whole entry at once, or not at all
            }

            trace?.Trace($"Growth pass #{iteration + 1} done: {before} -> {dict.Count} entries");
            if (dict.Count == before) break; // fixed point
        }

        var beforePrune = dict.Count;
        RemoveUnderSupportedAtomicWords(dict, corpus);
        trace?.Debug($"Removed under-supported atomic-word entries: {beforePrune} -> {dict.Count} entries");

        var official = BuildOfficialSet(dict, corpus);
        trace?.Debug($"Official (verbatim corpus match) entries: {official.Count}");

        // Sentence-aligned entries are added LAST and only where nothing is
        // already known, so they can never displace a name-field mapping.
        var sentenceAligned = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (english, mined) in SentenceAlignmentMiner.Mine(corpus))
        {
            if (dict.ContainsKey(english)) continue;
            if (meaningEvidence.Contains(english)) continue;
            if (manualExclusions.Contains(english)) continue;
            dict[english] = mined.Katakana;
            sentenceAligned[english] = (mined.SourceSummary, mined.Tier);
        }
        trace?.Debug($"Sentence-alignment mined entries: {sentenceAligned.Count} / transliteration dictionary final total {dict.Count}");

        return new CorpusTransliterator(dict, official, sentenceAligned);
    }

    /// <summary>Marks the entries the corpus itself vouches for: the exact
    /// English string, carrying the exact Japanese string, on some real record.
    /// Checked against the corpus rather than tracked through the mining passes,
    /// because that is precisely the property being claimed — "the official data
    /// says this pair exists" — and checking it directly cannot drift from the
    /// passes' internals. v0.37.0: keeps EVERY attesting entry's (SourceKind,
    /// Source), not just whether one exists, summarized via <see cref="SourceSummary"/>.</summary>
    private static Dictionary<string, (string SourceSummary, int Tier)> BuildOfficialSet(Dictionary<string, string> dict, IReadOnlyList<CorpusEntry> corpus)
    {
        var officialPairs = new Dictionary<(string, string), List<(string SourceKind, string Source)>>();
        foreach (var entry in corpus)
        {
            var key = (entry.English.Trim(), entry.Japanese.Trim());
            if (!officialPairs.TryGetValue(key, out var list)) officialPairs[key] = list = new();
            list.Add((entry.SourceKind, entry.Source));
        }

        var official = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (eng, jpn) in dict)
            if (officialPairs.TryGetValue((eng, jpn), out var provenance))
                official[eng] = (SourceSummary.Summarize(provenance), SourceTier.OfProvenance(provenance));

        return official;
    }

    /// <summary>
    /// A single ATOMIC word (no space, no hyphen — the finest, most ambiguous
    /// grain) that happens to have an ordinary-English meaning is exactly the
    /// shape of bug that produced both "Eye"→"アイ" (from a shout name and a
    /// nickname surname; the word is really "目" 22 times out of 25) and
    /// "Face"→"フェイス" (from a single surname "Skaggi Scar-Face"; found by the
    /// user asking "aren't overly-simple words like this a risk?"). The
    /// "of"-pattern counter-evidence check only catches mismatches in that one
    /// specific template, so this is a general fix — but the first attempt at a
    /// general fix (require an atomic word to be corroborated by ≥2 independent
    /// corpus entries) was too blunt: it also discarded genuinely rare-but-real
    /// proper nouns, which is the NORM for unique NPC first names (most named
    /// characters are mentioned exactly once), taking the dictionary from
    /// 2,247 words down to 849.
    ///
    /// The actual distinguishing signal is not frequency, it's whether the word
    /// is ever used as ordinary vocabulary: "eye" and "face" both appear in
    /// plain LOWERCASE elsewhere in this same corpus (e.g. "What dangers does
    /// the caravan face?", "keep an eye out" -type dialogue), which a real
    /// proper noun never does — a genuine name like "Adonato" never appears
    /// lowercased anywhere, because it's never used as a common word. So:
    /// exclude an atomic word if it's ever seen written in lowercase (its first
    /// letter not capitalized) ANYWHERE in the corpus — including places
    /// outside the name-field-filtered mining input, since detecting the
    /// lowercase usage doesn't require that occurrence to itself be a name.
    /// Multi-word phrase keys and hyphenated compound keys (e.g. "Gray-Mane",
    /// "Urag gro-Shub") are exempt — the compound structure itself is already
    /// much stronger evidence of being a genuine proper noun.
    /// </summary>
    private static void RemoveUnderSupportedAtomicWords(Dictionary<string, string> dict, IReadOnlyList<CorpusEntry> corpus)
    {
        var seenLowercase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in corpus)
        {
            foreach (var token in entry.English.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length > 0 && char.IsLower(token[0]))
                    seenLowercase.Add(token);
            }
        }

        var toRemove = dict.Keys
            .Where(key => !key.Contains(' ') && !key.Contains('-')) // phrase/compound keys are exempt
            .Where(key => seenLowercase.Contains(key))
            .ToList();
        foreach (var key in toRemove) dict.Remove(key);
    }

    /// <summary>A block resolves only when its word count exactly matches its
    /// segment count and every segment is pure katakana — full block or nothing,
    /// no partial credit (kept deliberately simple/conservative). On success,
    /// records every fine-token AND every contiguous multi-token span within the
    /// block, so both "Urag" and "Urag gro-Shub" become independently reusable.</summary>
    private static void ResolveBlock(List<FineToken> fine, List<string> segments, Action<string, string> commit)
    {
        if (fine.Count == 0 || fine.Count != segments.Count) return;
        if (!segments.All(IsPureKatakana)) return;
        if (!fine.All(f => f.Text.Length >= 2 && f.Text.All(char.IsAsciiLetter))) return;

        for (var i = 0; i < fine.Count; i++)
            commit(fine[i].Text, segments[i]);

        for (var start = 0; start < fine.Count; start++)
            for (var end = start + 2; end <= fine.Count; end++)
                commit(Reconstruct(fine, start, end), string.Join('・', segments.GetRange(start, end - start)));
    }

    /// <summary>
    /// Mining only ever looks at katakana-rendered entries, so a word that is
    /// almost always translated by MEANING is structurally invisible to it except
    /// on the rare occasion it happens to get swept up in a stylized, fully
    /// transliterated name — and that one occurrence then looks, to the miner,
    /// like the word's only precedent. Real example found by inspection: "Eye"
    /// appears 22 times in the corpus translated by meaning ("目", e.g. "Eye of
    /// Magnus" → "マグナスの目") and only 3 times transliterated ("アイ", from
    /// stylized outliers like a shout name and a nickname-style surname
    /// "Stone-Eye"). This builds a set of English head-words with DIRECT
    /// counter-evidence of being meaning-translated, using Bethesda's extremely
    /// common "&lt;word&gt; of &lt;something&gt;" naming template as the detection
    /// signal — if such an entry's Japanese isn't pure katakana, the head word is
    /// excluded from the transliteration dictionary even if mining also found a
    /// katakana instance of it elsewhere. Targeted, not exhaustive: it only
    /// catches the "of" pattern specifically (the pattern that caused the bug).
    /// </summary>
    private static HashSet<string> BuildMeaningCounterEvidence(IReadOnlyList<CorpusEntry> corpus)
    {
        var evidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in corpus)
        {
            var eng = entry.English.Trim();
            var jpn = entry.Japanese.Trim();
            if (jpn.Length == 0) continue;

            var tokens = eng.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (tokens[0].Length < 2 || !tokens[0].All(char.IsAsciiLetter) || !char.IsUpper(tokens[0][0])) continue;
            if (!tokens[1].Equals("of", StringComparison.OrdinalIgnoreCase)) continue;

            if (!IsPureKatakana(jpn.Replace("・", "")))
                evidence.Add(tokens[0]);
        }
        return evidence;
    }

    /// <summary>
    /// Manual, human-curated exclusion list (<c>Data/transliteration_exclusions.txt</c>)
    /// for entries that pass every automated safety check yet are still wrong to
    /// generalize — e.g. "Fireball"→"エクスプロージョン" is the REAL, official vanilla
    /// Skyrim.esm localization for that one spell (verified: the in-game spell
    /// tome literally reads "エクスプロージョンの巻物"), so no automated check flags
    /// it as an error. But it's a one-off quirk specific to that single record,
    /// not a general English→katakana rule, and applying it to some unrelated
    /// future candidate containing "Fireball" would produce a confusingly wrong
    /// result. Automated heuristics can't distinguish "surprising but correct
    /// precedent" from "surprising and should not generalize" — that's a human
    /// judgment call, so this file exists for exactly those cases as they're
    /// found. One word/phrase per line, "#" starts a comment line.
    /// </summary>
    private static HashSet<string> LoadManualExclusions()
    {
        var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "transliteration_exclusions.txt");
        if (!File.Exists(path)) return exclusions;

        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            exclusions.Add(trimmed);
        }
        return exclusions;
    }

    /// <summary>Exact whole-word/phrase lookup (case-insensitive). Answers ONLY
    /// from officially-attested pairs (see <see cref="_official"/>): standing
    /// alone as the entire translation of a candidate is exactly the position a
    /// self-derived slice must not occupy.</summary>
    public bool TryTranslateWord(string word, out string katakana, out string source)
    {
        katakana = "";
        source = "";
        if (!StandsAlone(word)) return false;
        if (!_words.TryGetValue(word, out katakana!)) return false;
        source = SourceOf(word);
        return true;
    }

    /// <summary>v0.38.0: same lookup as <see cref="TryTranslateWord"/>, but also
    /// returns the entry's <see cref="SourceTier"/> — used internally by
    /// <see cref="CorpusMeaningTranslator"/> to decide whether ITS OWN
    /// modifier-table entry for the same word should be trusted over this one, or
    /// the reverse (see that class's TryTranslate).</summary>
    internal bool TryTranslateWordWithTier(string word, out string katakana, out string source, out int tier)
    {
        katakana = "";
        source = "";
        tier = SourceTier.Of("");
        if (!StandsAlone(word)) return false;
        if (!_words.TryGetValue(word, out katakana!)) return false;
        source = SourceOf(word);
        tier = TierOf(word);
        return true;
    }

    /// <summary>
    /// v0.47.0: the same lookup as <see cref="TryTranslateWord"/>, but WITHOUT
    /// the <see cref="StandsAlone"/> restriction — for hint/display purposes
    /// only, never for auto-applying a translation. A "derived" entry (sliced
    /// out of a longer compound by this class's own bootstrap, e.g. "Nirn" out
    /// of "Nirnroot") is a real, computed rendering; it's just not trustworthy
    /// enough to stand alone as a CANDIDATE'S WHOLE translation (that's what
    /// <see cref="StandsAlone"/> guards against — see the class remarks' v0.7.1
    /// story). That restriction doesn't apply to merely SHOWING it as one
    /// data point alongside others for a human/AI-chat/local-LLM to weigh.
    ///
    /// Motivating case: "Nirn" (the planet) never appears as its own corpus/
    /// reference row — every occurrence is embedded in running prose or inside
    /// the unrelated compound "Nirnroot" — so it's "derived"-only and
    /// <see cref="TryTranslateWord"/> refuses it. Yet the bootstrap already
    /// mined "Nirn"→"ニルン" correctly; <see cref="PromptGenerator"/>'s per-
    /// candidate word hints (unlike the auto-resolution pipeline) have no
    /// "whole translation" to protect, so they can use it.
    /// </summary>
    public bool TryLookupWordForHint(string word, out string katakana, out string source)
    {
        katakana = "";
        source = "";
        if (!_words.TryGetValue(word, out katakana!)) return false;
        source = SourceOf(word);
        if (source.Length == 0) source = OriginOf(word); // "derived": no attesting entry, only the origin tag
        return true;
    }

    /// <summary>The full resolved word/phrase list, for dumping to a review file.
    /// Includes derived entries, flagged as such, since reviewing exactly those is
    /// the point of the dump. v0.37.0: also carries Source (blank for "derived" —
    /// a sliced-out piece has no single attesting entry of its own; see SourceOf).</summary>
    public IEnumerable<(string English, string Japanese, string Origin, string Source)> AllWords =>
        _words.Select(kv => (kv.Key, kv.Value, OriginOf(kv.Key), SourceOf(kv.Key)));

    /// <summary>May this entry BE a candidate's whole translation, rather than only
    /// a piece of a composition? Yes when the corpus itself exhibits the pair —
    /// verbatim on a record (official) or in an entry that witnesses it (sentence).
    /// No for a slice this tool cut out of a longer name (derived), which is the
    /// v0.7.1 rule that stopped "Knockback" → "ノックダウン".</summary>
    private bool StandsAlone(string word) =>
        _official.ContainsKey(word) || _sentenceAligned.ContainsKey(word);

    private string OriginOf(string english) =>
        _official.ContainsKey(english) ? "official"
        : _sentenceAligned.ContainsKey(english) ? "sentence"
        : "derived";

    /// <summary>v0.37.0: which corpus entries actually attest this word/phrase —
    /// see <see cref="BuildOfficialSet"/>/<see cref="SentenceAlignmentMiner"/>.
    /// Blank for a "derived" slice: it was cut out of some longer entry's Japanese
    /// by this class's own alignment logic (see the class remarks' iterative
    /// bootstrap), not attested on its own by any single corpus entry — tracing
    /// its "source" precisely would mean recording the whole derivation chain,
    /// which the log's Detail string reports separately as "音訳" already.</summary>
    private string SourceOf(string english) =>
        _official.TryGetValue(english, out var official) ? official.SourceSummary
        : _sentenceAligned.TryGetValue(english, out var sentence) ? sentence.SourceSummary
        : "";

    /// <summary>v0.38.0: the best <see cref="SourceTier"/> backing this word/phrase —
    /// counterpart to <see cref="SourceOf"/>. A "derived" slice (no direct
    /// attestation at all) is treated as the lowest tier, same as community data.</summary>
    private int TierOf(string english) =>
        _official.TryGetValue(english, out var official) ? official.Tier
        : _sentenceAligned.TryGetValue(english, out var sentence) ? sentence.Tier
        : SourceTier.Of("");

    /// <summary>Greedy longest-match word-break: can this single unspaced word be
    /// fully covered end-to-end by known transliterated pieces? Returns the
    /// concatenated katakana (no separator — it's one compound word) or null if
    /// any part of the word can't be accounted for. Never guesses at leftover
    /// characters, so a failed decomposition simply falls through to the AI pass.
    ///
    /// v0.7.1: a DERIVED entry may serve as a PIECE of a composition but can never
    /// cover the whole word by itself — covering the whole word alone is not
    /// "composing an unseen word out of known parts", it is just asserting our own
    /// inference as the answer, which is what produced "Knockback"→"ノックダウン".
    /// Real compositions (two or more pieces) are unaffected.</summary>
    /// <param name="pieces">v0.36.0: the actual (English piece, katakana) chain used,
    /// for per-candidate review logging — a composed word like "Frostmere" otherwise
    /// shows only the final katakana, with no way to tell which known fragments it
    /// was built from without re-running this same search by hand. v0.37.0: each
    /// piece also carries its Source (see <see cref="SourceOf"/> — blank for a
    /// "derived" piece, which has no single attesting entry).</param>
    public string? TryDecompose(string word, out List<(string Piece, string Kana, string Source)> pieces)
    {
        pieces = new List<(string, string, string)>();
        if (StandsAlone(word) && _words.TryGetValue(word, out var whole))
        {
            pieces.Add((word, whole, SourceOf(word)));
            return whole;
        }

        var n = word.Length;
        var memo = new string?[n + 1];
        var chosen = new (string Piece, string Kana)?[n + 1];
        memo[n] = "";
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = n; j >= i + MinPieceLength; j--)
            {
                if (memo[j] == null) continue;
                if (i == 0 && j == n) continue; // whole-word cover: handled above, official only
                var piece = word.Substring(i, j - i);
                if (_words.TryGetValue(piece, out var kana))
                {
                    memo[i] = kana + memo[j];
                    chosen[i] = (piece, kana);
                    break; // greedy longest-match first
                }
            }
        }

        if (memo[0] == null) return null;
        for (var i = 0; i >= 0 && chosen[i] != null;)
        {
            var (piece, kana) = chosen[i]!.Value;
            pieces.Add((piece, kana, SourceOf(piece)));
            i += piece.Length;
        }
        return memo[0];
    }

    /// <summary>One hyphen- or space-separated piece of an English phrase, plus
    /// the separator that preceded it (used to faithfully reconstruct spans,
    /// e.g. "Gray-Mane" keeps its hyphen, "Urag gro-Shub" keeps its space).</summary>
    private readonly record struct FineToken(string Text, string JoinerBefore);

    private static List<FineToken> Tokenize(string eng)
    {
        var result = new List<FineToken>();
        var spaceTokens = eng.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var spaceToken in spaceTokens)
        {
            var hyphenParts = spaceToken.Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var h = 0; h < hyphenParts.Length; h++)
            {
                var joiner = result.Count == 0 ? "" : h == 0 ? " " : "-";
                result.Add(new FineToken(hyphenParts[h], joiner));
            }
        }
        return result;
    }

    private static string Reconstruct(List<FineToken> fine, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            if (i > start) sb.Append(fine[i].JoinerBefore);
            sb.Append(fine[i].Text);
        }
        return sb.ToString();
    }

    private static int FindSubsequence(List<string> haystack, string[] needle)
    {
        if (needle.Length == 0) return -1;
        for (var i = 0; i + needle.Length <= haystack.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>A dictionary KEY must be plain words separated only by spaces/
    /// hyphens (no digits, punctuation, etc.) — guards against noisy corpus text
    /// (quest debug names, "DA16", "FX" tags) ending up as dictionary entries.</summary>
    private static bool IsPlainPhrase(string s) =>
        s.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .All(part => part.Length >= 2 && part.All(char.IsAsciiLetter));

    /// <summary>
    /// Gates which corpus entries are even eligible as mining INPUT (separate
    /// from <see cref="IsPlainPhrase"/>, which gates dictionary KEYS). The
    /// corpus is built from every record's Mutagen "Name" property regardless of
    /// record type, and for some types (FACT — Faction — in particular) that
    /// property doesn't hold a player-visible title at all: it holds an internal
    /// developer note, e.g. "used for combat", "put on doors to keep NPCs from
    /// opening", "anyone in this won't fight with mq101 alduin". 13,492 of this
    /// load order's ~29,587 corpus entries turned out to be exactly this kind of
    /// sentence-like, not-a-name text (found by checking capitalization, per the
    /// user's suggestion) — none of it ends up as an actual translation
    /// candidate (FACT isn't in <c>RecordSignatureMap.DsdFullNameSupported</c>),
    /// but it was silently available as mining input and as RAG precedent
    /// material. A genuine Bethesda name field is Title Case throughout by
    /// convention, so requiring every space-separated word to contain an
    /// uppercase letter SOMEWHERE is a simple, effective filter for it —
    /// checking specifically the FIRST letter would have been simpler but is
    /// wrong: some real in-game names have an intentionally-lowercase
    /// component, e.g. Orc patronymic names like "Urag gro-Shub" (the "gro-"
    /// prefix, meaning "son of", is always lowercase even in the official Title
    /// Case name field) — "gro-Shub" still contains an uppercase letter (the
    /// "S" in "Shub"), just not at position 0, so checking anywhere in the word
    /// is what correctly keeps this case while still rejecting fully-lowercase
    /// sentence fragments like "for"/"not"/"using" in "Faction for not using
    /// casual idles".
    /// </summary>
    // Moved to Core.NameFieldFilter so PrecedentRetriever can apply the identical
    // check to its reference-example index (see DESIGN_NOTES.md item 6). Without
    // its "of"/"the"/etc. exemption, LooksLikeNameField would reject every single
    // "X of Y" name outright (the word "of" has no uppercase letter at all),
    // which would silently break the "の"-pattern extraction pass below.
    private static bool LooksLikeNameField(string eng) => NameFieldFilter.LooksLikeNameField(eng);

    private static bool IsPureKatakana(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (c is 'ー' or '・') continue;
            if (c is < '゠' or > 'ヿ') return false;
        }
        return true;
    }
}
