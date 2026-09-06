using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

public class AppVersionTests
{
    [Fact]
    public void FormatWindowTitle_WithVersion_AppendsMajorMinorBuild()
    {
        var result = AppVersion.FormatWindowTitle("Skyrim JP Translation Supporter", new Version(0, 59, 4, 0));

        Assert.Equal("Skyrim JP Translation Supporter v0.59.4", result);
    }

    [Fact]
    public void FormatWindowTitle_NullVersion_ReturnsBaseNameUnchanged()
    {
        var result = AppVersion.FormatWindowTitle("Skyrim JP Translation Supporter", null);

        Assert.Equal("Skyrim JP Translation Supporter", result);
    }
}
