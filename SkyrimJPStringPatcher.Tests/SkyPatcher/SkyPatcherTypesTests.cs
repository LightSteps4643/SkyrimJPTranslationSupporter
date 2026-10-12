using SkyrimJPStringPatcher.SkyPatcher;

namespace SkyrimJPStringPatcher.Tests.SkyPatcher;

/// <summary>
/// The record types whose display name SkyPatcher can set ("fullName=~…~"; NPCs also
/// "shortName"): the 14 patcher subfolders below. Expected values come from the SkyPatcher
/// grammar's record-type table (§2: subfolder, toggle, primary filter, xEdit signature) and
/// DSD's "&lt;SIG&gt; FULL"/"NPC_ SHRT" type strings.
/// </summary>
public class SkyPatcherTypesTests
{
    public static IEnumerable<object[]> Types() =>
    [
        ["ingestible", "filterByAlchs", "iEnableIngestiblePatching", "ALCH FULL"],
        ["ammo", "filterByAmmos", "iEnableAmmoPatching", "AMMO FULL"],
        ["armor", "filterByArmors", "iEnableArmorPatching", "ARMO FULL"],
        ["book", "filterByBooks", "iEnableBookPatching", "BOOK FULL"],
        ["enchantment", "filterByEnchs", "iEnableEnchantmentPatching", "ENCH FULL"],
        ["ingredient", "filterByIngs", "iEnableIngredientPatching", "INGR FULL"],
        ["location", "filterByLocations", "iEnableLocationPatching", "LCTN FULL"],
        ["magicEffect", "filterByMgefs", "iEnableMagicEffectPatching", "MGEF FULL"],
        ["misc", "filterByMiscs", "iEnableMiscPatching", "MISC FULL"],
        ["npc", "filterByNpcs", "iEnableNPCPatching", "NPC_ FULL"],
        ["scroll", "filterByScrolls", "iEnableScrollPatching", "SCRL FULL"],
        ["soulGem", "filterBySoulGems", "iEnableSoulGemPatching", "SLGM FULL"],
        ["spell", "filterBySpells", "iEnableSpellPatching", "SPEL FULL"],
        ["weapon", "filterByWeapons", "iEnableWeaponPatching", "WEAP FULL"],
    ];

    [Theory]
    [MemberData(nameof(Types))]
    public void EachNameBearingSubfolder_HasItsPrimaryFilterToggleAndDsdType(string subfolder, string primaryFilter, string toggle, string fullNameType)
    {
        Assert.True(SkyPatcherTypes.TryGet(subfolder, out var type));
        Assert.Equal(primaryFilter, type.PrimaryFilter);
        Assert.Equal(toggle, type.Toggle);
        Assert.Equal(fullNameType, type.FullNameDsdType);
    }

    [Fact]
    public void OnlyNpcs_CanSetAShortName()
    {
        Assert.True(SkyPatcherTypes.TryGet("npc", out var npc));
        Assert.Equal("NPC_ SHRT", npc.ShortNameDsdType);

        foreach (var row in Types().Where(r => (string)r[0] != "npc"))
        {
            Assert.True(SkyPatcherTypes.TryGet((string)row[0], out var other));
            Assert.Null(other.ShortNameDsdType);
        }
    }

    [Theory]
    [InlineData("Armor")]
    [InlineData("ARMOR")]
    [InlineData("magiceffect")]
    public void SubfolderMatching_IgnoresCase(string subfolder)
    {
        // Windows file systems are case-insensitive (grammar §2).
        Assert.True(SkyPatcherTypes.TryGet(subfolder, out _));
    }

    [Theory]
    [InlineData("race")]
    [InlineData("container")]
    [InlineData("leveledList")]
    [InlineData("reference")]
    [InlineData("")]
    public void RecordTypesThatHaveNoDisplayNameOperation_AreNotNameBearing(string subfolder)
    {
        Assert.False(SkyPatcherTypes.TryGet(subfolder, out _));
    }
}
