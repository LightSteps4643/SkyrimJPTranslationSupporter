using System.Text;

namespace SkyrimJPStringPatcher.Core;

public sealed record ResolvedPlugin(string FileName, string AbsolutePath);

/// <summary>
/// v0.57.1: thrown by <see cref="Mo2InstanceReader.Read"/> for a problem that
/// stems from the SUPPLIED configuration (a wrong instance folder, a missing
/// ModOrganizer.ini key, a nonexistent override path) rather than a genuine
/// bug in this tool. Callers (`Program.cs`'s <c>pickuptarget</c> command)
/// catch this ONE specific type and turn it into a clean, deliberate exit
/// (code 1 + this exception's own readable <see cref="Exception.Message"/>)
/// instead of letting it propagate as an unhandled exception.
///
/// Added after a real user hit exactly that unhandled-crash path (reported on
/// a mod mirror site as "終了コード-532462766" — 0xE0434352, .NET's fixed
/// "unhandled exception" exit code, not anything MO2-specific) from an
/// ordinary input mistake: pointing at the wrong folder. Before this,
/// `Read()` let raw framework exceptions
/// (FileNotFoundException/KeyNotFoundException/DirectoryNotFoundException)
/// escape for every one of these cases, and `Program.cs` had no outer catch
/// to stop them from crashing the whole process. See DESIGN_NOTES.md's
/// v0.57.1 entry for the full black-box test matrix this satisfies.
/// </summary>
public sealed class Mo2InstanceConfigurationException : Exception
{
    public Mo2InstanceConfigurationException(string message) : base(message) { }
}

