using System.Text;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// Requirement: the names MO2 keeps in its profile files reach the tool as written, whichever of UTF-8 and
/// Windows-1252 the file's bytes are (each line is read by the rule used for a plugin's text). A plugin whose name
/// carries "é" and is listed in Windows-1252 must still be found in the load order; a mod folder named in Japanese
/// and listed in UTF-8 must keep working.
/// </summary>
public class Mo2InstanceTextEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_mo2enc_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private string Build(string modFolder, string pluginFile, byte[] modlistBytes, byte[] pluginsBytes)
    {
        var mo2Dir = Path.Combine(_root, "mo2");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(Path.Combine(mo2Dir, "mods", modFolder));
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));
        File.WriteAllText(Path.Combine(mo2Dir, "mods", modFolder, pluginFile), "");
        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" + $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" + "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllBytes(Path.Combine(profileDir, "modlist.txt"), modlistBytes);
        File.WriteAllBytes(Path.Combine(profileDir, "plugins.txt"), pluginsBytes);
        return mo2Dir;
    }

    [Fact]
    public void APluginListedInWindows1252_IsFoundInTheLoadOrder()
    {
        var mo2Dir = Build("TestMod", "Café Mod.esp",
            Ascii("+TestMod\r\n"),
            [.. Ascii("*Caf"), 0xE9, .. Ascii(" Mod.esp\r\n")]);

        var instance = Mo2InstanceReader.Read(mo2Dir);

        Assert.Contains(instance.LoadOrder, p => p.FileName == "Café Mod.esp");
    }

    [Fact]
    public void AModFolderListedInUtf8_KeepsWorking()
    {
        var mo2Dir = Build("鋼の剣 Mod", "Blade.esp",
            [.. Encoding.UTF8.GetBytes("+鋼の剣 Mod\r\n")],
            Ascii("*Blade.esp\r\n"));

        var instance = Mo2InstanceReader.Read(mo2Dir);

        Assert.Contains(instance.LoadOrder, p => p.FileName == "Blade.esp");
    }
}
