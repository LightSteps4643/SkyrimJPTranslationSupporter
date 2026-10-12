using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget.DsdTypeMatrix;

/// <summary>
/// Builds ONE throwaway plugin (in memory, via Mutagen — no binary fixture)
/// that contains, for every one of DSD's 67 type strings, a record whose
/// corresponding Mutagen field holds a unique English sentence; adds records
/// with DSD-UNSUPPORTED translated fields, empty/absent fields and non-marker
/// placed objects as negative cases; then runs the real PickUpTargetRunner
/// once against a fake MO2 instance in a temp folder (same setup as the other
/// PickUpTarget tests — no real MO2, game or network involved).
///
/// What each field must produce is decided HERE, from the DSD/Mutagen specs in
/// <see cref="DsdTypeSpec"/> — never from what the tool currently outputs.
/// Each sentence contains its own DSD type string, so a failing assertion
/// names the culprit directly.
/// </summary>
public sealed class DsdTypeMatrixFixture : IDisposable
{
    /// <summary>A candidate the tool MUST produce. Index/EditorId are only set
    /// where DSD's own spec defines them (see DsdTypeSpec's sources).</summary>
    public sealed record Expectation(string DsdType, string FormId, string Text, int? Index = null, string? EditorId = null);

    public List<Expectation> Expected { get; } = new();

    /// <summary>Texts stored in translated Mutagen fields DSD has no type for — no candidate may carry them.</summary>
    public List<string> UnsupportedTexts { get; } = new();

    /// <summary>Records that carry no usable text (unset / empty field, or a placed object without a map marker) — they must produce no candidate at all.</summary>
    public List<(string Why, string FormId)> NoTextRecords { get; } = new();

    public PickUpTargetResult Result { get; }

    private readonly string _root;

    public static string SampleText(string dsdType) => $"Matrix sample text for {dsdType}";

