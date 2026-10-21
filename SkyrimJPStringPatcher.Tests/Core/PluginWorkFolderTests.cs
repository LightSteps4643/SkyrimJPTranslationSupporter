using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// Requirement: the working folder of a plugin (Translation/out_temp/&lt;folder&gt;) is a valid Windows folder name,
/// different plugins never share one, and the name does not depend on which other plugins are installed.
/// The plugin's file name satisfies all three as it is: a file name is a valid name already, two plugins of one
/// load order never have the same file name, and the last character belongs to the extension (".esp"), so a space
/// before the extension ("Map Zoom Fixer PRO .esp") is not a trailing space — which Windows silently strips from a
/// folder name, so a file written below the unstripped path was not found. A name that drops the extension fails
/// the first two ("Map Zoom Fixer PRO " is stripped; "DynDOLOD.esm" and "DynDOLOD.esp" share "DynDOLOD", and one
/// overwrote the other). The GUI keeps its own copy of the rule (it must not reference the pipeline), so both are
/// pinned to the same table.
/// </summary>
public class PluginWorkFolderTests
{
    public static TheoryData<string, string> Cases => new()
    {
        { "Skyrim.esm", "Skyrim.esm" },
        { "Map Zoom Fixer PRO .esp", "Map Zoom Fixer PRO .esp" },
        { "DynDOLOD.esm", "DynDOLOD.esm" },
        { "DynDOLOD.esp", "DynDOLOD.esp" },
        { "A.B.esl", "A.B.esl" },
        { "鋼の剣 Mod.esp", "鋼の剣 Mod.esp" },
        { "A:B.esp", "A_B.esp" },       // not a possible file name; kept safe anyway
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFolderIsNamedAfterTheFileName_WithOnlyUnusableCharactersReplaced(string plugin, string expected)
    {
        Assert.Equal(expected, PluginWorkFolder.NameOf(plugin));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheGuiCopyOfTheRule_GivesTheSameName(string plugin, string _)
    {
        Assert.Equal(PluginWorkFolder.NameOf(plugin), PluginFolderName.From(plugin));
    }

    [Fact]
    public void PluginsThatShareABaseName_GetDifferentFolders()
    {
        var plugins = new[] { "DynDOLOD.esm", "DynDOLOD.esp", "Embers XD.esp", "Embers XD.esm", "Foo .esp", "Foo.esp", "Foo.esl" };

        var folders = plugins.Select(PluginWorkFolder.NameOf).ToList();

        Assert.Equal(plugins.Length, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ASpaceBeforeTheExtension_WorksAsAFolderNameOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_wf_{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(root, PluginWorkFolder.NameOf("Map Zoom Fixer PRO .esp"));
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "mod_glossary.tsv"), "x");   // threw DirectoryNotFoundException with the extension dropped

            Assert.True(File.Exists(Path.Combine(dir, "mod_glossary.tsv")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
