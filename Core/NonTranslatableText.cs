using System.Text.RegularExpressions;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Rejects candidate strings that pass <see cref="LanguageDetector.IsTranslatableEnglish"/>
/// (they contain ASCII letters and no Japanese) but are not player-readable
/// TEXT at all — they are markup whose visible result is a picture.
///
/// Found by inspecting real data per DSD type: 90% of this load order's 686
/// <c>ACTI RNAM</c> / <c>FLOR RNAM</c> candidates (the "activation prompt" type,
/// which should hold a short verb like "Search") were actually icon-font glyphs
/// such as <c>&lt;font face="Iconographia"&gt;G&lt;/Font&gt;</c>, emitted by a
/// Skymoji-style mod that replaces the prompt with a pictogram. Translating the
/// letter "G" to Japanese would replace the icon with a word — strictly worse
/// than leaving it alone. 649 candidates across ACTI/FLOR RNAM and PERK
/// EPF2/EPFD were of this shape.
/// </summary>
public static class NonTranslatableText
{
    private static readonly Regex Tag = new("<[^>]*>", RegexOptions.Compiled);

    /// <summary>Fonts whose "letters" render as pictograms rather than as the
    /// Latin characters they nominally are. Deliberately an explicit list, not a
    /// heuristic: Skyrim's own in-book fonts ($HandwrittenFont, $SkyrimBooks,
    /// and even the deliberately-unreadable $DaedricFont/$DragonFont/$FalmerFont)
    /// all wrap REAL text that should still be translated, so guessing from the
    /// markup alone would throw away genuine content. Note the missing "$" — an
    /// icon font supplied by a mod is named literally, where Skyrim's built-in
    /// font aliases all start with "$".</summary>
    private static readonly string[] IconFonts = { "Iconographia" };

