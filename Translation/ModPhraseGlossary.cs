using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// 2026-09-17: a per-MOD PHRASE glossary — <c>&lt;plugin's Translation output
/// folder&gt;/mod_glossary.tsv</c>, one file per plugin, feeding into issue
/// #4's "c" (same-mod hint) pool for ⑤ローカルLLM/⑥生成AI翻訳. The ⑤/⑥-facing
/// sibling of <see cref="ModGlossary"/> (Data/mod_glossary/*.tsv, ④固有名詞
/// 組み立て専用) — deliberately a SEPARATE file/mechanism, not a repurposing
/// of that one, since ④ is being deprecated and this needs to outlive it.
///
/// **Why phrases, not words.** ④'s ModGlossary is word-level because it
/// releases combinatorial name chains ("a series name × a body slot × a
/// variant") by deciding one word at a time. This file targets a different
/// failure mode entirely: a MOD-specific multi-word COMPOUND (e.g. "Light
/// Greatsword" — a weapon-speed-class term this MOD invented) that the LLM
/// mistranslates not because any single word is unknown (④/②/③ resolve
/// "Light" and "Greatsword" both just fine on their own) but because the
/// COMBINATION carries a domain-specific meaning no per-word resolution can
/// recover. A per-word check would therefore MISS exactly the case this
/// exists to catch (see <see cref="DetectCandidatePhrases"/>'s remarks).
///
/// **Detection (validated against 4 real mods — Light Greatswords.esp,
/// [Caenarvon] Cosplay Pack Gala.esp, Nodachi.esp, Soul Hunter Armor.esp —
/// recorded in the management repo's todo/active.md, 2026-09-17):**
/// a classic "keyword distinctive to one document" TF-IDF application (NOT
/// cosine similarity between two texts — there is no second text to compare
/// against here, just a frequency-based score per candidate n-gram):
/// <c>score = localCount × idf</c>, where <c>localCount</c> is how many times
/// an n-gram (1-5 words) appears within this ONE mod's own candidates, and
/// <c>idf</c> penalizes an n-gram that is equally common across the WHOLE
/// load order (so a generic filler combination like "of the" scores low even
/// if it happens to recur locally too). The top-ranked survivors are then
/// checked as a WHOLE PHRASE (not decomposed word-by-word — see above) against
/// <see cref="AutoTranslator.TryTranslate"/>; a phrase already resolved there
/// (e.g. "Soul Cairn", genuinely vanilla despite recurring often in one mod)
/// is dropped even though the frequency ranking alone can't tell it apart
/// from a genuine gap.
///
/// **Noise filtering (2026-09-17 real-data finding).** Plain function words
/// ("the", "a", "of", "its", "once"...) recur across a mod's own candidates
/// just like real content, so the TF-IDF ranking alone let them through as if
/// they were meaningful. Two independent, purpose-specific filters catch this
/// — deliberately NOT a reuse of <see cref="CorpusSimilarityIndex.StopWords"/>
/// (that list exists to reduce noise in TEXT-SIMILARITY scoring for
/// PrecedentRetriever/SameModHintFinder; this file's job — flagging likely
/// PROPER NOUNS a MOD invented — is different enough that tuning one must not
/// silently affect the other): (1) a dedicated, deliberately generous
/// <see cref="ScoringExclusionWords"/> set drops any n-gram made ENTIRELY of
/// function words ("the", "of the") — erring toward including more common
/// words rather than fewer, since the cost of over-excluding a rare genuine
/// candidate is far lower than the cost of the noise this exists to remove;
/// and (2) requiring the first word to start with a capital letter drops
/// ordinary lowercase descriptive words from flavor text ("wielded", "forged",
/// "once") that aren't in that list but also aren't the kind of proper-noun-
/// like term this glossary exists to surface — real MOD-specific terms
/// (item/spell/set names) are capitalized by English naming convention almost
/// without exception.
///
/// **Markup stripping (2026-09-17 real-data finding, book/note candidates).**
/// A book/note's candidate text embeds Skyrim's own book-formatting markup
/// verbatim (e.g. <c>&lt;font face='$HandwrittenFont'&gt;&lt;p
/// align='center'&gt;</c>, the literal token <c>[pagebreak]</c>, and escaped
/// newlines written as a literal backslash+n) — none of this is ever shown to
/// the player, and because every book/tome in a mod reuses the same template,
/// it recurs just as "locally frequent" as real content and can dominate the
/// ranking (observed: a font-tag fragment outscored the genuine "Spell Tome"
/// candidate). <see cref="StripMarkup"/> removes these BEFORE n-gram
/// extraction (not merely down-ranked after the fact) since they are
/// processing-only tokens, never real vocabulary. This targets only the
/// specific known tokens above — a real MOD-authored bracket tag like "[E]"
/// (a variant-marker prefix some armor series use on every item name) is left
/// untouched.
///
/// **Filled by a person, never by the tool** (same contract as ModGlossary):
/// this file only ever offers CANDIDATES; the Japanese column starts blank,
/// survives regeneration untouched once filled, and a blank row means
/// "no opinion" — it simply contributes nothing to "c", exactly as if this
/// mod had no glossary file at all.
/// </summary>
public static class ModPhraseGlossary
{
    private const int MinWordCount = 1;
    private const int MaxWordCount = 5;

