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
/// Fixtures/PickUpTarget/RecordContextTest.esp bundles one record of each
/// switch arm (2 armor slots/types, 2 weapon animation types, 2 NPCs
/// male/female sharing one RACE, all 3 book Teaches arms, spell/power spell
/// types, one magic effect) into a single plugin — built directly (not
/// through PickUpTargetRunner/MO2), since this class only needs a Mutagen
/// getter and a race-name lookup dictionary, both constructed by hand here.
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
    public void For_OneHandSword_ReportsOneHandedSword()
    {
        var mod = OpenFixture();
        var weapon = mod.Weapons.Single(w => w.EditorID == "SjptsSword");

        var context = RecordContextExtractor.For(weapon, RaceNames(mod));

        Assert.Equal("one-handed sword", context);
    }

    [Fact]
    public void For_Bow_ReportsBow()
    {
        var mod = OpenFixture();
        var weapon = mod.Weapons.Single(w => w.EditorID == "SjptsBow");

        var context = RecordContextExtractor.For(weapon, RaceNames(mod));

        Assert.Equal("bow", context);
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

    [Fact]
    public void For_Spell_ReportsSpell()
    {
        var mod = OpenFixture();
        var spell = mod.Spells.Single(s => s.EditorID == "SjptsSpell");

        var context = RecordContextExtractor.For(spell, RaceNames(mod));

        Assert.Equal("spell", context);
    }

    [Fact]
    public void For_Power_ReportsPower()
    {
        var mod = OpenFixture();
        var power = mod.Spells.Single(s => s.EditorID == "SjptsPower");

        var context = RecordContextExtractor.For(power, RaceNames(mod));

        Assert.Equal("power", context);
    }

    [Fact]
    public void For_MagicEffect_ReportsMagicSchool()
    {
        var mod = OpenFixture();
        var mgef = mod.MagicEffects.Single(m => m.EditorID == "SjptsMagicEffect");

        var context = RecordContextExtractor.For(mgef, RaceNames(mod));

        Assert.Equal("Destruction magic", context);
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
