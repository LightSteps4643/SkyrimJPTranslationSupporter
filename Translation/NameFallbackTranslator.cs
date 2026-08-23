using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <param name="Detail">v0.36.0: the word→piece breakdown actually used, for
/// per-candidate review logging (see PromptGenerator's per-plugin detail log).</param>
public sealed record NameFallbackResult(string Japanese, string Method, string Detail = "");

/// <summary>
/// Translation-stage-only, lower-confidence fallback for name-type (any DSD
/// type ending in " FULL") candidates that <see cref="AutoTranslator"/> could
/// not fully resolve. v0.29.0 rewrite.
///
/// Deliberately mechanical rather than grammatical: every word is translated
/// independently (corpus-mined word vocabulary → corpus transliteration →
/// curated glossary — see <see cref="TryResolveCore"/> for why this order, not
/// the more obvious "glossary first"; the JMdict-derived dictionary that used
/// to sit last was removed in v0.29.5, see that method's remarks) and the
/// results are chained
/// with a small, FIXED set of connector rules — no attempt at a "correct"
/// parse of the phrase's grammar. v0.39.0: "every word" is no longer quite
/// literal — <see cref="GroupKnownPhrases"/> first greedily matches adjacent
/// words against known multi-word corpus precedent, so a bound-stem word like
/// "Heavy" (alone: "重", a fragment that only reads naturally fused into a
/// compound) resolves as part of "Heavy Armor"→"重装" when the corpus attests
/// that whole phrase, instead of being glued to its neighbor with the default
/// "の" and reading as "重の鎧". This is intentionally a step down in quality
/// from <see cref="CorpusMeaningTranslator"/> (which requires a corroborated
/// whole-phrase match): it exists for whatever's left after that bar isn't
/// cleared, on the premise that a rough word-for-word rendering beats leaving
/// the whole name in English, and is easy for a person to spot and fix later
/// (Notes column carries a distinct tag for exactly that review).
///
/// **All-or-nothing (v0.30.0).** A word with no precedent anywhere abandons the
/// WHOLE candidate, which then flows to the AI-chat prompt untouched. Until
/// v0.29.13 such a word was instead left in English so the rest of the name
/// could still be translated — and measuring that against real output showed
/// the premise above does not survive contact with the data: of 2,502 emitted
/// fallback translations, 1,886 (75%) still carried an English word, producing
/// hybrids the player reads in their inventory ("Wayward Knight Legguards" →
/// "放浪の騎士のLegguards", "Reanimate Perk Keyword" → "蘇りの能力のKeyword").
/// A half-translated name is not a cheaper translation, it is a defect: it
/// looks finished, so nothing downstream flags it, yet it reads worse than the
/// untouched English would have. Giving up is the honest outcome — the
/// candidate stays visibly unresolved and reaches the one path that CAN finish
/// it. The cost is a larger manual pool; the benefit is that everything this
/// class does emit is complete Japanese.
///
/// Numbers and a bare "-" are exempt (see <see cref="IsPassThroughToken"/>):
/// they are passed through verbatim by design, not left untranslated.
///
/// The connector rules, confirmed against real examples with the user:
/// - Default: adjacent words join with "の" — "Steel Plate Boots" → each word
///   translated, chained left-to-right: "スチールのプレートのブーツ".
/// - "AAA BBB of CCC": the part after " of " (article stripped) moves to the
///   FRONT, but each side keeps its OWN internal left-to-right order (no
///   reversal) — "AAA BBB of CCC" → "CCC AAA BBB" → "CCCのAAAのBBB". Multiple
///   words on either side are fine: "AAA BBB of CCC DDD" → "CCCのDDDのAAAのBBB".
/// - "and" is a literal connector, not a word to translate — "Shoes and
///   Boots" → 靴とブーツ ("と" at that one seam instead of "の").
/// - A leading bracketed tag ("[E] Abyss Armlet Silver", a mod's own item-list
///   prefix) is set aside before all of the above and re-attached verbatim in
///   front of the result — it is brand marking, not part of the name.
/// - v0.40.0: "No X"/"Not X" — a word with no slot in either connector rule
///   above, since "の"/"と" both assume the pieces on either side genuinely
///   modify each other, not negate — resolves as one unit rendered
///   "（Xなし）" (see <see cref="GroupNegations"/>). Found via a real
///   mistranslation: "Twilight Princess Gloves - Red - No Bronze" resolved
///   "No" via its own corpus precedent ("No"→"いいえ", the dialogue answer)
///   and joined it with the default "の", producing "…いいえの青銅".
/// - v0.40.0: a word matching the fixed list in <c>Data/color_words.txt</c>
///   (see <see cref="IsColorWord"/>) is rendered "（訳語）" in place rather
///   than joined with "の" either side. A color word ending up as, say, the
///   chain's last (head) position under the default rule reads as a false
///   possessive — "Xmas Tinsel White" used to come out "クリスマスのモールの
///   ホワイト" ("the tinsel's white"), which the parenthetical avoids without
///   having to reorder anything: "クリスマスのモール（ホワイト）".
/// </summary>
public sealed class NameFallbackTranslator
{
    private readonly EnJaDictionary _glossary;
    private readonly AutoTranslator _auto;
    private readonly CorpusMeaningTranslator _meaning;
    private readonly CorpusTransliterator _transliterator;

