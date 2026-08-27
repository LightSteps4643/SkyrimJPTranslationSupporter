using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// NonTranslatableText's rules were each derived from real-data investigation
/// (see the class's own XML comments — icon-font glyphs, EditorID-style
/// internal identifiers, dev placeholders, asset paths...). There is no
/// Data/ file backing any of this (it is pure Core logic, not curated
/// vocabulary), so these cases are transcribed directly from the class's own
/// documented real examples rather than sourced from a fixture file.
/// </summary>
public class NonTranslatableTextTests
{
    [Theory]
    [InlineData("<font face=\"Iconographia\">G</Font>", true)] // icon font, 1 visible char (real: 619 ACTI/FLOR RNAM candidates)
    [InlineData("<img src='foo.dds'/>", true)] // pure layout markup, no letters at all
    [InlineData("<font face=\"Iconographia\">k</Font><br>Forge", false)] // icon glyph PREFIXES real text — must still be translated
    [InlineData("Search", false)] // plain text, no markup at all (fast path)
    [InlineData("<font face=\"$HandwrittenFont\">A letter from home</font>", false)] // built-in Skyrim font wraps REAL text, not a pictogram
    public void IsMarkupOnly(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.IsMarkupOnly(text));
    }

    [Theory]
    [InlineData("DialogueGenericGoodbye", true)] // lower->upper camelCase boundary
    [InlineData("AudioTemplateElk", true)]
    [InlineData("shieldChargeDamageStamina", true)]
    [InlineData("MS08Paralysis", true)] // letters+digits mix
    [InlineData("TestJeremyBig", true)]
    [InlineData("DCETAttack", true)] // v0.27.0: acronym-run-then-lowercase boundary (USSEP)
    [InlineData("REF_ATTACH_NODE", true)] // v0.48.1: SNAKE_CASE
    [InlineData("Quick Draw", false)] // has a space -> real display name, never caught
    [InlineData("Sounds good. Let's go.", false)]
    [InlineData("Ab", false)] // below the length floor
    public void LooksLikeInternalIdentifier(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.LooksLikeInternalIdentifier(text));
    }

    [Theory]
    [InlineData("AudioTemplateElk", true)]
    [InlineData("AudioTemplate Lurker", true)] // real data: Dragonborn.esm NPC_ FULL, written with a space
    [InlineData("Elk", false)]
    public void LooksLikeAudioTemplateName(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.LooksLikeAudioTemplateName(text));
    }

    [Theory]
    [InlineData("Do Not Delete Me - needed for export to work", true)] // real: Dragonborn.esm NPC_ FULL
    [InlineData("Please do not delete this", false)] // "Do Not Delete" must be at the START
    public void LooksLikeDoNotDeleteNote(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.LooksLikeDoNotDeleteNote(text));
    }

    [Theory]
    [InlineData("Retroactive fixes for 4.2.1", true)] // real: USSEP QUST FULL
    [InlineData("UDGP Retroactive Fixed for 1.1.2", true)]
    [InlineData("Compatibility patch for 2.0", true)] // 2-part x.y pattern (the regex's optional 3rd group) — not covered by either real 3-part example above
    [InlineData("Steel Sword", false)] // no version-number pattern at all
    [InlineData("Version tracking for the UDGP", false)] // no digits — despite being from the same USSEP family, this specific string carries no version number
    public void LooksLikeVersionTrackingQuestName(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.LooksLikeVersionTrackingQuestName(text));
    }

    [Theory]
    [InlineData("AtronachFrost fx", true)] // real example
    [InlineData("DLC01 Soul Cairn necro skeleton fx", true)] // real example
    [InlineData("Half fx", true)]
    [InlineData("Effects", false)] // does not end in the exact " fx" token
    [InlineData("fx incoming", false)] // "fx" not at the END
    public void LooksLikeInternalFxName(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.LooksLikeInternalFxName(text));
    }

    [Theory]
    [InlineData("TEMP", true)] // real: Skyrim.esm
    [InlineData("TEMP - LIGHTS OUT", true)]
    [InlineData("TEMP - TREASURE", true)]
    [InlineData("(temp) FX Placeholder", true)]
    [InlineData("Colovian Brandy TEMP", true)]
    [InlineData("Temple", false)] // word-boundary guard — a genuine word containing "temp" as a substring
    [InlineData("Temper", false)]
    [InlineData("Attempt", false)]
    [InlineData("Template", false)]
    public void LooksLikeDevTempPlaceholder(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.LooksLikeDevTempPlaceholder(text));
    }

    [Theory]
    [InlineData("xxx", true)] // real: USSEP QUST NNAM
    [InlineData("TODO", true)] // case-insensitive
    [InlineData("tbd", true)]
    [InlineData("n/a", true)]
    [InlineData("test", true)]
    [InlineData("placeholder", true)]
    [InlineData("Test the waters", false)] // whole-string match only — a real line containing the token as one word must not be caught
    public void IsPlaceholderToken(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.IsPlaceholderToken(text));
    }

    [Theory]
    [InlineData("YMMP", true)] // real: a mod's own plugin-name acronym leaking into a FULL field
    [InlineData("NFL", true)]
    [InlineData("MTV", true)]
    [InlineData("Mr", false)] // below the length floor
    [InlineData("TEST", false)] // contains a vowel -> not caught (this heuristic is vowel-based, not a dictionary)
    [InlineData("NFL TEAM", false)] // contains whitespace -> not a single acronym token
    [InlineData("Nfl", false)] // mixed case -> not an all-uppercase run
    public void LooksLikeNonWordAcronym(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.LooksLikeNonWordAcronym(text));
    }

    [Theory]
    [InlineData("Cubemaps\\CaveGreenCube_e.dds", true)] // real example
    [InlineData("Effects\\FXEmptyObject.nif", true)] // real example
    [InlineData("Sword", false)] // no extension, no backslash
    public void IsAssetPath(string text, bool expected)
    {
        Assert.Equal(expected, NonTranslatableText.IsAssetPath(text));
    }

    /// <summary>The false-positive guard the class's own remarks call out by
    /// name: an earlier attempt matched any candidate CONTAINING a path,
    /// which flagged 2,000+ book bodies because book text legitimately embeds
    /// `&lt;img src='img://Textures/...'&gt;`. "Whole string" (via the
    /// whitespace check) is what prevents that regression.</summary>
    [Fact]
    public void IsAssetPath_PathEmbeddedInProse_IsNotCaught()
    {
        Assert.False(NonTranslatableText.IsAssetPath("See the picture <img src='img://Textures/foo.dds'/> below."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AllPredicates_NullOrWhitespaceInput_ReturnFalse(string? text)
    {
        // IsMarkupOnly is the one deliberate exception: whitespace/empty text has
        // no visible translatable content, so it counts as "markup only" (nothing
        // to translate) rather than "not markup".
        Assert.True(NonTranslatableText.IsMarkupOnly(text));

        Assert.False(NonTranslatableText.LooksLikeInternalIdentifier(text));
        Assert.False(NonTranslatableText.LooksLikeAudioTemplateName(text));
        Assert.False(NonTranslatableText.LooksLikeDoNotDeleteNote(text));
        Assert.False(NonTranslatableText.LooksLikeVersionTrackingQuestName(text));
        Assert.False(NonTranslatableText.LooksLikeInternalFxName(text));
        Assert.False(NonTranslatableText.LooksLikeDevTempPlaceholder(text));
        Assert.False(NonTranslatableText.IsPlaceholderToken(text));
        Assert.False(NonTranslatableText.LooksLikeNonWordAcronym(text));
        Assert.False(NonTranslatableText.IsAssetPath(text));
    }
}
