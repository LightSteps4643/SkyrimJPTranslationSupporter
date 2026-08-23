using System.Text;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// A per-MOD word glossary — <c>Data/mod_glossary/&lt;safe plugin name&gt;.tsv</c>,
/// one file per plugin, consulted by <see cref="NameFallbackTranslator"/> only
/// for that plugin's own candidates.
///
/// **Why scoped, when Data/name_glossary.tsv already exists.** The global
/// glossary was cut from 205 entries to 82 across v0.29.4–v0.29.13 because a
/// single wrong or merely register-shifted entry ("ultra" → 超 vs ウルトラ)
/// silently reaches every mod in the load order. That pruning worked, but it
/// also hit a floor: the vocabulary still blocking translation is not the kind
/// of general English a global list can safely hold. Measured on the 2,458
/// unresolved <c>*_FULL</c> candidates, the words that block them break down as:
///
///   1 plugin only ... 824 words (87%)   2 plugins ... 74   3+ ... 39
///
/// "Fawnia", "Sena", "KSSMP", "Dremurai", "Mageali" are a mod's own coinages —
/// they cannot collide with another mod because they do not occur in one. For
/// 87% of the remaining vocabulary the cross-mod contamination risk that forced
/// the global glossary to be strict is, under per-mod scoping, structurally
/// impossible. That is what buys back the looser admission bar: inside one mod
/// the person filling this in can look at that mod's actual item list, and a
/// mistake costs that mod alone.
///
/// **Leverage.** A handful of words unblocks a whole mod, because mod item names
/// are combinatorial (a series name × a body slot × a variant): [ELLE] Sena
/// needs 8 words to release 110 names, Mageali 5 words for 61. So this file
/// changes what the AI-chat/manual step is even asked to do — "decide 8 words",
/// not "translate 110 item names" — and makes intra-mod consistency structural
/// rather than something each batch has to remember.
///
/// **Filled by a person or an AI chat, never by the tool.** A word lands here
/// precisely because no corpus, reference table, or mined lexicon anywhere
/// attests to it; the tool has nothing to derive a reading from, and machine
/// transliteration was measured too crude to substitute (Abyss → "アブユッス",
/// Shinobi → "シュイノビ", Top → "トプ"). The tool's half is everything around
/// that: finding which words block what, ranking them by how many names each
/// releases, generating and merging the template, and composing the names once
/// the column is filled.
///
/// **Blank is a supported answer.** An unfilled row means "do not translate
/// this word", and every name containing it simply stays unresolved — the same
/// place it is today. Skipping a mod entirely costs nothing.
/// </summary>
public sealed class ModGlossary
{
    /// <summary>Japanese-column marker meaning "keep this token exactly as written".
    /// For a mod's internal tokens that are not words in any language and read
    /// fine untranslated — "SMP", "3BBB", "DLC1" — where both translating and
    /// giving up are wrong answers.</summary>
    public const string PassThroughMarker = "=";

    private readonly Dictionary<string, string> _entries;

    private ModGlossary(Dictionary<string, string> entries) => _entries = entries;

    public static ModGlossary Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public int FilledCount => _entries.Count;

    /// <summary>True if this word is marked pass-through. Asked separately from
    /// <see cref="TryTranslateWord"/> because the CONNECTOR depends on it: a token
    /// kept in English is a marker appended to the name, not a noun the previous
    /// word modifies, so it takes a space rather than "の" ("スカート - SMP",
    /// never "スカートのSMP").</summary>
    public bool IsPassThrough(string word) =>
        _entries.TryGetValue(word, out var value) && value == PassThroughMarker;

    /// <summary>True if <paramref name="word"/> has a filled Japanese value.
    /// A pass-through row answers with the English token unchanged.</summary>
    public bool TryTranslateWord(string word, out string japanese)
    {
        if (_entries.TryGetValue(word, out var value))
        {
            japanese = value == PassThroughMarker ? word : value;
            return true;
        }
        japanese = "";
        return false;
    }

    /// <summary>
    /// Resolved against the WORKING directory, unlike the other Data/ files
    /// (name_glossary.tsv, skyrim_taiyaku_reference.tsv), which load from
    /// <see cref="AppContext.BaseDirectory"/>. The difference is deliberate and
    /// load-bearing: those files ship with the tool and are read-only at run
    /// time, so serving them from the build output is correct. These are the
    /// opposite — the tool WRITES them and a person EDITS them, and
    /// AppContext.BaseDirectory is <c>bin/Debug/net9.0/</c>, where nobody would
    /// think to look and where a "PreserveNewest" copy could overwrite the
    /// edits. Every other user-facing path this tool takes (PickUpTarget/out_temp,
    /// Translation/out_temp) is working-directory-relative for the same reason.
    /// </summary>
    public static string DirectoryPath => Path.Combine(Directory.GetCurrentDirectory(), "Data", "mod_glossary");

