namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// The command lines the GUI builds for the SkyPatcher steps (the CLI's skypatcher-pickup /
/// skypatcher-translation / skypatcher-generate). The three stages work in their own SkyPatcherText/
/// tree, so resetting or regenerating one side never touches the other's files; a run restricted to
/// selected MODs names them in a --mods-file (one MOD per line), exactly like the plugin side's
/// --plugins-file. The plugin side's corpus and translations are found by the CLI's own defaults.
/// </summary>
public static class SkyPatcherCliArgs
{
    public const string PickUpOutTemp = "SkyPatcherText/PickUpTarget/out_temp";
    public const string TranslationOutTemp = "SkyPatcherText/Translation/out_temp";

    public static string[] PickUp(string mo2Dir, string? modsDirOverride, string? profileDirOverride, string? overwriteDirOverride)
    {
        var args = new List<string> { "skypatcher-pickup", mo2Dir };
        if (!string.IsNullOrWhiteSpace(modsDirOverride)) args.Add($"--mods-dir={modsDirOverride}");
        if (!string.IsNullOrWhiteSpace(profileDirOverride)) args.Add($"--profile-dir={profileDirOverride}");
        if (!string.IsNullOrWhiteSpace(overwriteDirOverride)) args.Add($"--overwrite-dir={overwriteDirOverride}");
        return args.ToArray();
    }

    /// <summary>The "back to the first-step-only baseline" run (an initialisation): every earlier result of the
    /// named MODs (or of all, when <paramref name="modsFilePath"/> is null) is discarded.</summary>
    public static string[] Reset(string? modsFilePath, string importDir)
    {
        var args = new List<string> { "skypatcher-translation", PickUpOutTemp, TranslationOutTemp };
        args.Add(modsFilePath == null ? "--all" : $"--mods-file={modsFilePath}");
        args.AddRange(["--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits", $"--import={importDir}"]);
        return args.ToArray();
    }

    /// <summary>An ordinary translation run over the selected MODs. Earlier results are kept (no
    /// --discard-user-edits).</summary>
    public static string[] Translate(string modsFilePath, string cancelFlagPath, string importDir, IEnumerable<string> optionFlags)
    {
        var args = new List<string>
        {
            "skypatcher-translation", PickUpOutTemp, TranslationOutTemp,
            $"--mods-file={modsFilePath}", $"--cancel-flag-path={cancelFlagPath}", $"--import={importDir}",
        };
        args.AddRange(optionFlags);
        return args.ToArray();
    }

    public static string[] Generate(string modsFilePath, string outDir) =>
        ["skypatcher-generate", TranslationOutTemp, outDir, $"--mods-file={modsFilePath}"];
}