    /// <summary>v0.40.0: fixed list of English color words (Data/color_words.txt,
    /// same curated-list pattern as CorpusTransliterator's manual exclusions) —
    /// used only to decide the CONNECTOR/formatting for a word (see
    /// <see cref="IsColorWord"/>), never as a translation source of its own.</summary>
    private static readonly HashSet<string> ColorWords = LoadColorWords();

    private static HashSet<string> LoadColorWords()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "color_words.txt");
        if (!File.Exists(path)) return words;
        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            words.Add(trimmed);
        }
        return words;
    }

    private NameFallbackTranslator(EnJaDictionary glossary, AutoTranslator auto, CorpusMeaningTranslator meaning, CorpusTransliterator transliterator)
    {
        _glossary = glossary;
        _auto = auto;
        _meaning = meaning;
        _transliterator = transliterator;
    }

    /// <param name="glossary">The curated, name-only word list (Data/name_glossary.tsv)
    /// — checked AFTER the corpus-derived sources (see <see cref="TryResolveCore"/>
    /// for why), since it is this tool's own best-effort guesswork rather than
    /// attested precedent.</param>
    /// <param name="auto">Supplies <see cref="AutoTranslator.TryExactWord"/> — a
    /// single-word corpus/reference lookup that neither <paramref name="meaning"/>
    /// nor <paramref name="transliterator"/> can reach (v0.29.11, see
    /// <see cref="TryResolveCore"/>).</param>
    public static NameFallbackTranslator Build(EnJaDictionary glossary, AutoTranslator auto, CorpusMeaningTranslator meaning, CorpusTransliterator transliterator) =>
        new(glossary, auto, meaning, transliterator);

    /// <param name="recordType">Gated to the same "*_FULL" scope as
    /// <see cref="CorpusMeaningTranslator"/> — pass the candidate's DSD type
    /// unchanged.</param>
    /// <param name="mod">This plugin's own scoped glossary (v0.31.0). Consulted
    /// FIRST — it is the most specific evidence available, a decision made by a
    /// person looking at THIS mod's item list. In practice it cannot conflict
    /// with the corpus, because a word only ever reaches that file by having
    /// failed every other resolver first; the priority is insurance for the case
    /// where a later corpus grows a general entry for a word one mod uses in its
    /// own sense.</param>
    /// <param name="unresolvedWords">Receives the words that blocked this
    /// candidate — empty when the whole name resolved. This is what
    /// <see cref="ModGlossary.WriteTemplate"/> is built from: the resolution
    /// chain is the only thing that knows why a name failed, so it reports it
    /// rather than being re-derived by a second pass that could disagree.</param>
    public NameFallbackResult? TryTranslate(string englishText, string recordType, ModGlossary mod, out List<string> unresolvedWords)
    {
        unresolvedWords = new List<string>();
        if (!CorpusMeaningTranslator.AppliesToRecordType(recordType)) return null;

        var text = englishText.Trim();
        if (text.Length == 0) return null;

        // A leading "[Tag]" brand prefix (a mod's own item-list marker) is not
        // part of the name — set it aside and re-attach it verbatim.
        var prefix = "";
        if (text.StartsWith('[') )
        {
            var close = text.IndexOf(']');
            if (close > 0)
            {
                prefix = text[..(close + 1)] + " ";
                text = text[(close + 1)..].Trim();
            }
        }

        if (text.Length == 0 || !NameFieldFilter.LooksLikeNameField(text)) return null;

        var (words, connectors) = BuildChain(text, mod);
        if (words.Count == 0) return null;

        // v0.39.0: before resolving word-by-word, greedily group adjacent words
        // into the longest run that is ITSELF a known corpus/reference phrase —
        // see GroupKnownPhrases for why (a lone word's corpus rendering can be a
        // bound stem — "Heavy"→"重" — that only reads naturally glued into a
        // specific compound; the corpus's own multi-word precedent, when one
        // exists, is always safer than re-deriving the compound word-by-word).
        var phraseSegments = GroupKnownPhrases(words);

        // v0.40.0: merge a "No"/"Not" segment with the ONE segment right after
        // it into a single negation unit — see GroupNegations and the class
        // remarks.
        var units = GroupNegations(phraseSegments, words);

        // v0.40.1: the TAIL wrapping is a maximal RUN of consecutive single-word
        // color units, not just the very last one — "White Gold" (two colors
        // back to back) used to wrap only "Gold" (literally last) while "White"
        // fell back to the plain "の" chain, producing an inconsistent half-
        // wrapped read ("…ベルトのホワイト （ゴールド）"). Requires at least one
        // non-color unit before the run (a candidate that is ENTIRELY color
        // words falls back to the old default, same guard as v0.40.0's "not the
        // whole candidate" rule).
        var tailColorStart = units.Count;
        for (var k = units.Count - 1; k > 0; k--)
        {
            var u = units[k];
            if (u.Kind == UnitKind.Negation || u.PrecomputedJapanese != null || !IsColorWord(words[u.Start])) break;
            tailColorStart = k;
        }

        var pieces = new List<string>();
        var sources = new List<string>();
        var displayWords = new List<string>();
        var connectorBeforeIndex = new List<int>(); // parallel to pieces: original word-index of each piece's first word, for the connectors[] lookup
        var isAnnotation = new List<bool>();         // parallel to pieces: true for a "（…）" unit that must never take "の"/"と" on either side
        var tailColorRun = tailColorStart < units.Count;

        for (var u = 0; u < (tailColorRun ? tailColorStart : units.Count); u++)
        {
            var unit = units[u];
            connectorBeforeIndex.Add(unit.Start);
            displayWords.Add(string.Join(' ', words.Skip(unit.Start).Take(unit.Length)));

            if (unit.Kind == UnitKind.Negation)
            {
                var argStart = unit.Start + 1;
                var resolvedArg = unit.PrecomputedJapanese != null
                    ? (true, unit.PrecomputedJapanese, unit.PrecomputedSource!)
                    : ResolveSingleWord(words[argStart], mod);

                if (!resolvedArg.Item1)
                {
                    unresolvedWords.Add(words[argStart].TrimEnd(',', ':', ';'));
                    continue;
                }

                pieces.Add($"（{resolvedArg.Item2}なし）");
                sources.Add(resolvedArg.Item3);
                isAnnotation.Add(true);
                continue;
            }

            if (unit.PrecomputedJapanese != null)
            {
                pieces.Add(unit.PrecomputedJapanese);
                sources.Add(unit.PrecomputedSource!);
                isAnnotation.Add(false);
                continue;
            }

            var word = words[unit.Start]; // length == 1 whenever PrecomputedJapanese is null
            var resolved = ResolveSingleWord(word, mod);
            if (!resolved.Item1)
            {
                // v0.30.0: all-or-nothing. Any single WORD left unresolved abandons
                // the whole candidate — see the class remarks. The word is still
                // REPORTED (not just dropped) so the mod glossary template can ask
                // for exactly it.
                unresolvedWords.Add(word.TrimEnd(',', ':', ';'));
                continue;
            }

            pieces.Add(resolved.Item2);
            sources.Add(resolved.Item3);
            isAnnotation.Add(false);
        }

        // v0.40.0/v0.40.1: the trailing color run (if any) becomes ONE combined
        // annotation — "（Xなし）"-style, but joining multiple colors with "・"
        // (the ordinary Japanese way to list several attributes) when there is
        // more than one. A recognized color word in tail position reads as a
        // false possessive when joined with "の" — "Xmas Tinsel White" used to
        // come out "…モールのホワイト" ("the tinsel's white"); wrapped instead,
        // it resolves cleanly: "…モール（ホワイト）". A color word followed by
        // something else that is NOT part of this trailing run ("Apothecary
        // White Belt") is left in the plain "の" chain above — "ホワイトのベルト"
        // is ordinary Japanese, and wrapping there was tried and found to
        // fragment the phrase ("薬剤師 （ホワイト） ベルト").
        if (tailColorRun)
        {
            var tailJapanese = new List<string>();
            var tailSources = new List<string>();
            var tailWords = new List<string>();
            for (var u = tailColorStart; u < units.Count; u++)
            {
                var word = words[units[u].Start];
                var resolved = ResolveSingleWord(word, mod);
                if (!resolved.Item1)
                {
                    unresolvedWords.Add(word.TrimEnd(',', ':', ';'));
                    continue;
                }
                tailJapanese.Add(resolved.Item2);
                tailSources.Add(resolved.Item3);
                tailWords.Add(word);
            }

            if (unresolvedWords.Count == 0)
            {
                connectorBeforeIndex.Add(units[tailColorStart].Start);
                displayWords.Add(string.Join(' ', tailWords));
                pieces.Add($"（{string.Join("・", tailJapanese)}）");
                sources.Add(string.Join(", ", tailSources.Distinct()));
                isAnnotation.Add(true);
            }
        }

        if (unresolvedWords.Count > 0) return null;

        var body = pieces[0];
        for (var u = 1; u < pieces.Count; u++)
        {
            // v0.40.0/v0.40.1: an annotation unit (negation, or the trailing
            // color run) is not a grammatical modifier of its neighbor — always
            // a space boundary, overriding whatever BuildChain computed for the
            // words it spans.
            var connector = isAnnotation[u] || isAnnotation[u - 1] ? " " : connectors[connectorBeforeIndex[u] - 1];
            body += connector + pieces[u];
        }

        // v0.36.0: word→piece breakdown, each tagged with which source answered it
        // (MOD用語集 outranks everything else; "①③④/全体用語集" covers
        // TryExactWord/意味/音訳/Data/name_glossary.tsv without distinguishing
        // which — see TryResolveCore's own remarks for that internal order).
        // v0.39.0: a unit may now show several original words joined by a space
        // (the phrase/negation it was matched as a whole), not just one.
        var detail = string.Join(" + ", displayWords.Select((w, i) => $"\"{w}\"→\"{pieces[i]}\"({sources[i]})"));
        return new NameFallbackResult(prefix + body, "TranslationNameFallback", detail);
    }

    /// <summary>Resolves ONE original word via the same three-source chain the
    /// per-word loop always used (mod glossary → corpus/dictionary chain →
    /// pass-through), factored out so <see cref="TryTranslate"/>'s negation-unit
    /// branch can resolve a "No X"/"Not X" argument identically to a plain
    /// word.</summary>
    private (bool Ok, string Japanese, string Source) ResolveSingleWord(string word, ModGlossary mod)
    {
        // v0.31.0: the mod's own glossary outranks every shared source.
        if (TryModGlossary(mod, word, out var modJa)) return (true, modJa, "MOD用語集");
        if (TryTranslateWord(word, out var ja, out var wordSource)) return (true, ja, wordSource);
        if (IsPassThroughToken(word)) return (true, word, "そのまま");
        return (false, "", "");
    }

    private enum UnitKind { Plain, Negation }

    /// <param name="PrecomputedJapanese">For a Plain unit spanning 2+ words, or
    /// for a Negation unit whose argument itself spans 2+ words: the corpus
    /// phrase match already found by <see cref="GroupKnownPhrases"/> — null
    /// whenever there's a single word left to resolve normally (the argument's
    /// own word, for Negation; <see cref="Start"/>'s word, for Plain).</param>
    private readonly record struct Unit(int Start, int Length, string? PrecomputedJapanese, string? PrecomputedSource, UnitKind Kind);

    /// <summary>
    /// v0.40.0: merges a "No"/"Not" <paramref name="segments"/> entry with the
    /// ONE segment immediately after it into a single <see cref="UnitKind.Negation"/>
    /// unit — "no slot in either connector rule" (see the class remarks) is
    /// resolved by not using a connector at all: the pair is rendered as one
    /// "（Xなし）" annotation in <see cref="TryTranslate"/>, in its original
    /// position (no reordering).
    ///
    /// A single following segment is taken as the whole argument regardless of
    /// whether <see cref="GroupKnownPhrases"/> already grouped it into a
    /// multi-word phrase ("No Heavy Armor" would negate the already-resolved
    /// "Heavy Armor"→"重装" as one unit) or left it as one plain word ("No
    /// Bronze") — either way <see cref="Unit.PrecomputedJapanese"/> carries
    /// whichever is the case, so <see cref="TryTranslate"/> doesn't need to
    /// know which.
    /// </summary>
    private static List<Unit> GroupNegations(List<(int Start, int Length, string? Japanese, string? Source)> segments, List<string> words)
    {
        var units = new List<Unit>();
        var i = 0;
        while (i < segments.Count)
        {
            var seg = segments[i];
            if (seg.Length == 1 && i + 1 < segments.Count && IsNegationWord(words[seg.Start]))
            {
                var next = segments[i + 1];
                units.Add(new Unit(seg.Start, seg.Length + next.Length, next.Japanese, next.Source, UnitKind.Negation));
                i += 2;
            }
            else
            {
                units.Add(new Unit(seg.Start, seg.Length, seg.Japanese, seg.Source, UnitKind.Plain));
                i += 1;
            }
        }
        return units;
    }

    private static bool IsNegationWord(string token) =>
        token.Equals("No", StringComparison.OrdinalIgnoreCase) || token.Equals("Not", StringComparison.OrdinalIgnoreCase);

    /// <summary>v0.40.0: is this token (after the same trailing possessive/
    /// punctuation stripping <see cref="TryTranslateWord"/> applies before
    /// lookup) one of the fixed color words in <see cref="ColorWords"/>? Only
    /// decides HOW the word is joined into the chain (see <see cref="TryTranslate"/>
    /// and <see cref="BuildChain"/>) — the translation itself still comes from
    /// the normal resolution chain, never from this list, so there is exactly
    /// one place a color's Japanese rendering is decided.</summary>
    private static bool IsColorWord(string token)
    {
        var core = token;
        if (core.EndsWith("'s", StringComparison.OrdinalIgnoreCase)) core = core[..^2];
        core = core.TrimEnd(',', ':', ';');
        return ColorWords.Contains(core);
    }

    /// <summary>
    /// v0.39.0: greedy longest-match grouping of <paramref name="words"/> into
    /// known corpus/reference phrases, at WORD granularity — the exact same
    /// algorithm <see cref="CorpusTransliterator.TryDecompose"/> already uses at
    /// CHARACTER granularity within a single unspaced word (longest remaining
    /// span first, shrink by one, take the first hit). Real motivating case:
    /// "Eastmarch Guard's Heavy Armor" used to resolve "Heavy" and "Armor"
    /// independently ("Heavy"→"重" from a standalone corpus/GMST entry — a bound
    /// stem, not a free adjective) and glue them with the default "の" joiner,
    /// producing "重の鎧" — ungrammatical, since "重" only reads naturally fused
    /// into a specific compound. The corpus separately holds the correct
    /// multi-word precedent "Heavy Armor"→"重装" (vanilla `AVIF FULL`), but a
    /// pure per-word chain never looked for it. This pass does, for every
    /// contiguous run of the words actually present — checked via
    /// <see cref="AutoTranslator.TryExactWord"/>, which despite its name is a
    /// plain exact-string corpus/reference lookup and works identically for a
    /// space-joined multi-word phrase as for one word.
    ///
    /// Only WHOLE-PHRASE corpus precedent counts here (no ②意味/③音訳 composition
    /// at this stage — same "attested, not guessed" bar <see cref="TryResolveCore"/>
    /// already applies to the single-word ①完全一致 step), and only a match of 2
    /// or more words replaces the default one-word-per-segment behavior, so a
    /// candidate with no such precedent anywhere degrades to exactly the old
    /// per-word chain.
    ///
    /// Runs on <paramref name="words"/> AFTER <see cref="BuildChain"/>'s "of"
    /// reordering/"and"/"the" handling, so a phrase whose corpus form spans an
    /// "of" swap boundary won't be found here — accepted: that shape is rare,
    /// and the phrase's pieces still resolve individually as before.
    /// </summary>
    private List<(int Start, int Length, string? Japanese, string? Source)> GroupKnownPhrases(List<string> words)
    {
        var segments = new List<(int, int, string?, string?)>();
        var i = 0;
        while (i < words.Count)
        {
            var matched = false;
            for (var length = words.Count - i; length >= 2; length--)
            {
                var phrase = string.Join(' ', words.Skip(i).Take(length));
                if (_auto.TryExactWord(phrase, out var japanese, out var source))
                {
                    segments.Add((i, length, japanese, $"①完全一致[{source}]"));
                    i += length;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                segments.Add((i, 1, null, null));
                i += 1;
            }
        }
        return segments;
    }

    /// <summary>
    /// Mod-glossary lookup, applying the SAME token normalizations
    /// <see cref="TryTranslateWord"/> applies to every other source: a trailing
    /// possessive, trailing list punctuation, a wrapping parenthesis, and a naive
    /// plural. Without this the glossary silently under-matches in a way that
    /// looks like the file being ignored — found on real data, where a
    /// <c>Hide=革</c> row fixed "Cloak - Brown Hide" but left "Pelts and Hides"
    /// reading "毛皮と身隠し", because only the exact token was tried. Making the
    /// person add both singular and plural rows would push the tool's internal
    /// matching rules onto them for no reason.
    /// </summary>
    private static bool TryModGlossary(ModGlossary mod, string rawWord, out string japanese)
    {
        var core = rawWord;
        if (core.Length > 2 && core[0] == '(' && core[^1] == ')')
        {
            if (TryModGlossary(mod, core[1..^1], out var inner)) { japanese = "(" + inner + ")"; return true; }
        }
        if (core.EndsWith("'s", StringComparison.OrdinalIgnoreCase)) core = core[..^2];
        core = core.TrimEnd(',', ':', ';');

        if (mod.TryTranslateWord(core, out japanese!)) return true;

        if (core.Length > 3 && core.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && !core.EndsWith("ss", StringComparison.OrdinalIgnoreCase)
            && mod.TryTranslateWord(core[..^1], out japanese!)) return true;

        japanese = "";
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex RomanNumeral =
        new(@"^(I{1,3}|IV|V|VI{1,3}|VI|IX|X{1,3})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A token that legitimately survives into the Japanese output AS WRITTEN, so
    /// failing to "translate" it must not count against the all-or-nothing rule:
    ///
    /// - a bare number ("Fawnia Boots 2") and a bare "-" separator ("Wayward
    ///   Knight Helmet - Faceless"), both admitted by
    ///   <see cref="NameFieldFilter.LooksLikeNameField"/> (v0.29.7) for exactly
    ///   this reason: a Japanese reader parses them unchanged;
    /// - a lone uppercase letter and a Roman numeral (v0.31.0) — the variant and
    ///   tier markers mod authors append to a series ("Wayward Knight Armor S",
    ///   "DarkRebel Glove L", "Iron Giant Hammer II"). These are not words in any
    ///   language; "防具 S" is how a Japanese reader expects to see it, and both
    ///   translating and abandoning the name would be wrong answers.
    ///
    /// Anything else carrying letters is a real word the output would expose in
    /// English — including a mod's internal tags ("SMP", "3BBB", "DLC1"), which
    /// are NOT guessed at here: they go on the mod's own glossary template where
    /// a person can mark them pass-through deliberately (see
    /// <see cref="ModGlossary.PassThroughMarker"/>).
    /// </summary>
    private static bool IsPassThroughToken(string token) =>
        token == "-"
        || token.All(char.IsDigit)
        || (token.Length == 1 && char.IsUpper(token[0]))
        || RomanNumeral.IsMatch(token);

    /// <summary>
    /// Splits the (already prefix-stripped) text into a word chain plus the
    /// connector that sits between each adjacent pair — see the class remarks
    /// for the three rules this encodes ("of" relocation, "and" → と, default の).
    /// </summary>
    private static (List<string> Words, List<string> Connectors) BuildChain(string text, ModGlossary mod)
    {
        var ofIdx = text.IndexOf(" of ", StringComparison.Ordinal);
        var reordered = text;
        if (ofIdx > 0)
        {
            var pre = text[..ofIdx].Trim();
            var post = text[(ofIdx + 4)..].Trim();
            foreach (var article in new[] { "the ", "a ", "an " })
                if (post.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { post = post[article.Length..].Trim(); break; }

            if (pre.Length > 0 && post.Length > 0)
                reordered = $"{post} {pre}"; // each side keeps its own internal order — no reversal
        }

        var tokens = reordered.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>(tokens.Length);
        var connectors = new List<string>(tokens.Length);
        var pending = "の";
        foreach (var t in tokens)
        {
            if (t.Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                pending = "と";
                continue;
            }
            // A bare "the" mid-name ("Otar THE Mad") has no slot in either
            // connector rule — it's not a word to translate, and it's not the
            // leading article the " of " rule already strips. Dropping it
            // outright (keeping whatever connector was already pending) avoids
            // it surviving into the output as untranslated clutter ("Otar the
            // Madの…"), found via a real "Cloak of Otar the Mad" example.
            if (t.Equals("the", StringComparison.OrdinalIgnoreCase)) continue;

            if (words.Count > 0)
            {
                // Punctuation-driven overrides, found via real examples: a
                // parenthetical annotation ("Belt (Brown)") isn't grammatically
                // modified BY the previous word, so "の" in front of it reads as
                // a stray particle ("ベルトの(Brown)") — use a plain space
                // instead. A word ending in ":" is already acting as its own
                // label/separator ("Spell Tome:"), so the "の" that would
                // normally follow it is redundant clutter. And a bare "-"
                // separator (v0.29.7, "Wayward Knight Helmet - Faceless") isn't
                // a word to translate at all — it's already doing the joining
                // job the English text wanted, so surrounding it with "の" on
                // both sides ("兜の-の…") would double up on that job instead
                // of reproducing it; a plain space on both sides keeps the
                // original " - " punctuation intact.
                // v0.31.0: a pass-through token (a number, a lone letter, a Roman
                // numeral, the "-" separator) is a marker appended to the name, not
                // a noun the previous word modifies — "防具 S" / "ブーツ 2", never
                // "防具のS". A plain space on both sides reproduces the English
                // spacing, which is exactly how these read in Japanese too.
                // v0.40.0: color words are NOT special-cased here — see
                // TryTranslate's tail-only wrapping instead. A color word
                // followed by another word ("Apothecary White Belt") reads
                // fine chained with the default "の" ("ホワイトのベルト" is
                // ordinary Japanese); forcing a space here regardless of
                // position was tried and found to fragment exactly that case
                // ("薬剤師 （ホワイト） ベルト") — only the TAIL position (nothing
                // following) has the false-possessive problem this exists for.
                var connector = t.StartsWith('(') || words[^1].EndsWith(':')
                    || IsPassThroughToken(t) || IsPassThroughToken(words[^1])
                    || mod.IsPassThrough(t.TrimEnd(',', ':', ';')) || mod.IsPassThrough(words[^1].TrimEnd(',', ':', ';')) ? " " : pending;
                connectors.Add(connector);
            }
            words.Add(t);
            pending = "の";
        }
        return (words, connectors);
    }

    /// <summary>
    /// Resolves ONE token, trying (via <see cref="TryResolveCore"/>, in order)
    /// the corpus-mined word vocabulary, the corpus transliteration table, and
    /// the curated glossary — with three finite fallback rules layered on top
    /// when the token as written doesn't hit directly:
    /// - a trailing possessive "'s" is stripped before lookup ("Knight's" →
    ///   "Knight" — the の already carries the possessive meaning, so the
    ///   apostrophe would only ever end up as unresolved clutter)
    /// - a hyphenated compound ("Dai-Katana") is tried whole first, then
    ///   split on "-" and each half resolved independently, concatenated with
    ///   no separator (a hyphen binds tighter than a word boundary — a "の"
    ///   there would misrepresent it as two separate modified nouns)
    /// - a naive plural ("Greatswords") retries with the trailing "s" dropped
    ///   if the word as written doesn't resolve (accepted false-positive risk
    ///   on words that are already singular but end in "s", e.g. "Glass" — low
    ///   stakes given this whole path is an accepted-low-quality fallback)
    /// - a token that IS a parenthetical annotation start-to-finish
    ///   ("(Black)", "(Black-Blue)") is unwrapped, its inside resolved through
    ///   this same method (so the hyphen/plural/possessive rules above all
    ///   still apply to it), and re-wrapped in parentheses — found via real
    ///   data: "Bishop Belt (Brown)" left "(Brown)" untranslated even though
    ///   "brown" was already a known word, because the parentheses made the
    ///   token as a whole a literal miss.
    /// </summary>
    private bool TryTranslateWord(string rawWord, out string japanese, out string source)
    {
        if (rawWord.Length > 2 && rawWord[0] == '(' && rawWord[^1] == ')')
        {
            if (TryTranslateWord(rawWord[1..^1], out var innerJa, out source))
            {
                japanese = "(" + innerJa + ")";
                return true;
            }
            japanese = "";
            return false;
        }

        var core = rawWord;
        if (core.EndsWith("'s", StringComparison.OrdinalIgnoreCase)) core = core[..^2];

        // v0.29.11: trailing punctuation carried over from a longer sentence-like
        // list ("Alteration, 45th Edition" — the comma is a list separator, not
        // part of the word) blocks lookup the same way the parenthesis/possessive
        // cases above do. Stripped before lookup and simply dropped, not
        // re-attached — unlike "'s", it isn't meaningful punctuation.
        core = core.TrimEnd(',', ':', ';');

        if (TryResolveCore(core, out japanese, out source)) return true;

        if (core.Contains('-'))
        {
            var parts = core.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                // v0.30.0: every half must resolve, matching the all-or-nothing rule
                // one level up. Accepting a partial split was the same defect in
                // miniature — "Auto-Unlock" came back as "Auto解錠", half English.
                var pieces = new List<string>(parts.Length);
                var pieceSources = new List<string>(parts.Length);
                var all = true;
                foreach (var p in parts)
                {
                    if (TryResolveCore(p, out var pj, out var ps)) { pieces.Add(pj); pieceSources.Add(ps); }
                    else { all = false; break; }
                }
                if (all)
                {
                    japanese = string.Concat(pieces);
                    source = string.Join("+", pieceSources.Distinct());
                    return true;
                }
            }
        }

        if (core.Length > 3 && core.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !core.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            if (TryResolveCore(core[..^1], out japanese, out source)) return true;
        }

        japanese = "";
        source = "";
        return false;
    }

    /// <summary>
    /// v0.29.5: reordered so the two corpus-derived sources — <see cref="_meaning"/>
    /// and <see cref="_transliterator"/>, both mined from real shipped translations
    /// (this load order's own scan plus Data/skyrim_taiyaku_reference.tsv) — are
    /// tried BEFORE the curated <see cref="_glossary"/>. The glossary is this
    /// tool's own best-effort guesswork (per-word audit found 11 wrong entries
    /// out of 140 on the first pass, 127 more still unverified); the corpus
    /// sources are precedent, not guesses. Checking the glossary first meant a
    /// wrong glossary entry could silently override a CORRECT corpus answer —
    /// exactly what happened with "glass" (glossary said "ガラス", the corpus
    /// said "碧水晶", and the wrong one used to win by going first). The
    /// glossary's real job is filling gaps the corpus doesn't cover at all, so
    /// it belongs AFTER both corpus sources, not before them.
    ///
    /// A fourth source, the JMdict-derived <c>Data/en_ja_dict.tsv</c>, used to
    /// sit last here (and separately in <see cref="AutoTranslator"/>'s own ②
    /// step). Removed in v0.29.5: real data turned up homograph mistranslations
    /// from it in BOTH places ("lime"→"石灰"/quicklime instead of the fruit/
    /// color sense here; "Ward"→"区"/administrative district instead of the
    /// defensive spell, in AutoTranslator) for a small yield (well under 2% of
    /// all auto-resolved candidates combined). A word JMdict would have caught
    /// now stays unresolved and flows to the ordinary AI-chat prompt instead —
    /// slower, but never silently wrong.
    ///
    /// v0.29.11: <see cref="AutoTranslator.TryExactWord"/> checked FIRST, ahead
    /// of even <see cref="_meaning"/> — it answers only for a word the corpus
    /// or reference glossary attests to VERBATIM as its own entry ("Alteration"
    /// → "変性"), which is stronger evidence than a composed/mined guess.
    /// <see cref="_meaning"/> and <see cref="_transliterator"/> only ever learn
    /// vocabulary by mining it OUT of multi-word compounds, so a clean
    /// standalone single-word precedent was invisible to both — found via real
    /// data: "Twilight Princess Book - Alteration, 45th Edition" left
    /// "Alteration" untranslated despite "Alteration"→"変性" sitting in the
    /// corpus as its own row, because no multi-word compound existed for
    /// either mining step to learn it from.
    /// </summary>
    private bool TryResolveCore(string word, out string japanese, out string source)
    {
        if (_auto.TryExactWord(word, out japanese!, out var exactSource)) { source = $"①完全一致[{exactSource}]"; return true; }
        if (_meaning.TryTranslateWord(word, out japanese!, out var meaningSource)) { source = $"②意味" + (meaningSource.Length > 0 ? $"[{meaningSource}]" : ""); return true; }
        if (_transliterator.TryTranslateWord(word, out japanese!, out var translitSource)) { source = $"③音訳" + (translitSource.Length > 0 ? $"[{translitSource}]" : ""); return true; }
        if (_glossary.TryTranslateWord(word, out japanese!)) { source = "全体用語集(Data/name_glossary.tsv)"; return true; }
        japanese = "";
        source = "";
        return false;
    }
}
