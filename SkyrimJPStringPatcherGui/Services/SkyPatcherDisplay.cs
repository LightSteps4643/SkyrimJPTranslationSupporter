namespace SkyrimJPStringPatcherGui.Services;

/// <summary>What the SkyPatcher detail window shows. The reasons are stored in translations.tsv as language-
/// neutral codes (the CLI side's SkyPatcherInactive / SkyPatcherExclusion constants); this turns them into
/// Japanese for the "無効の理由" column. An unknown code is shown as it is — never hidden.</summary>
public static class SkyPatcherDisplay
{
    private static readonly Dictionary<string, string> InactiveLabels = new(StringComparer.Ordinal)
    {
        ["TypeDisabled"] = "種別が無効（SkyPatcher.ini）",
        ["FileGate"] = "ファイル名のゲート（そのプラグインがロードオーダーに無い）",
        ["LineGate"] = "行ゲートが成立しない（hasPlugins等）",
        ["TargetPluginMissing"] = "対象のプラグインがロードオーダーに無い",
        ["OverriddenInSameFile"] = "同じファイル内の後の行に上書きされる",
    };

    private static readonly Dictionary<string, string> ExcludedLabels = new(StringComparer.Ordinal)
    {
        ["AlreadyJapanese"] = "翻訳しない（すでに日本語）",
        ["NoLetters"] = "翻訳しない（英字を含まない）",
        ["MarkupOnly"] = "翻訳しない（マークアップのみ）",
        ["InterfaceKeyReference"] = "翻訳しない（$キーの参照。訳すと壊れる）",
        ["AssetPath"] = "翻訳しない（アセットのパス）",
        ["PlaceholderToken"] = "翻訳しない（プレースホルダー）",
        ["NonWordAcronym"] = "翻訳しない（母音のない略称）",
    };

    /// <summary>The short names the window title uses to say how many rows are left out and why.</summary>
    private static readonly Dictionary<string, string> InactiveShortLabels = new(StringComparer.Ordinal)
    {
        ["TypeDisabled"] = "種別が無効",
        ["FileGate"] = "ファイル名のゲート",
        ["LineGate"] = "行ゲート不成立",
        ["TargetPluginMissing"] = "対象のプラグインなし",
        ["OverriddenInSameFile"] = "後の行に上書きされる",
    };

    private const string UntranslatableShortLabel = "翻訳できない文字列";

    /// <summary>"翻訳対象 9件（対象のプラグインなし 3件、…）": the rows that are the work, and — in the same
    /// words as each row's own reason — how many are left out and why, most first. A row that cannot take
    /// effect is counted by that, even if it is also untranslatable (the same precedence as
    /// <see cref="ReasonLabel"/>); every untranslatable string is one group, whatever its kind.</summary>
    public static string Title(int toTranslate, IEnumerable<(string Inactive, string Excluded)> notTranslated)
    {
        var groups = notTranslated
            .Select(r => !string.IsNullOrEmpty(r.Inactive)
                ? InactiveShortLabels.GetValueOrDefault(r.Inactive, r.Inactive)
                : !string.IsNullOrEmpty(r.Excluded) ? UntranslatableShortLabel : "")
            .Where(label => label.Length > 0)
            .GroupBy(label => label)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}件")
            .ToList();
        return groups.Count == 0 ? $"翻訳対象 {toTranslate}件" : $"翻訳対象 {toTranslate}件（{string.Join("、", groups)}）";
    }

    /// <summary>The reason a row is greyed out. A row that cannot take effect is explained by that, even if
    /// it is also not translatable ("" for neither).</summary>
    public static string ReasonLabel(string inactive, string excluded)
    {
        if (!string.IsNullOrEmpty(inactive)) return InactiveLabels.GetValueOrDefault(inactive, inactive);
        if (!string.IsNullOrEmpty(excluded)) return ExcludedLabels.GetValueOrDefault(excluded, excluded);
        return "";
    }

    /// <summary>The "対象" column: the line's filter exactly as written, with the number of records when its
    /// first filter names several ("（2件）") — a line setting one name for several records is still one row.</summary>
    public static string TargetLabel(string sourceFilter)
    {
        if (sourceFilter.Length == 0) return "（フィルターなし）";

        var firstSegment = sourceFilter.Split(':')[0];
        var eq = firstSegment.IndexOf('=');
        var count = eq < 0 ? 1 : firstSegment[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return count > 1 ? $"{sourceFilter}（{count}件）" : sourceFilter;
    }
}
