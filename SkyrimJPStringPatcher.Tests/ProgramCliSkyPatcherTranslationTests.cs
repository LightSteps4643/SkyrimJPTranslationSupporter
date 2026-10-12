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
