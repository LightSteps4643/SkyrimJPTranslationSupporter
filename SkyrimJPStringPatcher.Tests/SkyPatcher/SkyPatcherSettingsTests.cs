using SkyrimJPStringPatcher.SkyPatcher;

namespace SkyrimJPStringPatcher.Tests.SkyPatcher;

/// <summary>
/// SkyPatcher.ini, grammar §8: "[Patcher] — one iEnable&lt;Type&gt;Patching=1|0 per record type
/// (all on by default). Turning a type off skips its whole subfolder."
/// </summary>
public class SkyPatcherSettingsTests
{
    [Fact]
    public void NoSettingsFile_EveryTypeIsEnabled()
    {
        Assert.True(SkyPatcherSettings.Default.IsEnabled("iEnableArmorPatching"));
    }

    [Fact]
    public void ATypeSetToZero_IsDisabled_TheOthersStayEnabled()
    {
        var settings = SkyPatcherSettings.Parse(["[Patcher]", "iEnableArmorPatching=0", "iEnableWeaponPatching=1", "", "[Log]", "iEnablelog=0"]);

        Assert.False(settings.IsEnabled("iEnableArmorPatching"));
        Assert.True(settings.IsEnabled("iEnableWeaponPatching"));
        Assert.True(settings.IsEnabled("iEnableMiscPatching"));
    }

    [Fact]
    public void OnlyThePatcherSection_Counts()
    {
        var settings = SkyPatcherSettings.Parse(["[Features]", "iEnableArmorPatching=0"]);

        Assert.True(settings.IsEnabled("iEnableArmorPatching"));
    }

    [Fact]
    public void KeysAreCaseInsensitive_AndTrailingSpacesAndCommentsAreIgnored()
    {
        // The shipped SkyPatcher.ini has values with trailing spaces ("iEnableSetLevelDirectlyByPCMult=0 ") and ';' comment lines.
        var settings = SkyPatcherSettings.Parse(["[Patcher]", ";iEnableBookPatching=0", "IENABLEARMORPATCHING = 0 "]);

        Assert.False(settings.IsEnabled("iEnableArmorPatching"));
        Assert.True(settings.IsEnabled("iEnableBookPatching"));
    }
}
