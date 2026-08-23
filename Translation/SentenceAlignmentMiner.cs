using System.Text.RegularExpressions;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Recovers English→katakana pairs that appear ONLY inside sentences, by
/// statistical alignment across the whole corpus.
///
/// The gap this fills: <see cref="CorpusTransliterator"/> mines only short,
/// name-shaped entries, so a word that Skyrim never uses as a NAME is invisible
/// to it no matter how common it is in prose. "Bosmer" is the case that exposed
/// this — it occurs 84 times in the corpus, always inside descriptions ("Your
/// Bosmer blood gives you..."), never as a record's name (the playable race is
/// named "Wood Elf"), so it had no entry at all.
///
/// How the alignment is decided — this is the part that matters. An earlier
/// attempt paired words by ELIMINATION: filter the English down to one
/// proper-noun-looking candidate, filter the Japanese down to one katakana run,
/// and pair whatever survived. That fails whenever the true partner is the thing
/// that got filtered out — it produced "Dual" → "ダメージ" (because "Damage" was
/// filtered as a common word) and "Pawned" → "プローン" (because "Prawn" was).
/// Elimination has no way to know which word corresponds; it only knows which
/// ones it did not rule out.
///
/// This instead measures CO-OCCURRENCE across the entire corpus, the standard
/// statistical word-alignment idea: "pen" pairs with "ペン" because ペン shows up
/// in the Japanese exactly when pen shows up in the English, and not otherwise.
/// Scored with the Dice coefficient, 2·co(w,k) / (count(w) + count(k)). On real
/// data it picks the right partner in exactly the cases elimination got wrong:
/// ダメージ→damage (0.89, over points 0.41), プローン→prawn (0.97, over pawned
/// 0.95), ボズマー→bosmer (1.00). It needs neither capitalization nor a
/// dictionary, which is what lets it recover ordinary nouns (damage→ダメージ,
/// quill→ペン, boots→ブーツ) that a proper-noun heuristic would discard — and
/// those are the entries the name-based miner could never have found.
///
/// Three further filters cut the residual noise. Every threshold was CALIBRATED
/// against the 1,516 pairs already known to be correct (the "official" entries),
/// not guessed, and each is set to keep 90–95% of them:
///
///   - initial-sound agreement (93.4% of known-good pairs satisfy it)
///   - katakana-to-letters length ratio within 0.45–1.15 (known-good: 5th
///     percentile 0.50, median 0.73, 95th 1.00)
///   - similarity ≥0.20 against <see cref="Transliterator"/>'s phonetic guess
///     (known-good: 5th percentile 0.20, median 0.67)
///
/// The last one re-uses an engine previously judged too weak to USE — its output
/// for "Whiterun" is "ホイテルン", not "ホワイトラン". That makes it unusable for
/// generating a translation but perfectly adequate for DISCRIMINATING between
/// candidates, which is a far easier question: ホワイトラン is obviously closer to
/// it than ダメージ is. Together the filters took a hand-checked sample from
/// roughly 10% wrong down to about 3%.
///
/// Entries are still recorded as <see cref="WordOrigin.Sentence"/> and, like
/// <see cref="WordOrigin.Derived"/>, are never allowed to stand alone as a whole
/// candidate's translation — the v0.7.1 rule.
/// </summary>
public static class SentenceAlignmentMiner
{
    private static readonly Regex KatakanaRun = new(@"[゠-ヿ][゠-ヿ・ー]*", RegexOptions.Compiled);
    private static readonly Regex EnglishWord = new(@"[A-Za-z][A-Za-z']*", RegexOptions.Compiled);

    // Recall knobs — how much text is examined and how weak a signal is still
    // worth considering. Widened by the "thorough" profile (see TuningProfile);
    // the precision checks below and the corpus-witness verification are NOT,
    // so a wider net produces more things to verify, not more things trusted.
    private static int MinCooccurrence => TuningProfile.Current.SentenceMinCooccurrence;
    private static double MinDice => TuningProfile.Current.SentenceMinDice;
    private static int MaxEnglishLength => TuningProfile.Current.SentenceMaxEnglishLength;
    private static int MaxWordsPerEntry => TuningProfile.Current.SentenceMaxWordsPerEntry;
    private static int MaxRunsPerEntry => TuningProfile.Current.SentenceMaxRunsPerEntry;