    /// <summary>
    /// True if the string carries no translatable text once markup is accounted for.
    /// Two independent rules, both confirmed against real candidates:
    ///
    /// 1. Stripping tags leaves no letters at all — e.g. a book page that is only
    ///    <c>&lt;img src='...'/&gt;</c>, or pure layout markup (23 real candidates).
    /// 2. The markup names an icon font AND at most one visible character survives —
    ///    that character IS the pictogram (619 real candidates). Crucially this
    ///    keeps the 30 real cases shaped like
    ///    <c>&lt;font face="Iconographia"&gt;k&lt;/Font&gt;&lt;br&gt;Forge</c>,
    ///    where a glyph merely PREFIXES genuine text ("Forge") that must still be
    ///    translated.
    /// </summary>
    public static bool IsMarkupOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!text.Contains('<')) return false; // fast path: no markup at all

        var visible = Tag.Replace(text, "").Trim();

        if (!visible.Any(char.IsLetter)) return true;

        var usesIconFont = IconFonts.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
        return usesIconFont && visible.Length <= 1;
    }

    /// <summary>
    /// True if the string is an EditorID-style internal identifier that was never
    /// written to be read — <c>DialogueGenericGoodbye</c>, <c>AudioTemplateElk</c>,
    /// <c>shieldChargeDamageStamina</c>, <c>MS08Paralysis</c>, <c>TestJeremyBig</c>.
    ///
    /// The test is deliberately narrow: NO whitespace at all, plus either a
    /// lower→upper camel-case boundary or a letters-and-digits mix. Requiring the
    /// absence of spaces is what makes it safe — every genuine display name of
    /// more than one word has them, so nothing like "Quick Draw" or "Sounds good.
    /// Let's go." can be caught. Verified against real candidates: of 486 DIAL
    /// FULL entries, the 132 without spaces are all internal topic IDs while the
    /// 354 with spaces are all real dialogue lines; the QUST FULL hits are
    /// dialogue-holder quests (<c>CreatureDialogueWerewolf</c>) that never appear
    /// in the journal.
    ///
    /// A single-word CamelCase name a mod author simply wrote without a space
    /// (<c>AncestralAwakening</c>) is the residual false-positive risk, which is
    /// why every exclusion is written to the run log for review.
    /// </summary>
    public static bool LooksLikeInternalIdentifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim();
        if (t.Length < 4) return false;
        if (t.Any(char.IsWhiteSpace)) return false;

        var hasCamelBoundary = false;
        for (var i = 1; i < t.Length; i++)
            if (char.IsLower(t[i - 1]) && char.IsUpper(t[i])) { hasCamelBoundary = true; break; }

        var mixesLettersAndDigits = t.Any(char.IsLetter) && t.Any(char.IsDigit);

        // v0.27.0: an acronym-then-word boundary (an UPPERCASE run of 2+ letters
        // immediately followed by a lowercase letter), e.g. USSEP's internal
        // dialogue topic ID "DCETAttack" — a run "DCETA" then "ttack". The
        // pre-existing lower→upper check alone misses this shape because there
        // is no lower letter before the run starts.
        var hasAcronymBoundary = false;
        for (var i = 1; i < t.Length; i++)
        {
            if (!char.IsLower(t[i])) continue;
            var upperRun = 0;
            for (var j = i - 1; j >= 0 && char.IsUpper(t[j]); j--) upperRun++;
            if (upperRun >= 2) { hasAcronymBoundary = true; break; }
        }

        // v0.48.1: SNAKE_CASE — an underscore-separated run of uppercase
        // letters/digits, e.g. a GMST DATA value like "REF_ATTACH_NODE". Real
        // display text never uses a bare underscore as a word separator (games
        // use spaces, hyphens, or camelCase for that), so this is a safe,
        // vowel-independent signal distinct from LooksLikeNonWordAcronym (which
        // requires the whole string to be vowel-less and rejects underscores
        // outright via its own whitespace-style character-class check).
        var hasSnakeCaseBoundary = t.Contains('_')
            && t.Where(c => c != '_').All(c => (char.IsUpper(c) && char.IsLetter(c)) || char.IsDigit(c));

        return hasCamelBoundary || mixesLettersAndDigits || hasAcronymBoundary || hasSnakeCaseBoundary;
    }

    /// <summary>
    /// True if the string begins with "AudioTemplate" — a Bethesda/modder
    /// naming convention for an NPC_ record that exists purely to hold
    /// voice-type/audio-template data for OTHER NPCs to reference via their
    /// own Template field, never placed or seen in the world. Usually written
    /// as one CamelCase word ("AudioTemplateElk", already caught by
    /// <see cref="LooksLikeInternalIdentifier"/>), but real data shows it can
    /// also be written with a space ("AudioTemplate Lurker", Dragonborn.esm's
    /// own NPC_ FULL) — which defeats that method's no-whitespace requirement.
    /// Narrow by design: only this one literal prefix.
    /// </summary>
    public static bool LooksLikeAudioTemplateName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.TrimStart().StartsWith("AudioTemplate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if the string begins with "Do Not Delete" — a real Creation Kit
    /// convention: a placeholder/keep-alive record kept only to preserve
    /// FormID ordering during an export, with a warning left in its own name
    /// field for future editors (never a real display name). Real example:
    /// Dragonborn.esm's own NPC_ FULL "Do Not Delete Me - needed for export
    /// to work".
    /// </summary>
    public static bool LooksLikeDoNotDeleteNote(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.TrimStart().StartsWith("Do Not Delete", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if the string is a version-number-bearing internal quest name — e.g.
    /// USSEP's <c>QUST FULL</c> entries "Retroactive fixes for 4.2.1", "UDGP
    /// Retroactive Fixed for 1.1.2", "Version tracking for the UDGP". These are
    /// never shown in the quest journal; they exist so the mod's own Papyrus
    /// scripts can detect which fix-pack revision already ran. 111 of USSEP's
    /// 161 untranslated candidates were this shape.
    ///
    /// Deliberately scoped by the CALLER to <c>QUST FULL</c> only (this method
    /// just tests the text) — a bare version-number pattern is common enough in
    /// genuine content elsewhere (a book title, an item's stated edition) that
    /// applying it load-order-wide would be reckless.
    /// </summary>
    private static readonly Regex VersionNumber = new(@"\b\d+\.\d+(\.\d+)?\b", RegexOptions.Compiled);

    public static bool LooksLikeVersionTrackingQuestName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return VersionNumber.IsMatch(text);
    }

    /// <summary>
    /// True if the string is an internal effect/placeholder name ending in a
    /// literal " fx" marker — e.g. "AtronachFrost fx", "DLC01 Soul Cairn necro
    /// skeleton fx". Mod authors use this suffix as a convention for effect
    /// records that back a visual/sound effect rather than name anything the
    /// player sees. Narrow by design: only the exact trailing token, so a
    /// genuine name that happens to contain "fx" elsewhere (or as its own word
    /// mid-sentence) is untouched.
    /// </summary>
    public static bool LooksLikeInternalFxName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimEnd();
        return t.EndsWith(" fx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if the string contains "temp" as a STANDALONE word (any case) — the
    /// convention Bethesda's own developers used to mark a record as a
    /// work-in-progress stand-in. Real examples from Skyrim.esm's own name
    /// fields: "TEMP", "TEMP - LIGHTS OUT", "TEMP - TREASURE",
    /// "(temp) FX Placeholder", "Colovian Brandy TEMP".
    ///
    /// <see cref="IsPlaceholderToken"/> cannot catch these: it is a whole-string
    /// match, so the bare "TEMP" hits but every variant carrying additional
    /// words slips past — and then <see cref="Translation.NameFallbackTranslator"/>
    /// half-translates the remainder into visible nonsense
    /// ("TEMP - LIGHTS OUT" → "TEMP - 光のOUT", confirmed in real output).
    ///
    /// The word boundary is load-bearing: matching "temp" as a substring would
    /// also flag "Temple", "Temper", "Attempt", "Template" — all genuine words
    /// that appear in real Skyrim content.
    ///
    /// Deliberately scoped by the CALLER to "* FULL" (name) types. The corpus
    /// holds 13 quest-stage/scene notes that legitimately BEGIN with "TEMP:" or
    /// "(temp placeholder)" and were nonetheless translated in the official
    /// Japanese release; those are long descriptive fields, not names, so
    /// restricting this to name types keeps them untouched. All 34 real hits in
    /// this load order are name types, so the restriction costs nothing.
    /// </summary>
    private static readonly Regex TempWord =
        new(@"(?<![A-Za-z])temp(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool LooksLikeDevTempPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return TempWord.IsMatch(text);
    }

    private static readonly HashSet<string> PlaceholderTokens =
        new(StringComparer.OrdinalIgnoreCase) { "xxx", "todo", "tbd", "n/a", "test", "placeholder" };

    /// <summary>
    /// True if the ENTIRE (trimmed) string is a known placeholder token — e.g.
    /// USSEP's <c>QUST NNAM</c> value "xxx". Whole-string match only, never a
    /// substring match: "test" alone is a placeholder, but "Test the waters"
    /// (a real line) must not be caught by it.
    /// </summary>
    public static bool IsPlaceholderToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return PlaceholderTokens.Contains(text.Trim());
    }

    private const string Vowels = "AEIOU";

    /// <summary>
    /// True if the string is an all-uppercase run with no vowel — e.g. "YMMP"
    /// (a mod's own plugin-name acronym leaking into a FULL field), or "NFL"/
    /// "MTV"-shaped abbreviations. A real English word or display name almost
    /// always contains a vowel, so the absence is a cheap, dictionary-free
    /// signal that the string is an acronym/tag rather than prose — at the cost
    /// of also excluding genuine vowel-less abbreviations, which are equally
    /// not worth machine-translating anyway (there's no Japanese equivalent to
    /// look up for an acronym).
    ///
    /// Length ≥ 3 avoids flagging short real abbreviations no exclusion is
    /// worth the false-positive risk for ("Mr", "Dr" — already blocked from
    /// even reaching here in practice, but kept as an explicit floor). "Y" is
    /// treated as a consonant, matching common convention for this kind of
    /// heuristic (a lone "Y" is far more often a soft-sign/initial than a true
    /// vowel across the words this would otherwise flag).
    /// </summary>
    public static bool LooksLikeNonWordAcronym(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Length < 3) return false;
        if (t.Any(char.IsWhiteSpace)) return false;
        if (!t.All(c => char.IsUpper(c) && char.IsLetter(c))) return false;
        return !t.Any(c => Vowels.Contains(c));
    }

    private static readonly string[] AssetExtensions = { ".nif", ".dds", ".wav", ".xwm", ".hkx", ".pex", ".swf", ".bik", ".tga" };

    /// <summary>
    /// True if the WHOLE string is an asset path — e.g. the texture and mesh paths
    /// stored in game settings (<c>Cubemaps\CaveGreenCube_e.dds</c>,
    /// <c>Effects\FXEmptyObject.nif</c>). Translating one would break the path
    /// rather than merely waste effort, so the value here exceeds the 12 hits.
    ///
    /// "Whole string" is load-bearing: an earlier attempt that matched any
    /// candidate CONTAINING a path flagged 2,000+ book bodies, because book text
    /// legitimately embeds <c>&lt;img src='img://Textures/...'&gt;</c>.
    /// </summary>
    public static bool IsAssetPath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim();
        if (t.Any(char.IsWhiteSpace)) return false;

        return AssetExtensions.Any(e => t.EndsWith(e, StringComparison.OrdinalIgnoreCase))
               || t.Contains('\\');
    }
}
