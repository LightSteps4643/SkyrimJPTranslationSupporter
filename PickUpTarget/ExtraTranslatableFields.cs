using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace SkyrimJPStringPatcher.PickUpTarget;

/// <summary>
/// v0.3.0 scope expansion: beyond the `.Name`/FULL field every record type
/// already goes through generically (via INamedGetter/ITranslatedNamedGetter in
/// PickUpTargetRunner), DSD supports a further set of flat, single-value,
/// FormID-only-matched TranslatedString fields — confirmed against real load
/// order data via houseCARL (see DESIGN_NOTES.md) before committing to this list.
/// Each record type exposes its own differently-named property for these (no
/// shared Mutagen interface the way FULL has via INamedGetter), so this is a
/// hand-written type switch rather than reflection — same style as
/// RecordSignatureMap, kept in one place so the mapping is auditable.
///
/// Only flat, single-value fields live here — nested list structures (QUST
/// objectives/log entries, INFO responses, MESG buttons, PERK effects) and the
/// EditorID-matched GMST exception are in <see cref="NestedTranslatableFields"/>
/// instead, since those need to yield multiple (type, index) pairs per record
/// rather than a flat one-property-per-type mapping.
///
/// "REFR FULL" remains entirely unimplemented: Mutagen exposes no
/// TranslatedString field on PlacedObject at all (confirmed both via the
/// Mutagen schema reference and, independently, by scanning the actual binary
/// data in SSEEdit — 0 of ~930,000 placed references in a real load order had
/// a non-empty FULL override, so this isn't costing any real candidates).
/// </summary>
public static class ExtraTranslatableFields
{
    public static IEnumerable<(string DsdType, ITranslatedStringGetter? Field)> For(IMajorRecordGetter record)
    {
        switch (record)
        {
            case IActivatorGetter acti:
                yield return ("ACTI RNAM", acti.ActivateTextOverride);
                break;
            case IFloraGetter flor:
                yield return ("FLOR RNAM", flor.ActivateTextOverride);
                break;
            case ILoadScreenGetter lscr:
                yield return ("LSCR DESC", lscr.Description);
                break;
            case IMagicEffectGetter mgef:
                yield return ("MGEF DNAM", mgef.Description);
                break;
            case IWordOfPowerGetter woop:
                yield return ("WOOP TNAM", woop.Translation);
                break;
            // v0.59.x (GitHub issue #2): these were swapped from day one.
            // Confirmed against Mutagen's own record definition
            // (Mutagen.Bethesda.Skyrim/Records/Major Records/Book.xml:
            // BookText has recordType="DESC", Description has
            // recordType="CNAM") and against DSD's own documentation
            // (docs/modules/ROOT/pages/index.adoc on
            // SkyHorizon3/SSE-Dynamic-String-Distributor: "BOOK CNAM" is
            // listed among its flat/short fields, and its own worked example
            // for "BOOK DESC" is a multi-paragraph in-character letter).
            // BOOK DESC = the book's actual body (BookText); BOOK CNAM = the
            // separate short description (Description).
            case IBookGetter book:
                yield return ("BOOK DESC", book.BookText);
                yield return ("BOOK CNAM", book.Description);
                break;
            case IAmmunitionGetter ammo:
                yield return ("AMMO DESC", ammo.Description);
                break;
            // Armor/Weapon/Spell/Scroll/Shout share IEquipmentTypeGetter-ish shapes
            // but not a common Description-bearing interface, so each needs its
            // own case despite the identical (single Description field) shape.
            case IArmorGetter armo:
                yield return ("ARMO DESC", armo.Description);
                break;
            case IWeaponGetter weap:
                yield return ("WEAP DESC", weap.Description);
                break;
            case ISpellGetter spel:
                yield return ("SPEL DESC", spel.Description);
                break;
            case IScrollGetter scrl:
                yield return ("SCRL DESC", scrl.Description);
                break;
            case IShoutGetter shou:
                yield return ("SHOU DESC", shou.Description);
                break;
            case IRaceGetter race:
                yield return ("RACE DESC", race.Description);
                break;
            case IMessageGetter mesg:
                yield return ("MESG DESC", mesg.Description);
                break;
            case IPerkGetter perk:
                yield return ("PERK DESC", perk.Description);
                break;
            case INpcGetter npc:
                yield return ("NPC_ SHRT", npc.ShortName);
                break;
        }
    }
}
