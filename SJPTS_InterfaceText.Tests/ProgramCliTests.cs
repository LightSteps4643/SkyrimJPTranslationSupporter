using System.Diagnostics;
using System.Text;

namespace SJPTS_InterfaceText.Tests;

/// <summary>
/// Black-box tests: run the actual built SJPTS_InterfaceText.exe as a subprocess
/// against a synthetic MO2 instance, same style as
/// SkyrimJPStringPatcher.Tests/ProgramCliTests.cs — a contract test for the
/// detect/translate/output verbs' actual argv shapes and file I/O, not
/// exhaustive coverage. `translate`'s LLM call itself is NOT exercised here
/// (real API cost + non-determinism); its wiring (prompt building, response
/// parsing, placeholder protection) is covered by InterfaceTextPromptGeneratorTests
/// instead — this file only checks translate's error path (missing tsv).
/// </summary>
public class ProgramCliTests
{
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "SJPTS_InterfaceText.exe");
    private static readonly Encoding Utf16LeWithBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

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

    private static string SetUpSyntheticMo2Instance(string root, string modName, IReadOnlyList<(string Key, string English)> entries)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", modName, "interface", "translations");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        var englishPath = Path.Combine(modDir, $"{modName}_english.txt");
        using (var writer = new StreamWriter(englishPath, append: false, Utf16LeWithBom))
        {
            writer.NewLine = "\r\n";
            foreach (var (key, english) in entries) writer.WriteLine($"{key}\t{english}");
        }

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), $"+{modName}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "");

        return mo2Dir;
    }

    [Fact]
    public void Detect_FindsUntranslatedKeys_WritesTsv()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_detect_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = SetUpSyntheticMo2Instance(root, "TestMod",
                new List<(string, string)> { ("$TestMod_Enable", "Enable feature") });

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", "--mod=TestMod", $"--work={root}\\out_temp");

            Assert.Equal(0, exitCode);
            var tsvPath = Path.Combine(root, "out_temp", "TestMod", "interface_translations.tsv");
            Assert.True(File.Exists(tsvPath), output);
            var lines = File.ReadAllLines(tsvPath);
            Assert.Equal(2, lines.Length); // header + 1 row
            Assert.Contains("$TestMod_Enable", lines[1]);
            Assert.Equal("0", lines[1].Split('\t')[3]); // Resolved=false, nothing translated yet
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Detect_KeyExactlyMatchingModName_KeptEnglishAndMarkedResolved()
    {
        // Real-world case found against Precision/TrueHUD (2026-09-11): the
        // model transliterates the mod's own title ($Precision, $TrueHUD) into
        // katakana instead of leaving it in English. Handled deterministically
        // in code (not left to prompt instruction-following) — see Program.cs.
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_detect4_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = SetUpSyntheticMo2Instance(root, "TestMod", new List<(string, string)>
            {
                ("$TestMod", "TestMod"),               // the mod's own title — exact match
                ("$TestMod_Enable", "Enable feature"),  // an ordinary option — has TestMod as a PREFIX only
            });

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", "--mod=TestMod", $"--work={root}\\out_temp");

            Assert.Equal(0, exitCode);
            var rows = InterfaceTranslationsTsv.Read(Path.Combine(root, "out_temp", "TestMod", "interface_translations.tsv"));

            var titleRow = rows.Single(r => r.Key == "$TestMod");
            Assert.True(titleRow.Resolved);
            Assert.Equal("TestMod", titleRow.Japanese); // kept English, not sent for translation

            var optionRow = rows.Single(r => r.Key == "$TestMod_Enable");
            Assert.False(optionRow.Resolved); // prefix match only — still a normal translation target
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Detect_ExistingJapaneseWithRealJapaneseText_MarksResolved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_detect2_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = SetUpSyntheticMo2Instance(root, "TestMod",
                new List<(string, string)> { ("$K", "Hello") });

            var japanesePath = Path.Combine(mo2Dir, "mods", "TestMod", "interface", "translations", "TestMod_japanese.txt");
            using (var writer = new StreamWriter(japanesePath, append: false, Utf16LeWithBom))
                writer.WriteLine("$K\tこんにちは");

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", "--mod=TestMod", $"--work={root}\\out_temp");

            Assert.Equal(0, exitCode);
            var lines = File.ReadAllLines(Path.Combine(root, "out_temp", "TestMod", "interface_translations.tsv"));
            Assert.Equal("1", lines[1].Split('\t')[3]); // Resolved=true
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Detect_ExistingJapaneseFileButStillEnglishText_NotMarkedResolved()
    {
        // Real-world case found against Precision (2026-09-11): a shipped
        // "_japanese.txt" whose values are still literally English.
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_detect3_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = SetUpSyntheticMo2Instance(root, "TestMod",
                new List<(string, string)> { ("$K", "Hello") });

            var japanesePath = Path.Combine(mo2Dir, "mods", "TestMod", "interface", "translations", "TestMod_japanese.txt");
            using (var writer = new StreamWriter(japanesePath, append: false, Utf16LeWithBom))
                writer.WriteLine("$K\tHello"); // placeholder stub, not actually translated

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", "--mod=TestMod", $"--work={root}\\out_temp");

            Assert.Equal(0, exitCode);
            var lines = File.ReadAllLines(Path.Combine(root, "out_temp", "TestMod", "interface_translations.tsv"));
            Assert.Equal("0", lines[1].Split('\t')[3]); // still Resolved=false
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>Real-world case (2026-09-12): "Oblivion Interaction Icons DSD
    /// 1.4.3 patch" ships "Interface\Translations\aaa_english.txt" — the file
    /// base name ("aaa", which stays the CLI's `target`/output-filename
    /// identifier) doesn't match the mod's own folder name. mod_folder_name.txt
    /// records the REAL providing mod, for the GUI's display-only "MOD名" column
    /// (design/interface_translations.md's original intent) — target itself
    /// must stay file-name-based (it drives the final &lt;target&gt;_japanese.txt
    /// name the game actually loads by).</summary>
    [Fact]
    public void Detect_FileNameDiffersFromModFolderName_WritesModFolderNameFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_modfolder_{Guid.NewGuid():N}");
        try
        {
            const string modFolderName = "Oblivion Interaction Icons DSD 1.4.3 patch";
            const string target = "aaa";

            var mo2Dir = Path.Combine(root, "mo2");
            var modDir = Path.Combine(mo2Dir, "mods", modFolderName, "interface", "translations");
            var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
            Directory.CreateDirectory(modDir);
            Directory.CreateDirectory(profileDir);
            Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

            using (var writer = new StreamWriter(Path.Combine(modDir, $"{target}_english.txt"), append: false, Utf16LeWithBom))
                writer.WriteLine("$qlie_Use\tW");

            File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
                "[General]\r\n" +
                $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
                "selected_profile=@ByteArray(Default)\r\n");
            File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), $"+{modFolderName}\r\n");
            File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "");

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", $"--mod={target}", $"--work={root}\\out_temp");

            Assert.Equal(0, exitCode);
            var modFolderNamePath = Path.Combine(root, "out_temp", target, "mod_folder_name.txt");
            Assert.True(File.Exists(modFolderNamePath), output);
            Assert.Equal(modFolderName, File.ReadAllText(modFolderNamePath));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>Regression test (2026-09-12): `--mod=` used to also match via
    /// `vfs[k].Contains(target, ...)` — a substring check against the winning
    /// file's full resolved PATH, not just its own filename. Since that path
    /// includes the providing mod's folder name (e.g. "...\Oblivion Interaction
    /// Icons DSD 1.4.3 patch\interface\translations\aaa_english.txt"), passing
    /// the mod's display name as `--mod=` used to match — and because `target`
    /// (here, the display name) drives the final `&lt;target&gt;_japanese.txt`
    /// output filename, this produced an output file that didn't pair with the
    /// mod's actual "aaa_english.txt" and so was never loaded by the game.
    /// `target` must only ever match by file-name prefix (see
    /// design/interface_translations.md), so a display-name argument that
    /// isn't also the file's own base name must NOT match.</summary>
    [Fact]
    public void Detect_ModArgumentIsDisplayNameNotFileName_DoesNotMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_displayname_{Guid.NewGuid():N}");
        try
        {
            const string modFolderName = "Oblivion Interaction Icons DSD 1.4.3 patch";
            const string target = "aaa";

            var mo2Dir = Path.Combine(root, "mo2");
            var modDir = Path.Combine(mo2Dir, "mods", modFolderName, "interface", "translations");
            var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
            Directory.CreateDirectory(modDir);
            Directory.CreateDirectory(profileDir);
            Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

            using (var writer = new StreamWriter(Path.Combine(modDir, $"{target}_english.txt"), append: false, Utf16LeWithBom))
                writer.WriteLine("$qlie_Use\tW");

            File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
                "[General]\r\n" +
                $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
                "selected_profile=@ByteArray(Default)\r\n");
            File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), $"+{modFolderName}\r\n");
            File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "");

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", $"--mod={modFolderName}", $"--work={root}\\out_temp");

            Assert.Equal(0, exitCode);
            Assert.Contains($"no *_english.txt found for '{modFolderName}'", output);
            Assert.False(File.Exists(Path.Combine(root, "out_temp", modFolderName, "interface_translations.tsv")), output);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>2026-09-12: `translate` used to open its own RunLog/TraceLog
    /// PER MOD (Path.Combine(workDir, target)) — a person debugging a failed
    /// run had to check a separate small log file per mod, easy to miss.
    /// Mirrors the ESP CLI's own Program.cs now: one log opened once per
    /// invocation, at the STAGE root (workDir's own parent), covering every
    /// mod processed in that run. No LLM call is needed to exercise this —
    /// the log is opened before ForEachTargetMod runs, so even a
    /// nothing-to-do mod (already fully resolved) still proves the location.</summary>
    [Fact]
    public void Translate_OpensOneConsolidatedLogAtStageRoot_NotPerMod()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_translatelog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workDir = Path.Combine(root, "Translation", "out_temp");
            var modWorkDir = Path.Combine(workDir, "TestMod");
            InterfaceTranslationsTsv.Write(Path.Combine(modWorkDir, "interface_translations.tsv"),
                new List<InterfaceTranslationRow> { new("$K", "Hello", "こんにちは", true) }); // already resolved — no LLM call needed

            var (exitCode, output) = RunCli(root, "translate", "--mod=TestMod", $"--work={workDir}");

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "interfacetext.log")), output);
            Assert.True(File.Exists(Path.Combine(root, "Translation", "interfacetext.trace.log")), output);
            Assert.False(File.Exists(Path.Combine(modWorkDir, "interfacetext.log"))); // not per-mod anymore
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>2026-09-12: --mo2-instance= used to have a hardcoded dev-machine
    /// fallback ("D:\Modding\MO2") when omitted — mirrors the ESP CLI's own
    /// pickuptarget (Program.cs), which requires the MO2 instance dir with no
    /// default at all.</summary>
    [Fact]
    public void Detect_MissingMo2Instance_FailsCleanlyWithNonZeroExit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_nomo2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (exitCode, output) = RunCli(root, "detect", "--mod=TestMod", $"--work={root}\\out_temp");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("--mo2-instance=", output);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Detect_ImportFolderTakesPriorityOverLoadOrderJapanese()
    {
        // 2026-09-12: --import= lets the user drop a curated *_japanese.txt
        // (same $Key<TAB>Text format the tool itself reads/writes — NOT the
        // ESP CLI's xTranslator XML import) that overrides whatever the load
        // order's own bundled _japanese.txt already has, per-key.
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_import_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = SetUpSyntheticMo2Instance(root, "TestMod",
                new List<(string, string)> { ("$K", "Hello"), ("$Other", "World") });

            var loadOrderJapanesePath = Path.Combine(mo2Dir, "mods", "TestMod", "interface", "translations", "TestMod_japanese.txt");
            using (var writer = new StreamWriter(loadOrderJapanesePath, append: false, Utf16LeWithBom))
            {
                writer.WriteLine("$K\tロードオーダー訳");
                writer.WriteLine("$Other\t別の訳");
            }

            var importDir = Path.Combine(root, "import");
            Directory.CreateDirectory(importDir);
            var importPath = Path.Combine(importDir, "TestMod_japanese.txt");
            using (var writer = new StreamWriter(importPath, append: false, Utf16LeWithBom))
                writer.WriteLine("$K\tインポート優先訳"); // only $K — $Other should still fall back to the load order's own value

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", "--mod=TestMod", $"--work={root}\\out_temp", $"--import={importDir}");

            Assert.Equal(0, exitCode);
            var rows = InterfaceTranslationsTsv.Read(Path.Combine(root, "out_temp", "TestMod", "interface_translations.tsv"));

            var kRow = rows.Single(r => r.Key == "$K");
            Assert.True(kRow.Resolved);
            Assert.Equal("インポート優先訳", kRow.Japanese); // import wins over the load order's own value

            var otherRow = rows.Single(r => r.Key == "$Other");
            Assert.True(otherRow.Resolved);
            Assert.Equal("別の訳", otherRow.Japanese); // not in the import file — falls back to the load order's value
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Detect_ImportFileStillEnglishPlaceholder_NotMarkedResolved()
    {
        // Same care as the load order's own _japanese.txt
        // (Detect_ExistingJapaneseFileButStillEnglishText_NotMarkedResolved) —
        // a file merely EXISTING in the import folder doesn't mean its values
        // are actually translated; the post-merge ContainsJapanese check
        // applies uniformly regardless of which source (load order vs import)
        // a value came from.
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_import2_{Guid.NewGuid():N}");
        try
        {
            var mo2Dir = SetUpSyntheticMo2Instance(root, "TestMod",
                new List<(string, string)> { ("$K", "Hello") });

            var importDir = Path.Combine(root, "import");
            Directory.CreateDirectory(importDir);
            var importPath = Path.Combine(importDir, "TestMod_japanese.txt");
            using (var writer = new StreamWriter(importPath, append: false, Utf16LeWithBom))
                writer.WriteLine("$K\tHello"); // placeholder stub, not actually translated

            var (exitCode, output) = RunCli(root, "detect", $"--mo2-instance={mo2Dir}", "--mod=TestMod", $"--work={root}\\out_temp", $"--import={importDir}");

            Assert.Equal(0, exitCode);
            var lines = File.ReadAllLines(Path.Combine(root, "out_temp", "TestMod", "interface_translations.tsv"));
            Assert.Equal("0", lines[1].Split('\t')[3]); // still Resolved=false
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Output_MergesTsvIntoFinalJapaneseFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_output_{Guid.NewGuid():N}");
        try
        {
            var tsvDir = Path.Combine(root, "out_temp", "TestMod");
            Directory.CreateDirectory(tsvDir);
            InterfaceTranslationsTsv.Write(Path.Combine(tsvDir, "interface_translations.tsv"),
                new List<InterfaceTranslationRow> { new("$K", "Hello", "こんにちは", true) });

            var (exitCode, output) = RunCli(root, "output", "--mod=TestMod", $"--work={root}\\out_temp", $"--out={root}\\final");

            Assert.Equal(0, exitCode);
            var outPath = Path.Combine(root, "final", "Interface", "Translations", "TestMod_japanese.txt");
            Assert.True(File.Exists(outPath), output);
            var entries = InterfaceTranslationsFile.Parse(outPath);
            Assert.Equal(("$K", "こんにちは"), entries[0]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Output_UnresolvedRow_FallsBackToEnglishText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_output2_{Guid.NewGuid():N}");
        try
        {
            var tsvDir = Path.Combine(root, "out_temp", "TestMod");
            Directory.CreateDirectory(tsvDir);
            InterfaceTranslationsTsv.Write(Path.Combine(tsvDir, "interface_translations.tsv"),
                new List<InterfaceTranslationRow> { new("$K", "Hello", "", false) });

            var (exitCode, output) = RunCli(root, "output", "--mod=TestMod", $"--work={root}\\out_temp", $"--out={root}\\final");

            Assert.Equal(0, exitCode);
            Assert.Contains("[warn]", output);
            var entries = InterfaceTranslationsFile.Parse(Path.Combine(root, "final", "Interface", "Translations", "TestMod_japanese.txt"));
            Assert.Equal(("$K", "Hello"), entries[0]); // English fallback, not blank
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Translate_MissingTsv_FailsCleanlyWithNonZeroExit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_clitest_translate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (exitCode, output) = RunCli(root, "translate", "--mod=NoSuchMod", $"--work={root}\\out_temp");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("detect", output); // tells the user to run detect first
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
