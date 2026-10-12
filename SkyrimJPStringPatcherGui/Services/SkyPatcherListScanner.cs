namespace SkyrimJPStringPatcherGui.Services;

/// <summary>Counts for one list row (a plugin, or a providing MOD).</summary>
public static class TranslationStats
{
    /// <summary>Total rows, the blank-Japanese ("untranslated") ones, and their share — by count and by
    /// characters (the text is TSV-escaped on disk, so it is unescaped first to count real characters).</summary>
    public static (int Total, int Untranslated, double Ratio, long UntranslatedChars, double CharsRatio) Compute(
        List<Dictionary<string, string>> rows)
    {
        var untranslatedRows = rows.Where(r => string.IsNullOrEmpty(r.GetValueOrDefault("Japanese"))).ToList();
        var untranslatedCount = untranslatedRows.Count;
        var untranslatedChars = untranslatedRows.Sum(r => (long)TsvEscaping.Unescape(r.GetValueOrDefault("EnglishText", "")).Length);
        var totalChars = rows.Sum(r => (long)TsvEscaping.Unescape(r.GetValueOrDefault("EnglishText", "")).Length);
        var ratio = rows.Count == 0 ? 100.0 : 100.0 * (rows.Count - untranslatedCount) / rows.Count;
        var charsRatio = totalChars == 0 ? 100.0 : 100.0 * (totalChars - untranslatedChars) / totalChars;
        return (rows.Count, untranslatedCount, ratio, untranslatedChars, charsRatio);
    }
}

public sealed record SkyPatcherListRow(string Mod, int Total, int Untranslated, double Ratio, long UntranslatedChars, double CharsRatio);

/// <summary>The SkyPatcher sub-tab's list, read straight from
/// SkyPatcherText/Translation/out_temp/&lt;MOD&gt;/translations.tsv (the GUI never asks the CLI). One row per
/// providing MOD, named by mod_folder_name.txt (the folder name when it is missing). Only rows offered for
/// translation are counted: one that cannot take effect (Inactive) or is not translatable (Excluded) is in
/// the file for the output's sake and is not part of the work.</summary>
public static class SkyPatcherListScanner
{
    public static List<SkyPatcherListRow> Scan(string translationOutTempDir)
    {
        var rows = new List<SkyPatcherListRow>();
        if (!Directory.Exists(translationOutTempDir)) return rows;

        foreach (var modDir in Directory.GetDirectories(translationOutTempDir))
        {
            var tsvRows = TsvReader.Read(Path.Combine(modDir, "translations.tsv"))
                .Where(IsOffered)
                .ToList();
            if (tsvRows.Count == 0) continue;

            var (total, untranslated, ratio, untranslatedChars, charsRatio) = TranslationStats.Compute(tsvRows);
            rows.Add(new SkyPatcherListRow(ModNameOf(modDir), total, untranslated, ratio, untranslatedChars, charsRatio));
        }
        return rows.OrderByDescending(r => r.UntranslatedChars).ThenBy(r => r.Mod, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A row that takes part in the translation work.</summary>
    public static bool IsOffered(Dictionary<string, string> row) =>
        string.IsNullOrEmpty(row.GetValueOrDefault("Inactive")) && string.IsNullOrEmpty(row.GetValueOrDefault("Excluded"));

    /// <summary>The MOD's real name: mod_folder_name.txt, else the folder name.</summary>
    public static string ModNameOf(string modDir)
    {
        var nameFile = Path.Combine(modDir, "mod_folder_name.txt");
        if (File.Exists(nameFile))
        {
            var name = File.ReadAllText(nameFile).Trim();
            if (name.Length > 0) return name;
        }
        return Path.GetFileName(modDir);
    }
}
