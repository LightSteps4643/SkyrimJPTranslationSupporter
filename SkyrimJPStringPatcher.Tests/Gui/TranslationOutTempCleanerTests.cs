using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

public class TranslationOutTempCleanerTests
{
    [Fact]
    public void Clear_ExistingDirectoryWithContent_DeletesItEntirely()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_cleaner_{Guid.NewGuid():N}");
        var pluginDir = Path.Combine(root, "SomePlugin.esp");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "translations.tsv"), "dummy");
        try
        {
            TranslationOutTempCleaner.Clear(root);

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Clear_NonexistentDirectory_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_cleaner_missing_{Guid.NewGuid():N}");

        TranslationOutTempCleaner.Clear(root);

        Assert.False(Directory.Exists(root));
    }
}
