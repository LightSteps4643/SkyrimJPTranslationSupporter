namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// The name of a plugin's working folder (<c>Translation/out_temp/&lt;name&gt;</c>): the plugin's file name itself,
/// extension included ("Skyrim.esm", "Map Zoom Fixer PRO .esp"). That is a valid folder name, differs between two
/// plugins of one load order ("DynDOLOD.esm" and "DynDOLOD.esp" are two folders), and does not depend on which other
/// plugins are installed. Dropping the extension broke all three: a space before the extension became a trailing
/// space, which Windows silently strips from a folder name, so a file written below the unstripped path was not
/// found; and plugins that share a base name shared a folder, the one processed last overwriting the other.
/// A real file name has no character a folder name cannot have; the replacement only keeps a stray input safe.
/// The GUI keeps a copy of this rule (it must not reference the pipeline); a test pins the two together.
/// </summary>
public static class PluginWorkFolder
{
    public static string NameOf(string plugin)
    {
        var name = plugin;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }
}