    /// <summary>An n-gram needs at least this many raw occurrences within the
    /// mod's own candidates before it is even considered — matches the real-
    /// data validation's threshold (below this, TF-IDF ranking on such tiny
    /// samples was not tested and single one-off phrases are ordinary vocabulary
    /// this glossary is not meant to hold).</summary>
    private const int MinLocalOccurrences = 2;

    /// <summary>2026-09-17実データ発見: 数字を丸ごとマッチ対象外にすると、
    /// "45th"のような英数字混在の序数表記で数字部分だけが消え、残った接尾辞
    /// （"th"）があたかも独立した単語であるかのように孤立して残ってしまう
    /// （例: "Book - Alteration, 45th Edition" → 壊れたn-gram「Book Alteration
    /// th」）。英数字をまとめて1語としてマッチさせたうえで、
    /// <see cref="ExtractNgrams"/>側で「数字のみで構成される語」だけを除外する
    /// （英字を含む語は、数字を含んでいても丸ごと残す）。</summary>
    private static readonly Regex WordPattern = new(@"[A-Za-z0-9’']+", RegexOptions.Compiled);

    /// <summary>HTMLタグ様の書式マークアップ（&lt;font face='...'&gt;、
    /// &lt;p align='...'&gt;、&lt;/font&gt;等）を除去する。中身が表示される
    /// ことは無い、処理専用のトークンのため。</summary>
    private static readonly Regex HtmlLikeTagPattern = new(@"<[^>]*>", RegexOptions.Compiled);

