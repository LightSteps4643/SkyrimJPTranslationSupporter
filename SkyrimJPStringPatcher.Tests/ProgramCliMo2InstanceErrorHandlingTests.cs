using System.Diagnostics;

namespace SkyrimJPStringPatcher.Tests;

/// <summary>
/// Black-box tests for `pickuptarget`'s handling of a bad MO2 instance folder
/// or bad path override, written BEFORE the fix per this project's usual
/// methodology (see DESIGN_NOTES.md's cross-mod-precedent section for the
/// prior instance of this pattern): define the desired behavior as tests
/// first, confirm they fail against today's code (proving the bug is real,
/// not assumed), then implement to make them pass.
///
/// Origin: a real user comment on a mod mirror site (2game.info, mod 189369)
/// reported "終了コード-532462766" ("exit code -532462766") when trying to
/// load their MO2 instance folder in the settings window. That number is
/// 0xE0434352 as unsigned 32-bit — the fixed exit code the .NET runtime uses
/// for an UNHANDLED exception (nicknamed "CLR" in its own byte layout), not
/// anything MO2-specific. Reproduced directly (see this session's own
/// investigation): `Mo2InstanceReader.Read` (called only from
/// `PickUpTargetRunner.Run`, itself called only by the CLI's `pickuptarget`
/// command — confirmed by grep, not assumed) throws a raw
/// FileNotFoundException/KeyNotFoundException/DirectoryNotFoundException for
/// several ordinary user mistakes, and `Program.cs`'s pickuptarget case logs
/// then RE-THROWS with no outer handler, so the whole process crashes with
/// this opaque code. The GUI (MainForm.cs's error dialog) then shows only
/// "終了コード {ExitCode}" — the raw number, no readable cause — which is
/// exactly what the reporting user saw.
///
/// Agreed contract this file encodes (2026-08-30 conversation): ANY problem
/// resolving the MO2 instance — the main instance folder OR any of the three
/// optional path overrides (`--mods-dir=`/`--profile-dir=`/`--overwrite-dir=`,
/// v0.57.0) — must end the process with exit code 1 (a deliberate, ordinary
/// failure — matching this file's other `return 1;` paths) and a readable
/// message identifying the problem, never a raw unhandled-exception crash.
/// This applies uniformly whether the bad path came from an explicit
/// override or from auto-derivation off the main instance folder — per the
/// user's explicit call: "空欄であっても、デフォルト値としてエラーチェック
/// すべき" (even when left blank, the auto-derived default should be
/// error-checked too) — there is deliberately NO special-casing that lets a
/// missing default `mods`/`overwrite` folder pass through silently, unlike
/// today's code (see B3/D3 below).
///
/// Scope: only `pickuptarget` touches `Mo2InstanceReader` — confirmed by
/// grepping every non-test call site of `Mo2InstanceReader.Read` in this
/// repo, there is exactly one (`PickUpTargetRunner.cs`). `translation` and
/// `generatedsdfile` never re-read the MO2 folder (they only consume
/// `PickUpTarget`'s already-written TSVs), so they are out of scope for this
/// bug and this file — this matches the two GUI call sites that actually run
/// `pickuptarget`: MainForm's "MO2再読込＆初期化" button and SettingsForm's
/// "MO2フォルダをロード" button, both of which build their argv via the
/// shared `MainForm.BuildPickupTargetArgs` helper.
/// </summary>
public class ProgramCliMo2InstanceErrorHandlingTests
{
    private const int ClrUnhandledExceptionExitCode = -532462766; // 0xE0434352 as signed int32 -- must never reappear once fixed
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "SkyrimJPStringPatcher.exe");
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

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
        var exited = process.WaitForExit(60_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException($"CLI process did not exit within 60s. Args: {string.Join(' ', arguments)}\nOutput so far:\n{stdout}{stderr}");
        }
        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>Builds a fully valid, self-contained MO2 instance under
    /// &lt;root&gt;/instance — one plugin (StaleTest.esp) inside mods/TestMod,
    /// a matching profile, and an (empty but present) overwrite folder,
    /// mirroring real MO2's own instance-creation layout (all three of
    /// mods/profiles/overwrite always exist from the moment MO2 creates an
    /// instance, even before anything is installed).</summary>
    private static string BuildValidInstance(string root)
    {
        var instanceDir = Path.Combine(root, "instance");
        var gameDir = Path.Combine(root, "game");
        var modDir = Path.Combine(instanceDir, "mods", "TestMod");
        var profileDir = Path.Combine(instanceDir, "profiles", "Default");
        var overwriteDir = Path.Combine(instanceDir, "overwrite");

        Directory.CreateDirectory(Path.Combine(gameDir, "Data"));
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(overwriteDir);

        File.Copy(Path.Combine(FixturesDir, "PickUpTarget", "StaleTest.esp"), Path.Combine(modDir, "StaleTest.esp"));
        WriteIni(instanceDir, gamePath: gameDir, selectedProfile: "Default");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*StaleTest.esp\r\n");

        return instanceDir;
    }

    private static void WriteIni(string instanceDir, string? gamePath, string? selectedProfile)
    {
        var lines = new List<string> { "[General]" };
        if (gamePath != null) lines.Add($"gamePath=@ByteArray({gamePath.Replace('\\', '/')})");
        if (selectedProfile != null) lines.Add($"selected_profile=@ByteArray({selectedProfile})");
        File.WriteAllLines(Path.Combine(instanceDir, "ModOrganizer.ini"), lines);
    }

    private static void AssertGracefulFailure((int ExitCode, string Output) result, string expectedKeyword)
    {
        Assert.Equal(1, result.ExitCode); // NOT the CLR crash code (-532462766), NOT a silent success (0)
        Assert.DoesNotContain("Unhandled exception", result.Output);
        Assert.Contains(expectedKeyword, result.Output);
    }

    // === A. MO2インスタンス本体フォルダ ===

    [Fact]
    public void A1_ValidInstance_Succeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var (exitCode, output) = RunCli(root, "pickuptarget", instanceDir);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "PickUpTarget", "out_temp", "candidates.tsv")), output);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void A2_InstanceFolderDoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var missingDir = Path.Combine(root, "does_not_exist");
            var result = RunCli(root, "pickuptarget", missingDir);
            AssertGracefulFailure(result, "does_not_exist");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void A3_ModOrganizerIniMissing_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            File.Delete(Path.Combine(instanceDir, "ModOrganizer.ini"));

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "ModOrganizer.ini");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void A4_GamePathKeyMissingFromIni_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            WriteIni(instanceDir, gamePath: null, selectedProfile: "Default"); // gamePath key absent

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "gamePath");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void A5_SelectedProfileKeyMissingFromIni_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            WriteIni(instanceDir, gamePath: Path.Combine(root, "game"), selectedProfile: null); // selected_profile key absent

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "selected_profile");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void A6_SelectedProfileFolderDoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            Directory.Delete(Path.Combine(instanceDir, "profiles", "Default"), recursive: true);

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "Default"); // the selected profile's name, so the user can tell which one
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void A7_ModlistTxtMissing_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            File.Delete(Path.Combine(instanceDir, "profiles", "Default", "modlist.txt"));

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "modlist.txt");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void A8_PluginsTxtMissing_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            File.Delete(Path.Combine(instanceDir, "profiles", "Default", "plugins.txt"));

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "plugins.txt");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>The scenario investigated live with the user (2026-08-30):
    /// today this does NOT crash, but silently drops every vanilla/DLC/CC
    /// implicit master from the load order with zero warning (confirmed by
    /// running it: "active plugins (incl. implicit masters): 1" instead of
    /// the expected several, `Translation candidates: 3` still produced from
    /// the MO2-managed mod alone). Decided: this is worse than a crash
    /// (silent corpus-quality loss, not a visible failure) and must become
    /// the same kind of clear, graceful error as the others.</summary>
    [Fact]
    public void A9_GamePathTargetDoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var nonexistentGamePath = Path.Combine(root, "no_such_game_folder");
            WriteIni(instanceDir, gamePath: nonexistentGamePath, selectedProfile: "Default");

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "no_such_game_folder");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    // === B. modsフォルダの上書き（--mods-dir=） ===

    [Fact]
    public void B1_ModsDirOverride_Valid_Succeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var redirectedMods = Path.Combine(root, "redirected_mods");
            Directory.Move(Path.Combine(instanceDir, "mods"), redirectedMods);

            var (exitCode, output) = RunCli(root, "pickuptarget", instanceDir, $"--mods-dir={redirectedMods}");

            Assert.Equal(0, exitCode);
            Assert.Contains("Iron Blade Updated", File.ReadAllText(Path.Combine(root, "PickUpTarget", "out_temp", "candidates.tsv")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void B2_ModsDirOverride_PathDoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root); // default mods/ is still valid here -- proves the override itself is what's checked
            var missingOverride = Path.Combine(root, "no_such_mods_dir");

            var result = RunCli(root, "pickuptarget", instanceDir, $"--mods-dir={missingOverride}");
            AssertGracefulFailure(result, "no_such_mods_dir");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>No --mods-dir given at all (the ordinary/default case) — but
    /// the auto-derived default `&lt;instanceDir&gt;/mods` itself doesn't
    /// exist. Per the agreed contract this must be checked exactly like an
    /// explicit override, not silently tolerated.</summary>
    [Fact]
    public void B3_ModsDirDefault_DoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            Directory.Delete(Path.Combine(instanceDir, "mods"), recursive: true);

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "mods");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    // === C. 選択中プロファイルフォルダの上書き（--profile-dir=） ===

    [Fact]
    public void C1_ProfileDirOverride_Valid_Succeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var redirectedProfile = Path.Combine(root, "redirected_profile");
            Directory.Move(Path.Combine(instanceDir, "profiles", "Default"), redirectedProfile);

            var (exitCode, output) = RunCli(root, "pickuptarget", instanceDir, $"--profile-dir={redirectedProfile}");

            Assert.Equal(0, exitCode);
            Assert.Contains("Iron Blade Updated", File.ReadAllText(Path.Combine(root, "PickUpTarget", "out_temp", "candidates.tsv")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void C2_ProfileDirOverride_PathDoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var missingOverride = Path.Combine(root, "no_such_profile_dir");

            var result = RunCli(root, "pickuptarget", instanceDir, $"--profile-dir={missingOverride}");
            AssertGracefulFailure(result, "no_such_profile_dir");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void C3_ProfileDirOverride_ModlistTxtMissing_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var redirectedProfile = Path.Combine(root, "redirected_profile");
            Directory.Move(Path.Combine(instanceDir, "profiles", "Default"), redirectedProfile);
            File.Delete(Path.Combine(redirectedProfile, "modlist.txt"));

            var result = RunCli(root, "pickuptarget", instanceDir, $"--profile-dir={redirectedProfile}");
            AssertGracefulFailure(result, "modlist.txt");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void C4_ProfileDirOverride_PluginsTxtMissing_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var redirectedProfile = Path.Combine(root, "redirected_profile");
            Directory.Move(Path.Combine(instanceDir, "profiles", "Default"), redirectedProfile);
            File.Delete(Path.Combine(redirectedProfile, "plugins.txt"));

            var result = RunCli(root, "pickuptarget", instanceDir, $"--profile-dir={redirectedProfile}");
            AssertGracefulFailure(result, "plugins.txt");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    // === D. overwriteフォルダの上書き（--overwrite-dir=） ===

    [Fact]
    public void D1_OverwriteDirOverride_Valid_Succeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var redirectedOverwrite = Path.Combine(root, "redirected_overwrite");
            Directory.Move(Path.Combine(instanceDir, "overwrite"), redirectedOverwrite);

            var (exitCode, output) = RunCli(root, "pickuptarget", instanceDir, $"--overwrite-dir={redirectedOverwrite}");

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "PickUpTarget", "out_temp", "candidates.tsv")), output);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void D2_OverwriteDirOverride_PathDoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            var missingOverride = Path.Combine(root, "no_such_overwrite_dir");

            var result = RunCli(root, "pickuptarget", instanceDir, $"--overwrite-dir={missingOverride}");
            AssertGracefulFailure(result, "no_such_overwrite_dir");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>No --overwrite-dir given (default case), but the auto-derived
    /// default &lt;instanceDir&gt;/overwrite itself doesn't exist. Same
    /// "check the default too" contract as B3.</summary>
    [Fact]
    public void D3_OverwriteDirDefault_DoesNotExist_FailsGracefully()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_mo2err_{Guid.NewGuid():N}");
        try
        {
            var instanceDir = BuildValidInstance(root);
            Directory.Delete(Path.Combine(instanceDir, "overwrite"), recursive: true);

            var result = RunCli(root, "pickuptarget", instanceDir);
            AssertGracefulFailure(result, "overwrite");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }
}
