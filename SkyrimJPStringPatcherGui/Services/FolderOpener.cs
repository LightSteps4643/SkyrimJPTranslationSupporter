namespace SkyrimJPStringPatcherGui.Services;

/// <summary>v0.54.2: shared by SettingsForm/MainForm — both open the same kind of
/// folder (Translation/import, out) via Explorer, so the "does it exist yet"
/// check and the actual Process.Start call live in one place instead of two.</summary>
public static class FolderOpener
{
    public static void OpenOrWarn(IWin32Window owner, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(owner, $"フォルダがまだありません:\n{path}", "フォルダが見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
