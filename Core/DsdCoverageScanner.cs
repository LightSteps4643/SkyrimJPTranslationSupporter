using System.Globalization;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace SkyrimJPStringPatcher.Core;

public sealed record DsdCoverageEntry(string TranslatedString, string? OriginalRecorded, string SourceFile, int Index = 0, string? EditorId = null);

/// <summary>
/// v0.3.0: three views over the same scanned coverage, since different DSD
/// TranslationTypes match differently:
/// - <see cref="ByFormTypeIndex"/>: the common case — FormID + type + index
///   identifies a single covered field (covers FULL/DESC/RNAM/SHRT/TNAM and
///   indexed types like QUST NNAM, INFO NAM1/RNAM, MESG ITXT).
/// - <see cref="ByFormType"/>: ALL entries sharing a (FormID, type), regardless
///   of index — needed for "QUST CNAM" (DSD's kRuntimeLegacy exception, which
///   matches by original TEXT content across every log entry on the quest, not
///   by index at all).
/// - <see cref="ByEditorId"/>: keyed by "type|editorId" — needed for "GMST
///   DATA" (kGameSetting matches by EditorID, not FormID, since GMST FormIDs
///   are documented as unstable across game versions).
/// </summary>
public sealed record DsdCoverageIndex(
    Dictionary<(FormKey FormKey, string Type, int Index), DsdCoverageEntry> ByFormTypeIndex,
    Dictionary<(FormKey FormKey, string Type), List<DsdCoverageEntry>> ByFormType,
    Dictionary<string, DsdCoverageEntry> ByEditorId)
{
    public int Count => ByFormTypeIndex.Count;
}

/// <summary>
/// Resolves what the load order's *existing* DSD (Dynamic String Distributor)
/// files already cover, respecting MO2's VFS priority — if two mods both ship
/// a DSD json at the exact same relative path, only the higher-priority one's
/// content is real; DSD itself only reads a plugin-named folder at all if that
/// plugin is active, so folders for inactive plugins are ignored too.
/// </summary>
public static class DsdCoverageScanner
{
    private const string DsdRelativeRoot = "SKSE/Plugins/DynamicStringDistributor";

    public static DsdCoverageIndex Scan(Mo2Instance instance)
    {
        var byFormTypeIndex = new Dictionary<(FormKey, string, int), DsdCoverageEntry>();
        var byFormType = new Dictionary<(FormKey, string), List<DsdCoverageEntry>>();
        var byEditorId = new Dictionary<string, DsdCoverageEntry>(StringComparer.OrdinalIgnoreCase);

        var activePlugins = instance.LoadOrder
            .Select(p => p.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var winningFiles = Mo2InstanceReader.BuildVfsDirectoryMerge(instance, DsdRelativeRoot);

        // Group by the plugin-name folder (first path segment) so we only read
        // json files that live under a folder DSD would actually load.
        var byGatingPlugin = winningFiles
            .Where(kv => Path.GetExtension(kv.Key).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .GroupBy(kv => kv.Key.Split(Path.DirectorySeparatorChar, 2)[0]);

        foreach (var group in byGatingPlugin)
        {
            var gatingPlugin = group.Key;
            if (!activePlugins.Contains(gatingPlugin)) continue; // DSD wouldn't load this folder at all

            foreach (var (_, physicalPath) in group)
            {
                List<DsdSourceEntry>? entries;
                try
                {
                    var json = File.ReadAllText(physicalPath);
                    entries = JsonSerializer.Deserialize<List<DsdSourceEntry>>(json);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[warn] failed to parse DSD json '{physicalPath}': {ex.Message}");
                    continue;
                }
                if (entries == null) continue;

                foreach (var entry in entries)
                {
                    // Accept every type here (not just the ones PickUpTarget currently
                    // generates candidates for) — restricting to a subset would make
                    // PickUpTarget blind to existing coverage for types outside its
                    // current scope, which future scope expansion will need to see.
                    var coverageEntry = new DsdCoverageEntry(entry.String, entry.Original, physicalPath, entry.Index, entry.EditorId);

                    if (!string.IsNullOrWhiteSpace(entry.EditorId))
                    {
                        byEditorId[$"{entry.Type}|{entry.EditorId}"] = coverageEntry;
                        // GMST-style entries may still carry a form_id; fall through so
                        // they're ALSO indexed by form if parseable, harmless either way.
                    }

                    if (!TryParseFormId(entry.FormId, out var formKey)) continue;

                    byFormTypeIndex[(formKey, entry.Type, entry.Index)] = coverageEntry;

                    var formTypeKey = (formKey, entry.Type);
                    if (!byFormType.TryGetValue(formTypeKey, out var list))
                    {
                        list = new List<DsdCoverageEntry>();
                        byFormType[formTypeKey] = list;
                    }
                    list.Add(coverageEntry);
                }
            }
        }

        return new DsdCoverageIndex(byFormTypeIndex, byFormType, byEditorId);
    }

    /// <summary>
    /// Parses a DSD "XXXXXXXX|Plugin.esp" (or "XXXXXX|Plugin.esp") token. Only
    /// the LAST 6 hex digits are the real local FormID — a leading load-order
    /// index byte, if present, is an artifact of whatever load order the
    /// translation was authored against and is meaningless here since the
    /// master plugin name is already given explicitly.
    /// </summary>
    private static bool TryParseFormId(string token, out FormKey formKey)
    {
        formKey = default;
        var parts = token.Split('|', 2);
        if (parts.Length != 2) return false;

        var hex = parts[0];
        if (hex.Length > 6) hex = hex[^6..];
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var localId))
            return false;

        try
        {
            var modKey = ModKey.FromFileName(parts[1]);
            formKey = new FormKey(modKey, localId);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
