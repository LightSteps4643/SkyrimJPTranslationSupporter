using System.Text.RegularExpressions;

namespace SkyrimJPStringPatcher.SkyPatcher;

/// <summary>Reason codes for a name entry that cannot take effect ("" = it can).</summary>
public static class SkyPatcherInactive
{
    /// <summary>SkyPatcher.ini turns the type's whole subfolder off.</summary>
    public const string TypeDisabled = "TypeDisabled";
    /// <summary>The file is named after a plugin (Plugin.esp.ini) that is not in the load order.</summary>
    public const string FileGate = "FileGate";
    /// <summary>hasPlugins / hasPluginsOr on the line is not satisfied.</summary>
    public const string LineGate = "LineGate";
    /// <summary>Every form the line targets is in a plugin that is not loaded (SkyPatcher skips those silently).</summary>
    public const string TargetPluginMissing = "TargetPluginMissing";
    /// <summary>A later, effective line of the same file sets the same field of every record this line targets.</summary>
    public const string OverriddenInSameFile = "OverriddenInSameFile";
}

/// <summary>What the evaluation needs to know about the user's setup.</summary>
public sealed class SkyPatcherContext
{
    private readonly HashSet<string> _activePlugins;

    public SkyPatcherContext(IEnumerable<string> activePlugins, SkyPatcherSettings settings)
    {
        _activePlugins = new HashSet<string>(activePlugins, StringComparer.OrdinalIgnoreCase);
        Settings = settings;
    }

    public SkyPatcherSettings Settings { get; }

    public bool IsPluginActive(string plugin) => _activePlugins.Contains(plugin.Trim());
}

/// <summary>One name operation of one line of a SkyPatcher .ini.</summary>
/// <param name="SourcePath">The .ini's path relative to SKSE/Plugins/SkyPatcher, '/'-separated, type folder first (e.g. "armor/Foo.ini").</param>
/// <param name="Occurrence">1-based: how many lines of this file, up to and including this one, have exactly this filter part.</param>
public sealed record SkyPatcherEntry(
    string SourcePath, int LineNumber, string FilterPart, int Occurrence,
    SkyPatcherNameKind Kind, string DsdType, string Text, string Inactive)
{
    /// <summary>The row key: stable across edits elsewhere in the file (a line number is not).</summary>
    public string RowKey => $"{SourcePath}|{FilterPart}|{Occurrence}";
}

