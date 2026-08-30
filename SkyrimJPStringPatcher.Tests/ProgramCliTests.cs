using System.Diagnostics;

namespace SkyrimJPStringPatcher.Tests;

/// <summary>
/// Program.cs (the CLI entry point, top-level statements — not directly
/// unit-testable) is the GUI's ONLY touch point with the actual pipeline:
/// SkyrimJPStringPatcherGui/Services/CliRunner.cs spawns this exact built exe
/// as a subprocess for every single GUI action. A flag-parsing bug here breaks
/// every GUI button, not just direct CLI use — but an obvious crash would
/// surface immediately in normal GUI use, so per the user's own framing
/// (2026-08-28), this doesn't chase coverage: it runs the exe as a real
/// subprocess against EXACTLY the argv shapes SkyrimJPStringPatcherGui/MainForm.cs
/// actually constructs (grep-confirmed against MainForm.cs), verifying each
/// one exits cleanly and produces the expected output — a contract test for
/// the GUI's own call sites, not exhaustive flag-combination coverage.
///
/// SkyrimJPStringPatcher.Tests references the main console project, so its
/// own build output already contains a ready-to-run SkyrimJPStringPatcher.exe
/// (confirmed: same folder as AppContext.BaseDirectory) — no separate build
/// step or path gymnastics needed.
/// </summary>
public class ProgramCliTests
{
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "SkyrimJPStringPatcher.exe");
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static (int ExitCode, string Stdout) RunCli(string workingDirectory, params string[] arguments)
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
            throw new TimeoutException($"CLI process did not exit within 60s. Args: {string.Join(' ', arguments)}\nStdout so far:\n{stdout}\nStderr so far:\n{stderr}");
        }
        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>MainForm.cs: `RunCliAsync(new[] { "pickuptarget", Mo2Dir })` —
    /// the GUI's initial-scan button.</summary>
    [Fact]
    public void Pickuptarget_ExactGuiArgv_ProducesExpectedOutputFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_pickuptarget_{Guid.NewGuid():N}");
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));
        try
        {
            File.Copy(Path.Combine(FixturesDir, "PickUpTarget", "StaleTest.esp"), Path.Combine(modDir, "StaleTest.esp"));
            File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
                "[General]\r\n" +
                $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
                "selected_profile=@ByteArray(Default)\r\n");
            File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
            File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*StaleTest.esp\r\n");

            var (exitCode, output) = RunCli(root, "pickuptarget", mo2Dir);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "PickUpTarget", "out_temp", "candidates.tsv")), output);
            Assert.True(File.Exists(Path.Combine(root, "PickUpTarget", "out_temp", "corpus.tsv")), output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>MainForm.cs: `RunCliAsync(new[] { "translation", "PickUpTarget/out_temp",
    /// "Translation/out_temp", "--all", "--no-meaning", "--no-translit", "--no-namefallback" })`
    /// — the GUI's post-scan "①のみで一括生成" step.</summary>
    [Fact]
    public void TranslationAll_ExactGuiArgv_ProducesPerPluginFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_transall_{Guid.NewGuid():N}");
        var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
        Directory.CreateDirectory(pickUpTargetOutDir);
        try
        {
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "candidates.tsv"), Path.Combine(pickUpTargetOutDir, "candidates.tsv"));
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "corpus.tsv"), Path.Combine(pickUpTargetOutDir, "corpus.tsv"));

            var (exitCode, output) = RunCli(root, "translation", "PickUpTarget/out_temp", "Translation/out_temp", "--all", "--no-meaning", "--no-translit", "--no-namefallback");

            Assert.Equal(0, exitCode);
            Assert.Contains("Stage overrides: meaning=OFF, transliteration=OFF, NameFallbackTranslator=OFF", output);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "out_temp", "SjptsTestMod", "translations.tsv")), output);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "out_temp", "translation_index.txt")), output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>MainForm.cs: `RunCliAsync(new[] { "translation", "PickUpTarget/out_temp",
    /// "Translation/out_temp", plugin, "--no-meaning", "--no-translit", "--no-namefallback",
    /// "--discard-user-edits" })` — the grid's per-row "この行をリセット" action.</summary>
    [Fact]
    public void TranslationSinglePluginDiscardUserEdits_ExactGuiArgv_ResetsThatPluginOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_transone_{Guid.NewGuid():N}");
        var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
        Directory.CreateDirectory(pickUpTargetOutDir);
        try
        {
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "candidates.tsv"), Path.Combine(pickUpTargetOutDir, "candidates.tsv"));
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "corpus.tsv"), Path.Combine(pickUpTargetOutDir, "corpus.tsv"));

            var (exitCode, output) = RunCli(root, "translation", "PickUpTarget/out_temp", "Translation/out_temp", "SjptsTestMod.esp",
                "--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits");

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "out_temp", "SjptsTestMod", "translations.tsv")), output);
            // Positional single-plugin mode is NOT the --all path -- the
            // load-order-wide summary files are a --all-only contract.
            Assert.False(File.Exists(Path.Combine(root, "Translation", "out_temp", "translation_index.txt")), output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>MainForm.cs: `RunCliAsync(new List&lt;string&gt; { "translation",
    /// "PickUpTarget/out_temp", "Translation/out_temp", $"--plugins-file={pluginsFilePath}",
    /// "--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits" })` —
    /// the grid's multi-row "選択した行をリセット" action.</summary>
    [Fact]
    public void TranslationPluginsFileDiscardUserEdits_ExactGuiArgv_ResetsListedPluginsOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_transmany_{Guid.NewGuid():N}");
        var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
        Directory.CreateDirectory(pickUpTargetOutDir);
        try
        {
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "candidates.tsv"), Path.Combine(pickUpTargetOutDir, "candidates.tsv"));
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "corpus.tsv"), Path.Combine(pickUpTargetOutDir, "corpus.tsv"));
            var pluginsFilePath = Path.Combine(root, "selected_plugins.txt");
            File.WriteAllText(pluginsFilePath, "SjptsMultiPluginA.esp\nSjptsMultiPluginB.esp\n");

            var (exitCode, output) = RunCli(root, "translation", "PickUpTarget/out_temp", "Translation/out_temp", $"--plugins-file={pluginsFilePath}",
                "--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits");

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "out_temp", "SjptsMultiPluginA", "translations.tsv")), output);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "out_temp", "SjptsMultiPluginB", "translations.tsv")), output);
            // Plugins NOT listed in the file must never be touched.
            Assert.False(Directory.Exists(Path.Combine(root, "Translation", "out_temp", "SjptsTestMod")), output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>MainForm.cs's main "翻訳実行" button: `{"translation", ..., "--plugins-file=...",
    /// "--cancel-flag-path=..."}` plus BuildOptionFlags()'s ⑤ローカルLLM combination
    /// (`--llm-local`, `--llm-local-model=`, `--llm-local-endpoint=`) — verifies this
    /// exact flag combination parses and runs to completion without crashing even
    /// when the configured local LLM endpoint is unreachable (an immediately-refusing
    /// port, not a hanging one, so this stays fast), matching the documented "server
    /// unavailable -> candidate just stays unresolved" contract.</summary>
    [Fact]
    public void TranslationPluginsFileWithCancelFlagAndLocalLlm_ExactGuiArgv_RunsToCompletionWithoutCrashing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_translive_{Guid.NewGuid():N}");
        var pickUpTargetOutDir = Path.Combine(root, "PickUpTarget", "out_temp");
        Directory.CreateDirectory(pickUpTargetOutDir);
        try
        {
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "candidates.tsv"), Path.Combine(pickUpTargetOutDir, "candidates.tsv"));
            File.Copy(Path.Combine(FixturesDir, "Translation", "PromptGenerator", "corpus.tsv"), Path.Combine(pickUpTargetOutDir, "corpus.tsv"));
            var pluginsFilePath = Path.Combine(root, "selected_plugins.txt");
            File.WriteAllText(pluginsFilePath, "SjptsMultiPluginB.esp\n");
            var cancelFlagPath = Path.Combine(root, "cancel.flag"); // deliberately never created -- must run to completion

            var (exitCode, output) = RunCli(root, "translation", "PickUpTarget/out_temp", "Translation/out_temp",
                $"--plugins-file={pluginsFilePath}", $"--cancel-flag-path={cancelFlagPath}",
                "--llm-local", "--llm-local-model=sjpts-test-model", "--llm-local-endpoint=http://127.0.0.1:1/v1/chat/completions");

            Assert.Equal(0, exitCode);
            Assert.Contains("Step 5 (local LLM): ENABLED", output);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "out_temp", "SjptsMultiPluginB", "translations.tsv")), output);
            // Only the ONE plugin named in --plugins-file must be touched -- if
            // --plugins-file were silently ignored, this would fall through to
            // RunAll and process every plugin in candidates.tsv instead, which
            // would ALSO leave SjptsMultiPluginB's own folder populated (so
            // checking only for its existence, as above, isn't enough on its own
            // to catch that regression).
            Assert.False(Directory.Exists(Path.Combine(root, "Translation", "out_temp", "SjptsTestMod")), output);
            Assert.False(File.Exists(Path.Combine(root, "Translation", "out_temp", "translation_index.txt")), output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>MainForm.cs: `RunCliAsync(new[] { "generatedsdfile" })` — the GUI's
    /// final "DSD出力" button, using every documented default path.</summary>
    [Fact]
    public void Generatedsdfile_ExactGuiArgv_ProducesDsdJsonOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_clitest_gendsd_{Guid.NewGuid():N}");
        var translationOutDir = Path.Combine(root, "Translation", "out_temp");
        Directory.CreateDirectory(Path.GetDirectoryName(translationOutDir)!);
        try
        {
            CopyDirectory(Path.Combine(FixturesDir, "GenerateDsdFile", "directory_input"), translationOutDir);

            var (exitCode, output) = RunCli(root, "generatedsdfile");

            Assert.Equal(0, exitCode);
            var dsdRoot = Path.Combine(root, "out", "SKSE", "Plugins", "DynamicStringDistributor");
            Assert.True(Directory.Exists(dsdRoot), output);

            // v0.57.4: the real CLI path (no explicit timestamp override, unlike
            // the DsdWriter/DsdJsonGenerator unit tests) must actually produce a
            // timestamped name, not the old fixed "SkyrimJPStringPatcher.json" --
            // that fixed name is exactly what let two separate incremental runs'
            // output collide and silently erase each other once both were
            // installed into the same MO2 plugin folder (see DsdWriter.cs's own
            // remarks, and DsdWriterTests' dedicated coexistence test).
            var writtenFiles = Directory.GetFiles(dsdRoot, "*.json", SearchOption.AllDirectories);
            Assert.NotEmpty(writtenFiles);
            foreach (var file in writtenFiles)
            {
                var name = Path.GetFileName(file);
                Assert.Matches(@"^SkyrimJPStringPatcher_\d{14}\.json$", name);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