    private static string PathFor(string plugin) =>
        Path.Combine(DirectoryPath, PromptGenerator.MakeSafeFolderName(plugin) + ".tsv");

    /// <summary>Loads the glossary for one plugin. Rows with an empty Japanese
    /// column are skipped rather than stored — an unfilled template row must
    /// behave exactly like no row at all.</summary>
    public static ModGlossary LoadFor(string plugin)
    {
        var path = PathFor(plugin);
        if (!File.Exists(path)) return Empty;

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        return new ModGlossary(entries);
    }

    /// <summary>One blocking word: how many of this plugin's name candidates it
    /// alone keeps unresolved, and a real example to judge the sense from.</summary>
    public sealed record Blocker(string Word, int BlockedCount, string Example);

    /// <summary>
    /// Writes (or MERGES into) this plugin's template.
    ///
    /// Merge, never overwrite: a row whose Japanese column is already filled
    /// keeps its value verbatim, and a row that no longer blocks anything is
    /// retained with a count of 0 rather than dropped. Both rules exist for the
    /// same reason — this file holds human work that the tool cannot reproduce,
    /// so a regeneration must never be able to destroy it. A mod update that
    /// renames items therefore ADDS rows and zeroes stale ones; it never loses a
    /// decision already made.
    /// </summary>
    public static void WriteTemplate(string plugin, IReadOnlyList<Blocker> blockers)
    {
        var path = PathFor(plugin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existingOrder = new List<string>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var cells = line.Split('\t');
                if (cells.Length < 1 || cells[0].Trim().Length == 0) continue;
                var english = cells[0].Trim();
                if (existing.ContainsKey(english)) continue;
                existing[english] = cells.Length > 1 ? cells[1].Trim() : "";
                existingOrder.Add(english);
            }
        }

        var current = blockers.ToDictionary(b => b.Word, b => b, StringComparer.OrdinalIgnoreCase);

        // Still-blocking words first (most names released first, so a person
        // filling this top-down gets the most back for the least work), then any
        // retired rows, kept only to preserve their filled values.
        var ordered = blockers.OrderByDescending(b => b.BlockedCount).ThenBy(b => b.Word, StringComparer.OrdinalIgnoreCase).ToList();
        var retired = existingOrder.Where(w => !current.ContainsKey(w)).ToList();

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine($"# {plugin} — glossary scoped to this mod only");
        writer.WriteLine("#");
        writer.WriteLine("# The words below are ones the tool could not resolve while trying to translate this mod's item");
        writer.WriteLine("# \"names\". Fill in the Japanese column and re-run translation to resolve every name containing it.");
        writer.WriteLine("# \"Remaining\" is how many candidates are STILL unresolved because of that word (most first).");
        writer.WriteLine("# After you fill it in and re-run, that word's \"Remaining\" becomes 0 — 0 is normal (it means done).");
        writer.WriteLine("#");
        writer.WriteLine("# * Write the translation as a NOUN. Words are automatically joined with the particle \"の\", so");
        writer.WriteLine("#   writing an adjectival/verb form (e.g. Japanese \"〜した\"/\"〜れた\"/\"〜く\") breaks the composition");
        writer.WriteLine("#   (produces something like \"卓越したの体力\", grammatically wrong).");
        writer.WriteLine("#   Example: 卓越した→卓越, 汚染された→汚染, 錆びた→錆, なびく→はためき");
        writer.WriteLine("#   A trailing \"の\" is also unnecessary (\"魂の\"→\"魂\" — keeping it produces the doubled \"魂のの抱擁\").");
        writer.WriteLine("#");
        writer.WriteLine("# - Leaving a row blank is fine — any name containing that word simply stays untranslated (in English).");
        writer.WriteLine("#   You can skip an entire mod's file too.");
        writer.WriteLine($"# - For a word that's an internal tag and should be output as-is, untranslated (e.g. \"SMP\", \"3BBB\"), write \"{PassThroughMarker}\" in the Japanese column.");
        writer.WriteLine("# - This glossary is used ONLY for this mod's candidates. Unlike the shared Data/name_glossary.tsv,");
        writer.WriteLine("#   it never affects any other mod, so it only needs to fit this mod's own item lineup (no need for strict generality).");
        writer.WriteLine("# - The Japanese column is preserved across regeneration (never overwritten), and rows are never dropped.");
        writer.WriteLine("#");
        writer.WriteLine("# English\tJapanese\tRemaining\tExample");

        foreach (var b in ordered)
            writer.WriteLine($"{b.Word}\t{existing.GetValueOrDefault(b.Word, "")}\t{b.BlockedCount}\t{b.Example}");

        foreach (var word in retired)
            writer.WriteLine($"{word}\t{existing[word]}\t0\t");
    }
}