/// <summary>
/// Reads a Mod Organizer 2 instance (ModOrganizer.ini + the active profile's
/// modlist.txt / plugins.txt) and resolves each active plugin to the physical
/// file that MO2's VFS would actually serve, without needing MO2 itself running.
/// </summary>
public static class Mo2InstanceReader
{
    /// <param name="instanceDir">Where ModOrganizer.ini lives — this is what
    /// "instance folder" means by definition, so unlike the three overrides
    /// below it is never optional.</param>
    /// <param name="modsDirOverride">v0.57.0: overrides the derived
    /// `&lt;instanceDir&gt;/mods` location. Needed only when MO2's own "Paths"
    /// settings tab (<c>[Settings] mod_directory</c> in ModOrganizer.ini, which
    /// this reader deliberately does not parse — see DESIGN_NOTES.md) redirects
    /// it elsewhere; null/empty keeps the existing auto-derived default.</param>
    /// <param name="profileDirOverride">Overrides the derived
    /// `&lt;instanceDir&gt;/profiles/&lt;selected_profile&gt;` location — the
    /// full path to the active profile's own folder, not just a base
    /// `profiles` directory (mirrors `profiles_directory` + selected_profile
    /// in MO2's own resolution). Null/empty keeps the auto-derived default.</param>
    /// <param name="overwriteDirOverride">Overrides the derived
    /// `&lt;instanceDir&gt;/overwrite` location. Null/empty keeps the
    /// auto-derived default.</param>
    public static Mo2Instance Read(
        string instanceDir, string? modsDirOverride = null, string? profileDirOverride = null, string? overwriteDirOverride = null)
    {
        var iniPath = Path.Combine(instanceDir, "ModOrganizer.ini");
        if (!File.Exists(iniPath))
            throw new Mo2InstanceConfigurationException(
                $"ModOrganizer.ini not found under '{instanceDir}' — is this actually the MO2 instance folder " +
                "(the one ModOrganizer.ini itself lives in directly), not a parent folder or MO2's own install folder?");

        var ini = ParseIni(iniPath);
        var gamePath = UnwrapByteArray(RequireIniValue(ini, "gamePath", iniPath));
        var profileName = UnwrapByteArray(RequireIniValue(ini, "selected_profile", iniPath));

        // v0.57.1: gamePath not existing doesn't crash today -- it silently
        // drops every vanilla/DLC/CC "implicit master" from the load order
        // with zero warning (AddImplicitMasters below just returns early).
        // That's worse than a crash (a silent corpus-quality loss, not a
        // visible failure), so it gets the same clear-error treatment.
        if (!Directory.Exists(gamePath))
            throw new Mo2InstanceConfigurationException(
                $"gamePath in '{iniPath}' points to a folder that doesn't exist: '{gamePath}'");

        var modsDir = string.IsNullOrWhiteSpace(modsDirOverride) ? Path.Combine(instanceDir, "mods") : modsDirOverride;
        var overwriteDir = string.IsNullOrWhiteSpace(overwriteDirOverride) ? Path.Combine(instanceDir, "overwrite") : overwriteDirOverride;
        var profileDir = string.IsNullOrWhiteSpace(profileDirOverride) ? Path.Combine(instanceDir, "profiles", profileName) : profileDirOverride;

        // v0.57.3: profileDirOverride can point at ANY profile folder, not just
        // the one ModOrganizer.ini's own selected_profile names -- that's the
        // whole point (scan a different profile without touching what's
        // active in MO2 itself). So when it's set, what this run actually
        // read is the override folder's OWN name, not the ini's selected_profile
        // -- using the ini value here would silently mislabel every log line
        // (found while explaining this override's behavior to the user).
        var effectiveProfileName = string.IsNullOrWhiteSpace(profileDirOverride)
            ? profileName
            : Path.GetFileName(profileDirOverride.TrimEnd('/', '\\'));

        // v0.57.1: checked uniformly whether each path came from an explicit
        // override or was auto-derived from instanceDir -- a missing default
        // gets exactly the same clear error as a missing override, never a
        // silent skip (see DESIGN_NOTES.md's v0.57.1 entry for why: a real
        // MO2 instance always has all three of mods/profiles/overwrite from
        // the moment it's created, so a missing one is always a genuine
        // misconfiguration worth surfacing, not a tolerable "empty" state).
        RequireDirectory(modsDir, "mods");
        RequireDirectory(overwriteDir, "overwrite");
        RequireDirectory(profileDir, $"profiles/{effectiveProfileName}");
        RequireFile(Path.Combine(profileDir, "modlist.txt"), "modlist.txt");
        RequireFile(Path.Combine(profileDir, "plugins.txt"), "plugins.txt");

        var modPriorityAll = ReadModListPriority(Path.Combine(profileDir, "modlist.txt"), enabledOnly: false);
        var modPriorityEnabled = ReadModListPriority(Path.Combine(profileDir, "modlist.txt"), enabledOnly: true);
        var pluginOrder = ReadActivePluginOrder(Path.Combine(profileDir, "plugins.txt"));

        var resolved = new List<ResolvedPlugin>();

        AddImplicitMasters(gamePath, pluginOrder, resolved);

        // v0.52.0a: one Directory.EnumerateFiles pass per mod folder, not one
        // File.Exists probe per (plugin, mod) pair. The old ResolvePluginPath
        // walked the full mod-priority list per plugin, so a 332-plugin/
        // several-hundred-mod instance meant tens of thousands of disk hits for
        // something a single top-level directory listing per mod already answers.
        var pluginPathIndex = BuildPluginPathIndex(modPriorityAll, modsDir);

        var alreadyAdded = resolved.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pluginFileName in pluginOrder)
        {
            if (alreadyAdded.Contains(pluginFileName)) continue; // implicit master, added above
            var path = ResolvePluginPath(pluginFileName, pluginPathIndex, overwriteDir, gamePath);
            if (path != null)
                resolved.Add(new ResolvedPlugin(pluginFileName, path));
            // A plugin we can't physically find (e.g. a DLC/CC entry that lives
            // only in the game's own Data folder) falls through to the gamePath
            // check inside ResolvePluginPath; if still null, it's genuinely missing
            // and is skipped with a console warning there.
        }

