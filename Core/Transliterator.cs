using System.Text;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Approximate English→katakana transliteration for proper nouns that have no
/// corpus precedent and no dictionary entry — mostly invented fantasy names
/// (e.g. "Rorikstead", "Solstheim") that no real-world EN/JA resource would ever
/// contain. This is a spelling-based heuristic (not a real phonetic engine), so
/// results are approximate; every auto-transliterated row is tagged so it stays
/// easy to find and correct in the translations.tsv before Stage 3 is run.
/// Multi-word names are transliterated word-by-word and joined with "・", the
/// standard Japanese convention for foreign multi-part proper nouns.
/// </summary>
public static class Transliterator
{
    // Longest-match-first digraph/trigraph → kana substitutions, applied before
    // the generic single-consonant CV table below.
    private static readonly (string Pattern, string Kana)[] MultiLetterRules =
    {
        ("tion", "ション"), ("sion", "ション"),
        ("augh", "オー"), ("ough", "オー"), ("eigh", "エイ"), ("ight", "アイト"),
        ("tch", "ッチ"), ("dge", "ッジ"),
        ("ph", "フ"), ("th", "ス"), ("sh", "シュ"), ("ch", "チ"), ("wh", "ホ"),
        ("ck", "ック"), ("ng", "ング"), ("qu", "ク"),
        ("ee", "イー"), ("ea", "イー"), ("oo", "ウー"),
        ("ou", "アウ"), ("ow", "アウ"), ("oi", "オイ"), ("oy", "オイ"),
        ("ai", "エイ"), ("ay", "エイ"), ("au", "オー"), ("aw", "オー"), ("ie", "アイ"),
    };

    // consonant -> [か行, き行, く行, け行, こ行] style rows for a/i/u/e/o.
    private static readonly Dictionary<char, string[]> ConsonantVowelTable = new()
    {
        ['k'] = new[] { "カ", "キ", "ク", "ケ", "コ" },
        ['g'] = new[] { "ガ", "ギ", "グ", "ゲ", "ゴ" },
        ['s'] = new[] { "サ", "シ", "ス", "セ", "ソ" },
        ['z'] = new[] { "ザ", "ジ", "ズ", "ゼ", "ゾ" },
        ['t'] = new[] { "タ", "チ", "ツ", "テ", "ト" },
        ['d'] = new[] { "ダ", "ジ", "ド", "デ", "ド" },
        ['n'] = new[] { "ナ", "ニ", "ヌ", "ネ", "ノ" },
        ['h'] = new[] { "ハ", "ヒ", "フ", "ヘ", "ホ" },
        ['f'] = new[] { "ファ", "フィ", "フ", "フェ", "フォ" },
        ['b'] = new[] { "バ", "ビ", "ブ", "ベ", "ボ" },
        ['p'] = new[] { "パ", "ピ", "プ", "ペ", "ポ" },
        ['m'] = new[] { "マ", "ミ", "ム", "メ", "モ" },
        ['y'] = new[] { "ヤ", "イ", "ユ", "イェ", "ヨ" },
        ['r'] = new[] { "ラ", "リ", "ル", "レ", "ロ" },
        ['l'] = new[] { "ラ", "リ", "ル", "レ", "ロ" },
        ['v'] = new[] { "ヴァ", "ヴィ", "ヴ", "ヴェ", "ヴォ" },
        ['w'] = new[] { "ワ", "ウィ", "ウ", "ウェ", "ウォ" },
        ['j'] = new[] { "ジャ", "ジ", "ジュ", "ジェ", "ジョ" },
        ['x'] = new[] { "ザ", "ジ", "ズ", "ゼ", "ゾ" }, // approximate ("ks" sound)
        ['c'] = new[] { "カ", "シ", "ク", "セ", "コ" }, // soft c before i/e handled in Transliterate
    };

    private static readonly Dictionary<char, string> LoneVowel = new()
    {
        ['a'] = "ア", ['i'] = "イ", ['u'] = "ウ", ['e'] = "エ", ['o'] = "オ",
    };

    // Word-final (or pre-consonant) coda for a consonant with no following vowel,
    // per standard Japanese loanword convention (e.g. final "t"→ト, final "n"→ン).
    private static readonly Dictionary<char, string> Coda = new()
    {
        ['k'] = "ク", ['g'] = "グ", ['s'] = "ス", ['z'] = "ズ", ['t'] = "ト", ['d'] = "ド",
        ['n'] = "ン", ['h'] = "フ", ['f'] = "フ", ['b'] = "ブ", ['p'] = "プ", ['m'] = "ム",
        ['r'] = "ル", ['l'] = "ル", ['v'] = "ヴ", ['j'] = "ジ", ['x'] = "クス", ['c'] = "ク",
    };

    public static string TransliterateName(string name) =>
        string.Join("・", name.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(TransliterateWord));

    public static string TransliterateWord(string rawWord)
    {
        var word = new string(rawWord.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if (word.Length == 0) return rawWord;

        // Drop a trailing silent "e" (e.g. "stone" -> "ston") when preceded by a consonant.
        if (word.Length > 3 && word[^1] == 'e' && !IsVowel(word[^2]))
            word = word[..^1];

        var sb = new StringBuilder();
        var i = 0;
        while (i < word.Length)
        {
            var matched = false;
            foreach (var (pattern, kana) in MultiLetterRules)
            {
                if (i + pattern.Length <= word.Length && word.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                {
                    sb.Append(kana);
                    i += pattern.Length;
                    matched = true;
                    break;
                }
            }
            if (matched) continue;

            var c = word[i];
            if (IsVowel(c))
            {
                sb.Append(LoneVowel[c]);
                i++;
                continue;
            }

            if (ConsonantVowelTable.TryGetValue(c, out var row))
            {
                var vowelIdx = i + 1 < word.Length ? VowelIndex(word[i + 1]) : -1;
                if (c == 'c' && i + 1 < word.Length && (word[i + 1] == 'e' || word[i + 1] == 'i' || word[i + 1] == 'y'))
                    row = new[] { "サ", "シ", "ス", "セ", "ソ" }; // soft "c"
                if (vowelIdx >= 0)
                {
                    sb.Append(row[vowelIdx]);
                    i += 2;
                }
                else
                {
                    // consonant cluster or word-final: geminate if the same consonant repeats.
                    if (i + 1 < word.Length && word[i + 1] == c && Coda.ContainsKey(c) && c != 'n')
                    {
                        sb.Append('ッ');
                        i++;
                        continue;
                    }
                    sb.Append(Coda.TryGetValue(c, out var coda) ? coda : row[2]);
                    i++;
                }
                continue;
            }

            // Unknown letter (shouldn't normally happen for a-z) — skip it.
            i++;
        }

        return sb.Length > 0 ? sb.ToString() : rawWord;
    }

    private static bool IsVowel(char c) => "aeiou".IndexOf(c) >= 0;

    private static int VowelIndex(char c) => c switch
    {
        'a' => 0, 'i' => 1, 'u' => 2, 'e' => 3, 'o' => 4, _ => -1,
    };
}