    /// <summary>ページ区切りを示す処理専用トークン。実在するMOD側の
    /// ブラケットタグ（"[E]"等）と衝突しないよう、この既知の語だけを対象にする。</summary>
    private static readonly Regex PagebreakMarkerPattern = new(@"\[pagebreak\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>実際の改行文字ではなく、書物本文中に文字どおり残っている
    /// エスケープシーケンス（バックスラッシュ+n）。</summary>
    private static readonly Regex EscapedNewlinePattern = new(@"\\n", RegexOptions.Compiled);

    /// <summary>n-gram抽出前に、表示されない処理専用のマークアップを取り除く
    /// （2026-09-17実データ発見、クラスの remarks 参照）。</summary>
    private static string StripMarkup(string text) =>
        EscapedNewlinePattern.Replace(PagebreakMarkerPattern.Replace(HtmlLikeTagPattern.Replace(text, " "), " "), " ");

    /// <summary>2026-09-17実データ発見: 単純な機能語（the/a/of/to/by/its/once等）が
    /// このMOD内で繰り返し出現し、あたかも意味のあるフレーズであるかのように
    /// ランキング上位へ混入してしまう問題への対策。「ストップワード」ではなく
    /// 「固有度計算除外単語リスト」と呼ぶ——文章の意味を除去するための語ではなく、
    /// あくまでTF-IDFの固有度スコア計算からノイズとなる機能語だけを除外する
    /// ための語彙である、という目的の違いを明確にするため（2026-09-17ユーザー
    /// 指摘）。
    ///
    /// <see cref="CorpusSimilarityIndex.StopWords"/>（PrecedentRetriever/
    /// SameModHintFinderが使うTF-IDF+コサイン類似度エンジン向け）を再利用する
    /// 案も検討したが、目的が別（あちらは「文章同士の類似度計算のノイズ除去」、
    /// こちらは「固有名詞らしきMOD特有語の検出」）なので、あえて独立したリストと
    /// する——将来どちらかの用途で調整が必要になっても、もう片方に影響しない。
    ///
    /// 意図的に多めに登録する——本当に珍しいMOD特有語を1件取りこぼすコストより、
    /// 機能語ノイズを1件見逃すコストの方が実害が大きい（ユーザーが目視で
    /// スキップする手間が増えるだけで済む一方、ノイズは本命候補を埋もれさせる）。
    ///
    /// 除外はn-gram内の**すべての**単語がこのリストに含まれる場合のみ（AND条件）
    /// —— そのため、将来ここに追加した語がたまたま実在の固有名詞句の一部と
    /// 重なっても（例: 仮に"light"を追加しても）、"Light Greatsword"のように
    /// 他の単語が固有語である限り誤って除外されることはない。</summary>
    private static readonly HashSet<string> ScoringExclusionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the",
        "of", "to", "by", "in", "on", "at", "as", "into", "onto", "upon", "over", "under",
        "above", "below", "between", "among", "through", "during", "before", "after", "about",
        "against", "without", "within", "along", "across", "behind", "beyond", "except", "up",
        "down", "off", "out", "once", "again", "further", "then", "than",
        "and", "or", "but", "nor", "for", "with", "from", "if",
        "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did",
        "has", "have", "had", "can", "could", "will", "would", "shall", "should", "may", "might", "must",
        "this", "that", "these", "those",
        "it", "its", "itself", "he", "him", "his", "himself", "she", "her", "hers", "herself",
        "they", "them", "their", "theirs", "themselves", "we", "us", "our", "ours", "ourselves",
        "i", "me", "my", "mine", "myself", "you", "your", "yours", "yourself", "yourselves",
        "who", "whom", "whose", "which", "what",
        "all", "any", "both", "each", "few", "more", "most", "other", "some", "such",
        "no", "not", "only", "own", "same", "so", "too", "very",
        "when", "where", "why", "how", "there", "here",
    };

    /// <summary>A detected phrase and its detection stats — Count (raw local
    /// occurrences) and Score (TF-IDF distinctiveness), surfaced in
    /// mod_glossary.tsv so a person can judge which candidates are worth
    /// translating (2026-09-17 user request).</summary>
    public readonly record struct DetectedPhrase(string Phrase, int Count, double Score);

    /// <summary>2026-09-17実データ発見: グローバルなn-gram文書頻度表
    /// （ロードオーダー全体を対象に、ある n-gram が何個の候補テキストに
    /// 出現するか）は、渡す<c>allTexts</c>（＝ロードオーダー全体の候補一覧）
    /// が変わらない限り常に同じ結果になるにもかかわらず、従来は
    /// <see cref="DetectCandidatePhrases(IReadOnlyList{string}, IReadOnlyList{string}, AutoTranslator, int)"/>
    /// を呼ぶたびに（＝プラグイン1件ごとに）ゼロから構築し直していた。
    /// `--all`実行（183プラグイン、候補21,528件）で実測したところ、この
    /// 無駄な再計算だけで実行時間が約6倍（10秒→62秒）に増大していたため、
    /// 呼び出し側（PromptGenerator.BuildContext）で実行全体につき1回だけ
    /// <see cref="Build"/>し、全プラグインの処理で使い回す。
    ///
    /// プラグインを絞って実行した場合でも、<c>allTexts</c>自体はロードオーダー
    /// 全体を渡す契約は変わらない——「対象を絞っても集計母数は縮小しない」
    /// という既存の設計を、1回だけ計算する形に変えても壊さないため。</summary>
    public sealed class GlobalNgramFrequency
    {
        private readonly Dictionary<string, int> _docFreq;

        public int TotalDocs { get; }

        private GlobalNgramFrequency(Dictionary<string, int> docFreq, int totalDocs)
        {
            _docFreq = docFreq;
            TotalDocs = totalDocs;
        }

