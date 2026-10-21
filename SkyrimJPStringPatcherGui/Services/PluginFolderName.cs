namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Mirrors Core/PluginWorkFolder.NameOf exactly — the name of a plugin's working folder
/// Translation/out_temp/&lt;name&gt; is the plugin's file name itself, extension included ("Skyrim.esm" ->
/// "Skyrim.esm"; only characters a folder name cannot have are replaced) — so the GUI finds the folder the CLI
/// wrote. A small, stable, pure string function — not pipeline logic — so duplicating it here (rather than
/// referencing Core) keeps the "GUI has no reference to the pipeline projects" boundary intact; a test pins the
/// two together. (An earlier version dropped the extension, which broke for a space before the extension and let
/// "DynDOLOD.esm" and "DynDOLOD.esp" share a folder.)
/// </summary>
public static class PluginFolderName
{
    public static string From(string plugin)
    {
        var name = plugin;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }
}
