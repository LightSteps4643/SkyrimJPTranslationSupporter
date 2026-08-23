namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Mutagen's WithStringsFolder(path) expects ONE real directory to read
/// .STRINGS/.DLSTRINGS/.ILSTRINGS from. MO2's VFS spreads the WINNING copy of
/// each such file across many different mod folders (e.g. the vanilla
/// Skyrim_Japanese.STRINGS lives in a BSA, but a translation-update mod's loose
/// copy wins over it). This builds a throwaway staging folder containing just
/// the winning copy of every Strings/* file, so Mutagen can be pointed at a
/// single directory that reflects the VFS-correct result.
/// </summary>
public static class StringsStaging
{
    private const string StringsRelativeRoot = "Strings";

    /// <summary>Builds the staging folder and returns its path. Caller should delete it when done.</summary>
    public static string Build(Mo2Instance instance)
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), "SkyrimJPStringPatcher_Strings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        var winningFiles = Mo2InstanceReader.BuildVfsDirectoryMerge(instance, StringsRelativeRoot);

        // The vanilla game's own Data/Strings folder (loose, if present) and its
        // BSAs are the fallback winner for files no mod overrides — MO2 serves
        // BSA-packed content too, but this prototype only stages LOOSE files;
        // extracting from BSAs is a known simplification (see DESIGN_NOTES.md).
        var gameStringsDir = Path.Combine(instance.GamePath, "Data", StringsRelativeRoot);
        if (Directory.Exists(gameStringsDir))
        {
            foreach (var file in Directory.EnumerateFiles(gameStringsDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(gameStringsDir, file);
                if (!winningFiles.ContainsKey(rel))
                    winningFiles[rel] = file;
            }
        }

        foreach (var (relativePath, physicalPath) in winningFiles)
        {
            var dest = Path.Combine(stagingDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(physicalPath, dest, overwrite: true);
        }

        return stagingDir;
    }
}
