using System.IO.Compression;
using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

public class TranslationBackupTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sjpts_translationbackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteSourceFolder(string outTempDir, string folderName, string tsvFileName, string tsvContent, DateTime lastWriteTime)
    {
        var dir = Path.Combine(outTempDir, folderName);
        Directory.CreateDirectory(dir);
        var tsvPath = Path.Combine(dir, tsvFileName);
        File.WriteAllText(tsvPath, tsvContent);
        File.SetLastWriteTime(tsvPath, lastWriteTime);
    }

    [Fact]
    public void Backup_CreatesZipContainingSourceFolderContents()
    {
        var root = TempDir();
        try
        {
            var outTempDir = Path.Combine(root, "Translation", "out_temp");
            WriteSourceFolder(outTempDir, "SomePlugin.esp", "translations.tsv", "content-A", DateTime.Now);

            TranslationBackup.Backup(outTempDir, new[] { "SomePlugin.esp" }, "translations.tsv");

            var bakDir = Path.Combine(root, "Translation", "bak");
            var zipPath = Assert.Single(Directory.GetFiles(bakDir, "*.zip"));
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("SomePlugin.esp/translations.tsv", entry.FullName.Replace('\\', '/'));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Regression test (2026-09-12): the backup's timestamp is derived
    /// from the source tsv's own LastWriteTime (second precision), not from
    /// "when Backup() ran" — two destructive-reset operations whose source
    /// data happens to share the same LastWriteTime (e.g. a fast operation run
    /// twice in quick succession, or content that legitimately differs but
    /// still lands in the same second) used to collide on the exact same zip
    /// filename and silently overwrite the earlier backup via FileMode.Create.
    /// This is exactly the case the backup exists to protect against, so a
    /// collision must produce a second, distinct file instead.</summary>
    [Fact]
    public void Backup_TimestampCollisionWithDifferentContent_DoesNotOverwritePreviousBackup()
    {
        var root = TempDir();
        try
        {
            var outTempDir = Path.Combine(root, "Translation", "out_temp");
            var sameSecond = new DateTime(2026, 9, 12, 10, 0, 0);

            WriteSourceFolder(outTempDir, "SomePlugin.esp", "translations.tsv", "content-before-import-added", sameSecond);
            TranslationBackup.Backup(outTempDir, new[] { "SomePlugin.esp" }, "translations.tsv");

            // Simulate a second destructive reset whose source data now differs
            // (e.g. a translation-import file was picked up) but whose
            // LastWriteTime still lands in the exact same second.
            WriteSourceFolder(outTempDir, "SomePlugin.esp", "translations.tsv", "content-after-import-added", sameSecond);
            TranslationBackup.Backup(outTempDir, new[] { "SomePlugin.esp" }, "translations.tsv");

            var bakDir = Path.Combine(root, "Translation", "bak");
            var zipPaths = Directory.GetFiles(bakDir, "*.zip");
            Assert.Equal(2, zipPaths.Length);

            var contents = zipPaths.Select(path =>
            {
                using var archive = ZipFile.OpenRead(path);
                using var reader = new StreamReader(archive.Entries.Single().Open());
                return reader.ReadToEnd();
            }).ToList();

            Assert.Contains("content-before-import-added", contents);
            Assert.Contains("content-after-import-added", contents);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Backup_NoExistingSourceFolders_DoesNotCreateBakDirectory()
    {
        var root = TempDir();
        try
        {
            var outTempDir = Path.Combine(root, "Translation", "out_temp");
            Directory.CreateDirectory(outTempDir);

            TranslationBackup.Backup(outTempDir, new[] { "NonexistentPlugin.esp" }, "translations.tsv");

            Assert.False(Directory.Exists(Path.Combine(root, "Translation", "bak")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
