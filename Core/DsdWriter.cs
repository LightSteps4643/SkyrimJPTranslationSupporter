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
    // it covered.
    //
    // v0.57.4: a FIXED tool-specific name (still "SkyrimJPStringPatcher.json" up to
    // v0.57.3) sidesteps collision with OTHER mods, but not with this tool's OWN
    // prior output for the SAME plugin — found via a user's own incremental-workflow
    // walkthrough: translate 90/100 records now, install; later translate the
    // remaining 10 (PickUpTarget's own DSD-coverage scan correctly excludes the
    // already-covered 90 from being re-candidated, so this run's output is JUST the
    // 10 new ones); installing that second batch — whether by overwriting the same
    // mod's file, or as a second separate mod — lands at the IDENTICAL relative path
    // in MO2's VFS either way, so the 90 that aren't in this batch get shadowed out
    // entirely by the 10-only file. A per-run timestamp in the filename makes
    // successive incremental batches coexist as separate, non-colliding files DSD
    // merges together, instead of replacing/shadowing each other.
    //
    // Residual risk (accepted, not solved by this alone — see DESIGN_NOTES.md's
    // v0.57.4 entry): DSD's own duplicate-(FormID,Type) resolution is "first file
    // processed wins" (confirmed by reading Manager.cpp directly — see the v0.4.0
    // section of DESIGN_HISTORY.md), and directory_iterator's order is not something
    // this filename scheme controls. So a RE-translation of an already-covered
    // record (e.g. via --include-stale after the source mod's text changed) isn't
    // guaranteed to take effect just by adding a new timestamped file — the OLD
    // entry may still win. This only matters for correcting an existing record, not
    // for adding newly-translated ones (the common case, and now safe); the user
    // decided this trade-off is worth it as-is.
    private static string BuildOutputFileName(DateTime timestamp) => $"SkyrimJPStringPatcher_{timestamp:yyyyMMddHHmmss}.json";

    /// <summary>
    /// Writes one DSD json per winning plugin under
    /// &lt;outputRoot&gt;/SKSE/Plugins/DynamicStringDistributor/&lt;WinningPlugin&gt;/SkyrimJPStringPatcher_&lt;timestamp&gt;.json,
    /// wiping outputRoot first so every run produces a clean, reproducible result.
    /// Deliberately additive alongside any other mod's DSD json in the same
    /// plugin folder, AND alongside an earlier run's own output once both are
    /// installed together — see remarks on <see cref="BuildOutputFileName"/>.
    /// </summary>
    /// <param name="timestamp">Stamped into the output filename (see
    /// <see cref="BuildOutputFileName"/>). Defaults to the real current time;
    /// callers only ever pass an explicit value to get a deterministic,
    /// reproducible filename (tests, golden-file fixtures).</param>
    public static void WriteAll(string outputRoot, IReadOnlyDictionary<string, List<DsdEntry>> entriesByWinningPlugin, TraceLog? trace = null, DateTime? timestamp = null)
    {
        if (Directory.Exists(outputRoot))
        {
            trace?.Debug($"Deleting existing output dir: {outputRoot}");
            Directory.Delete(outputRoot, recursive: true);
        }

        var dsdRoot = Path.Combine(outputRoot, "SKSE", "Plugins", "DynamicStringDistributor");
        Directory.CreateDirectory(dsdRoot);

        var outputFileName = BuildOutputFileName(timestamp ?? DateTime.Now);

        foreach (var (winningPlugin, entries) in entriesByWinningPlugin)
        {
            if (entries.Count == 0) continue;

            var pluginDir = Path.Combine(dsdRoot, winningPlugin);
            Directory.CreateDirectory(pluginDir);

            var jsonPath = Path.Combine(pluginDir, outputFileName);
            trace?.Trace($"Write start: {jsonPath} ({entries.Count} entries)");
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(jsonPath, json);
            trace?.Trace($"Write done: {jsonPath}");
        }
    }
}