        return new Mo2Instance(gamePath, effectiveProfileName, modsDir, overwriteDir, modPriorityEnabled, resolved);
    }

    /// <summary>
    /// The plugins the game force-loads before anything in plugins.txt, in the
    /// order it loads them. MO2 does not list these (they are not user-toggleable),
    /// so they have to be supplied here or they are simply absent from the load
    /// order.
    ///
    /// v0.11.1 bug fix: this list previously held only Skyrim.esm and Update.esm,
    /// which silently dropped **all three DLC masters** plus the AE Creation Club
    /// content. The symptom that exposed it: "Dragonbone Arrow" had no Japanese
    /// precedent in the corpus despite obviously having one in game — because
    /// Dawnguard.esm was never opened at all. Every DLC-derived English→Japanese
    /// pair was missing, which depressed both the exact-match step and the
    /// precedent quality across the board.
    ///
    /// The order is NOT alphabetical and must not be re-sorted — it is the game's
    /// own fixed sequence (verified against a live load order: Fish, then
    /// SurvivalMode, then Curios, then AdvDSGS, then _ResourcePack). Entries that
    /// do not exist on disk are skipped, so an install without a given DLC or CC
    /// item is handled by the same list.
    /// </summary>
    private static readonly string[] ImplicitMasters =
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
        "ccBGSSSE001-Fish.esm", "ccQDRSSE001-SurvivalMode.esl", "ccBGSSSE037-Curios.esl",
        "ccBGSSSE025-AdvDSGS.esm", "_ResourcePack.esl",
    };

    private static void AddImplicitMasters(string gamePath, List<string> pluginOrder, List<ResolvedPlugin> resolved)
    {
        var dataDir = Path.Combine(gamePath, "Data");
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var master in ImplicitMasters)
        {
            var path = Path.Combine(dataDir, master);
            if (!File.Exists(path)) continue;
            resolved.Add(new ResolvedPlugin(master, path));
            added.Add(master);
        }

        // Anything else the game ships that plugins.txt does not list — separately
        // purchased Creation Club content, for instance — would otherwise vanish
        // the same way the DLCs just did. Appended after the known set and
        // reported, since their true position in the game's sequence is not
        // knowable from disk alone.
        if (!Directory.Exists(dataDir)) return;
        var listed = pluginOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.es?")
                     .Where(f => Path.GetExtension(f) is ".esm" or ".esl")
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (added.Contains(name) || listed.Contains(name)) continue;
            Console.WriteLine($"[info] force-loaded plugin not in plugins.txt, appended after the known set: {name}");
            resolved.Add(new ResolvedPlugin(name, file));
            added.Add(name);
        }
    }

    /// <summary>
    /// Builds the merged "winning file per relative path" view of the VFS for
    /// everything under &lt;Data&gt;/&lt;relativeSubPath&gt;, exactly as MO2 would
    /// serve it: overwrite always wins, then mods from lowest to highest
    /// priority overwrite each other's entries for the SAME relative path,
    /// while distinct relative paths from different mods all coexist.
    /// Only enabled mods (modlist.txt '+' entries) are considered.
    /// </summary>
    public static Dictionary<string, string> BuildVfsDirectoryMerge(Mo2Instance instance, string relativeSubPath)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Lowest priority first, so later (higher priority) writes win the dictionary slot.
        foreach (var modName in instance.EnabledModPriorityHighFirst.Reverse())
        {
            var modSubDir = Path.Combine(instance.ModsDir, modName, relativeSubPath);
            if (!Directory.Exists(modSubDir)) continue;

            foreach (var file in Directory.EnumerateFiles(modSubDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(modSubDir, file);
                merged[rel] = file;
            }
        }

        // Overwrite folder always wins over every mod.
        var overwriteSubDir = Path.Combine(instance.OverwriteDir, relativeSubPath);
        if (Directory.Exists(overwriteSubDir))
        {
            foreach (var file in Directory.EnumerateFiles(overwriteSubDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(overwriteSubDir, file);
                merged[rel] = file;
            }
        }

        return merged;
    }

    /// <summary>Builds "plugin filename -&gt; winning mod's physical path" for
    /// every .esm/.esp/.esl sitting at the top level of any mod folder (plugins
    /// are never nested inside a mod's own subfolders, unlike loose assets), by
    /// listing each mod folder once instead of probing per-plugin. Lowest
    /// priority first, so a higher-priority mod's file overwrites the dictionary
    /// slot exactly like <see cref="BuildVfsDirectoryMerge"/> does for assets.</summary>
    private static Dictionary<string, string> BuildPluginPathIndex(IReadOnlyList<string> modPriorityHighFirst, string modsDir)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modName in modPriorityHighFirst.Reverse())
        {
            var modDir = Path.Combine(modsDir, modName);
            if (!Directory.Exists(modDir)) continue;
            foreach (var file in Directory.EnumerateFiles(modDir, "*.es?", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetExtension(file) is not (".esm" or ".esp" or ".esl")) continue;
                index[Path.GetFileName(file)] = file;
            }
        }
        return index;
    }

    private static string? ResolvePluginPath(
        string pluginFileName, IReadOnlyDictionary<string, string> pluginPathIndex,
        string overwriteDir, string gamePath)
    {
        var inOverwrite = Path.Combine(overwriteDir, pluginFileName);
        if (File.Exists(inOverwrite)) return inOverwrite;

        if (pluginPathIndex.TryGetValue(pluginFileName, out var fromMod)) return fromMod;

        var inGameData = Path.Combine(gamePath, "Data", pluginFileName);
        if (File.Exists(inGameData)) return inGameData;

        Console.Error.WriteLine($"[warn] could not resolve physical path for plugin '{pluginFileName}' — skipping");
        return null;
    }

    /// <summary>v0.57.1: replaces the raw <c>Dictionary&lt;,&gt;</c> indexer's
    /// KeyNotFoundException (unreadable, and — being an ordinary framework
    /// exception, not <see cref="Mo2InstanceConfigurationException"/> — would
    /// crash the whole process rather than exit cleanly) for a
    /// ModOrganizer.ini missing an expected `[General]` key (e.g. a
    /// never-launched/hand-edited instance).</summary>
    private static string RequireIniValue(Dictionary<string, Dictionary<string, string>> ini, string key, string iniPath)
    {
        if (!ini["General"].TryGetValue(key, out var value))
            throw new Mo2InstanceConfigurationException($"'{key}' not found in the [General] section of '{iniPath}'");
        return value;
    }

    private static void RequireDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
            throw new Mo2InstanceConfigurationException($"{label} folder not found: '{path}'");
    }

    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path))
            throw new Mo2InstanceConfigurationException($"{label} not found: '{path}'");
    }

    /// <summary>Highest priority first (matches modlist.txt top-to-bottom order).</summary>
    private static List<string> ReadModListPriority(string modlistPath, bool enabledOnly)
    {
        var result = new List<string>();
        foreach (var rawLine in File.ReadAllLines(modlistPath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line[0] != '+' && line[0] != '-') continue;
            if (enabledOnly && line[0] != '+') continue;

            var name = line[1..];
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            // For plugin-path resolution (enabledOnly: false) we deliberately
            // include disabled mods too, at their recorded priority slot, in
            // case a plugin only exists in a disabled mod's folder — Resolve
            // is only ever asked for plugins that are ALSO active in
            // plugins.txt, so this is a harmless fallback. For loose-file VFS
            // merging (enabledOnly: true) disabled mods must NOT contribute,
            // since MO2 genuinely does not serve their files at all.
            result.Add(name);
        }
        return result;
    }

    private static List<string> ReadActivePluginOrder(string pluginsPath)
    {
        var result = new List<string>();
        foreach (var rawLine in File.ReadAllLines(pluginsPath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line[0] != '*') continue; // only active plugins participate in the load order
            result.Add(line[1..]);
        }
        return result;
    }

    private static string UnwrapByteArray(string value)
    {
        // MO2 stores some ini values as @ByteArray(...) — strip that wrapper if present.
        const string prefix = "@ByteArray(";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(')'))
            return value[prefix.Length..^1];
        return value;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = "General";
        sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                if (!sections.ContainsKey(current))
                    sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            sections[current][key] = value;
        }
        return sections;
    }
}

public sealed record Mo2Instance(
    string GamePath,
    string ProfileName,
    string ModsDir,
    string OverwriteDir,
    IReadOnlyList<string> EnabledModPriorityHighFirst,
    IReadOnlyList<ResolvedPlugin> LoadOrder);
