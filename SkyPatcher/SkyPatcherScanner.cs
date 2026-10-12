using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.SkyPatcher;

/// <param name="TotalRows">Every name row found; each is counted in exactly one of the three categories below.</param>
/// <param name="Translatable">Rows that can take effect and are offered for translation.</param>
/// <param name="Excluded">Rows that can take effect but are not offered (reason code -&gt; count).</param>
/// <param name="Inactive">Rows that cannot take effect (reason code -&gt; count); takes precedence over Excluded.</param>
public sealed record SkyPatcherScanStats(
    int FilesRead, int TotalRows, int Translatable,
    IReadOnlyDictionary<string, int> Excluded, IReadOnlyDictionary<string, int> Inactive);

public sealed record SkyPatcherScanResult(IReadOnlyList<Candidate> Candidates, SkyPatcherScanStats Stats);

/// <summary>Scans an MO2 instance's loose SkyPatcher .ini files (SKSE/Plugins/SkyPatcher/&lt;type&gt;/…) into
/// candidates. Reads what the game would read: enabled mods only, and per relative path the VFS winner
/// (Overwrite, then the highest-priority mod). Every name line becomes a row — even one that cannot take
/// effect or is not offered for translation — because the output reproduces every line in order.
/// Our own earlier output (the zzz_SJPTS subfolder) is not a source; it is read back so its translations
/// carry forward.</summary>
public static class SkyPatcherScanner
{
    private static readonly string SkyPatcherRelativeDir = Path.Combine("SKSE", "Plugins", "SkyPatcher");

    private sealed record Previous(string Original, string Japanese, string Notes);

    public static SkyPatcherScanResult Scan(Mo2Instance instance)
    {
        var context = new SkyPatcherContext(instance.LoadOrder.Select(p => p.FileName), ReadSettings(instance));

        var sources = new List<(string Path, string Physical)>();
        var previousFiles = new List<(string SourcePath, string Physical)>();
        foreach (var (rel, physical) in Mo2InstanceReader.BuildVfsDirectoryMerge(instance, SkyPatcherRelativeDir)
                     .Select(kv => (Rel: kv.Key.Replace('\\', '/'), Physical: kv.Value))
                     .OrderBy(x => x.Rel, StringComparer.OrdinalIgnoreCase))
        {
            if (!rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = rel.Split('/');
            if (parts.Length < 2 || !SkyPatcherTypes.TryGet(parts[0], out _)) continue;

            if (parts.Length >= 3 && parts[1].Equals(SkyPatcherOutputFormat.FolderName, StringComparison.OrdinalIgnoreCase))
                previousFiles.Add((string.Join('/', parts.Where((_, i) => i != 1)), physical));
            else
                sources.Add((rel, physical));
        }

        var previous = new Dictionary<(string Key, SkyPatcherNameKind Kind), Previous>();
        foreach (var (sourcePath, physical) in previousFiles)
            ReadPreviousOutput(sourcePath, File.ReadAllLines(physical), previous);

        var candidates = new List<Candidate>();
        var excluded = new Dictionary<string, int>();
        var inactive = new Dictionary<string, int>();
        var translatable = 0;
        foreach (var (path, physical) in sources)
        {
            var provider = ProviderOf(instance, physical);
            foreach (var entry in SkyPatcherEvaluator.Evaluate(path, File.ReadAllLines(physical), context))
            {
                var reason = SkyPatcherExclusion.Reason(entry.Text) ?? "";
                var (coveredJapanese, coveredNotes, staleOriginal, staleTranslation) =
                    reason.Length == 0 ? CarryForward(entry, previous) : ("", "", "", "");

                candidates.Add(new Candidate(provider, entry.RowKey, entry.DsdType, entry.Text,
                    DsdCoveredJapanese: coveredJapanese, DsdCoveredNotes: coveredNotes,
                    StaleOriginal: staleOriginal, StaleTranslation: staleTranslation,
                    SourcePath: entry.SourcePath, SourceFilter: entry.FilterPart, SourceLine: entry.LineNumber,
                    Inactive: entry.Inactive, Excluded: reason));

                if (entry.Inactive.Length > 0) inactive[entry.Inactive] = inactive.GetValueOrDefault(entry.Inactive) + 1;
                else if (reason.Length > 0) excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
                else translatable++;
            }
        }

        return new SkyPatcherScanResult(candidates,
            new SkyPatcherScanStats(sources.Count, candidates.Count, translatable, excluded, inactive));
    }

    /// <summary>The same original, translated into Japanese last time: carry the translation (and how it was
    /// made) forward as already-covered. A different original: the old translation is stale — offered as
    /// a reference only.</summary>
    private static (string CoveredJapanese, string CoveredNotes, string StaleOriginal, string StaleTranslation) CarryForward(
        SkyPatcherEntry entry, Dictionary<(string Key, SkyPatcherNameKind Kind), Previous> previous)
    {
        if (!previous.TryGetValue((entry.RowKey, entry.Kind), out var prev) || !LanguageDetector.ContainsJapanese(prev.Japanese))
            return ("", "", "", "");

        return prev.Original == entry.Text
            ? (prev.Japanese, prev.Notes.Length > 0 ? prev.Notes : "SJPTS_AutoCorpusDsd", "", "")
            : ("", "", prev.Original, prev.Japanese);
    }

    private static void ReadPreviousOutput(string sourcePath, string[] lines, Dictionary<(string Key, SkyPatcherNameKind Kind), Previous> into)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var comments = new List<(SkyPatcherNameKind Kind, string Notes, string Original)>();
        for (var n = 0; n < lines.Length; n++)
        {
            if (SkyPatcherOutputFormat.TryParseComment(lines[n], out var kind, out var notes, out var original))
            {
                comments.Add((kind, notes, original));
                continue;
            }

            var line = SkyPatcherLineParser.Parse(lines[n], n + 1);
            if (line == null) { if (lines[n].Trim().Length > 0 && !lines[n].TrimStart().StartsWith(';')) comments.Clear(); continue; }

            occurrences[line.FilterPart] = occurrences.GetValueOrDefault(line.FilterPart) + 1;
            var key = $"{sourcePath}|{line.FilterPart}|{occurrences[line.FilterPart]}";
            foreach (var op in line.NameOps)
            {
                var comment = comments.LastOrDefault(c => c.Kind == op.Kind);
                if (comment.Original != null)
                    into[(key, op.Kind)] = new Previous(comment.Original, op.Text, comment.Notes);
            }
            comments.Clear();
        }
    }

    private static SkyPatcherSettings ReadSettings(Mo2Instance instance)
    {
        var relative = Path.Combine("SKSE", "Plugins", "SkyPatcher.ini");
        var candidates = new[] { Path.Combine(instance.OverwriteDir, relative) }
            .Concat(instance.EnabledModPriorityHighFirst.Select(mod => Path.Combine(instance.ModsDir, mod, relative)));
        var winner = candidates.FirstOrDefault(File.Exists);
        return winner == null ? SkyPatcherSettings.Default : SkyPatcherSettings.Parse(File.ReadAllLines(winner));
    }

    /// <summary>The MOD folder the file sits in; "Overwrite" for MO2's overwrite folder.</summary>
    private static string ProviderOf(Mo2Instance instance, string physical)
    {
        var underOverwrite = Path.GetRelativePath(instance.OverwriteDir, physical);
        if (!underOverwrite.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(underOverwrite)) return "Overwrite";

        var underMods = Path.GetRelativePath(instance.ModsDir, physical);
        return underMods.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
    }
}