/// <summary>Decides which name-setting lines of one SkyPatcher .ini can take effect (grammar §2 gates and
/// conflict resolution, §4 addressing, §6 hasPlugins). The governing principle: whatever cannot be judged
/// with certainty stays ACTIVE — a wrongly inactive line would be left untranslated (English in game),
/// a wrongly active one only costs a needless translation. Files are never compared with each other.</summary>
public static class SkyPatcherEvaluator
{
    private static readonly Regex PluginNamedIni =
        new(@"^(?<plugin>.+\.(?:esp|esm|esl))\.ini$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record Item(SkyPatcherLine Line, int Occurrence, string Inactive, string? OtherFiltersSignature, HashSet<string>? Targets);

    public static List<SkyPatcherEntry> Evaluate(string sourcePath, IReadOnlyList<string> lines, SkyPatcherContext context)
    {
        var path = sourcePath.Replace('\\', '/');
        var parts = path.Split('/');
        if (!SkyPatcherTypes.TryGet(parts[0], out var type)) return [];

        var fileInactive = "";
        if (!context.Settings.IsEnabled(type.Toggle))
            fileInactive = SkyPatcherInactive.TypeDisabled;
        else if (PluginNamedIni.Match(parts[^1]) is { Success: true } gate && !context.IsPluginActive(gate.Groups["plugin"].Value))
            fileInactive = SkyPatcherInactive.FileGate;

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<Item>();
        for (var n = 0; n < lines.Count; n++)
        {
            var line = SkyPatcherLineParser.Parse(lines[n], n + 1);
            if (line == null) continue;

            occurrences[line.FilterPart] = occurrences.GetValueOrDefault(line.FilterPart) + 1;

            var inactive = fileInactive;
            if (inactive.Length == 0 && !PassesLineGates(line, context)) inactive = SkyPatcherInactive.LineGate;
            var (signature, targets) = Targets(line, type.PrimaryFilter);
            if (inactive.Length == 0 && targets != null && AllFormIdTargetsInUnloadedPlugins(targets, context))
                inactive = SkyPatcherInactive.TargetPluginMissing;

            items.Add(new Item(line, occurrences[line.FilterPart], inactive, signature, targets));
        }

        var result = new List<SkyPatcherEntry>();
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var op in items[i].Line.NameOps)
            {
                var dsdType = op.Kind == SkyPatcherNameKind.FullName ? type.FullNameDsdType : type.ShortNameDsdType;
                if (dsdType == null) continue;   // this type has no such field (shortName is NPC-only)

                var inactive = items[i].Inactive;
                if (inactive.Length == 0 && IsOverridden(items, i, op.Kind))
                    inactive = SkyPatcherInactive.OverriddenInSameFile;

                result.Add(new SkyPatcherEntry(path, items[i].Line.LineNumber, items[i].Line.FilterPart, items[i].Occurrence,
                    op.Kind, dsdType, op.Text, inactive));
            }
        }
        return result;
    }

    private static bool PassesLineGates(SkyPatcherLine line, SkyPatcherContext context)
    {
        foreach (var segment in line.FilterSegments)
        {
            var key = SkyPatcherLineParser.KeyOf(segment);
            var all = key.Equals("hasPlugins", StringComparison.OrdinalIgnoreCase);
            var any = key.Equals("hasPluginsOr", StringComparison.OrdinalIgnoreCase);
            if (!all && !any) continue;

            var plugins = ValueOf(segment).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (plugins.Length == 0) continue;
            var passes = all ? plugins.All(context.IsPluginActive) : plugins.Any(context.IsPluginActive);
            if (!passes) return false;
        }
        return true;
    }

    /// <summary>The line's targets (normalized) and a signature of its other filter segments. Both are null
    /// unless the line has exactly one primary-filter segment — otherwise we cannot tell which records it names.</summary>
    private static (string? Signature, HashSet<string>? Targets) Targets(SkyPatcherLine line, string primaryFilter)
    {
        var primary = line.FilterSegments.Where(s => SkyPatcherLineParser.KeyOf(s).Equals(primaryFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (primary.Count != 1) return (null, null);

        var targets = ValueOf(primary[0])
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTarget)
            .ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0) return (null, null);

        var others = line.FilterSegments.Where(s => !ReferenceEquals(s, primary[0]))
            .Select(s => s.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal);
        return (string.Join("\u0001", others), targets);
    }

    /// <summary>"Plugin|FormID" -> "plugin|FORMID" (case, a 0x prefix and leading zeros do not make a different
    /// record); an EditorID -> "@editorid" (never equal to a FormID form: we do not resolve names to forms).</summary>
    private static string NormalizeTarget(string token)
    {
        var bar = token.IndexOf('|');
        if (bar < 0) return "@" + token.Trim().ToLowerInvariant();

        var plugin = token[..bar].Trim().ToLowerInvariant();
        var id = token[(bar + 1)..].Trim();
        if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) id = id[2..];
        id = id.TrimStart('0').ToUpperInvariant();
        return $"{plugin}|{(id.Length == 0 ? "0" : id)}";
    }

    private static bool AllFormIdTargetsInUnloadedPlugins(HashSet<string> targets, SkyPatcherContext context) =>
        targets.All(t => t.Contains('|') && !context.IsPluginActive(t[..t.IndexOf('|')]));

    /// <summary>A later effective line of the same file, with identical other filters, whose targets include
    /// all of this line's targets and which sets the same field, wins over this line for every record this
    /// line names.</summary>
    private static bool IsOverridden(List<Item> items, int index, SkyPatcherNameKind kind)
    {
        var item = items[index];
        if (item.Targets == null) return false;

        for (var j = index + 1; j < items.Count; j++)
        {
            var later = items[j];
            if (later.Inactive.Length != 0 || later.Targets == null) continue;
            if (!later.Line.NameOps.Any(o => o.Kind == kind)) continue;
            if (later.OtherFiltersSignature != item.OtherFiltersSignature) continue;
            if (item.Targets.IsSubsetOf(later.Targets)) return true;
        }
        return false;
    }

    private static string ValueOf(string segment)
    {
        var eq = segment.IndexOf('=');
        return eq < 0 ? "" : segment[(eq + 1)..].Trim();
    }
}