    public DsdTypeMatrixFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdmatrix_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(_root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        const string plugin = "SjptsDsdMatrix.esp";
        var mod = new SkyrimMod(ModKey.FromNameAndExtension(plugin), SkyrimRelease.SkyrimSE);
        BuildAllRecords(mod);
        mod.WriteToBinary(Path.Combine(modDir, plugin));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{plugin}\r\n");

        using var log = RunLog.Open(Path.Combine(_root, "PickUpTarget"), "PickUpTarget");
        Result = PickUpTargetRunner.Run(mo2Dir, log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ---- helpers -------------------------------------------------------

    /// <summary>Sets a DSD-supported field to its unique sample sentence and records the expectation.</summary>
    private void Field(string dsdType, IMajorRecordGetter record, Action<string> assign, int? index = null, string? editorId = null)
    {
        var text = SampleText(dsdType);
        assign(text);
        Expected.Add(new Expectation(dsdType, record.FormKey.ToString(), text, index, editorId));
    }

    /// <summary>Sets a translated field DSD cannot apply; nothing may come out of it.</summary>
    private void Unsupported(string label, Action<string> assign)
    {
        var text = $"Unsupported field sample text for {label}";
        assign(text);
        UnsupportedTexts.Add(text);
    }

    // ---- records -------------------------------------------------------

    private void BuildAllRecords(SkyrimMod mod)
    {
        // Simple records: one translated Name (+ the DSD-supported extras below).
        var acti = mod.Activators.AddNew();
        Field("ACTI FULL", acti, t => acti.Name = t);
        Field("ACTI RNAM", acti, t => acti.ActivateTextOverride = t);

        var alch = mod.Ingestibles.AddNew();
        Field("ALCH FULL", alch, t => alch.Name = t);
        Unsupported("Ingestible.Description", t => alch.Description = t);

        var ammo = mod.Ammunitions.AddNew();
        Field("AMMO FULL", ammo, t => ammo.Name = t);
        Field("AMMO DESC", ammo, t => ammo.Description = t);

        var appa = mod.AlchemicalApparatuses.AddNew();
        Field("APPA FULL", appa, t => appa.Name = t);
        Unsupported("AlchemicalApparatus.Description", t => appa.Description = t);

        var armo = mod.Armors.AddNew();
        Field("ARMO FULL", armo, t => armo.Name = t);
        Field("ARMO DESC", armo, t => armo.Description = t);

        var avif = mod.ActorValueInformation.AddNew();
        Field("AVIF FULL", avif, t => avif.Name = t);
        Field("AVIF DESC", avif, t => avif.Description = t);

        var book = mod.Books.AddNew();
        Field("BOOK FULL", book, t => book.Name = t);
        Field("BOOK DESC", book, t => book.BookText = t);   // Mutagen Book.xml: BookText -> DESC
        Field("BOOK CNAM", book, t => book.Description = t); // Mutagen Book.xml: Description -> CNAM

        var cont = mod.Containers.AddNew();
        Field("CONT FULL", cont, t => cont.Name = t);

        var clas = mod.Classes.AddNew();
        Field("CLAS FULL", clas, t => clas.Name = t);
        Unsupported("Class.Description", t => clas.Description = t);

        var door = mod.Doors.AddNew();
        Field("DOOR FULL", door, t => door.Name = t);

        var ench = mod.ObjectEffects.AddNew();
        Field("ENCH FULL", ench, t => ench.Name = t);

        var expl = mod.Explosions.AddNew();
        Field("EXPL FULL", expl, t => expl.Name = t);

        var flor = mod.Florae.AddNew();
        Field("FLOR FULL", flor, t => flor.Name = t);
        Field("FLOR RNAM", flor, t => flor.ActivateTextOverride = t);

        var furn = mod.Furniture.AddNew();
        Field("FURN FULL", furn, t => furn.Name = t);

        var hazd = mod.Hazards.AddNew();
        Field("HAZD FULL", hazd, t => hazd.Name = t);

        var ingr = mod.Ingredients.AddNew();
        Field("INGR FULL", ingr, t => ingr.Name = t);

        var keym = mod.Keys.AddNew();
        Field("KEYM FULL", keym, t => keym.Name = t);

        var lctn = mod.Locations.AddNew();
        Field("LCTN FULL", lctn, t => lctn.Name = t);

        var ligh = mod.Lights.AddNew();
        Field("LIGH FULL", ligh, t => ligh.Name = t);

        var lscr = mod.LoadScreens.AddNew();
        Field("LSCR DESC", lscr, t => lscr.Description = t);

        var mgef = mod.MagicEffects.AddNew();
        Field("MGEF FULL", mgef, t => mgef.Name = t);
        Field("MGEF DNAM", mgef, t => mgef.Description = t);

        var misc = mod.MiscItems.AddNew();
        Field("MISC FULL", misc, t => misc.Name = t);

        var proj = mod.Projectiles.AddNew();
        Field("PROJ FULL", proj, t => proj.Name = t);

        var race = mod.Races.AddNew();
        Field("RACE FULL", race, t => race.Name = t);
        Field("RACE DESC", race, t => race.Description = t);

        var scrl = mod.Scrolls.AddNew();
        Field("SCRL FULL", scrl, t => scrl.Name = t);
        Field("SCRL DESC", scrl, t => scrl.Description = t);

        var shou = mod.Shouts.AddNew();
        Field("SHOU FULL", shou, t => shou.Name = t);
        Field("SHOU DESC", shou, t => shou.Description = t);

        var slgm = mod.SoulGems.AddNew();
        Field("SLGM FULL", slgm, t => slgm.Name = t);

        var spel = mod.Spells.AddNew();
        Field("SPEL FULL", spel, t => spel.Name = t);
        Field("SPEL DESC", spel, t => spel.Description = t);

        var tact = mod.TalkingActivators.AddNew();
        Field("TACT FULL", tact, t => tact.Name = t);

        var tree = mod.Trees.AddNew();
        Field("TREE FULL", tree, t => tree.Name = t);

        var watr = mod.Waters.AddNew();
        Field("WATR FULL", watr, t => watr.Name = t);

        var weap = mod.Weapons.AddNew();
        Field("WEAP FULL", weap, t => weap.Name = t);
        Field("WEAP DESC", weap, t => weap.Description = t);

        var woop = mod.WordsOfPower.AddNew();
        Field("WOOP FULL", woop, t => woop.Name = t);
        Field("WOOP TNAM", woop, t => woop.Translation = t);

        var wrld = mod.Worldspaces.AddNew();
        Field("WRLD FULL", wrld, t => wrld.Name = t);

        var npc = mod.Npcs.AddNew();
        Field("NPC_ FULL", npc, t => npc.Name = t);
        Field("NPC_ SHRT", npc, t => npc.ShortName = t);

        // GMST: DSD identifies game settings by EditorID (docs: "EditorID required").
        var gmst = new GameSettingString(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "sSjptsMatrixSample" };
        mod.GameSettings.Add(gmst);
        Field("GMST DATA", gmst, t => gmst.Data = t, editorId: "sSjptsMatrixSample");

        // REGN RDMP: the region map's name.
        var regn = mod.Regions.AddNew();
        Field("REGN RDMP", regn, t => regn.Map = new RegionMap { Name = t });

        // MESG: Name, Description and ITXT per button. DSD docs: ITXT index = "list index like you can see them in xEdit", starting at 0.
        var mesg = mod.Messages.AddNew();
        Field("MESG FULL", mesg, t => mesg.Name = t);
        Field("MESG DESC", mesg, t => mesg.Description = t);
        mesg.MenuButtons.Add(new MessageButton { Text = SampleText("MESG ITXT") + " (button 0)" });
        mesg.MenuButtons.Add(new MessageButton { Text = SampleText("MESG ITXT") + " (button 1)" });
        Expected.Add(new Expectation("MESG ITXT", mesg.FormKey.ToString(), SampleText("MESG ITXT") + " (button 0)", Index: 0));
        Expected.Add(new Expectation("MESG ITXT", mesg.FormKey.ToString(), SampleText("MESG ITXT") + " (button 1)", Index: 1));

        // QUST: Name; objective text (DSD docs: NNAM index = "Objective index", e.g. 60); log entries (CNAM —
        // DSD matches these by original TEXT at runtime, so only distinctness of the candidates is required).
        var qust = mod.Quests.AddNew();
        Field("QUST FULL", qust, t => qust.Name = t);
        qust.Objectives.Add(new QuestObjective { Index = 60, DisplayText = SampleText("QUST NNAM") });
        Expected.Add(new Expectation("QUST NNAM", qust.FormKey.ToString(), SampleText("QUST NNAM"), Index: 60));
        var stage = new QuestStage { Index = 30 };
        stage.LogEntries.Add(new QuestLogEntry { Entry = SampleText("QUST CNAM") + " (entry 0)" });
        stage.LogEntries.Add(new QuestLogEntry { Entry = SampleText("QUST CNAM") + " (entry 1)" });
        qust.Stages.Add(stage);
        Expected.Add(new Expectation("QUST CNAM", qust.FormKey.ToString(), SampleText("QUST CNAM") + " (entry 0)"));
        Expected.Add(new Expectation("QUST CNAM", qust.FormKey.ToString(), SampleText("QUST CNAM") + " (entry 1)"));

        // Quest.Description (the quest-level NNAM) has no DSD type. It is built on a QUEST OF ITS OWN: when a quest
        // carries both a description and objectives, Mutagen's binary write/read round trip puts the description's
        // text into the last objective (both are "NNAM" subrecords) — confirmed 2026-10-12 by writing and re-reading
        // such a quest. That is a Mutagen quirk of this test setup, not behavior of the tool under test.
        var questWithOnlyADescription = mod.Quests.AddNew();
        Unsupported("Quest.Description", t => questWithOnlyADescription.Description = t);

        // DIAL + INFO: topic name; response prompt (RNAM, no index); response text (NAM1, index = response number).
        var dial = mod.DialogTopics.AddNew();
        Field("DIAL FULL", dial, t => dial.Name = t);
        var info = new DialogResponses(mod) { Prompt = SampleText("INFO RNAM") };
        info.Responses.Add(new DialogResponse { ResponseNumber = 2, Text = SampleText("INFO NAM1") });
        dial.Responses.Add(info);
        Expected.Add(new Expectation("INFO RNAM", info.FormKey.ToString(), SampleText("INFO RNAM")));
        Expected.Add(new Expectation("INFO NAM1", info.FormKey.ToString(), SampleText("INFO NAM1"), Index: 2));

        // PERK: a playable, visible perk (hidden / non-playable perks are legitimately excluded as "not player-facing").
        // Entry-point text effect. The effect's Text is the entry point's verb string (EPFD in the binary format; the
        // Mutagen XML gives it no recordType). ButtonLabel (EPF2) is NOT set here: a ButtonLabel does not survive
        // Mutagen's binary write/read round trip (already documented in NestedTranslatableFieldsTests; confirmed again
        // 2026-10-12), so PERK EPF2 is verified by its own in-memory test in DsdTypeMatrixTests instead.
        var perk = mod.Perks.AddNew();
        perk.Playable = true;
        perk.Hidden = false;
        Field("PERK FULL", perk, t => perk.Name = t);
        Field("PERK DESC", perk, t => perk.Description = t);
        var setText = new PerkEntryPointSetText { Text = SampleText("PERK EPFD") };
        perk.Effects.Add(setText);
        Expected.Add(new Expectation("PERK EPFD", perk.FormKey.ToString(), SampleText("PERK EPFD")));

        // CELL + placed objects: map markers live in the placed object's MapMarker (XMRK) block.
        var cell = new Cell(mod) { Name = SampleText("CELL FULL") };
        Expected.Add(new Expectation("CELL FULL", cell.FormKey.ToString(), SampleText("CELL FULL")));

        var markerRef = new PlacedObject(mod)
        {
            MapMarker = new MapMarker { Name = SampleText("REFR FULL"), Type = MapMarker.MarkerType.City },
        };
        cell.Temporary.Add(markerRef);
        Expected.Add(new Expectation("REFR FULL", markerRef.FormKey.ToString(), SampleText("REFR FULL")));

        var markerWithoutName = new PlacedObject(mod) { MapMarker = new MapMarker { Type = MapMarker.MarkerType.City } };
        cell.Temporary.Add(markerWithoutName);
        NoTextRecords.Add(("placed object with a map marker whose name is empty", markerWithoutName.FormKey.ToString()));

        var plainRef = new PlacedObject(mod);
        cell.Temporary.Add(plainRef);
        NoTextRecords.Add(("placed object without a map marker", plainRef.FormKey.ToString()));

        mod.Cells.Records.Add(new CellBlock
        {
            BlockNumber = 0,
            GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks =
            {
                new CellSubBlock
                {
                    BlockNumber = 0,
                    GroupType = GroupTypeEnum.InteriorCellSubBlock,
                    Cells = { cell },
                },
            },
        });

        // Negative: a record of a DSD-supported type whose translated field is simply unset.
        var emptyKey = mod.Keys.AddNew();
        NoTextRecords.Add(("key with no name", emptyKey.FormKey.ToString()));
        var emptyFlora = mod.Florae.AddNew();
        NoTextRecords.Add(("flora with no name", emptyFlora.FormKey.ToString()));
        var emptyClass = mod.Classes.AddNew();
        NoTextRecords.Add(("class with no name", emptyClass.FormKey.ToString()));

        // Negative: translated fields DSD has no type for (records of types DSD does not support at all).
        var eyes = mod.Eyes.AddNew();
        Unsupported("Eyes.Name", t => eyes.Name = t);
        var faction = mod.Factions.AddNew();
        Unsupported("Faction.Name", t => faction.Name = t);
        var headPart = mod.HeadParts.AddNew();
        Unsupported("HeadPart.Name", t => headPart.Name = t);
        var moveableStatic = mod.MoveableStatics.AddNew();
        Unsupported("MoveableStatic.Name", t => moveableStatic.Name = t);
    }
}
