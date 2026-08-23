using System.Text.RegularExpressions;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Shared "does this English text look like a real Bethesda name field, not an
/// internal developer note" filter. Originally written for
/// <c>CorpusTransliterator</c>'s dictionary-mining input (see its remarks for the
/// FACT "used for combat" story — 13,492 of one load order's corpus entries were
/// sentence-like internal notes, not player-visible names). Also applied to
/// <c>PrecedentRetriever</c>'s reference-example index, since the same noise
/// pollutes AI-chat prompt "参考例" output otherwise.
/// </summary>
public static class NameFieldFilter
{
    // Prepositions/articles that stay lowercase by convention even inside an
    // otherwise Title Case English name (e.g. "Eye of Magnus", "Blade of Woe") —
    // without this exemption, every single "X of Y" name would be rejected
    // outright (the word "of" has no uppercase letter at all).
    private static readonly HashSet<string> LowercaseTitleConnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "the", "and", "in", "on", "at", "a", "an",
    };

    private static readonly Regex Ordinal = new(@"^\d+(st|nd|rd|th)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A genuine Bethesda name field is Title Case throughout by
    /// convention, so requiring every space-separated word to contain an
    /// uppercase letter SOMEWHERE (checking specifically the first letter would
    /// be wrong: Orc patronymic names like "Urag gro-Shub" keep "gro-" lowercase
    /// even in the official Title Case name field, but "gro-Shub" still contains
    /// an uppercase letter — just not at position 0) is a simple, effective
    /// filter against sentence-like fragments such as "for not using casual
    /// idles".
    ///
    /// v0.29.7: a word that is ALL DIGITS ("48", "0" — a numbered variant like
    /// "Fawnia Boots 2") or exactly "-" (a bare separator like "Wayward Knight
    /// Helmet - Faceless") also passes. Neither carries the "this is sentence
    /// prose" signal the uppercase check exists to catch — a lone number or dash
    /// is neutral, never part of an internal developer note. Before this, a
    /// numbered variant name failed the check on that ONE token and got
    /// rejected whole, even though the surrounding words were real, resolvable
    /// names — found via real data: 844 of one load order's 1,156 unresolved
    /// ARMO FULL candidates carried a bare number this way.
    ///
    /// v0.29.11: an ordinal ("45th", "3rd" — "Twilight Princess Book - Cursed,
    /// 45th Edition") also passes, same reasoning as the bare-digit case above —
    /// it's neutral, not a signal of sentence prose.</summary>
    public static bool LooksLikeNameField(string eng) =>
        eng.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => w.Any(char.IsUpper) || LowercaseTitleConnectors.Contains(w) || w == "-" || w.All(char.IsDigit) || Ordinal.IsMatch(w));
}
