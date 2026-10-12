using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// The command lines the GUI builds for the SkyPatcher steps. They are the GUI's whole contract with the
/// CLI (the CLI side is covered by the ProgramCliSkyPatcher* tests, which also run these exact arrays), so
/// each is checked against the documented shape of its command: the three stages work in their own
/// SkyPatcherText/ tree; a restricted run names its MODs with --mods-file; the plugin side is never named.
/// </summary>
public class SkyPatcherCliArgsTests
{
    [Fact]
    public void PickUp_NamesTheInstance_AndAppendsOnlyTheOverridesThatAreSet()
    {
        Assert.Equal(["skypatcher-pickup", "D:/MO2"], SkyPatcherCliArgs.PickUp("D:/MO2", "", "", ""));
        Assert.Equal(
            ["skypatcher-pickup", "D:/MO2", "--mods-dir=E:/mods", "--profile-dir=E:/profile", "--overwrite-dir=E:/ow"],
            SkyPatcherCliArgs.PickUp("D:/MO2", "E:/mods", "E:/profile", "E:/ow"));
        Assert.Equal(["skypatcher-pickup", "D:/MO2", "--profile-dir=E:/profile"], SkyPatcherCliArgs.PickUp("D:/MO2", null, "E:/profile", "  "));
    }

    [Fact]
    public void ResetAll_IsTheFirstStepOnlyBaseline_ForEveryMod()
    {
        Assert.Equal(
            ["skypatcher-translation", "SkyPatcherText/PickUpTarget/out_temp", "SkyPatcherText/Translation/out_temp", "--all",
                "--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits", "--import=C:/imp"],
            SkyPatcherCliArgs.Reset(modsFilePath: null, "C:/imp"));
    }

    [Fact]
    public void ResetSelected_NamesTheModsFile_InsteadOfAll()
    {
        var args = SkyPatcherCliArgs.Reset("C:/tmp/mods.txt", "C:/imp");

        Assert.Contains("--mods-file=C:/tmp/mods.txt", args);
        Assert.DoesNotContain("--all", args);
        Assert.Contains("--discard-user-edits", args);
    }

    [Fact]
    public void Translate_RestrictsToTheSelectedMods_AndCarriesCancelImportAndTheOptionFlags()
    {
        var args = SkyPatcherCliArgs.Translate("C:/tmp/mods.txt", "C:/tmp/cancel.flag", "C:/imp", ["--no-namefallback", "--llm-local", "--llm-local-model=gemma4:26b"]);

        Assert.Equal(
            ["skypatcher-translation", "SkyPatcherText/PickUpTarget/out_temp", "SkyPatcherText/Translation/out_temp",
                "--mods-file=C:/tmp/mods.txt", "--cancel-flag-path=C:/tmp/cancel.flag", "--import=C:/imp",
                "--no-namefallback", "--llm-local", "--llm-local-model=gemma4:26b"],
            args);
        Assert.DoesNotContain("--discard-user-edits", args);   // an ordinary run never wipes earlier results
    }

    [Fact]
    public void Generate_ReadsTheSkyPatcherTranslations_AndWritesToTheSharedOutputFolder()
    {
        Assert.Equal(
            ["skypatcher-generate", "SkyPatcherText/Translation/out_temp", "C:/work/out", "--mods-file=C:/tmp/mods.txt"],
            SkyPatcherCliArgs.Generate("C:/tmp/mods.txt", "C:/work/out"));
    }

    [Fact]
    public void TheWorkFolders_AreTheSkyPatcherTreeNotThePluginOnes()
    {
        Assert.Equal("SkyPatcherText/Translation/out_temp", SkyPatcherCliArgs.TranslationOutTemp);
        Assert.Equal("SkyPatcherText/PickUpTarget/out_temp", SkyPatcherCliArgs.PickUpOutTemp);
    }
}
