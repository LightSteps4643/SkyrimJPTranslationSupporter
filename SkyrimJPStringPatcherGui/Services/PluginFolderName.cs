namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Mirrors Translation/PromptGenerator.cs's MakeSafeFolderName exactly (strip
/// the file extension, replace invalid filename characters) — the CLI names
/// each plugin's Translation/out_temp/&lt;name&gt;/ folder this way (e.g.
/// "Skyrim.esm" -> "Skyrim"), so the GUI needs the same transform to find that
/// folder again. A small, stable, pure string function — not pipeline logic —
/// so duplicating it here (rather than referencing Core) keeps the "GUI has no
/// reference to the pipeline projects" boundary intact.
/// </summary>
public static class PluginFolderName
{
    public static string From(string plugin)
    {
        var name = Path.GetFileNameWithoutExtension(plugin);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }
}
