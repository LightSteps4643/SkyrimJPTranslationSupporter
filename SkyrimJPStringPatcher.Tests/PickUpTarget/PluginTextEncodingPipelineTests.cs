using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// Requirement: the text a plugin carries reaches the tool exactly as the game and the Creation Kit read it.
/// The bytes of a plugin's own (non-localized) text carry no encoding label. Skyrim reads each string as
/// UTF-8 and, when the bytes are not valid UTF-8, as Windows-1252 (the Creation Kit writes Windows-1252;
/// UESP "String Table File Format", Mutagen issue #318). The tool asks Mutagen for the Japanese strings
/// tables, and Mutagen then decodes every other language's embedded text as UTF-8 only — so a Windows-1252
/// "’" (0x92), "é" (0xE9), "“" (0x93) … arrived as U+FFFD, no longer matched the xTranslator XML's
/// "King’s Blade", and could not be repaired downstream.
/// Windows-1252 expectations come from the code page's definition (0x92 = U+2019, 0xE9 = U+00E9, …), UTF-8 ones
/// from the UTF-8 definition (U+2019 = E2 80 99); neither is read back from the tool.
/// </summary>
public class PluginTextEncodingPipelineTests
{
    private sealed record Weapon(string EditorId, string Name, string? Marker = null, byte[]? ReplaceMarkerWith = null);

    /// <summary>Builds a one-plugin fake MO2 instance. A weapon with a marker gets its marker bytes replaced in the
    /// written file (same length), which is how a plugin whose text bytes are UTF-8 is made — Mutagen itself
    /// writes non-localized text as Windows-1252.</summary>
    private static (string Mo2Dir, string Root) BuildInstance(string plugin, params Weapon[] weapons)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_textenc_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        var mod = new SkyrimMod(ModKey.FromNameAndExtension(plugin), SkyrimRelease.SkyrimSE);
        foreach (var weapon in weapons)
        {
            var record = mod.Weapons.AddNew();
            record.EditorID = weapon.EditorId;
            record.Name = weapon.Name;
        }
        var path = Path.Combine(modDir, plugin);
        mod.WriteToBinary(path);

        var bytes = File.ReadAllBytes(path);
        foreach (var weapon in weapons.Where(w => w.Marker != null))
        {
            var marker = Encoding.ASCII.GetBytes(weapon.Marker!);
            Assert.Equal(marker.Length, weapon.ReplaceMarkerWith!.Length);
            var at = bytes.AsSpan().IndexOf(marker);
            Assert.True(at >= 0, $"marker '{weapon.Marker}' not found in the written plugin");
            weapon.ReplaceMarkerWith.CopyTo(bytes, at);
        }
        File.WriteAllBytes(path, bytes);

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{plugin}\r\n");
        return (mo2Dir, root);
    }

    private static PickUpTargetResult Run(string mo2Dir, string root)
    {
        using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");
        return PickUpTargetRunner.Run(mo2Dir, log);
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData("King’s Blade")]                       // 0x92
    [InlineData("Café Sword")]                         // 0xE9
    [InlineData("“Iron” Sword – Mk2")]       // 0x93 0x94 0x96
    [InlineData("Dagon’s — Edge…")]          // 0x92 0x97 0x85
    public void Windows1252TextInAPlugin_ReachesTheCandidateIntact(string name)
    {
        var (mo2Dir, root) = BuildInstance("SjptsText1252.esp", new Weapon("SjptsText1252Weap", name));
        try
        {
            var candidate = Assert.Single(Run(mo2Dir, root).Candidates, c => c.RecordType == "WEAP FULL");

            Assert.Equal(name, candidate.CurrentText);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Utf8TextInAPlugin_ReachesTheCandidateIntact()
    {
        var (mo2Dir, root) = BuildInstance("SjptsTextUtf8.esp",
            new Weapon("SjptsTextUtf8Quote", "King@@@s Blade", "@@@", [0xE2, 0x80, 0x99]),   // U+2019 in UTF-8
            new Weapon("SjptsTextUtf8Acute", "Caf@@ Sword", "@@", [0xC3, 0xA9]));            // U+00E9 in UTF-8
        try
        {
            var texts = Run(mo2Dir, root).Candidates.Where(c => c.RecordType == "WEAP FULL").Select(c => c.CurrentText).ToList();

            Assert.Contains("King’s Blade", texts);
            Assert.Contains("Café Sword", texts);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void APluginMixingBothEncodings_IsReadStringByString()
    {
        var (mo2Dir, root) = BuildInstance("SjptsTextMixed.esp",
            new Weapon("SjptsTextMixedLegacy", "Elder’s Staff"),                              // Windows-1252, as the Creation Kit writes it
            new Weapon("SjptsTextMixedUtf8", "Ranger@@@s Bow", "@@@", [0xE2, 0x80, 0x99]));       // UTF-8, as other editors write it
        try
        {
            var texts = Run(mo2Dir, root).Candidates.Where(c => c.RecordType == "WEAP FULL").Select(c => c.CurrentText).ToList();

            Assert.Contains("Elder’s Staff", texts);
            Assert.Contains("Ranger’s Bow", texts);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ACandidateWithATypographicApostrophe_MatchesTheXTranslatorXmlSource()
    {
        const string plugin = "SjptsTextXml.esp";
        const string name = "King’s Blade";
        var (mo2Dir, root) = BuildInstance(plugin, new Weapon("SjptsTextXmlWeap", name));
        try
        {
            var result = Run(mo2Dir, root);
            var importDir = Path.Combine(root, "import");
            Directory.CreateDirectory(importDir);
            File.WriteAllText(Path.Combine(importDir, "SjptsTextXml_english_japanese.xml"),
                "<SSTXMLRessources><Params><Addon>" + plugin + "</Addon></Params><Content>" +
                "<String><REC>WEAP:FULL</REC><Source>" + name + "</Source><Dest>王の剣</Dest></String>" +
                "</Content></SSTXMLRessources>");
            using var log = RunLog.Open(Path.Combine(root, "Translation"), "Translation");

            var imported = XTranslatorImporter.Load(importDir, result.Candidates.ToList(), log);

            var entry = Assert.Single(imported);
            Assert.Equal((name, "王の剣"), (entry.English, entry.Japanese));
        }
        finally { Cleanup(root); }
    }
}
