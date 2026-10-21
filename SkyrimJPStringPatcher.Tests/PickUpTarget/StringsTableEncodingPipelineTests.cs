using System.IO.Abstractions;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using Noggog;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// Requirement: the text of a localized plugin's strings tables reaches the tool as the game reads it — each string
/// as UTF-8, and as Windows-1252 when the bytes are not valid UTF-8 — whatever the language of the table.
/// Skyrim SE's own English tables carry non-ASCII text as UTF-8 (the vanilla "Daedric Plate" message title holds a
/// no-break space, C2 A0), while Mutagen decodes an English table as Windows-1252 only ("DaedricÂ Plate").
/// UTF-8 expectations come from the UTF-8 definition (U+00E9 = C3 A9, U+00A0 = C2 A0), Windows-1252 ones from the
/// code page's definition (0xE9 = U+00E9, 0x92 = U+2019).
/// </summary>
public class StringsTableEncodingPipelineTests
{
    private sealed class FixedEncodingProvider(Encoding encoding) : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => new MutagenEncodingWrapper(encoding);
    }

    /// <summary>A fake MO2 instance holding one localized plugin whose English table is written with the given encoding.</summary>
    private static (string Mo2Dir, string Root) BuildInstance(string plugin, Encoding tableEncoding, params (string EditorId, string Name)[] weapons)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tableenc_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        var modKey = ModKey.FromNameAndExtension(plugin);
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        foreach (var (editorId, name) in weapons)
        {
            var weapon = mod.Weapons.AddNew();
            weapon.EditorID = editorId;
            weapon.Name = new TranslatedString(Language.English, name);
        }
        Directory.CreateDirectory(Path.Combine(modDir, "Strings"));
        mod.WriteToBinary(Path.Combine(modDir, plugin), new BinaryWriteParameters
        {
            StringsWriter = new StringsWriter(GameRelease.SkyrimSE, modKey, new DirectoryPath(Path.Combine(modDir, "Strings")), new FixedEncodingProvider(tableEncoding), new FileSystem()),
        });

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{plugin}\r\n");
        return (mo2Dir, root);
    }

    private static List<string> WeaponNames(string mo2Dir, string root)
    {
        using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");
        return PickUpTargetRunner.Run(mo2Dir, log).Candidates.Where(c => c.RecordType == "WEAP FULL").Select(c => c.CurrentText).ToList();
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void AnEnglishTableWrittenAsUtf8_ReachesTheCandidatesIntact()
    {
        var (mo2Dir, root) = BuildInstance("SjptsTableUtf8.esp", new UTF8Encoding(false),
            ("SjptsTableUtf8Acute", "Café Sword"),
            ("SjptsTableUtf8Space", "Daedric Plate"));
        try
        {
            var names = WeaponNames(mo2Dir, root);

            Assert.Contains("Café Sword", names);
            Assert.Contains("Daedric Plate", names);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void AnEnglishTableWrittenAsWindows1252_ReachesTheCandidatesIntact()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var (mo2Dir, root) = BuildInstance("SjptsTable1252.esp", Encoding.GetEncoding(1252),
            ("SjptsTable1252Acute", "Café Sword"),
            ("SjptsTable1252Quote", "King’s Blade"));
        try
        {
            var names = WeaponNames(mo2Dir, root);

            Assert.Contains("Café Sword", names);
            Assert.Contains("King’s Blade", names);
        }
        finally { Cleanup(root); }
    }
}
