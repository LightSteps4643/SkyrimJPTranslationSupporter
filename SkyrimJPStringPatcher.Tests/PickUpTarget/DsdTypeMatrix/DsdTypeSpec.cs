namespace SkyrimJPStringPatcher.Tests.PickUpTarget.DsdTypeMatrix;

/// <summary>
/// The REQUIREMENTS side of the DSD-type coverage tests: what DSD supports and
/// which Mutagen property each DSD type corresponds to. Everything here is a
/// hand-written LITERAL taken from the two upstream specifications — never
/// derived from this project's own production code (DsdFullNameSupported,
/// RecordSignatureMap, ExtraTranslatableFields ...), because expected values
/// computed from the code under test would only ever confirm the code to
/// itself. If a test using this spec fails, check THIS FILE against the
/// upstream sources first; change the production code only if the spec says
/// the code is wrong, and change this file only with spec evidence (note the
/// evidence in a comment next to the change).
///
/// Sources (both re-read on 2026-10-12):
///  - DSD: SkyHorizon3/SSE-Dynamic-String-Distributor, src/Manager.cpp,
///    getTranslationType() — the complete list of "XXXX YYYY" type strings
///    and the TranslationType each maps to. Index semantics: the project's
///    docs/modules/ROOT/pages/index.adoc ("Index" section).
///  - Mutagen: Mutagen-Modding/Mutagen, Mutagen.Bethesda.Skyrim/Records/Major
///    Records/*.xml — which property is stored in which subrecord
///    (recordType attribute), and the compiled Mutagen.Bethesda.Skyrim.dll
///    for the property types.
/// </summary>
internal static class DsdTypeSpec
{
    /// <summary>DSD's TranslationType → every type string routed to it.
    /// 17 TranslationTypes, 67 type strings in total.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> TypesByTranslationType =
        new Dictionary<string, string[]>
        {
            ["kFullName"] = new[]
            {
                "ACTI FULL", "ALCH FULL", "AMMO FULL", "APPA FULL", "ARMO FULL", "AVIF FULL",
                "BOOK FULL", "CELL FULL", "CONT FULL", "CLAS FULL", "DOOR FULL", "ENCH FULL",
                "EXPL FULL", "FLOR FULL", "FURN FULL", "HAZD FULL", "INGR FULL", "KEYM FULL",
                "LCTN FULL", "LIGH FULL", "MESG FULL", "MGEF FULL", "MISC FULL", "PERK FULL",
                "PROJ FULL", "QUST FULL", "RACE FULL", "SCRL FULL", "SHOU FULL", "SLGM FULL",
                "SPEL FULL", "TACT FULL", "TREE FULL", "WATR FULL", "WEAP FULL", "WOOP FULL",
                "WRLD FULL", "NPC_ FULL",
            },
            ["kLoadScreenDescription"] = new[] { "LSCR DESC" },
            ["kMagicDescription"] = new[] { "MGEF DNAM" },
            ["kShortName"] = new[] { "NPC_ SHRT" },
            ["kRegion"] = new[] { "REGN RDMP" },
            ["kWordOfPower"] = new[] { "WOOP TNAM" },
            ["kButtonText1"] = new[] { "MESG ITXT" },
            ["kButtonText2"] = new[] { "PERK EPF2" },
            ["kQuestObjective"] = new[] { "QUST NNAM" },
            ["kPerkVerb"] = new[] { "PERK EPFD" },
            ["kActivationText"] = new[] { "ACTI RNAM", "FLOR RNAM" },
            ["kRuntime1"] = new[]
            {
                "DIAL FULL", "AMMO DESC", "ARMO DESC", "AVIF DESC", "BOOK DESC", "MESG DESC",
                "PERK DESC", "RACE DESC", "SCRL DESC", "SHOU DESC", "SPEL DESC", "WEAP DESC",
            },
            ["kGameSetting"] = new[] { "GMST DATA" },
            ["kReference"] = new[] { "REFR FULL" },
            ["kRuntimeLegacy"] = new[] { "QUST CNAM" },
            ["kRuntime2"] = new[] { "BOOK CNAM", "INFO RNAM" },
            ["kRuntimeIndex"] = new[] { "INFO NAM1" },
        };

    public static IEnumerable<string> AllTypes => TypesByTranslationType.Values.SelectMany(v => v);

    /// <summary>xUnit [MemberData] source: one row per DSD type string.</summary>
    public static IEnumerable<object[]> AllTypeRows() => AllTypes.Select(t => new object[] { t });