    // Precision checks — calibrated against known-good pairs, identical in both
    // profiles. Loosening these would change what counts as correct, which is not
    // what "spend more time" should mean.
    private const double MinLengthRatio = 0.45;
    private const double MaxLengthRatio = 1.15;
    private const double MinPhoneticSimilarity = 0.20;

    /// <summary>
    /// Keeps only the mined pairs the corpus independently VOUCHES FOR: some entry
    /// whose English contains the word and whose Japanese contains the katakana,
    /// where that entry's Japanese carries at most <see cref="MaxRunsForWitness"/>
    /// katakana runs so the correspondence is not diluted across many candidates.
    ///
    /// This is evidence of a DIFFERENT kind from the statistic that produced the
    /// pair — co-occurrence across thousands of entries versus one entry that
    /// plainly exhibits the pair — which is what makes it a real check rather than
    /// a restatement. And the corpus is the right authority to appeal to: it is
    /// built from Skyrim's own shipped Japanese, so a pair it exhibits is correct
    /// by construction.
    ///
    /// It also corrected the author of this code. Reviewing the mined output, the
    /// dragon-language shout words looked wrong — "Fus" → "ファス" seemed like it
    /// ought to be "フス" — and the type they came from (WOOP FULL) has no Japanese
    /// counterpart in the corpus, which appeared to confirm the suspicion. The
    /// corpus says otherwise: it ships "Fus..." → "ファス…" and "Fus!" → "ファス！"
    /// outright, because the words appear in spoken dialogue even though the WOOP
    /// records themselves are untranslated. Every one of the supposedly-bad
    /// entries (Dov, Lok, Gol, Klo, Drem, Nuz) turned out to be attested the same
    /// way. Checking beat judgement.
    ///
    /// Measured on real data at this threshold: 476 of 522 pairs are vouched for,
    /// including every case worth keeping (bosmer, fus, akaviri, altmer), while
    /// the one known-bad pair ("ulse" → "グロ", a fragment of an orc name) is
    /// rejected. The rest of the rejects are dragon-language function words
    /// (fen, los, nau, hin) that are not nouns and should not be in a glossary.
    /// </summary>
    private const int MaxRunsForWitness = 2;
    private const int MaxWitnessEnglishLength = 150;

    /// <summary>v0.37.0: also returns a <see cref="SourceSummary"/> built from
    /// every witnessing corpus entry (not just proof that SOME witness exists) —
    /// so a "sentence"-origin word's log entry can say e.g. which plugin's
    /// dialogue actually attested "claymore"↔"クレイモア", the same traceability
    /// <see cref="CorpusMeaningTranslator"/> gained for its head/modifier tables.
    /// v0.38.0: also returns the best <see cref="SourceTier"/> among the
    /// witnesses, so a sentence-mined word backed only by e.g. an xTranslator
    /// import can be told apart from one a vanilla/reference witness confirms.</summary>
    private static Dictionary<string, (string Katakana, string SourceSummary, int Tier)> KeepVouchedFor(
        Dictionary<string, string> mined, IReadOnlyList<CorpusEntry> corpus)
    {
        var witnesses = corpus
            .Where(e => e.English.Length is > 0 and <= MaxWitnessEnglishLength)
            .Select(e => (e.English, e.Japanese, e.Source, e.SourceKind, Runs: KatakanaRun.Matches(e.Japanese).Count))
            .Where(e => e.Runs is > 0 and <= MaxRunsForWitness)
            .ToList();

        var vouched = new Dictionary<string, (string, string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (english, katakana) in mined)
        {
            var wordBoundary = new Regex($@"(^|[^A-Za-z]){Regex.Escape(english)}([^A-Za-z]|$)", RegexOptions.IgnoreCase);
            var matches = witnesses
                .Where(w => w.Japanese.Contains(katakana, StringComparison.Ordinal) && wordBoundary.IsMatch(w.English))
                .ToList();
            if (matches.Count > 0)
            {
                var provenance = matches.Select(w => (w.SourceKind, w.Source)).ToList();
                vouched[english] = (katakana, SourceSummary.Summarize(provenance), SourceTier.OfProvenance(provenance));
            }
        }
        return vouched;
    }

