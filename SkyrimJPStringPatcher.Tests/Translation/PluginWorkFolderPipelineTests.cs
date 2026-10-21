using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.GenerateDsdFile;
using SkyrimJPStringPatcher.PickUpTarget;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// Requirement, end to end (pick up → translate → DSD output): every plugin keeps its own working folder, named
/// after its file name. Two reports of v0.63.0 came from the folder name dropping the extension:
///  - "Map Zoom Fixer PRO .esp" (a space before the extension) left a folder name Windows silently shortens, and
///    the first write below it threw DirectoryNotFoundException;
///  - a load order holding both "DynDOLOD.esm" and "DynDOLOD.esp" sent both to "DynDOLOD": the plugin processed
///    last overwrote the other's translations.tsv, so its translations never reached the DSD output.
/// </summary>
public class PluginWorkFolderPipelineTests
{
    private static readonly (string Plugin, string EditorId, string Name, string Japanese)[] Plugins =
    [
        ("Collide.esm", "SjptsCollideEsm", "Steel Blade", "鋼の刃"),
        ("Collide.esp", "SjptsCollideEsp", "Iron Blade", "鉄の刃"),
        ("Gap .esp", "SjptsGap", "Gilded Blade", "金の刃"),
    ];

    [Fact]
    public void EveryPlugin_GetsItsOwnFolder_AndItsTranslationsReachTheDsdOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_wfpipe_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));
        try
        {
            foreach (var (plugin, editorId, name, _) in Plugins)
            {
                var mod = new SkyrimMod(ModKey.FromNameAndExtension(plugin), SkyrimRelease.SkyrimSE);
                var weapon = mod.Weapons.AddNew();
                weapon.EditorID = editorId;
                weapon.Name = name;
                mod.WriteToBinary(Path.Combine(modDir, plugin));
            }
            File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
                "[General]\r\n" + $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" + "selected_profile=@ByteArray(Default)\r\n");
            File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
            File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Concat(Plugins.Select(p => $"*{p.Plugin}\r\n")));

            // Pick up.
            var pickUpDir = Path.Combine(root, "PickUpTarget", "out_temp");
            Directory.CreateDirectory(pickUpDir);
            PickUpTargetResult picked;
            using (var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget"))
                picked = PickUpTargetRunner.Run(mo2Dir, log);
            var candidatesPath = Path.Combine(pickUpDir, "candidates.tsv");
            var corpusPath = Path.Combine(pickUpDir, "corpus.tsv");
            CandidateIo.WriteTsv(candidatesPath, picked.Candidates);
            CorpusIo.WriteTsv(corpusPath, picked.Corpus);

            // The translations come from xTranslator files (one per plugin), so every row is resolved.
            var importDir = Path.Combine(root, "Translation", "import");
            Directory.CreateDirectory(importDir);
            foreach (var (plugin, _, name, japanese) in Plugins)
                File.WriteAllText(Path.Combine(importDir, plugin + ".xml"),
                    $"<SSTXMLRessources><Params><Addon>{plugin}</Addon></Params><Content>" +
                    $"<String><REC>WEAP:FULL</REC><Source>{name}</Source><Dest>{japanese}</Dest></String></Content></SSTXMLRessources>");

            // Translate (the pre-v0.63.1 "folder = name without extension" threw here for "Gap .esp").
            var translationDir = Path.Combine(root, "Translation", "out_temp");
            using (var log = RunLog.Open(Path.Combine(root, "Translation"), "Translation"))
                PromptGenerator.RunAll(candidatesPath, corpusPath, importDir, translationDir, log);

            foreach (var (plugin, _, name, japanese) in Plugins)
            {
                var tsv = Path.Combine(translationDir, plugin, "translations.tsv");
                Assert.True(File.Exists(tsv), $"no translations.tsv in the folder named after {plugin}");
                var text = File.ReadAllText(tsv);
                Assert.Contains(name, text);
                Assert.Contains(japanese, text);
            }

            // DSD output: one folder per plugin file name, each with its own translation.
            var outDir = Path.Combine(root, "out");
            using (var log = RunLog.Open(Path.Combine(root, "GenerateDsdFile"), "GenerateDsdFile"))
                DsdJsonGenerator.Run(translationDir, outDir, log);
            foreach (var (plugin, _, _, japanese) in Plugins)
            {
                var pluginOut = Path.Combine(outDir, "SKSE", "Plugins", "DynamicStringDistributor", plugin);
                var json = Assert.Single(Directory.GetFiles(pluginOut, "*.json"));
                Assert.Contains(japanese, File.ReadAllText(json));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
