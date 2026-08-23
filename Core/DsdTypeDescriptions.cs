namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Turns a DSD type string ("ARMO FULL", "ACTI RNAM", ...) into a short English
/// description of what that string actually IS in game — "the name of a piece of
/// armor/clothing", "the action prompt (verb) shown when examining it" and so on.
///
/// The point (user's observation): the xEdit signature embedded in every DSD type
/// string already tells you the string's PURPOSE, and purpose is exactly the
/// context a translator needs. "Coat" under ARMO FULL is a garment; the same word
/// elsewhere might not be. Until now the generated prompt printed the raw
/// "[ARMO FULL @ 000800:Sentinel.esp]" tag and left the AI to infer what that
/// meant; now it can be told outright.
///
/// Descriptions compose from two halves — what the RECORD is (from the 4-char
/// signature) and which FIELD of it is being translated (from the 4-char
/// subrecord) — so a type this table has never seen still degrades to something
/// useful rather than to nothing. Subrecords whose meaning does NOT follow from
/// the record type (a quest's CNAM is a journal entry, not "a quest's CNAM") are
/// listed as explicit whole-type overrides.
///
/// v0.48.0: translated to English (see prompt.txt's own remarks — the AI-chat/
/// local-LLM prompt this feeds into is fixed to English throughout, on the
/// theory that instructions to an LLM generally work best in English and this
/// tool may see non-Japanese-speaking maintainers/users in the future).
/// </summary>
public static class DsdTypeDescriptions
{
    /// <summary>Whole-type meanings that composition would get wrong. Verified
    /// against DSD's own getTranslationType() mapping (see DESIGN_NOTES.md).</summary>
    private static readonly Dictionary<string, string> WholeType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACTI RNAM"] = "the action prompt shown when looking at the object (a short verb like \"Examine\"/\"Take\")",
        ["FLOR RNAM"] = "the action prompt shown when looking at a plant/vein (a short verb like \"Harvest\")",
        ["BOOK CNAM"] = "a book's body text (long-form; may contain formatting tags — leave tags untranslated, as-is)",
        ["DIAL FULL"] = "a dialogue topic heading (may contain an internal-management identifier)",
        ["GMST DATA"] = "generic UI text stored as a game setting",
        ["INFO NAM1"] = "an NPC's actual spoken dialogue line",
        ["INFO RNAM"] = "a player-side dialogue choice shown in the conversation menu",
        ["LSCR DESC"] = "a loading-screen tip / lore blurb",
        ["MESG ITXT"] = "a message-box button label (short)",
        ["MGEF DNAM"] = "a magic effect's description (shown on-screen as the spell's effect)",
        ["NPC_ SHRT"] = "an NPC's short name/title",
        ["PERK EPF2"] = "a button label for an action prompt a perk overrides (short)",
        ["PERK EPFD"] = "the verb text for an action prompt a perk overrides (short)",
        ["QUST CNAM"] = "text recorded in the quest journal",
        ["QUST NNAM"] = "text shown in the quest tracker as an objective",
        ["REGN RDMP"] = "a region's display name on the map",
        ["WOOP TNAM"] = "one \"Word\" making up a Shout (a single Dragon Language word)",
    };

    /// <summary>What each 4-char record signature IS. Used for the generic
    /// "<field> of a <record>" composition below.</summary>
    private static readonly Dictionary<string, string> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACTI"] = "an object that can be examined",
        ["ALCH"] = "a potion/food item",
        ["AMMO"] = "an arrow/bolt",
        ["ARMO"] = "a piece of armor/clothing",
        ["BOOK"] = "a book/scroll",
        ["CELL"] = "an interior area",
        ["CONT"] = "a container",
        ["DOOR"] = "a door",
        ["ENCH"] = "an enchantment effect",
        ["EXPL"] = "an explosion effect",
        ["FLOR"] = "a harvestable plant/vein",
        ["FURN"] = "furniture/a workbench",
        ["HAZD"] = "a placed hazard",
        ["INGR"] = "an alchemy ingredient",
        ["KEYM"] = "a key",
        ["LCTN"] = "a location (place name)",
        ["LSCR"] = "a loading screen",
        ["MESG"] = "a message",
        ["MGEF"] = "a magic effect",
        ["MISC"] = "a misc. item",
        ["NPC_"] = "an NPC",
        ["PERK"] = "a perk",
        ["PROJ"] = "a projectile",
        ["QUST"] = "a quest",
        ["RACE"] = "a race",
        ["SCRL"] = "a scroll",
        ["SHOU"] = "a Shout",
        ["SLGM"] = "a soul gem",
        ["SPEL"] = "a spell",
        ["TACT"] = "a sound-emitting object",
        ["TREE"] = "a tree/harvestable plant",
        ["WATR"] = "water",
        ["WEAP"] = "a weapon",
        ["WOOP"] = "a Shout Word",
    };

    /// <summary>What each 4-char subrecord (field) IS, for generic composition.</summary>
    private static readonly Dictionary<string, string> Subrecords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FULL"] = "name",
        ["DESC"] = "description",
    };

    /// <summary>Short English description of the type, or null if nothing useful
    /// is known (caller should then simply omit the annotation rather than print
    /// a guess).</summary>
    public static string? Describe(string dsdType)
    {
        if (WholeType.TryGetValue(dsdType, out var whole)) return whole;

        var parts = dsdType.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        var hasSignature = Signatures.TryGetValue(parts[0], out var record);
        var hasSubrecord = Subrecords.TryGetValue(parts[1], out var field);

        if (hasSignature && hasSubrecord) return $"the {field} of {record}";
        if (hasSignature) return $"a string belonging to {record}";
        return null;
    }
}
