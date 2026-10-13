using System.Diagnostics;

namespace SkyrimJPStringPatcher.Tests;

/// <summary>
/// Black-box contract of the `skypatcher-generate` command, run as the real exe like the GUI will
/// run it: default input SkyPatcherText/Translation/out_temp (one translations.tsv per providing
/// MOD), default output "out" (shared with the DSD json), `--mods-file=` to restrict to the
/// selected MODs, and a clean failure (exit code 1, readable message) for a missing input.
/// </summary>
public class ProgramCliSkyPatcherGenerateTests
{
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "SJPTS_InGameText.exe");

    private const string Header = "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck\tSourcePath\tSourceFilter\tSourceLine\tInactive\tExcluded";

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
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException($"CLI process did not exit within 60s. Args: {string.Join(' ', arguments)}");
        }
        return (process.ExitCode, stdout + stderr);
    }

    private static void WriteMod(string root, string mod, string iniFile, string english, string japanese)
    {
        var dir = Path.Combine(root, "SkyPatcherText", "Translation", "out_temp", mod);
        Directory.CreateDirectory(dir);
        var path = $"armor/{iniFile}";
        File.WriteAllLines(Path.Combine(dir, "translations.tsv"),
        [
            Header,
            string.Join('\t', $"{path}|filterByArmors=X.esp|1|1", mod, "ARMO FULL", english, japanese, "SJPTS_X", "0", "", "", path, "filterByArmors=X.esp|1", "1", "", ""),
        ]);
    }

    [Fact]
    public void SkypatcherGenerate_DefaultFolders_WritesTheMirroredIni()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spg_{Guid.NewGuid():N}");
        try
        {
            WriteMod(root, "ModA", "A.ini", "Fur Coat", "毛皮のコート");

            var (exitCode, output) = RunCli(root, "skypatcher-generate");

            Assert.Equal(0, exitCode);
            var written = Path.Combine(root, "out", "SKSE", "Plugins", "SkyPatcher", "armor", "zzz_SJPTS", "A.ini");
            Assert.True(File.Exists(written), output);
            Assert.Contains("filterByArmors=X.esp|1:fullName=~毛皮のコート~", File.ReadAllText(written));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherGenerate_ModsFile_RestrictsToTheListedMods()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spg_mods_{Guid.NewGuid():N}");
        try
        {
            WriteMod(root, "ModA", "A.ini", "Name A", "名前A");
            WriteMod(root, "ModB", "B.ini", "Name B", "名前B");
            var modsFile = Path.Combine(root, "mods.txt");
            File.WriteAllText(modsFile, "ModB\r\n");

            var (exitCode, output) = RunCli(root, "skypatcher-generate", "SkyPatcherText/Translation/out_temp", "out", $"--mods-file={modsFile}");

            Assert.Equal(0, exitCode);
            var dir = Path.Combine(root, "out", "SKSE", "Plugins", "SkyPatcher", "armor", "zzz_SJPTS");
            Assert.True(File.Exists(Path.Combine(dir, "B.ini")), output);
            Assert.False(File.Exists(Path.Combine(dir, "A.ini")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherGenerate_WithNoModTranslations_SucceedsAndWritesNoIni()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spg_none_{Guid.NewGuid():N}");
        try
        {
            // What a setup without SkyPatcher leaves behind: the translations folder exists, with no MOD in it.
            Directory.CreateDirectory(Path.Combine(root, "SkyPatcherText", "Translation", "out_temp"));
            var modsFile = Path.Combine(root, "mods.txt");
            File.WriteAllText(modsFile, "");

            foreach (var arguments in new[] { new[] { "skypatcher-generate" }, ["skypatcher-generate", $"--mods-file={modsFile}"] })
            {
                var (exitCode, output) = RunCli(root, arguments);

                Assert.Equal(0, exitCode);
                Assert.Contains("Wrote 0 SkyPatcher .ini file(s)", output);
                var outDir = Path.Combine(root, "out");
                Assert.Empty(Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*.ini", SearchOption.AllDirectories) : []);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherGenerate_MissingInput_ExitsWithOneAndAReadableMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spg_missing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (exitCode, output) = RunCli(root, "skypatcher-generate");

            Assert.Equal(1, exitCode);
            Assert.Contains("not found", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherGenerate_MissingModsFile_ExitsWithOne()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spg_nomods_{Guid.NewGuid():N}");
        try
        {
            WriteMod(root, "ModA", "A.ini", "Name A", "名前A");

            var (exitCode, output) = RunCli(root, "skypatcher-generate", "SkyPatcherText/Translation/out_temp", "out", "--mods-file=does_not_exist.txt");

            Assert.Equal(1, exitCode);
            Assert.Contains("--mods-file", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