    public static Dictionary<string, (string Katakana, string SourceSummary, int Tier)> Mine(IReadOnlyList<CorpusEntry> corpus)
    {
        var wordCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var runCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var together = new Dictionary<(string Word, string Run), int>();

        foreach (var entry in corpus)
        {
            var english = entry.English;
            if (english.Length == 0 || english.Length > MaxEnglishLength) continue;

            var words = EnglishWord.Matches(english).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
            var runs = KatakanaRun.Matches(entry.Japanese).Select(m => m.Value).Where(r => r.Length >= 2).Distinct().ToList();
            if (words.Count is 0 || runs.Count is 0) continue;
            if (words.Count > MaxWordsPerEntry || runs.Count > MaxRunsPerEntry) continue;

            foreach (var word in words) wordCount[word] = wordCount.GetValueOrDefault(word) + 1;
            foreach (var run in runs) runCount[run] = runCount.GetValueOrDefault(run) + 1;
            foreach (var word in words)
                foreach (var run in runs)
                    together[(word, run)] = together.GetValueOrDefault((word, run)) + 1;
        }

        // One winner per katakana run: the English word it co-occurs with most
        // distinctively. Taking the best per RUN (rather than per word) keeps the
        // result a function of the Japanese side, which is the side we are trying
        // to explain.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in together.Where(t => t.Value >= MinCooccurrence).GroupBy(t => t.Key.Run))
        {
            var winner = group
                .Select(t => (t.Key.Word, Dice: 2.0 * t.Value / (wordCount[t.Key.Word] + runCount[t.Key.Run])))
                .OrderByDescending(x => x.Dice)
                .First();

            if (winner.Dice < MinDice) continue;
            if (!Plausible(winner.Word, group.Key)) continue;

            result.TryAdd(winner.Word, group.Key);
        }
        return KeepVouchedFor(result, corpus);
    }

    private static bool Plausible(string english, string katakana)
    {
        if (!InitialSoundAgrees(english, katakana)) return false;

        var letters = english.Count(char.IsAsciiLetter);
        if (letters == 0) return false;
        var ratio = (double)katakana.Length / letters;
        if (ratio < MinLengthRatio || ratio > MaxLengthRatio) return false;

        var guess = Transliterator.TransliterateName(english);
        if (string.IsNullOrEmpty(guess)) return false;
        return Similarity(guess, katakana) >= MinPhoneticSimilarity;
    }

    /// <summary>Katakana a given English initial can plausibly begin with. Broad
    /// on purpose — it only has to reject gross mismatches ("scripted" against
    /// "リフテンスクリプトシーン"), not adjudicate fine phonetics.</summary>
    private static readonly Dictionary<char, string> InitialSounds = new()
    {
        ['A'] = "アエオ", ['B'] = "バビブベボヴ", ['C'] = "カキクケコサシスセソチシャ", ['D'] = "ダヂヅデド",
        ['E'] = "エイア", ['F'] = "ファフィフフェフォ", ['G'] = "ガギグゲゴジ", ['H'] = "ハヒフヘホ",
        ['I'] = "イアエ", ['J'] = "ジャジジュジェジョヤ", ['K'] = "カキクケコ", ['L'] = "ラリルレロ",
        ['M'] = "マミムメモ", ['N'] = "ナニヌネノ", ['O'] = "オア", ['P'] = "パピプペポ",
        ['Q'] = "クキ", ['R'] = "ラリルレロ", ['S'] = "サシスセソシャシュショ", ['T'] = "タチツテトティ",
        ['U'] = "ウアオ", ['V'] = "ヴバビブベボ", ['W'] = "ワウヲヴ", ['X'] = "ザエ",
        ['Y'] = "ヤユヨイ", ['Z'] = "ザジズゼゾ",
    };

    private static bool InitialSoundAgrees(string english, string katakana)
    {
        if (english.Length == 0 || katakana.Length == 0) return false;
        return InitialSounds.TryGetValue(char.ToUpperInvariant(english[0]), out var allowed)
               && allowed.Contains(katakana[0]);
    }

    private static double Similarity(string a, string b)
    {
        var distance = EditDistance(a, b);
        var longest = Math.Max(a.Length, b.Length);
        return longest == 0 ? 0 : 1.0 - (double)distance / longest;
    }

    private static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                   d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
