using System.Text.Json;

namespace SkyrimJPStringPatcher.Core;

public static class DsdWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // DSD (Manager.cpp: processFiles()) scans every *.json file inside a plugin-named
    // folder via directory_iterator and merges them all — a folder is NOT limited to
    // one file, and DSD does not care what a file is named. We deliberately do NOT
    // name our output "<Plugin>.json": that's also the filename convention several
    // real-world Japanese translation packs use for the same folder (e.g. a
    // "<Plugin> gating-folder>/<Plugin>.json" USSEP patch), and if our mod is
    // installed with higher MO2 priority than one of those, an identically-named
    // file would completely shadow (not merge with) the existing, far more complete
    // translation in MO2's virtual filesystem — silently un-translating everything
    // it covered. A fixed, tool-specific filename sidesteps that collision entirely.
    private const string OutputFileName = "SkyrimJPStringPatcher.json";

    /// <summary>
    /// Writes one DSD json per winning plugin under
    /// &lt;outputRoot&gt;/SKSE/Plugins/DynamicStringDistributor/&lt;WinningPlugin&gt;/SkyrimJPStringPatcher.json,
    /// wiping outputRoot first so every run produces a clean, reproducible result.
    /// Deliberately additive alongside any other mod's DSD json in the same
    /// plugin folder — see remarks on <see cref="OutputFileName"/>.
    /// </summary>
    public static void WriteAll(string outputRoot, IReadOnlyDictionary<string, List<DsdEntry>> entriesByWinningPlugin, TraceLog? trace = null)
    {
        if (Directory.Exists(outputRoot))
        {
            trace?.Debug($"Deleting existing output dir: {outputRoot}");
            Directory.Delete(outputRoot, recursive: true);
        }

        var dsdRoot = Path.Combine(outputRoot, "SKSE", "Plugins", "DynamicStringDistributor");
        Directory.CreateDirectory(dsdRoot);

        foreach (var (winningPlugin, entries) in entriesByWinningPlugin)
        {
            if (entries.Count == 0) continue;

            var pluginDir = Path.Combine(dsdRoot, winningPlugin);
            Directory.CreateDirectory(pluginDir);

            var jsonPath = Path.Combine(pluginDir, OutputFileName);
            trace?.Trace($"Write start: {jsonPath} ({entries.Count} entries)");
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(jsonPath, json);
            trace?.Trace($"Write done: {jsonPath}");
        }
    }
}
