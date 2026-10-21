using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.GenerateDsdFile;

namespace SkyrimJPStringPatcher.Tests.GenerateDsdFile;

/// <summary>
/// Requirement: when the DSD output is generated from a folder of working folders (Translation/out_temp), a
/// translations.tsv counts only if its folder is named after the plugin its rows belong to — the folder name equals
/// every row's WinningPlugin (case-insensitively). A folder left over from an earlier version (named without the
/// extension: "DynDOLOD"), or a copy or backup of one ("Foo.esp - コピー", "Foo.esp.bak"), is not a plugin's
/// working folder, and reading it next to the real one puts the same entries into a file twice, an old
/// translation against a new one. What a person names explicitly (one file, or the folder itself) is read as given.
/// </summary>
public class DsdFolderNameRuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdfolder_{Guid.NewGuid():N}");
    private string InDir => Path.Combine(_root, "out_temp");
    private string OutDir => Path.Combine(_root, "out");
    private string DsdDir => Path.Combine(OutDir, "SKSE", "Plugins", "DynamicStringDistributor");
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteFolder(string folder, string winningPlugin, string english, string japanese, string formId = "000800")
    {
        var dir = Path.Combine(InDir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "translations.tsv");
        File.WriteAllLines(path,
        [
            "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId",
            $"{formId}:{winningPlugin}\t{winningPlugin}\tWEAP FULL\t{english}\t{japanese}\tSJPTS_AutoCorpus\t0\t",
        ]);
        return path;
    }

    private string Generate(string input, out string logText)
    {
        var logDir = Path.Combine(_root, "GenerateDsdFile");
        using (var log = RunLog.Open(logDir, "GenerateDsdFile"))
            DsdJsonGenerator.Run(input, OutDir, log, outputTimestamp: Stamp);
        logText = string.Concat(Directory.GetFiles(logDir, "*.log").Select(File.ReadAllText));
        return OutDir;
    }

    private bool HasOutputFor(string plugin) =>
        Directory.Exists(Path.Combine(DsdDir, plugin)) && Directory.GetFiles(Path.Combine(DsdDir, plugin), "*.json").Length > 0;

    [Fact]
    public void FoldersNamedAfterTheirPlugins_AreAllRead_EvenWhenTheyShareABaseName()
    {
        WriteFolder("Collide.esm", "Collide.esm", "Steel Blade", "鋼の刃");
        WriteFolder("Collide.esp", "Collide.esp", "Iron Blade", "鉄の刃");
        WriteFolder("Map Zoom Fixer PRO .esp", "Map Zoom Fixer PRO .esp", "Gilded Blade", "金の刃");

        Generate(InDir, out _);

        Assert.True(HasOutputFor("Collide.esm"));
        Assert.True(HasOutputFor("Collide.esp"));
        Assert.True(HasOutputFor("Map Zoom Fixer PRO .esp"));
    }

    [Fact]
    public void TheFolderNameIsComparedWithoutRegardToCase()
    {
        WriteFolder("COLLIDE.ESP", "Collide.esp", "Iron Blade", "鉄の刃");

        Generate(InDir, out _);

        Assert.True(HasOutputFor("Collide.esp"));
    }

    [Theory]
    [InlineData("Collide")]                      // the folder name an earlier version gave it (extension dropped)
    [InlineData("Collide.esp - コピー")]          // a copy
    [InlineData("Collide.esp.bak")]              // a backup
    public void AFolderNotNamedAfterItsPlugin_IsIgnored_AndTheLogSaysSo(string folder)
    {
        WriteFolder("Collide.esp", "Collide.esp", "Iron Blade", "鉄の刃", "000800");
        WriteFolder(folder, "Collide.esp", "Iron Blade", "古い訳", "000800");   // the same entry, an older translation

        Generate(InDir, out var logText);

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(DsdDir, "Collide.esp"), "*.json")));
        Assert.Contains("鉄の刃", json);
        Assert.DoesNotContain("古い訳", json);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"form_id\"").Count);   // once, not twice
        Assert.Contains(folder, logText);
    }

    [Fact]
    public void TheConsoleNamesAFewIgnoredFolders_TheLogNamesAll()
    {
        WriteFolder("Collide.esp", "Collide.esp", "Iron Blade", "鉄の刃");
        for (var i = 1; i <= 8; i++) WriteFolder($"Old{i}", "Collide.esp", $"Blade {i}", $"刃{i}", $"00080{i}");
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        string logText;
        try { Generate(InDir, out logText); }
        finally { Console.SetError(originalError); }

        Assert.Contains("Ignored 8 working folder(s)", captured.ToString());
        Assert.Contains("and 3 more", captured.ToString());          // five are named, the other three are counted
        foreach (var i in Enumerable.Range(1, 8)) Assert.Contains($"Old{i}", logText);
    }

    [Fact]
    public void WhatIsNamedExplicitly_IsReadAsGiven_NoMatterTheFolderName()
    {
        var single = WriteFolder("Collide", "Collide.esp", "Iron Blade", "鉄の刃");

        Generate(single, out _);                                  // one file
        Assert.True(HasOutputFor("Collide.esp"));

        Directory.Delete(OutDir, recursive: true);
        Generate(Path.GetDirectoryName(single)!, out _);          // the plugin's own folder
        Assert.True(HasOutputFor("Collide.esp"));
    }
}
