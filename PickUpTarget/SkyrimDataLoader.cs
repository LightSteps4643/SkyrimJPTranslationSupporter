

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Strings;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.PickUpTarget
{
    public class SkyrimDataLoader
    {

        public class RecordItem
        {
            public FormKey FormKey; //It is best not to use this FormKey as the primary key.
            public string UniqueKey = string.Empty;
            public ITranslatedStringGetter? String;

            public string Sig = "";
            public string EditID = null;

            public RecordItem(FormKey FormKey, string Key,ITranslatedStringGetter? String,string Sig,string EditID)
            {
                this.FormKey = FormKey;
                this.UniqueKey = Key;
                this.String = String;
                this.Sig = Sig;
            }

            public CorpusEntry ToCorpusEntry()
            {
                //Perform the conversion here.
                return null;
            }
        }

        public enum ObjSelect
        {
            Null = 99, All = 0, Hazards = 28, HeadParts = 27, Npcs = 26, Worldspaces = 1, Shouts = 25, Trees = 23, Ingestibles = 22, Quests = 2, Factions = 3, Perks = 5, Weapons = 6, SoulGems = 7, Armors = 8, Keys = 9, Containers = 10, Activators = 11, MiscItems = 12, Books = 13, Messages = 15, DialogTopics = 16, Spells = 17, MagicEffects = 18, ObjectEffects = 19, Cells = 20, Races = 21
        }

        public static List<ObjSelect> QueryParams(EspReader Reader)
        {
            List<ObjSelect> ObjSelects = new List<ObjSelect>();

            if (Reader.Hazards.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Hazards);
            }

            if (Reader.HeadParts.Count > 0)
            {
                ObjSelects.Add(ObjSelect.HeadParts);
            }

            if (Reader.Npcs.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Npcs);
            }

            if (Reader.Worldspaces.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Worldspaces);
            }

            if (Reader.Shouts.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Shouts);
            }

            if (Reader.Trees.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Trees);
            }

            if (Reader.Ingestibles.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Ingestibles);
            }

            if (Reader.Races.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Races);
            }

            if (Reader.Quests.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Quests);
            }

            if (Reader.Factions.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Factions);
            }

            if (Reader.Perks.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Perks);
            }

            if (Reader.Weapons.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Weapons);
            }

            if (Reader.SoulGems.Count > 0)
            {
                ObjSelects.Add(ObjSelect.SoulGems);
            }

            if (Reader.Armors.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Armors);
            }

            if (Reader.Keys.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Keys);
            }

            if (Reader.Containers.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Containers);
            }

            if (Reader.Activators.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Activators);
            }

            if (Reader.MiscItems.Count > 0)
            {
                ObjSelects.Add(ObjSelect.MiscItems);
            }

            if (Reader.Books.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Books);
            }

            if (Reader.Messages.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Messages);
            }

            if (Reader.DialogTopics.Count > 0)
            {
                ObjSelects.Add(ObjSelect.DialogTopics);
            }

            if (Reader.Spells.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Spells);
            }

            if (Reader.MagicEffects.Count > 0)
            {
                ObjSelects.Add(ObjSelect.MagicEffects);
            }

            if (Reader.ObjectEffects.Count > 0)
            {
                ObjSelects.Add(ObjSelect.ObjectEffects);
            }

            if (Reader.Cells.Count > 0)
            {
                ObjSelects.Add(ObjSelect.Cells);
            }

            ObjSelects.Add(ObjSelect.All);

            return ObjSelects;
        }

        public static string GenUniqueKey(string EditorID, string SetType)
        {
            return (EditorID + "(" + SetType + ")");
        }

        public static List<RecordItem> Load(ObjSelect Type, EspReader Reader)
        {
            if (Type == ObjSelect.All)
            {
                return LoadAll(Reader);
            }
            if (Type == ObjSelect.Hazards)
            {
                return LoadHazards(Reader);
            }
            else
            if (Type == ObjSelect.HeadParts)
            {
                return LoadHeadParts(Reader);
            }
            else
            if (Type == ObjSelect.Npcs)
            {
                return LoadNpcs(Reader);
            }
            else
            if (Type == ObjSelect.Worldspaces)
            {
                return LoadWorldspaces(Reader);
            }
            else
            if (Type == ObjSelect.Shouts)
            {
                return LoadShouts(Reader);
            }
            else
            if (Type == ObjSelect.Trees)
            {
                return LoadTrees(Reader);
            }
            else
            if (Type == ObjSelect.Ingestibles)
            {
                return LoadIngestibles(Reader);
            }
            else
            if (Type == ObjSelect.Races)
            {
                return LoadRaces(Reader);
            }
            else
            if (Type == ObjSelect.Quests)
            {
                return LoadQuests(Reader);
            }
            else
            if (Type == ObjSelect.Factions)
            {
                return LoadFactions(Reader);
            }
            else
            if (Type == ObjSelect.Perks)
            {
                return LoadPerks(Reader);
            }
            else
            if (Type == ObjSelect.Weapons)
            {
                return LoadWeapons(Reader);
            }
            else
            if (Type == ObjSelect.SoulGems)
            {
                return LoadSoulGems(Reader);
            }
            else
            if (Type == ObjSelect.Armors)
            {
                return LoadArmors(Reader);
            }
            else
            if (Type == ObjSelect.Keys)
            {
                return LoadKeys(Reader);
            }
            else
            if (Type == ObjSelect.Containers)
            {
                return LoadContainers(Reader);
            }
            else
            if (Type == ObjSelect.Activators)
            {
                return LoadActivators(Reader);
            }
            else
            if (Type == ObjSelect.MiscItems)
            {
                return LoadMiscItems(Reader);
            }
            else
            if (Type == ObjSelect.Books)
            {
                return LoadBooks(Reader);
            }
            else
            if (Type == ObjSelect.Messages)
            {
                List<RecordItem> Records = new List<RecordItem>();
                Records.AddRange(LoadMessages(Reader));
                Records.AddRange(LoadMessageButtons(Reader));

                return Records;
            }
            else
            if (Type == ObjSelect.DialogTopics)
            {
                return LoadDialogTopics(Reader);
            }
            else
            if (Type == ObjSelect.Spells)
            {
                return LoadSpells(Reader);
            }
            else
            if (Type == ObjSelect.MagicEffects)
            {
                return LoadMagicEffects(Reader);
            }
            else
            if (Type == ObjSelect.ObjectEffects)
            {
                return LoadObjectEffects(Reader);
            }
            else
            if (Type == ObjSelect.Cells)
            {
                return LoadCells(Reader);
            }
            return null;
        }

        public static List<RecordItem> LoadAll(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            Records.AddRange(LoadHazards(Reader));
            Records.AddRange(LoadHeadParts(Reader));
            Records.AddRange(LoadNpcs(Reader));
            Records.AddRange(LoadWorldspaces(Reader));
            Records.AddRange(LoadShouts(Reader));
            Records.AddRange(LoadTrees(Reader));
            Records.AddRange(LoadIngestibles(Reader));
            Records.AddRange(LoadRaces(Reader));
            Records.AddRange(LoadQuests(Reader));
            Records.AddRange(LoadFactions(Reader));
            Records.AddRange(LoadPerks(Reader));
            Records.AddRange(LoadWeapons(Reader));
            Records.AddRange(LoadSoulGems(Reader));
            Records.AddRange(LoadArmors(Reader));
            Records.AddRange(LoadKeys(Reader));
            Records.AddRange(LoadContainers(Reader));
            Records.AddRange(LoadActivators(Reader));
            Records.AddRange(LoadMiscItems(Reader));
            Records.AddRange(LoadBooks(Reader));
            Records.AddRange(LoadMessages(Reader));
            Records.AddRange(LoadMessageButtons(Reader));
            Records.AddRange(LoadDialogTopics(Reader));
            Records.AddRange(LoadSpells(Reader));
            Records.AddRange(LoadMagicEffects(Reader));
            Records.AddRange(LoadObjectEffects(Reader));
            Records.AddRange(LoadCells(Reader));

            return Records;
        }

        public static List<RecordItem> LoadHazards(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Hazards != null)
                for (int i = 0; i < Reader.Hazards.Count; i++)
                {
                    try
                    {
                        var GetHashKey = Reader.Hazards.ElementAt(i).Key;
                        var GetHazardItem = Reader.Hazards[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetHazardItem.FormKey, GetHazardItem.EditorID);

                        var GetName = GetHazardItem.Name; //HAZD FULL
                        if (GetName !=null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetHazardItem.FormKey, GetUniqueKey,GetName, "HAZD FULL",GetHazardItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Hazard item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadHeadParts(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.HeadParts != null)
                for (int i = 0; i < Reader.HeadParts.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.HeadParts.ElementAt(i).Key;
                        var GetHeadPartItem = Reader.HeadParts[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetHeadPartItem.FormKey, GetHeadPartItem.EditorID);

                        var GetName = GetHeadPartItem.Name; //HDPT FULL
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetHeadPartItem.FormKey, GetUniqueKey, GetName, "HDPT FULL", GetHeadPartItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading HeadPart item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadNpcs(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Npcs != null)
                for (int i = 0; i < Reader.Npcs.Count; i++)
                {
                    try
                    {
                        var GetHashKey = Reader.Npcs.ElementAt(i).Key;
                        var GetNpcItem = Reader.Npcs[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetNpcItem.FormKey, GetNpcItem.EditorID);

                        var GetName = GetNpcItem.Name; //NPC FULL
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetNpcItem.FormKey,GetUniqueKey, GetName, "NPC_ FULL", GetNpcItem.EditorID));
                        }

                        var GetShortName = GetNpcItem.ShortName; //NPC SHRT
                        if (GetShortName != null)
                        {
                            string SetType = "ShortName";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetNpcItem.FormKey,GetUniqueKey, GetShortName, "NPC_ SHRT", GetNpcItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Npc item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadWorldspaces(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Worldspaces != null)
                for (int i = 0; i < Reader.Worldspaces.Count; i++)
                {
                    try
                    {
                        var GetHashKey = Reader.Worldspaces.ElementAt(i).Key;
                        var GetWorldspaceItem = Reader.Worldspaces[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetWorldspaceItem.FormKey, GetWorldspaceItem.EditorID);

                        var GetName = GetWorldspaceItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetWorldspaceItem.FormKey,GetUniqueKey, GetName, "WRLD FULL", GetWorldspaceItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Worldspace item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadShouts(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Shouts != null)
                for (int i = 0; i < Reader.Shouts.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Shouts.ElementAt(i).Key;
                        var GetShoutItem = Reader.Shouts[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetShoutItem.FormKey, GetShoutItem.EditorID);

                        var GetName = GetShoutItem.Name; //SHOU FULL
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetShoutItem.FormKey, GetUniqueKey, GetName, "SHOU FULL", GetShoutItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Shout item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadTrees(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Trees != null)
                for (int i = 0; i < Reader.Trees.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Trees.ElementAt(i).Key;
                        var GetTreeItem = Reader.Trees[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetTreeItem.FormKey, GetTreeItem.EditorID);

                        var GetName = GetTreeItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name"; //TREE FULL
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetTreeItem.FormKey, GetUniqueKey, GetName, "TREE FULL", GetTreeItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Tree item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadIngestibles(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Ingestibles != null)
                for (int i = 0; i < Reader.Ingestibles.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Ingestibles.ElementAt(i).Key;
                        var GetIngestibleItem = Reader.Ingestibles[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetIngestibleItem.FormKey, GetIngestibleItem.EditorID);

                        var GetName = GetIngestibleItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name"; //ALCH FULL
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetIngestibleItem.FormKey, GetUniqueKey, GetName, "ALCH FULL", GetIngestibleItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Ingestible item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadRaces(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Races != null)
                for (int i = 0; i < Reader.Races.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Races.ElementAt(i).Key;
                        var GetRaceItem = Reader.Races[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetRaceItem.FormKey, GetRaceItem.EditorID);

                        var GetName = GetRaceItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetRaceItem.FormKey, GetUniqueKey, GetName, "RACE FULL", GetRaceItem.EditorID));
                        }

                        var GetDescription = GetRaceItem.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);


                            Records.Add(new RecordItem(GetRaceItem.FormKey, GetUniqueKey, GetDescription, "RACE DESC", GetRaceItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Race item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadQuests(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Quests != null)
                for (int i = 0; i < Reader.Quests.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Quests.ElementAt(i).Key;
                        var GetQuestItem = Reader.Quests[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetQuestItem.FormKey, GetQuestItem.EditorID);

                        var GetName = GetQuestItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetQuestItem.FormKey, GetUniqueKey, GetName, "QUST FULL", GetQuestItem.EditorID));
                        }
                        var GetDescription = GetQuestItem.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetQuestItem.FormKey,GetUniqueKey, GetDescription,"???", GetQuestItem.EditorID));
                            Log.Instance.Error($"Key:[{GetQuestItem.FormKey}] Unknown data detected.");
                        }

                        if (GetQuestItem.Objectives != null)
                            if (GetQuestItem.Objectives.Count > 0)
                            {
                                int CountObjective = 0;
                                for (int ir = 0; ir < GetQuestItem.Objectives.Count; ir++)
                                {
                                    try
                                    {
                                        CountObjective++;
                                        var GetObjectiveItem = GetQuestItem.Objectives[ir];
                                        var GetDisplayText = GetObjectiveItem.DisplayText;
                                        if (GetDisplayText != null)
                                        {
                                            string SetType = string.Format("DisplayText[{0}]", CountObjective);
                                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                            Records.Add(new RecordItem(GetQuestItem.FormKey,GetUniqueKey, GetDisplayText,"QOBJ NNAM", GetQuestItem.EditorID));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                       Log.Instance.Error($"Error loading Quest Objective item at index {ir} for Quest {GetHashKey}: {ex.Message}");
                                    }
                                }
                            }

                        if (GetQuestItem.Stages != null)
                            if (GetQuestItem.Stages.Count > 0)
                            {
                                int CountStage = 0;
                                for (int ii = 0; ii < GetQuestItem.Stages.Count; ii++)
                                {
                                    try
                                    {
                                        CountStage++;
                                        for (int iii = 0; iii < GetQuestItem.Stages[ii].LogEntries.Count; iii++)
                                        {
                                            try
                                            {
                                                CountStage++;
                                                var GetLogEntrieItem = GetQuestItem.Stages[ii].LogEntries[iii];

                                                var GetEntry = GetLogEntrieItem.Entry;
                                                if (GetEntry != null)
                                                {
                                                    string SetType = string.Format("Entry[{0}]", CountStage);
                                                    string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                                    Records.Add(new RecordItem(GetQuestItem.FormKey,GetUniqueKey, GetEntry,"QSDT CNAM", GetQuestItem.EditorID));
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Instance.Error($"Error loading Quest Log Entry item at index {iii} in Stage {ii} for Quest {GetHashKey}: {ex.Message}");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Instance.Error($"Error loading Quest Stage item at index {ii} for Quest {GetHashKey}: {ex.Message}");
                                    }
                                }
                            }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Quest item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadFactions(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Factions != null)
                for (int i = 0; i < Reader.Factions.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Factions.ElementAt(i).Key;
                        var GetFactionItem = Reader.Factions[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetFactionItem.FormKey, GetFactionItem.EditorID);

                        var GetName = GetFactionItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetFactionItem.FormKey, GetUniqueKey, GetName, "FACT FULL", GetFactionItem.EditorID));
                        }

                        if (GetFactionItem.Ranks != null)
                            if (GetFactionItem.Ranks.Count > 0)
                            {
                                int CountRank = 0;
                                if (GetFactionItem.Ranks != null)
                                    foreach (var GetRank in GetFactionItem.Ranks)
                                    {
                                        try
                                        {
                                            CountRank++;
                                            if (GetRank.Title != null)
                                            {
                                                var GetFemale = GetRank.Title.Female;
                                                var GetMale = GetRank.Title.Male;

                                                if (GetFemale != null)
                                                {
                                                    string SetType = string.Format("Female[{0}]", CountRank);
                                                    string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                                    Records.Add(new RecordItem(GetFactionItem.FormKey, GetUniqueKey, GetFemale, "FACT FNAM", GetFactionItem.EditorID));
                                                }
                                                if (GetMale != null)
                                                {
                                                    string SetType = string.Format("Male[{0}]", CountRank);
                                                    string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                                    Records.Add(new RecordItem(GetFactionItem.FormKey, GetUniqueKey, GetMale, "FACT MNAM", GetFactionItem.EditorID));
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Instance.Error($"Error loading Faction Rank item for Faction {GetHashKey}: {ex.Message}");
                                        }
                                    }
                            }

                        if (GetFactionItem.Relations != null)
                            if (GetFactionItem.Relations.Count > 0)
                            {
                                // No data processing here, so no specific try-catch needed unless relations processing is added.
                            }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Faction item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadPerks(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Perks != null)
                for (int i = 0; i < Reader.Perks.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Perks.ElementAt(i).Key;
                        var GetPerkItem = Reader.Perks[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetPerkItem.FormKey, GetPerkItem.EditorID);

                        var GetName = GetPerkItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetPerkItem.FormKey, GetUniqueKey, GetName, "PERK FULL", GetPerkItem.EditorID));
                        }

                        var GetDescription = GetPerkItem.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetPerkItem.FormKey, GetUniqueKey, GetDescription, "PERK DESC", GetPerkItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Perk item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadWeapons(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Weapons != null)
                for (int i = 0; i < Reader.Weapons.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Weapons.ElementAt(i).Key;
                        var GetWeapon = Reader.Weapons[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetWeapon.FormKey, GetWeapon.EditorID);

                        var GetName = GetWeapon.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetWeapon.FormKey, GetUniqueKey, GetName, "WEAP FULL", GetWeapon.EditorID));
                        }

                        var GetDescription = GetWeapon.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);


                            Records.Add(new RecordItem(GetWeapon.FormKey, GetUniqueKey, GetDescription, "WEAP DESC", GetWeapon.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Weapon item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadSoulGems(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.SoulGems != null)
                for (int i = 0; i < Reader.SoulGems.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.SoulGems.ElementAt(i).Key;
                        var GetSoulGem = Reader.SoulGems[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetSoulGem.FormKey, GetSoulGem.EditorID);

                        var GetName = GetSoulGem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetSoulGem.FormKey, GetUniqueKey, GetName, "SLGM FULL", GetSoulGem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Soul Gem item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadArmors(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Armors != null)
                for (int i = 0; i < Reader.Armors.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Armors.ElementAt(i).Key;
                        var GetArmor = Reader.Armors[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetArmor.FormKey, GetArmor.EditorID);

                        var GetName = GetArmor.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetArmor.FormKey, GetUniqueKey, GetName, "ARMO FULL", GetArmor.EditorID));
                        }

                        var GetDescription = GetArmor.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetArmor.FormKey, GetUniqueKey, GetDescription, "ARMO DESC", GetArmor.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Armor item at index {i}: {ex.Message}");
                    }
                }

            return Records;
        }

        public static List<RecordItem> LoadKeys(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Keys != null)
                for (int i = 0; i < Reader.Keys.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Keys.ElementAt(i).Key;
                        var GetKey = Reader.Keys[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetKey.FormKey, GetKey.EditorID);

                        var GetName = GetKey.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetKey.FormKey, GetUniqueKey, GetName, "KEYM FULL", GetKey.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Key item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadContainers(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Containers != null)
                for (int i = 0; i < Reader.Containers.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Containers.ElementAt(i).Key;
                        var GetContainer = Reader.Containers[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetContainer.FormKey, GetContainer.EditorID);

                        var GetName = GetContainer.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetContainer.FormKey, GetUniqueKey, GetName, "CONT FULL", GetContainer.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Container item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadActivators(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Activators != null)
                for (int i = 0; i < Reader.Activators.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Activators.ElementAt(i).Key;
                        var GetActivator = Reader.Activators[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetActivator.FormKey, GetActivator.EditorID);

                        var GetName = GetActivator.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetActivator.FormKey, GetUniqueKey, GetName, "ACTI FULL", GetActivator.EditorID));
                        }

                        var GetActivateTextOverride = GetActivator.ActivateTextOverride;
                        if (GetActivateTextOverride != null)
                        {
                            string SetType = "ActivateTextOverride";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetActivator.FormKey, GetUniqueKey, GetActivateTextOverride, "ACTI RNAM", GetActivator.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Activator item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadMiscItems(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.MiscItems != null)
                for (int i = 0; i < Reader.MiscItems.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.MiscItems.ElementAt(i).Key;
                        var GetMiscItem = Reader.MiscItems[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetMiscItem.FormKey, GetMiscItem.EditorID);

                        var GetName = GetMiscItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetMiscItem.FormKey, GetUniqueKey, GetName, "MISC FULL", GetMiscItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Misc Item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadBooks(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Books != null)
                for (int i = 0; i < Reader.Books.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Books.ElementAt(i).Key;
                        var Books = Reader.Books[GetHashKey];

                        string AutoKey = EspReader.GenKey(Books.FormKey, Books.EditorID);

                        var GetName = Books.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(Books.FormKey, GetUniqueKey, GetName, "BOOK FULL", Books.EditorID));
                        }

                        var GetDescription = Books.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(Books.FormKey, GetUniqueKey, GetDescription, "BOOK CNAM", Books.EditorID));
                        }

                        var GetBookText = Books.BookText;
                        if (GetBookText != null)
                        {
                            string SetType = "BookText";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(Books.FormKey, GetUniqueKey, GetBookText, "BOOK DESC", Books.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Book item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadMessages(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Messages != null)
                for (int i = 0; i < Reader.Messages.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Messages.ElementAt(i).Key;
                        var GetMessageItem = Reader.Messages[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetMessageItem.FormKey, GetMessageItem.EditorID);

                        var GetName = GetMessageItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetMessageItem.FormKey, GetUniqueKey, GetName, "MESG FULL", GetMessageItem.EditorID));
                        }

                        var GetDescription = GetMessageItem.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetMessageItem.FormKey, GetUniqueKey, GetDescription, "MESG DESC", GetMessageItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Message item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadMessageButtons(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Messages != null)
                for (int i = 0; i < Reader.Messages.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Messages.ElementAt(i).Key;
                        var GetMessageItem = Reader.Messages[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetMessageItem.FormKey, GetMessageItem.EditorID);

                        var GetButtons = GetMessageItem.MenuButtons;
                        if (GetButtons != null)
                        {
                            if (GetButtons.Count > 0)
                            {
                                for (int ir = 0; ir < GetButtons.Count; ir++)
                                {
                                    try
                                    {
                                        var GetButton = GetButtons[ir].Text;
                                        if (GetButton != null)
                                        {
                                            string SetType = string.Format("Button[{0}]", ir);
                                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                            Records.Add(new RecordItem(GetMessageItem.FormKey, GetUniqueKey, GetButton, "MESG ITXT", GetMessageItem.EditorID));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Instance.Error($"Error loading Message Button item at index {ir} for Message {GetHashKey}: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error processing Message buttons for item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadDialogTopics(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.DialogTopics != null)
                for (int i = 0; i < Reader.DialogTopics.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.DialogTopics.ElementAt(i).Key;
                        var GetDialogTopicItem = Reader.DialogTopics[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetDialogTopicItem.FormKey, GetDialogTopicItem.EditorID);

                        var GetName = GetDialogTopicItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                           Records.Add(new RecordItem(GetDialogTopicItem.FormKey, GetUniqueKey, GetName, "DIAL FULL", GetDialogTopicItem.EditorID));
                        }

                        var GetResponses = GetDialogTopicItem.Responses;
                        int ForCount = 0;
                        if (GetResponses != null)
                            foreach (var GetChild in GetResponses)
                            {
                                try
                                {
                                    ForCount++;
                                    var GetPrompt = GetChild.Prompt;
                                    if (GetPrompt != null)
                                    {
                                        string SetType = string.Format("ResponsePrompt[{0}]", ForCount);
                                        string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                        Records.Add(new RecordItem(GetChild.FormKey, GetUniqueKey, GetPrompt, "INFO RNAM", GetChild.EditorID));
                                    }

                                    if (GetChild.Responses != null)
                                        foreach (var GetChildA in GetChild.Responses)
                                        {
                                            try
                                            {
                                                ForCount++;

                                                var GetValue = GetChildA.Text;
                                                if (GetValue != null)
                                                {
                                                    string SetType = string.Format("Response[{0}]", ForCount);
                                                    string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                                    Records.Add(new RecordItem(GetChild.FormKey, GetUniqueKey, GetValue, "INFO NAM1", GetChild.EditorID));
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Instance.Error($"Error loading Dialog Topic child response for Dialog Topic {GetHashKey}: {ex.Message}");
                                            }
                                        }
                                }
                                catch (Exception ex)
                                {
                                    Log.Instance.Error($"Error loading Dialog Topic response for Dialog Topic {GetHashKey}: {ex.Message}");
                                }
                            }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Dialog Topic item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadSpells(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Spells != null)
                for (int i = 0; i < Reader.Spells.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Spells.ElementAt(i).Key;
                        var GetSpellItem = Reader.Spells[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetSpellItem.FormKey, GetSpellItem.EditorID);

                        var GetName = GetSpellItem.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetSpellItem.FormKey, GetUniqueKey, GetName, "SPEL FULL", GetSpellItem.EditorID));
                        }

                        var GetDescription = GetSpellItem.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetSpellItem.FormKey, GetUniqueKey, GetDescription, "SPEL DESC", GetSpellItem.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Spell item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadObjectEffects(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.ObjectEffects != null)
                for (int i = 0; i < Reader.ObjectEffects.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.ObjectEffects.ElementAt(i).Key;
                        var GetObjectEffect = Reader.ObjectEffects[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetObjectEffect.FormKey, GetObjectEffect.EditorID);

                        var GetName = GetObjectEffect.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetObjectEffect.FormKey, GetUniqueKey, GetName, "ENCH FULL", GetObjectEffect.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Object Effect item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadMagicEffects(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.MagicEffects != null)
                for (int i = 0; i < Reader.MagicEffects.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.MagicEffects.ElementAt(i).Key;
                        var GetMagicEffect = Reader.MagicEffects[GetHashKey];

                        string AutoKey = EspReader.GenKey(GetMagicEffect.FormKey, GetMagicEffect.EditorID);

                        var GetName = GetMagicEffect.Name;
                        if (GetName != null)
                        {
                            string SetType = "Name";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetMagicEffect.FormKey, GetUniqueKey, GetName, "MGEF FULL", GetMagicEffect.EditorID));
                        }

                        var GetDescription = GetMagicEffect.Description;
                        if (GetDescription != null)
                        {
                            string SetType = "Description";
                            string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                            Records.Add(new RecordItem(GetMagicEffect.FormKey, GetUniqueKey, GetDescription, "MGEF DESC", GetMagicEffect.EditorID));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Magic Effect item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }

        public static List<RecordItem> LoadCells(EspReader Reader)
        {
            List<RecordItem> Records = new List<RecordItem>();
            if (Reader.Cells != null)
                for (int i = 0; i < Reader.Cells.Count; i++)
                {
                    try
                    {
                        string GetTransStr = "";

                        var GetHashKey = Reader.Cells.ElementAt(i).Key;
                        var GetCell = Reader.Cells[GetHashKey];
                        int ForID = 0;
                        if (GetCell.SubBlocks != null)
                            foreach (var Get in GetCell.SubBlocks)
                            {
                                try
                                {
                                    ForID++;
                                    if (Get.Cells != null)
                                        foreach (var GetChild in Get.Cells)
                                        {
                                            try
                                            {
                                                ForID++;
                                                var GetName = GetChild.Name;
                                                if (GetName != null)
                                                {
                                                    string AutoKey = EspReader.GenKey(GetChild.FormKey, GetChild.EditorID);

                                                    string SetType = string.Format("Cell[{0}]", ForID);
                                                    string GetUniqueKey = GenUniqueKey(AutoKey, SetType);

                                                    Records.Add(new RecordItem(GetChild.FormKey, GetUniqueKey, GetName, "CELL FULL", GetChild.EditorID));
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Instance.Error($"Error loading Cell sub-block child cell for Cell {GetHashKey}: {ex.Message}");
                                            }
                                        }
                                }
                                catch (Exception ex)
                                {
                                    Log.Instance.Error($"Error loading Cell sub-block for Cell {GetHashKey}: {ex.Message}");
                                }
                            }
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.Error($"Error loading Cell item at index {i}: {ex.Message}");
                    }
                }
            return Records;
        }
    }
}
