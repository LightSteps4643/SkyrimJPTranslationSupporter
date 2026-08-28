using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// RecordContextExtractor.For extracts per-record context (armor slot/type,
/// weapon animation type, NPC gender/race, book teaches-kind, spell/power/
/// ability, magic school) that feeds the LLM prompt. Wrong context degrades
/// translation QUALITY rather than corrupting data, but a regression here is
/// otherwise invisible — nothing else downstream would catch it.
///
/// Fixtures/PickUpTarget/RecordContextTest.esp bundles one record of every
/// switch arm this class has (3 armor types/slots, all 10 weapon animation
/// types, 2 NPCs male/female sharing one RACE, all 3 book Teaches arms, all
/// 7 spell types, all 5 magic schools) into a single plugin — built directly
/// (not through PickUpTargetRunner/MO2), since this class only needs a
/// Mutagen getter and a race-name lookup dictionary, both constructed by
/// hand here. Extended 2026-08-28 (coverage-driven pass) from an initial
/// smaller set that only exercised 1-2 arms per switch.
/// </summary>
public class RecordContextExtractorTests
{
    private static ISkyrimModGetter OpenFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "RecordContextTest.esp");
        var modKey = ModKey.FromNameAndExtension("RecordContextTest.esp");
        return SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, path), SkyrimRelease.SkyrimSE);
    }

    private static IReadOnlyDictionary<FormKey, string> RaceNames(ISkyrimModGetter mod) =>
        mod.Races.ToDictionary(r => r.FormKey, r => r.Name?.String ?? r.EditorID ?? "");

    [Fact]
    public void For_LightArmor_ReportsArmorTypeAndBodySlot()
    {
        var mod = OpenFixture();
        var armor = mod.Armors.Single(a => a.EditorID == "SjptsLightArmor");

        var context = RecordContextExtractor.For(armor, RaceNames(mod));

        Assert.Contains("light armor", context);
        Assert.Contains("slot: body", context);
    }

    [Fact]
    public void For_HeavyArmor_ReportsArmorTypeAndHeadSlot()
    {
        var mod = OpenFixture();
        var armor = mod.Armors.Single(a => a.EditorID == "SjptsHeavyArmor");

        var context = RecordContextExtractor.For(armor, RaceNames(mod));

        Assert.Contains("heavy armor", context);
        Assert.Contains("slot: head", context);
    }

    [Fact]
    public void For_Clothing_ReportsClothingAccessory()
    {
        var mod = OpenFixture();
        var armor = mod.Armors.Single(a => a.EditorID == "SjptsClothing");

        var context = RecordContextExtractor.For(armor, RaceNames(mod));

        Assert.Contains("clothing/accessory", context);
    }

    [Theory]
    [InlineData("SjptsSword", "one-handed sword")]
    [InlineData("SjptsBow", "bow")]
    [InlineData("SjptsDagger", "dagger")]
    [InlineData("SjptsOneHandAxe", "one-handed axe")]
    [InlineData("SjptsMace", "one-handed mace")]
    [InlineData("SjptsTwoHandSword", "two-handed sword")]
    [InlineData("SjptsTwoHandAxe", "two-handed axe/warhammer")]
    [InlineData("SjptsCrossbow", "crossbow")]
    [InlineData("SjptsStaff", "staff")]
    [InlineData("SjptsHandToHand", "hand-to-hand")]
    public void For_Weapon_ReportsTheAnimationType(string editorId, string expectedContext)
    {
        var mod = OpenFixture();
        var weapon = mod.Weapons.Single(w => w.EditorID == editorId);

        var context = RecordContextExtractor.For(weapon, RaceNames(mod));

        Assert.Equal(expectedContext, context);
    }

    [Fact]
    public void For_FemaleNpcWithRace_ReportsGenderAndRaceName()
    {
        var mod = OpenFixture();
        var npc = mod.Npcs.Single(n => n.EditorID == "SjptsFemaleNpc");

        var context = RecordContextExtractor.For(npc, RaceNames(mod));

        Assert.Contains("female", context);
        Assert.Contains("race: Test Race", context);
    }

    [Fact]
    public void For_MaleNpcWithRace_ReportsGenderAndRaceName()
    {
        var mod = OpenFixture();
        var npc = mod.Npcs.Single(n => n.EditorID == "SjptsMaleNpc");

        var context = RecordContextExtractor.For(npc, RaceNames(mod));

        Assert.Contains("male", context);
        Assert.DoesNotContain("female", context);
        Assert.Contains("race: Test Race", context);
    }

    [Fact]
    public void For_SpellTomeBook_ReportsSpellTome()
    {
        var mod = OpenFixture();
        var book = mod.Books.Single(b => b.EditorID == "SjptsSpellTome");

        var context = RecordContextExtractor.For(book, RaceNames(mod));

        Assert.Equal("spell tome (teaches a spell)", context);
    }

    [Fact]
    public void For_SkillBook_ReportsSkillBook()
    {
        var mod = OpenFixture();
        var book = mod.Books.Single(b => b.EditorID == "SjptsSkillBook");

        var context = RecordContextExtractor.For(book, RaceNames(mod));

        Assert.Equal("skill book (raises a skill)", context);
    }

    /// <summary>A book that teaches nothing (plain reading matter) gets no
    /// context hint at all — the empty-string default case.</summary>
    [Fact]
    public void For_PlainNovel_ReportsNoContext()
    {
        var mod = OpenFixture();
        var book = mod.Books.Single(b => b.EditorID == "SjptsNovel");

        var context = RecordContextExtractor.For(book, RaceNames(mod));

        Assert.Equal("", context);
    }

    [Theory]
    [InlineData("SjptsSpell", "spell")]
    [InlineData("SjptsPower", "power")]
    [InlineData("SjptsLesserPower", "lesser power")]
    [InlineData("SjptsAbility", "ability (passive)")]
    [InlineData("SjptsDisease", "disease")]
    [InlineData("SjptsPoison", "poison")]
    [InlineData("SjptsVoice", "Shout effect")]
    public void For_Spell_ReportsTheSpellType(string editorId, string expectedContext)
    {
        var mod = OpenFixture();
        var spell = mod.Spells.Single(s => s.EditorID == editorId);

        var context = RecordContextExtractor.For(spell, RaceNames(mod));

        Assert.Equal(expectedContext, context);
    }

    [Theory]
    [InlineData("SjptsMagicEffect", "Destruction magic")]
    [InlineData("SjptsMagicEffectRestoration", "Restoration magic")]
    [InlineData("SjptsMagicEffectConjuration", "Conjuration magic")]
    [InlineData("SjptsMagicEffectIllusion", "Illusion magic")]
    [InlineData("SjptsMagicEffectAlteration", "Alteration magic")]
    public void For_MagicEffect_ReportsTheMagicSchool(string editorId, string expectedContext)
    {
        var mod = OpenFixture();
        var mgef = mod.MagicEffects.Single(m => m.EditorID == editorId);

        var context = RecordContextExtractor.For(mgef, RaceNames(mod));

        Assert.Equal(expectedContext, context);
    }

    /// <summary>A record type not covered by any switch arm (RACE itself)
    /// gets the empty-string default, never throws.</summary>
    [Fact]
    public void For_UnhandledRecordType_ReturnsEmptyString()
    {
        var mod = OpenFixture();
        var race = mod.Races.Single();

        var context = RecordContextExtractor.For(race, RaceNames(mod));

        Assert.Equal("", context);
    }
}
