namespace SkyrimJPStringPatcher.SkyPatcher;

/// <summary>One SkyPatcher patcher whose records have a display name SkyPatcher can set: its
/// subfolder under SKSE/Plugins/SkyPatcher, the filter that selects its records by form (the
/// "primary filter"), the SkyPatcher.ini toggle that switches the whole subfolder on/off, and the
/// DSD type strings our candidates carry for its names.</summary>
public sealed record SkyPatcherType(string Subfolder, string PrimaryFilter, string Toggle, string FullNameDsdType, string? ShortNameDsdType = null);

/// <summary>The 14 patcher subfolders whose records have a name ("fullName=~…~"; NPCs also
/// "shortName=~…~"). Source: the SkyPatcher grammar's record-type table (subfolder, toggle,
/// primary filter, xEdit signature); other patchers (race, container, …) set no display name.</summary>
public static class SkyPatcherTypes
{
    private static readonly Dictionary<string, SkyPatcherType> BySubfolder = new SkyPatcherType[]
    {
        new("ingestible", "filterByAlchs", "iEnableIngestiblePatching", "ALCH FULL"),
        new("ammo", "filterByAmmos", "iEnableAmmoPatching", "AMMO FULL"),
        new("armor", "filterByArmors", "iEnableArmorPatching", "ARMO FULL"),
        new("book", "filterByBooks", "iEnableBookPatching", "BOOK FULL"),
        new("enchantment", "filterByEnchs", "iEnableEnchantmentPatching", "ENCH FULL"),
        new("ingredient", "filterByIngs", "iEnableIngredientPatching", "INGR FULL"),
        new("location", "filterByLocations", "iEnableLocationPatching", "LCTN FULL"),
        new("magicEffect", "filterByMgefs", "iEnableMagicEffectPatching", "MGEF FULL"),
        new("misc", "filterByMiscs", "iEnableMiscPatching", "MISC FULL"),
        new("npc", "filterByNpcs", "iEnableNPCPatching", "NPC_ FULL", "NPC_ SHRT"),
        new("scroll", "filterByScrolls", "iEnableScrollPatching", "SCRL FULL"),
        new("soulGem", "filterBySoulGems", "iEnableSoulGemPatching", "SLGM FULL"),
        new("spell", "filterBySpells", "iEnableSpellPatching", "SPEL FULL"),
        new("weapon", "filterByWeapons", "iEnableWeaponPatching", "WEAP FULL"),
    }.ToDictionary(t => t.Subfolder, StringComparer.OrdinalIgnoreCase);

    /// <summary>Case-insensitive: Windows file systems are (grammar §2).</summary>
    public static bool TryGet(string subfolder, out SkyPatcherType type)
    {
        if (BySubfolder.TryGetValue(subfolder, out var found)) { type = found; return true; }
        type = null!;
        return false;
    }
}