        public static GlobalNgramFrequency Build(IReadOnlyList<string> allTexts)
        {
            var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var text in allTexts)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ng in ExtractNgrams(text)) seen.Add(ng);
                foreach (var ng in seen) docFreq[ng] = docFreq.GetValueOrDefault(ng) + 1;
            }
            return new GlobalNgramFrequency(docFreq, allTexts.Count);
        }

        internal int GetOrZero(string ngram) => _docFreq.GetValueOrDefault(ngram, 0);
    }

    public static string PathFor(string pluginDir) => Path.Combine(pluginDir, "mod_glossary.tsv");

    private static List<string> ExtractNgrams(string text)
    {
        var words = WordPattern.Matches(StripMarkup(text)).Select(m => m.Value).Where(w => !w.All(char.IsDigit)).ToList();
        var result = new List<string>();
        for (var n = MinWordCount; n <= MaxWordCount; n++)
            for (var i = 0; i + n <= words.Count; i++)
                result.Add(string.Join(" ", words.Skip(i).Take(n)));
        return result;
    }

    /// <summary>Ranks this mod's own recurring n-grams by how distinctively
    /// LOCAL they are (see class remarks for the exact TF-IDF formula), then
    /// keeps only the ones NOT already resolvable as a whole phrase via
    /// ①②③ (<paramref name="auto"/>), most-distinctive first. A near-duplicate
    /// (one candidate is a substring of an already-chosen, higher-ranked one)
    /// is skipped purely to keep the output presentable — this is safe
    /// (unlike pruning by containment for its OWN sake) only because it never
    /// discards a DIFFERENT word-order variant sharing no containment
    /// relationship (e.g. "Fire And Moon" vs "Moon And Fire" are unrelated by
    /// this rule and both survive if both rank highly).</summary>
    public static List<DetectedPhrase> DetectCandidatePhrases(
        IReadOnlyList<string> pluginTexts, IReadOnlyList<string> allTexts, AutoTranslator auto, int maxResults = 10) =>
        DetectCandidatePhrases(pluginTexts, GlobalNgramFrequency.Build(allTexts), auto, maxResults);

    /// <summary>Same detection, but taking an already-built
    /// <see cref="GlobalNgramFrequency"/> instead of the raw load-order texts
    /// — the perf-critical path for processing many plugins in one run (see
    /// <see cref="GlobalNgramFrequency"/>'s remarks): the caller builds it
    /// ONCE and passes the same instance in for every plugin.</summary>
    public static List<DetectedPhrase> DetectCandidatePhrases(
        IReadOnlyList<string> pluginTexts, GlobalNgramFrequency globalFrequency, AutoTranslator auto, int maxResults = 10)
    {
        var totalDocs = globalFrequency.TotalDocs;

        var localCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in pluginTexts)
            foreach (var ng in ExtractNgrams(text))
                localCounts[ng] = localCounts.GetValueOrDefault(ng) + 1;

        var scored = new List<(string Ngram, int Count, double Score)>();
        foreach (var (ngram, local) in localCounts)
        {
            if (local < MinLocalOccurrences) continue;
            // 機能語だけで構成されるn-gram（"the"単体、"of the"等）は除外する。
            if (ngram.Split(' ').All(w => ScoringExclusionWords.Contains(w))) continue;
            // 先頭が小文字の候補（"of the Depths"のような文中の断片、または
            // "wielded"/"forged"のような説明文中の一般的な語）も除外する——
            // 実際のMOD特有語（アイテム名等）は英語の命名規則上ほぼ必ず
            // 先頭が大文字になるため。
            if (!char.IsUpper(ngram[0])) continue;
            var gdf = globalFrequency.GetOrZero(ngram);
            var idf = Math.Log((totalDocs + 1) / (double)(gdf + 1)) + 1;
            scored.Add((ngram, local, local * idf));
        }

        var ranked = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Ngram.Split(' ').Length)
            .ThenBy(s => s.Ngram, StringComparer.Ordinal)
            .ToList();

        var result = new List<DetectedPhrase>();
        foreach (var (ngram, count, score) in ranked)
        {
            if (result.Count >= maxResults) break;
            if (result.Any(chosen => chosen.Phrase.Contains(ngram, StringComparison.Ordinal) || ngram.Contains(chosen.Phrase, StringComparison.Ordinal)))
                continue;
            if (auto.TryTranslate(ngram, "") != null) continue; // already resolved as a whole phrase
            result.Add(new DetectedPhrase(ngram, count, score));
        }
        return result;
    }

    /// <summary>The filled (non-blank Japanese) rows only — feeds directly into
    /// issue #4's "c" same-mod hint pool. Returns empty (not an error) when no
    /// file exists yet, exactly like having no glossary at all.</summary>
    public static Dictionary<string, string> LoadFilled(string pluginDir)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = PathFor(pluginDir);
        if (!File.Exists(path)) return entries;

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var cells = line.Split('\t');
            if (cells.Length < 2) continue;
            var english = cells[0].Trim();
            var japanese = cells[1].Trim();
            if (english.Length == 0 || japanese.Length == 0) continue;
            entries[english] = japanese;
        }
        return entries;
    }

    /// <summary>Writes (or MERGES into) this plugin's phrase-glossary template.
    /// Merge, never overwrite — mirrors <see cref="ModGlossary.WriteTemplate"/>'s
    /// contract: an already-filled Japanese value keeps its value verbatim.
    /// Count/Score are always refreshed to the latest detection result for a
    /// phrase still detected; a phrase no longer detected is RETAINED (never
    /// dropped, so a person's translation work is never silently destroyed)
    /// with Count/Score zeroed out, exactly like ModGlossary's "Remaining"
    /// retirement convention. Rows are written in Score-descending order.</summary>
    public static void WriteTemplate(string pluginDir, string plugin, IReadOnlyList<DetectedPhrase> detectedPhrases)
    {
        var path = PathFor(pluginDir);
        Directory.CreateDirectory(pluginDir);

        var existingJapanese = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var cells = line.Split('\t');
                if (cells.Length < 1 || cells[0].Trim().Length == 0) continue;
                var english = cells[0].Trim();
                if (existingJapanese.ContainsKey(english)) continue;
                existingJapanese[english] = cells.Length > 1 ? cells[1].Trim() : "";
            }
        }

        var detectedByPhrase = detectedPhrases.ToDictionary(d => d.Phrase, StringComparer.OrdinalIgnoreCase);
        var rows = new List<DetectedPhrase>(detectedPhrases);
        foreach (var (english, _) in existingJapanese)
            if (!detectedByPhrase.ContainsKey(english))
                rows.Add(new DetectedPhrase(english, 0, 0)); // retired: keep the row, zero the stats

        var ranked = rows
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Count)
            .ThenBy(r => r.Phrase, StringComparer.Ordinal)
            .ToList();

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine($"# {plugin} — このMODだけに効く語彙集（⑤ローカルLLM・⑥生成AI翻訳へのヒントとして使われます）");
        writer.WriteLine("#");
        writer.WriteLine("# 以下は、このMOD内で繰り返し使われているが、まだ確立した訳が無い語句の候補です。");
        writer.WriteLine("# Count＝このMOD内での出現回数、Score＝TF-IDFによる固有度（高いほどMOD特有）です。");
        writer.WriteLine("# 日本語列に書き込んでおくと、このMODの他の未翻訳候補を訳す際のヒントとして使われます");
        writer.WriteLine("# （このMODの候補にしか影響しません。他のMODには一切影響しません）。");
        writer.WriteLine("# 空欄のままでも問題ありません（このファイルが無いのと同じ扱いになるだけです）。");
        writer.WriteLine("# 記入済みの日本語列は再生成時も保持されます（消えません。Count/Scoreは毎回最新値に更新されます）。");
        writer.WriteLine("#");
        writer.WriteLine("# English\tJapanese\tCount\tScore");

        foreach (var row in ranked)
        {
            var japanese = existingJapanese.GetValueOrDefault(row.Phrase, "");
            var score = row.Score.ToString("0.###", CultureInfo.InvariantCulture);
            writer.WriteLine($"{row.Phrase}\t{japanese}\t{row.Count}\t{score}");
        }
    }
}
