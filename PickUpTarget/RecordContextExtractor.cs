using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimJPStringPatcher.PickUpTarget;

/// <summary>
/// Extracts per-RECORD context that a translator needs but that the DSD type
/// string alone can never supply.
///
/// v0.5.0's <see cref="SkyrimJPStringPatcher.Core.DsdTypeDescriptions"/> answers
/// "what KIND of string is this" ("the name of a piece of armor/clothing") purely
/// from the type, which Translation could have derived for itself. This class
/// answers the strictly harder question — "what is this PARTICULAR record" (light
/// armor or heavy, a sword or a bow, a male or female NPC) — and PickUpTarget is
/// the only stage that can, because it is the only one holding a Mutagen record.
/// That was the real content of the user's "PickUpTargetの段階でやれば精度が
/// 上がるのでは" suggestion.
///
/// Motivation from real data: the Sentinel.esp trial's weakest translations were
/// exactly the entries whose English name was ambiguous on its own — a
/// "Rogue's Hood, Lowered" (headwear? a cowl? which slot?), a "Common Bearded
/// Axe" (left as katakana because nothing said it was an axe).
///
/// Every field read here was verified against the bundled Mutagen schema
/// reference before use — no invented field names.
///
/// v0.48.0: translated to English (see prompt.txt's own remarks).
/// </summary>
public static class RecordContextExtractor
{
    /// <summary>Short English context for the record, or "" when nothing useful
    /// is known. Deliberately terse: this is appended to a prompt line, and a
    /// paragraph per candidate would drown the text being translated.</summary>
    public static string For(IMajorRecordGetter record, IReadOnlyDictionary<FormKey, string> raceNames)
    {
        return record switch
        {
            IArmorGetter armor => Armor(armor),
            IWeaponGetter weapon => Weapon(weapon),
            INpcGetter npc => Npc(npc, raceNames),
            IBookGetter book => Book(book),
            IMagicEffectGetter mgef => MagicSchool(mgef.MagicSkill),
            ISpellGetter spell => Spell(spell),
            _ => "",
        };
    }

    /// <summary>ArmorType is an enum {LightArmor, HeavyArmor, Clothing};
    /// FirstPersonFlags is the biped-slot bitfield, whose flag names are already
    /// the body part ("Head", "Body", "Feet"), so they read acceptably as-is.</summary>
    private static string Armor(IArmorGetter armor)
    {
        var parts = new List<string>();

        var armorType = armor.BodyTemplate?.ArmorType;
        if (armorType != null)
        {
            parts.Add(armorType switch
            {
                ArmorType.LightArmor => "light armor",
                ArmorType.HeavyArmor => "heavy armor",
                ArmorType.Clothing => "clothing/accessory",
                _ => "",
            });
        }

        var slots = armor.BodyTemplate?.FirstPersonFlags;
        if (slots != null && slots.Value != default)
        {
            var described = DescribeSlots(slots.Value);
            if (described.Length > 0) parts.Add($"slot: {described}");
        }

        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    /// <summary>Vanilla biped slots that carry a meaningful body part. The enum's
    /// own ToString() is unusable as context on its own: a modder-added slot has
    /// no name, so it renders as a bare bit value ("65536") that tells a
    /// translator nothing. Named slots are translated; unnamed bits are reported
    /// as the slot NUMBER the modding community actually uses (slot = bit + 30).</summary>
    private static readonly Dictionary<BipedObjectFlag, string> SlotNames = new()
    {
        [BipedObjectFlag.Head] = "head",
        [BipedObjectFlag.Hair] = "hair",
        [BipedObjectFlag.Body] = "body",
        [BipedObjectFlag.Hands] = "hands",
        [BipedObjectFlag.Forearms] = "forearms",
        [BipedObjectFlag.Amulet] = "amulet",
        [BipedObjectFlag.Ring] = "ring",
        [BipedObjectFlag.Feet] = "feet",
        [BipedObjectFlag.Calves] = "calves",
        [BipedObjectFlag.Shield] = "shield",
        [BipedObjectFlag.Tail] = "tail",
        [BipedObjectFlag.LongHair] = "long hair",
        [BipedObjectFlag.Circlet] = "circlet",
        [BipedObjectFlag.Ears] = "ears",
    };

    private static string DescribeSlots(BipedObjectFlag flags)
    {
        var described = new List<string>();
        for (var bit = 0; bit < 32; bit++)
        {
            var flag = (BipedObjectFlag)(1u << bit);
            if (!flags.HasFlag(flag)) continue;
            described.Add(SlotNames.TryGetValue(flag, out var name) ? name : $"slot {bit + 30}");
        }
        return string.Join("/", described);
    }

    private static string Weapon(IWeaponGetter weapon) => weapon.Data?.AnimationType switch
    {
        WeaponAnimationType.OneHandSword => "one-handed sword",
        WeaponAnimationType.OneHandDagger => "dagger",
        WeaponAnimationType.OneHandAxe => "one-handed axe",
        WeaponAnimationType.OneHandMace => "one-handed mace",
        WeaponAnimationType.TwoHandSword => "two-handed sword",
        WeaponAnimationType.TwoHandAxe => "two-handed axe/warhammer",
        WeaponAnimationType.Bow => "bow",
        WeaponAnimationType.Crossbow => "crossbow",
        WeaponAnimationType.Staff => "staff",
        WeaponAnimationType.HandToHand => "hand-to-hand",
        _ => "",
    };

    /// <summary>Gender comes from the Female flag on NpcConfiguration.Flags; the
    /// race is a FormLink, resolved via the EditorID map PickUpTarget builds
    /// while it enumerates records (Translation has no link cache).</summary>
    private static string Npc(INpcGetter npc, IReadOnlyDictionary<FormKey, string> raceNames)
    {
        var parts = new List<string>
        {
            npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? "female" : "male",
        };

        if (!npc.Race.IsNull && raceNames.TryGetValue(npc.Race.FormKey, out var race) && race.Length > 0)
            parts.Add($"race: {race}");

        return string.Join(", ", parts);
    }

    /// <summary>Teaches is polymorphic — a spell tome (BookSpell), a skill book
    /// (BookSkill), or plain reading matter (BookTeachesNothing). A spell tome's
    /// title follows a fixed naming convention in Japanese, so saying which it is
    /// materially changes the translation.</summary>
    private static string Book(IBookGetter book) => book.Teaches switch
    {
        IBookSpellGetter => "spell tome (teaches a spell)",
        IBookSkillGetter => "skill book (raises a skill)",
        _ => "",
    };

    private static string Spell(ISpellGetter spell)
    {
        var kind = spell.Type switch
        {
            SpellType.Spell => "spell",
            SpellType.Power => "power",
            SpellType.LesserPower => "lesser power",
            SpellType.Ability => "ability (passive)",
            SpellType.Disease => "disease",
            SpellType.Poison => "poison",
            SpellType.Voice => "Shout effect",
            _ => "",
        };
        return kind;
    }

    /// <summary>MagicSkill is an ActorValue; only the five magic schools are
    /// meaningful here (it is None/an unrelated value on many effects).</summary>
    private static string MagicSchool(ActorValue skill) => skill switch
    {
        ActorValue.Destruction => "Destruction magic",
        ActorValue.Restoration => "Restoration magic",
        ActorValue.Conjuration => "Conjuration magic",
        ActorValue.Illusion => "Illusion magic",
        ActorValue.Alteration => "Alteration magic",
        _ => "",
    };
}
