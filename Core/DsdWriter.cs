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
    // 2026-09-17: confirmed by reading Manager.cpp (processFiles()) directly —
    // DSD's own duplicate-(FormID,Type) resolution is NOT actually OS/filesystem-
    // order-dependent as the note below originally assumed. Files within one
    // plugin folder are explicitly re-sorted by filename in REVERSE alphabetical
    // order ("reverse order Z first, A last", per Manager.cpp's own comment)
    // before being processed, and the FIRST one processed for a given key wins
    // (checked via a "does this key already exist" guard before insert, or
    // try_emplace, depending on the TranslationType). This means the winner is
    // fully deterministic and IS controllable via filename: whichever file sorts
    // alphabetically LAST gets processed FIRST and wins.
    //
    // The "zzz_" prefix below exploits this: a lowercase-letter-led name sorts
    // after (byte-comparison-wise) the vast majority of real-world community
    // translation-patch names, which are typically plugin-name-led or uppercase-
    // led (see DsdWriterTests.WriteAll_OutputFileName_SortsAfterTypicalCommunityPatchNames).
    // Combined with the pre-existing per-run timestamp (below), this makes a
    // re-translation of an already-covered record (e.g. via --include-stale)
    // reliably win over BOTH this tool's own earlier output (guaranteed — the
    // timestamp always increases) AND, in most but not all cases, another mod's
    // differently-named DSD file (not guaranteed — a third-party file using the
    // same or a later-sorting trick could still win; this remains a residual,
    // accepted risk per the original v0.57.4 note, just a smaller one now).
    private static string BuildOutputFileName(DateTime timestamp) => $"zzz_SkyrimJPStringPatcher_{timestamp:yyyyMMddHHmmss}.json";

    /// <summary>
    /// Writes one DSD json per winning plugin under
    /// &lt;outputRoot&gt;/SKSE/Plugins/DynamicStringDistributor/&lt;WinningPlugin&gt;/zzz_SkyrimJPStringPatcher_&lt;timestamp&gt;.json,
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
