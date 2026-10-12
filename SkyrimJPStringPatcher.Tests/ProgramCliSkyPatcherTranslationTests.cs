using System.Diagnostics;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests;

/// <summary>
/// Black-box contract of the `skypatcher-translation` command, run as the real exe like the GUI will
/// run it: defaults SkyPatcherText/PickUpTarget/out_temp (input) and SkyPatcherText/Translation/out_temp
/// (output), the plugin side's corpus.tsv found at PickUpTarget/out_temp/corpus.tsv, `--mods-file=` and
/// a positional MOD name to restrict, and a clean failure (exit code 1, readable message) when an input
/// is missing. Nothing on the plugin side is written.
/// </summary>
public class ProgramCliSkyPatcherTranslationTests
{
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "SJPTS_InGameText.exe");

    private static (int ExitCode, string Output) RunCli(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException($"CLI process did not exit within 120s. Args: {string.Join(' ', arguments)}");
        }
        return (process.ExitCode, stdout + stderr);
    }

    private static string Prepare(string root, bool withCorpus = true)
    {
        var spDir = Path.Combine(root, "SkyPatcherText", "PickUpTarget", "out_temp");
        Directory.CreateDirectory(spDir);
        CandidateIo.WriteTsv(Path.Combine(spDir, "candidates.tsv"),
        [
            new Candidate("ModA", "armor/A.ini|filterByArmors=X.esp|1|1", "ARMO FULL", "Steel Cuirass", SourcePath: "armor/A.ini", SourceFilter: "filterByArmors=X.esp|1", SourceLine: 1),
            new Candidate("ModB", "armor/B.ini|filterByArmors=X.esp|2|1", "ARMO FULL", "Steel Cuirass", SourcePath: "armor/B.ini", SourceFilter: "filterByArmors=X.esp|2", SourceLine: 1),
        ]);
        if (withCorpus)
        {
            var pluginDir = Path.Combine(root, "PickUpTarget", "out_temp");
            Directory.CreateDirectory(pluginDir);
            CorpusIo.WriteTsv(Path.Combine(pluginDir, "corpus.tsv"), [new CorpusEntry("Steel Cuirass", "鋼鉄の鎧", "Skyrim.esm", "vanilla", "ARMO FULL")]);
        }
        return spDir;
    }

    [Fact]
    public void SkypatcherTranslation_DefaultFolders_WritesOneFolderPerMod_AndLeavesThePluginSideAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_{Guid.NewGuid():N}");
        try
        {
            Prepare(root);

            var (exitCode, output) = RunCli(root, "skypatcher-translation");

            Assert.Equal(0, exitCode);
            foreach (var mod in new[] { "ModA", "ModB" })
            {
                var tsv = Path.Combine(root, "SkyPatcherText", "Translation", "out_temp", mod, "translations.tsv");
                Assert.True(File.Exists(tsv), output);
                Assert.Contains("鋼鉄の鎧", File.ReadAllText(tsv));          // resolved from the plugin side's corpus
            }
            Assert.False(Directory.Exists(Path.Combine(root, "Translation")), output);   // the plugin side's Translation/ is not created or written
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherTranslation_TellsTheProgressBarWhichModsFinished()
    {
        // The GUI's progress bar advances on the "Target: <name> (…)" line each processed unit prints.
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_progress_{Guid.NewGuid():N}");
        try
        {
            Prepare(root);

            var (exitCode, output) = RunCli(root, "skypatcher-translation");

            Assert.Equal(0, exitCode);
            var finished = output.Split('\n').Select(l => l.TrimEnd('\r'))
                .Select(l => (Ok: SkyrimJPStringPatcherGui.Services.TranslationProgressParser.TryParsePluginCompleted(l, out var name), Name: name))
                .Where(x => x.Ok).Select(x => x.Name).ToList();
            Assert.Equal(["ModA", "ModB"], finished);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The exact argument arrays the GUI builds (SkyPatcherCliArgs) are accepted by the real exe, and the
    /// three steps chain: a baseline reset, an ordinary translation of the selected MOD, then the output.</summary>
    [Fact]
    public void TheGuisOwnArguments_ResetTranslateAndGenerate_AreAcceptedAndChain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_gui_{Guid.NewGuid():N}");
        try
        {
            Prepare(root);
            var modsFile = Path.Combine(root, "mods.txt");
            File.WriteAllText(modsFile, "ModA\r\n");
            var cancelFlag = Path.Combine(root, "cancel.flag");
            var import = Path.Combine(root, "import");

            var reset = RunCli(root, SkyrimJPStringPatcherGui.Services.SkyPatcherCliArgs.Reset(null, import));
            var translate = RunCli(root, SkyrimJPStringPatcherGui.Services.SkyPatcherCliArgs.Translate(modsFile, cancelFlag, import, ["--no-namefallback"]));
            var generate = RunCli(root, SkyrimJPStringPatcherGui.Services.SkyPatcherCliArgs.Generate(modsFile, Path.Combine(root, "out")));

            Assert.True(reset.ExitCode == 0, reset.Output);
            Assert.True(translate.ExitCode == 0, translate.Output);
            Assert.True(generate.ExitCode == 0, generate.Output);
            Assert.True(File.Exists(Path.Combine(root, "out", "SKSE", "Plugins", "SkyPatcher", "armor", "zzz_SJPTS", "A.ini")), generate.Output);
            Assert.False(File.Exists(Path.Combine(root, "out", "SKSE", "Plugins", "SkyPatcher", "armor", "zzz_SJPTS", "B.ini")));   // only the selected MOD
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherTranslation_AMod_NamedOnTheCommandLine_RestrictsTheRun()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_one_{Guid.NewGuid():N}");
        try
        {
            Prepare(root);

            var (exitCode, output) = RunCli(root, "skypatcher-translation", "SkyPatcherText/PickUpTarget/out_temp", "SkyPatcherText/Translation/out_temp", "ModB");

            Assert.Equal(0, exitCode);
            var outTemp = Path.Combine(root, "SkyPatcherText", "Translation", "out_temp");
            Assert.True(File.Exists(Path.Combine(outTemp, "ModB", "translations.tsv")), output);
            Assert.False(Directory.Exists(Path.Combine(outTemp, "ModA")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherTranslation_ModsFile_RestrictsTheRun()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_mods_{Guid.NewGuid():N}");
        try
        {
            Prepare(root);
            var modsFile = Path.Combine(root, "mods.txt");
            File.WriteAllText(modsFile, "ModA\r\n");

            var (exitCode, output) = RunCli(root, "skypatcher-translation", "SkyPatcherText/PickUpTarget/out_temp", "SkyPatcherText/Translation/out_temp", $"--mods-file={modsFile}");

            Assert.Equal(0, exitCode);
            var outTemp = Path.Combine(root, "SkyPatcherText", "Translation", "out_temp");
            Assert.True(File.Exists(Path.Combine(outTemp, "ModA", "translations.tsv")), output);
            Assert.False(Directory.Exists(Path.Combine(outTemp, "ModB")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherTranslation_WithoutCandidates_ExitsWithOneAndSaysWhatToRunFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_nocand_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (exitCode, output) = RunCli(root, "skypatcher-translation");

            Assert.Equal(1, exitCode);
            Assert.Contains("skypatcher-pickup", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherTranslation_WithoutThePluginSidesCorpus_ExitsWithOneAndSaysWhatToRunFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_nocorpus_{Guid.NewGuid():N}");
        try
        {
            Prepare(root, withCorpus: false);

            var (exitCode, output) = RunCli(root, "skypatcher-translation");

            Assert.Equal(1, exitCode);
            Assert.Contains("pickuptarget", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherTranslation_ExplicitCorpusPath_IsUsed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spt_corpus_{Guid.NewGuid():N}");
        try
        {
            Prepare(root, withCorpus: false);
            var corpusPath = Path.Combine(root, "elsewhere", "my_corpus.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(corpusPath)!);
            CorpusIo.WriteTsv(corpusPath, [new CorpusEntry("Steel Cuirass", "鋼鉄の鎧", "Skyrim.esm", "vanilla", "ARMO FULL")]);

            var (exitCode, output) = RunCli(root, "skypatcher-translation", $"--corpus={corpusPath}");

            Assert.Equal(0, exitCode);
            Assert.Contains("鋼鉄の鎧", File.ReadAllText(Path.Combine(root, "SkyPatcherText", "Translation", "out_temp", "ModA", "translations.tsv")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
