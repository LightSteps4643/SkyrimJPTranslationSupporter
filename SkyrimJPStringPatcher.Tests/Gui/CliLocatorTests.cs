using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// CliLocator.TryGetProductRoot/TryAutoDetect are pure file-existence checks
/// against a "GUI base directory" — testable in isolation by passing a
/// synthetic folder in place of the real AppContext.BaseDirectory (which a
/// test can't swap out, since it's always the test assembly's own build
/// output, not anything resembling a real release). See CliLocator.cs's own
/// remarks for why each of the three layouts below exists.
/// </summary>
public class CliLocatorTests
{
    private const string GuiExeName = "Skyrim_JP_Translation_Supporter.exe"; // not actually checked by CliLocator, just realism
    private const string CliExeName = "SkyrimJPStringPatcher.exe";

    [Fact]
    public void CurrentReleaseLayout_CliNestedInOwnSubfolder_FindsRootAndNestedExe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_clilocator_{Guid.NewGuid():N}");
        var cliDir = Path.Combine(root, "SkyrimJPStringPatcher");
        Directory.CreateDirectory(cliDir);
        try
        {
            File.WriteAllText(Path.Combine(root, GuiExeName), "");
            File.WriteAllText(Path.Combine(cliDir, CliExeName), "");

            Assert.Equal(root, CliLocator.TryGetProductRoot(root));
            Assert.Equal(Path.Combine("SkyrimJPStringPatcher", CliExeName), CliLocator.TryAutoDetect(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void OldReleaseLayout_BothExesFlatSideBySide_FindsRootAndFlatExe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_clilocator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, GuiExeName), "");
            File.WriteAllText(Path.Combine(root, CliExeName), "");

            Assert.Equal(root, CliLocator.TryGetProductRoot(root));
            Assert.Equal(CliExeName, CliLocator.TryAutoDetect(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Mirrors the real dev-tree shape: the GUI's own build output sits
    /// under `&lt;root&gt;/SkyrimJPStringPatcherGui/bin/&lt;Config&gt;/net9.0-windows/`,
    /// and the CLI's own build output sits separately under
    /// `&lt;root&gt;/bin/&lt;Config&gt;/net9.0/`. TryGetProductRoot walks UP from the
    /// GUI's base directory looking for an ancestor literally named
    /// "SkyrimJPStringPatcherGui" and returns its parent.</summary>
    [Fact]
    public void DevLayout_GuiNestedUnderNamedAncestorFolder_FindsRootAndBinRelativeExe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_clilocator_{Guid.NewGuid():N}");
        var guiBaseDir = Path.Combine(root, "SkyrimJPStringPatcherGui", "bin", "Debug", "net9.0-windows");
        var cliBinDir = Path.Combine(root, "bin", "Debug", "net9.0");
        Directory.CreateDirectory(guiBaseDir);
        Directory.CreateDirectory(cliBinDir);
        try
        {
            File.WriteAllText(Path.Combine(guiBaseDir, GuiExeName), "");
            File.WriteAllText(Path.Combine(cliBinDir, CliExeName), "");

            Assert.Equal(root, CliLocator.TryGetProductRoot(guiBaseDir));
            Assert.Equal(Path.Combine("bin", "Debug", "net9.0", CliExeName), CliLocator.TryAutoDetect(guiBaseDir));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void NoLayoutMatches_ReturnsNullForBoth()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_clilocator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, GuiExeName), ""); // GUI exe alone, no CLI anywhere findable

            Assert.Null(CliLocator.TryGetProductRoot(root));
            Assert.Null(CliLocator.TryAutoDetect(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>If a folder somehow matched both the current and old release
    /// layout at once (shouldn't happen from a real publish, but the check
    /// order is still a real behavioral guarantee worth locking in), the
    /// nested-subfolder layout must win -- it's checked first in both methods.</summary>
    [Fact]
    public void BothReleaseLayoutsPresentAtOnce_PrefersTheNestedSubfolderLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_clilocator_{Guid.NewGuid():N}");
        var cliDir = Path.Combine(root, "SkyrimJPStringPatcher");
        Directory.CreateDirectory(cliDir);
        try
        {
            File.WriteAllText(Path.Combine(root, GuiExeName), "");
            File.WriteAllText(Path.Combine(cliDir, CliExeName), ""); // new layout
            File.WriteAllText(Path.Combine(root, CliExeName), ""); // old layout, also present

            Assert.Equal(Path.Combine("SkyrimJPStringPatcher", CliExeName), CliLocator.TryAutoDetect(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