    /// <summary>
    /// Every Mutagen.Bethesda.Skyrim property of type ITranslatedStringGetter,
    /// as "RecordGetterName.PropertyPath" → the DSD type string that stores it,
    /// or null when DSD has NO type for it (a translated field DSD cannot
    /// apply — a real limitation of DSD, not a gap in this tool; nothing may
    /// be emitted for these because the output would never reach the game).
    ///
    /// The record name is the Mutagen getter interface name without the
    /// leading "I" and trailing "Getter". Paths are exactly as the
    /// reflection walk in DsdTypeMatrixTests produces them. DSD type strings
    /// come from the list above; the Mutagen side from the XML definitions
    /// (recordType attribute), e.g. Book.xml: Name=FULL, BookText=DESC,
    /// Description=CNAM.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> MutagenTranslatedFields =
        new Dictionary<string, string?>
        {
            ["Activator.Name"] = "ACTI FULL",
            ["Activator.ActivateTextOverride"] = "ACTI RNAM",
            ["ActorValueInformation.Name"] = "AVIF FULL",
            ["ActorValueInformation.Description"] = "AVIF DESC",
            ["AlchemicalApparatus.Name"] = "APPA FULL",
            ["AlchemicalApparatus.Description"] = null,
            ["Ammunition.Name"] = "AMMO FULL",
            ["Ammunition.Description"] = "AMMO DESC",
            ["Armor.Name"] = "ARMO FULL",
            ["Armor.Description"] = "ARMO DESC",
            ["BodyPartData.Parts.Name"] = null,
            ["Book.Name"] = "BOOK FULL",
            ["Book.BookText"] = "BOOK DESC",
            ["Book.Description"] = "BOOK CNAM",
            ["Cell.Name"] = "CELL FULL",
            ["Class.Name"] = "CLAS FULL",
            ["Class.Description"] = null,
            ["CollisionLayer.Description"] = null,
            ["ColorRecord.Name"] = null,
            ["Container.Name"] = "CONT FULL",
            ["DialogResponses.Prompt"] = "INFO RNAM",
            ["DialogResponses.Responses.Text"] = "INFO NAM1",
            ["DialogTopic.Name"] = "DIAL FULL",
            ["Door.Name"] = "DOOR FULL",
            ["Explosion.Name"] = "EXPL FULL",
            ["Eyes.Name"] = null,
            ["Faction.Name"] = null,
            ["Flora.Name"] = "FLOR FULL",
            ["Flora.ActivateTextOverride"] = "FLOR RNAM",
            ["Furniture.Name"] = "FURN FULL",
            ["GameSettingString.Data"] = "GMST DATA",
            ["Hazard.Name"] = "HAZD FULL",
            ["HeadPart.Name"] = null,
            ["Ingestible.Name"] = "ALCH FULL",
            ["Ingestible.Description"] = null,
            ["Ingredient.Name"] = "INGR FULL",
            ["Key.Name"] = "KEYM FULL",
            ["Light.Name"] = "LIGH FULL",
            ["LoadScreen.Description"] = "LSCR DESC",
            ["Location.Name"] = "LCTN FULL",
            ["MagicEffect.Name"] = "MGEF FULL",
            ["MagicEffect.Description"] = "MGEF DNAM",
            ["Message.Name"] = "MESG FULL",
            ["Message.Description"] = "MESG DESC",
            ["Message.MenuButtons.Text"] = "MESG ITXT",
            ["MiscItem.Name"] = "MISC FULL",
            ["MoveableStatic.Name"] = null,
            ["Npc.Name"] = "NPC_ FULL",
            ["Npc.ShortName"] = "NPC_ SHRT",
            ["ObjectEffect.Name"] = "ENCH FULL",
            ["Perk.Name"] = "PERK FULL",
            ["Perk.Description"] = "PERK DESC",
            ["Perk.Effects.ButtonLabel"] = "PERK EPF2",
            // PerkEntryPointSetText.Text: Mutagen's XML gives it no recordType; in the binary format it is the entry
            // point's text data (EPFD), which DSD exposes as "PERK EPFD" (the perk "verb"). The reflection walk in
            // the tests does not reach this deeper polymorphic arm, so it is listed here for the 67-type check only.
            ["Perk.Effects.Text"] = "PERK EPFD",
            ["PlacedObject.MapMarker.Name"] = "REFR FULL",
            ["Projectile.Name"] = "PROJ FULL",
            // Quest.Description is stored in a quest-level NNAM subrecord, but DSD's
            // "QUST NNAM" applies to OBJECTIVES (index = objective index) — DSD has
            // no type for the quest-level description.
            ["Quest.Description"] = null,
            ["Quest.Name"] = "QUST FULL",
            ["Quest.Objectives.DisplayText"] = "QUST NNAM",
            ["Quest.Stages.LogEntries.Entry"] = "QUST CNAM",
            ["Race.Name"] = "RACE FULL",
            ["Race.Description"] = "RACE DESC",
            ["Region.Map.Name"] = "REGN RDMP",
            ["Scroll.Name"] = "SCRL FULL",
            ["Scroll.Description"] = "SCRL DESC",
            ["Shout.Name"] = "SHOU FULL",
            ["Shout.Description"] = "SHOU DESC",
            ["SoulGem.Name"] = "SLGM FULL",
            ["SoundCategory.Name"] = null,
            ["SoundDescriptor.String"] = null,
            ["Spell.Name"] = "SPEL FULL",
            ["Spell.Description"] = "SPEL DESC",
            ["TalkingActivator.Name"] = "TACT FULL",
            ["Tree.Name"] = "TREE FULL",
            ["Water.Name"] = "WATR FULL",
            ["Weapon.Name"] = "WEAP FULL",
            ["Weapon.Description"] = "WEAP DESC",
            ["WordOfPower.Name"] = "WOOP FULL",
            ["WordOfPower.Translation"] = "WOOP TNAM",
            ["Worldspace.Name"] = "WRLD FULL",
        };
}
