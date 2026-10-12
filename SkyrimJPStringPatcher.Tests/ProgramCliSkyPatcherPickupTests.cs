using System.Diagnostics;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests;

/// <summary>
/// Black-box contract of the `skypatcher-pickup` command, run as the real exe like the GUI will
/// run it: the same arguments as `pickuptarget` (MO2 instance folder, optional output folder and
/// the three path overrides), output under SkyPatcherText/PickUpTarget/out_temp by default, and
/// the same clean failure (exit code 1 with a readable message, never an unhandled-exception
/// crash) for a bad MO2 instance folder.
/// </summary>
public class ProgramCliSkyPatcherPickupTests
{
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "SJPTS_InGameText.exe");
    private const int ClrUnhandledExceptionExitCode = -532462766;

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

    private static string BuildInstance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        var skyPatcherDir = Path.Combine(mo2Dir, "mods", "Sentinel", "SKSE", "Plugins", "SkyPatcher", "armor");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(skyPatcherDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+Sentinel\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "");
        File.WriteAllLines(Path.Combine(skyPatcherDir, "Sentinel.ini"),
        [
            "filterByArmors=ArmorBanditCuirass:fullName=~Fur Coat~",
            "filterByArmors=ArmorBanditHelmet:fullName=~毛皮の帽子~",
        ]);
        return mo2Dir;
    }

    [Fact]
    public void SkypatcherPickup_WritesCandidatesUnderSkyPatcherText_AndListsWhatItLeftOut()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spp_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = BuildInstance(root);

            var (exitCode, output) = RunCli(root, "skypatcher-pickup", mo2Dir);

            Assert.Equal(0, exitCode);
            var outDir = Path.Combine(root, "SkyPatcherText", "PickUpTarget", "out_temp");
            var rows = CandidateIo.ReadTsv(Path.Combine(outDir, "candidates.tsv"));
            Assert.Equal(2, rows.Count);
            var coat = rows.Single(r => r.CurrentText == "Fur Coat");
            Assert.Equal("Sentinel", coat.WinningPlugin);
            Assert.Equal("ARMO FULL", coat.RecordType);
            Assert.Equal("armor/Sentinel.ini", coat.SourcePath);
            Assert.Equal("", coat.Excluded);
            Assert.Equal("AlreadyJapanese", rows.Single(r => r.CurrentText == "毛皮の帽子").Excluded);

            // The excluded row is also listed in plain text, with its reason, for a person to read.
            var excludedList = File.ReadAllText(Path.Combine(outDir, "excluded_candidates.txt"));
            Assert.Contains("AlreadyJapanese", excludedList);
            Assert.Contains("毛皮の帽子", excludedList);
            Assert.DoesNotContain("Fur Coat", excludedList);

            // Plugin-side output is untouched by this command.
            Assert.False(Directory.Exists(Path.Combine(root, "PickUpTarget")), output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherPickup_ExplicitOutputFolder_IsHonoured()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spp_out_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = BuildInstance(root);
            var custom = Path.Combine(root, "custom_out");

            var (exitCode, _) = RunCli(root, "skypatcher-pickup", mo2Dir, custom);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(custom, "candidates.tsv")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherPickup_ABadInstanceFolder_ExitsWithOneAndAReadableMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spp_bad_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (exitCode, output) = RunCli(root, "skypatcher-pickup", Path.Combine(root, "not_an_instance"));

            Assert.Equal(1, exitCode);
            Assert.NotEqual(ClrUnhandledExceptionExitCode, exitCode);
            Assert.Contains("ModOrganizer.ini", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SkypatcherPickup_WithoutAnInstanceFolder_PrintsUsageAndExitsWithOne()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_spp_usage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (exitCode, output) = RunCli(root, "skypatcher-pickup");

            Assert.Equal(1, exitCode);
            Assert.Contains("skypatcher-pickup", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
