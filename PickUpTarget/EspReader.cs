
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings.DI;
using Noggog;
using System.IO.Abstractions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Exceptions;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.PickUpTarget
{
    public class EspReader
    {
        public FileSystem GlobalFileSystem = null;

        public SkyrimMod? CurrentMod = null;
        public Dictionary<string, Hazard> Hazards = new Dictionary<string, Hazard>();
        public Dictionary<string, HeadPart> HeadParts = new Dictionary<string, HeadPart>();
        public Dictionary<string, Npc> Npcs = new Dictionary<string, Npc>();
        public Dictionary<string, Worldspace> Worldspaces = new Dictionary<string, Worldspace>();
        public Dictionary<string, Shout> Shouts = new Dictionary<string, Shout>();
        public Dictionary<string, Tree> Trees = new Dictionary<string, Tree>();
        public Dictionary<string, Ingestible> Ingestibles = new Dictionary<string, Ingestible>();
        public Dictionary<string, Race> Races = new Dictionary<string, Race>();
        public Dictionary<string, Quest> Quests = new Dictionary<string, Quest>();
        public Dictionary<string, Faction> Factions = new Dictionary<string, Faction>();
        public Dictionary<string, Perk> Perks = new Dictionary<string, Perk>();
        public Dictionary<string, Weapon> Weapons = new Dictionary<string, Weapon>();
        public Dictionary<string, SoulGem> SoulGems = new Dictionary<string, SoulGem>();
        public Dictionary<string, Armor> Armors = new Dictionary<string, Armor>();
        public Dictionary<string, Key> Keys = new Dictionary<string, Key>();
        public Dictionary<string, Mutagen.Bethesda.Skyrim.Activator> Activators = new Dictionary<string, Mutagen.Bethesda.Skyrim.Activator>();
        public Dictionary<string, MiscItem> MiscItems = new Dictionary<string, MiscItem>();
        public Dictionary<string, Book> Books = new Dictionary<string, Book>();
        public Dictionary<string, Mutagen.Bethesda.Skyrim.Message> Messages = new Dictionary<string, Mutagen.Bethesda.Skyrim.Message>();
        public Dictionary<string, DialogTopic> DialogTopics = new Dictionary<string, DialogTopic>();
        public Dictionary<string, Spell> Spells = new Dictionary<string, Spell>();
        public Dictionary<string, MagicEffect> MagicEffects = new Dictionary<string, MagicEffect>();
        public Dictionary<string, ObjectEffect> ObjectEffects = new Dictionary<string, ObjectEffect>();
        public Dictionary<string, CellBlock> Cells = new Dictionary<string, CellBlock>();
        public Dictionary<string, Container> Containers = new Dictionary<string, Container>();

        public bool AutoCompress = false;
        public SkyrimRelease GameType;
        public EspReader(SkyrimRelease GameType = SkyrimRelease.SkyrimSE)
        {
            GlobalFileSystem = new FileSystem();
            this.GameType = GameType;
        }

        public void ClearRam()
        {
            Hazards.Clear();
            HeadParts.Clear();
            Npcs.Clear();
            Worldspaces.Clear();
            Shouts.Clear();
            Trees.Clear();
            Ingestibles.Clear();
            Races.Clear();
            Quests.Clear();
            Factions.Clear();
            Perks.Clear();
            Weapons.Clear();
            SoulGems.Clear();
            Armors.Clear();
            Keys.Clear();
            Activators.Clear();
            MiscItems.Clear();
            Books.Clear();
            Messages.Clear();
            DialogTopics.Clear();
            Spells.Clear();
            MagicEffects.Clear();
            ObjectEffects.Clear();
            Cells.Clear();
            Containers.Clear();
        }

        public void Close()
        {
            ClearRam();
            CurrentMod = null;
        }
       
        public static string GenKey(FormKey? Key, string? EditID)
        {
            string MergeKey = "";

            if (Key != null)
            {
                MergeKey = "[" + Key.ToString() + "]";
            }

            if (EditID != null)
            {
                MergeKey += EditID;
            }

            return MergeKey;
        }

        private void ToRam()
        {
            ClearRam();

            if (CurrentMod != null)
            {
                foreach (var Get in this.CurrentMod.Hazards.ToList())
                {
                    Hazards.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.HeadParts.ToList())
                {
                    HeadParts.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Npcs.ToList())
                {
                    Npcs.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Worldspaces.ToList())
                {
                    Worldspaces.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Shouts.ToList())
                {
                    Shouts.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Trees.ToList())
                {
                    Trees.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Ingestibles.ToList())
                {
                    Ingestibles.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Races.ToList())
                {
                    Races.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Quests.ToList())
                {
                    Quests.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Factions.ToList())
                {
                    Factions.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Perks.ToList())
                {
                    Perks.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Weapons.ToList())
                {
                    Weapons.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.SoulGems.ToList())
                {
                    SoulGems.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Armors.ToList())
                {
                    Armors.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Keys.ToList())
                {
                    Keys.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Containers.ToList())
                {
                    Containers.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Activators.ToList())
                {
                    Activators.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.MiscItems.ToList())
                {
                    MiscItems.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Books.ToList())
                {
                    Books.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Messages.ToList())
                {
                    Messages.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.DialogTopics.ToList())
                {
                    DialogTopics.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Spells.ToList())
                {
                    Spells.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.MagicEffects.ToList())
                {
                    MagicEffects.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.ObjectEffects.ToList())
                {
                    ObjectEffects.Add(GenKey(Get.FormKey, Get.EditorID), Get);
                }

                foreach (var Get in this.CurrentMod.Cells.ToList())
                {
                    if (Get != null)
                    {
                        Cells.Add(Get.GetHashCode().ToString(), Get);
                    }
                }

            }
        }

        public SkyrimMod? DefReadMod(string FilePath)
        {
            return ReadMod(FilePath);
        }

        public enum EncodingTypes
        {
            UTF8_1256 = 0, UTF8_1252 = 1, UTF8_1250 = 2, UTF8_1253 = 3, UTF8 = 5
        }

        public SkyrimMod? ReadMod(string FilePath)
        {
            if (File.Exists(FilePath) && (FilePath.ToLower().EndsWith(".esp") || FilePath.ToLower().EndsWith(".esm") || FilePath.ToLower().EndsWith(".esl")))
            {
                Cache<IModMasterStyledGetter, ModKey>? FlagsLookup = null;

                var AutoEncoding = MutagenEncoding._utf8;

                var SetParam = new BinaryReadParameters()
                {
                    StringsParam = new Mutagen.Bethesda.Strings.StringsReadParameters()
                    {
                        NonTranslatedEncodingOverride = AutoEncoding,
                        NonLocalizedEncodingOverride = AutoEncoding
                    },
                    FileSystem = GlobalFileSystem,
                    MasterFlagsLookup = FlagsLookup,

                };
                try
                {
                    //var Mask = new GroupMask(false);
                    //var Mask = new GroupMask(true);
                    //Mask.AddonNodes = false;

                    //DNAM...

                    if (GameType == SkyrimRelease.SkyrimSE)
                    {
                        CurrentMod = SkyrimMod
                       .CreateFromBinary(FilePath, SkyrimRelease.SkyrimSE, SetParam);
                    }
                    else
                    {
                        CurrentMod = SkyrimMod
                        .CreateFromBinary(FilePath, SkyrimRelease.SkyrimLE, SetParam);
                    }
                }
                catch (RecordException rex)
                {
                    GC.Collect();
                }

                ToRam();

                return CurrentMod;
            }

            return null;
        }

        public bool DefSaveMod(SkyrimMod SourceMod, string OutPutPath)
        {
            var AutoEncoding = MutagenEncoding._utf8;

            return SaveMod(SourceMod, OutPutPath, new EncodingBundle(AutoEncoding, AutoEncoding));
        }


        public bool SaveMod(SkyrimMod SourceMod, string OutPutPath, EncodingBundle SetEncodingBundle)
        {
            if (CurrentMod == null)
            {
                return false;
            }
            if (File.Exists(OutPutPath))
            {
                return false;
            }

            try
            {
                if (!AutoCompress)
                {
                    foreach (var Item in SourceMod.EnumerateMajorRecords())
                    {
                        Item.IsCompressed = false;
                    }
                }

                Task.Run(async () =>
                {
                    try
                    {
                        await SourceMod.BeginWrite.ToPath(OutPutPath)
                       .WithLoadOrderFromHeaderMasters()
                       .WithNoDataFolder()
                       .WithEmbeddedEncodings(SetEncodingBundle)
                       .WithFileSystem(GlobalFileSystem)
                       .WithRecordCount(RecordCountOption.Iterate)
                       .WithModKeySync(ModKeyOption.CorrectToPath)
                       .WithMastersListContent(MastersListContentOption.NoCheck)
                       .WithMastersListOrdering(MastersListOrderingOption.NoCheck)
                       .NoFormIDUniquenessCheck()
                       .NoFormIDCompactnessCheck()
                       .NoCheckIfLowerRangeDisallowed()
                       .NoNullFormIDStandardization()
                       .WriteAsync();
                    }
                    catch (Exception Ex)
                    {
                        Log.Instance.Error(Ex.Message);
                    }
                }).Wait();

                return true;
            }
            catch { return false; }
        }

    }

}
