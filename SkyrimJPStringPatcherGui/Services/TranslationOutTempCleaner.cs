namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// v0.60.0: "MO2再読込＆初期化" backs up Translation/out_temp (TranslationBackup)
/// and then regenerates it via pickuptarget+translation --all — but regenerating
/// only WRITES the plugins the fresh scan still finds candidates for, it never
/// deleted the folders of plugins that dropped out of that set (e.g. now fully
/// covered by an existing DSD file, or removed from the load order). Those
/// stale <plugin>/translations.tsv files stayed on disk indefinitely, and the
/// plugin grid (MainForm.LoadData -> ScanTranslationsOutTemp) reads every
/// folder under Translation/out_temp directly — so a plugin's last real scan
/// kept showing in the grid (with old, no-longer-accurate untranslated counts)
/// long after it stopped being a real candidate. Clearing the whole directory
/// before the fresh scan (backup already ran first) means only what the fresh
/// scan actually writes survives — no leftover ghosts.
/// </summary>
public static class TranslationOutTempCleaner
{
    public static void Clear(string translationOutTempDir)
    {
        if (Directory.Exists(translationOutTempDir))
            Directory.Delete(translationOutTempDir, recursive: true);
    }
}
