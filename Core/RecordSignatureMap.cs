namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Maps Mutagen's CLR class name for a record (e.g. "NpcBinaryOverlay") to the
/// actual 4-character Bethesda record signature (e.g. "NPC_") that DSD's own
/// "type" field uses (e.g. "NPC_ FULL"). Mutagen doesn't expose the raw
/// signature via a public property, so this table is maintained by hand
/// against the known signature list DSD itself recognizes.
/// </summary>
public static class RecordSignatureMap
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["Npc"] = "NPC_",
        ["Weapon"] = "WEAP",
        ["Armor"] = "ARMO",
        ["Book"] = "BOOK",
        ["Ingestible"] = "ALCH",
        ["Ammunition"] = "AMMO",
        ["AlchemicalApparatus"] = "APPA",
        ["ActorValueInformation"] = "AVIF",
        ["Cell"] = "CELL",
        ["Container"] = "CONT",
        ["Class"] = "CLAS",
        ["Door"] = "DOOR",
        ["ObjectEffect"] = "ENCH",
        ["Explosion"] = "EXPL",
        ["Flora"] = "FLOR",
        ["Furniture"] = "FURN",
        ["Hazard"] = "HAZD",
        ["Ingredient"] = "INGR",
        ["Key"] = "KEYM",
        ["Location"] = "LCTN",
        ["Light"] = "LIGH",
        ["MessageBox"] = "MESG",
        ["Message"] = "MESG",
        ["MagicEffect"] = "MGEF",
        ["MiscItem"] = "MISC",
        ["Perk"] = "PERK",
        ["Projectile"] = "PROJ",
        ["Quest"] = "QUST",
        ["Race"] = "RACE",
        ["Scroll"] = "SCRL",
        ["Shout"] = "SHOU",
        ["SoulGem"] = "SLGM",
        ["Spell"] = "SPEL",
        ["TalkingActivator"] = "TACT",
        ["Tree"] = "TREE",
        ["Water"] = "WATR",
        ["WordOfPower"] = "WOOP",
        ["Worldspace"] = "WRLD",
        ["Region"] = "REGN",
        ["GameSetting"] = "GMST",
        ["DialogTopic"] = "DIAL",
        ["DialogResponses"] = "INFO",
        ["Activator"] = "ACTI",
        ["Faction"] = "FACT",
        ["PlacedObject"] = "REFR",
        ["PlacedNpc"] = "REFR",
        ["PlacedArrow"] = "REFR",
        ["PlacedBarrier"] = "REFR",
        ["PlacedBeam"] = "REFR",
        ["PlacedCone"] = "REFR",
        ["PlacedFlame"] = "REFR",
        ["PlacedHazard"] = "REFR",
        ["PlacedMissile"] = "REFR",
        ["PlacedTrap"] = "REFR",
        ["ColorRecord"] = "CLFM",
        ["HeadPart"] = "HDPT",
        ["MaterialType"] = "MATT",
        ["MovementType"] = "MOVT",
        ["SoundCategory"] = "SNCT",
    };

    /// <summary>The exact set of signatures DSD's own getTranslationType() maps to
    /// kFullName (i.e. a plain "&lt;SIG&gt; FULL" DSD entry will actually be read).
    /// Verified against SSE-Dynamic-String-Distributor's Manager.cpp source.
    /// CLFM/HDPT/MATT/MOVT/SNCT (and others not listed) are NOT supported by
    /// DSD for FULL and are deliberately excluded from candidates.
    /// "DIAL" is included here too: DIAL FULL's Mutagen shape (a plain .Name
    /// TranslatedString) is identical to every other FULL field even though
    /// DSD internally matches it via kRuntime1 (dynamic search) rather than
    /// kFullName (const/cached) — that distinction only affects how DSD looks
    /// the translation up at runtime, not the "type"/"form_id"/"string" JSON we
    /// need to emit, so DIAL rides the same extraction path as every other
    /// FULL-supported signature (v0.3.0 scope expansion).</summary>
    public static readonly HashSet<string> DsdFullNameSupported = new()
    {
        "ACTI", "ALCH", "AMMO", "APPA", "ARMO", "AVIF", "BOOK", "CELL", "CONT",
        "CLAS", "DOOR", "ENCH", "EXPL", "FLOR", "FURN", "HAZD", "INGR", "KEYM",
        "LCTN", "LIGH", "MESG", "MGEF", "MISC", "PERK", "PROJ", "QUST", "RACE",
        "SCRL", "SHOU", "SLGM", "SPEL", "TACT", "TREE", "WATR", "WEAP", "WOOP",
        "WRLD", "NPC_", "DIAL",
    };

    private static readonly HashSet<string> WarnedUnmapped = new();

    /// <summary>Strips Mutagen's "BinaryOverlay"/"Setter"/"Getter" suffixes and looks up the signature.
    /// Falls back to the raw (uppercased, 4-char-padded) stem and logs a one-time warning if unmapped,
    /// so gaps in this table are visible instead of silently wrong.</summary>
    public static string Resolve(string clrTypeName)
    {
        var stem = clrTypeName;
        foreach (var suffix in new[] { "BinaryOverlay", "Setter", "Getter" })
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
                stem = stem[..^suffix.Length];
        }

        if (Map.TryGetValue(stem, out var signature))
            return signature;

        if (WarnedUnmapped.Add(stem))
            Console.Error.WriteLine($"[warn] RecordSignatureMap has no entry for CLR type '{stem}' (from '{clrTypeName}') — falling back to raw name. Add it to Core/RecordSignatureMap.cs.");

        return stem.Length >= 4 ? stem[..4].ToUpperInvariant() : stem.ToUpperInvariant().PadRight(4, '_');
    }
}
