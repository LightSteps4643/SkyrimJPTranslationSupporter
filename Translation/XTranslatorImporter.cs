using System.Xml.Linq;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// v0.23.0: imports xTranslator's SSTXMLRessources export format (a per-plugin
/// XML of EditorID/RecordType/Source/Dest string quads — see
/// https://github.com/MGuffin/xTranslator) so a community translation someone
/// already made in xTranslator becomes precedent for this tool's own automatic
/// resolution, without hand-editing.
///
/// Fits the tool's redefined mission (see DESIGN_NOTES.md, "ツールのミッション
/// 再定義"): rather than compete with xTranslator as a translation ENGINE,
/// this is the orchestration layer that takes translation work done
/// elsewhere — by xTranslator, by a community, by AI, by hand — and gets it
/// into DSD, load-order-aware.
///
/// v0.33.0: re-shaped from a standalone `importxtranslator` command that
/// rewrote an ALREADY-generated translations.tsv in place, into a loader that
/// <see cref="PromptGenerator"/> calls at the START of every `translation` run,
/// returning corpus precedent directly. The old shape required a specific
/// three-step dance (run `translation` once to create the row skeleton, run
/// `importxtranslator` to fill it, run `translation` again to fold that back
/// in as precedent) that was easy to forget a step of — concretely, forgetting
/// the middle step after a fresh out_temp once cut this load order's
/// auto-resolved count in half. Loading directly from the XML every time also
/// fixes an ordering inversion the old shape had: because the standalone
/// command explicitly protected any row already filled by a NON-import source,
/// a candidate the tool's own <see cref="NameFallbackTranslator"/> had already
/// (mechanically) guessed at would block a genuine xTranslator answer arriving
/// later — the tool's own guess outranking real community/human data. Loading
/// straight into the corpus removes that inversion for free: this class's
/// output is merged into the corpus BEFORE <see cref="AutoTranslator"/> is
/// built, so it is checked at step ①, ahead of ④/③ and ahead of
/// NameFallbackTranslator entirely (see PromptGenerator's corpus assembly
/// order and DESIGN_HISTORY.md's v0.33.0 section for the agreed priority:
/// official Bethesda/DSD data, then this class's imports, then this session's
/// own fed-back precedent, then the reference glossary).
///
/// Matching key: (RecordType, EnglishText), NOT EditorID/FormId. xTranslator's
/// EDID is only reliably usable if this tool ALSO resolved a FormId->EditorID
/// mapping via Mutagen, which Translation deliberately never does (PickUpTarget
/// alone touches Mutagen/MO2 — see the stage-boundary note in DESIGN_NOTES.md).
/// The English text Mutagen actually read for this load order's winning
/// records — available directly from PickUpTarget's own candidate list, with
/// no need to wait for a generated translations.tsv — is the one thing both
/// sides of the match are guaranteed to agree on when the two haven't drifted
/// apart (a mod update, etc.) — same principle DSD itself relies on for the
/// record types it matches by original text rather than by index
/// (kRuntimeLegacy, see PickUpTarget/DsdTypeMatching.cs).
/// </summary>
public static class XTranslatorImporter
{
    /// <summary>
    /// Parses every XML in <paramref name="importDir"/> and returns the result as
    /// corpus precedent (SourceKind "imported"), ready to be merged into the same
    /// corpus <see cref="AutoTranslator"/> is built from. Matches only against
    /// (RecordType, EnglishText) pairs <paramref name="allCandidates"/> actually
    /// has this run — an entry with no match is the normal, expected case for an
    /// already-well-covered plugin (see WarnIfLikelyVersionMismatch's remarks),
    /// not something to log per-item.
    /// </summary>
    public static List<CorpusEntry> Load(string importDir, IReadOnlyList<Candidate> allCandidates, RunLog log, TraceLog? trace = null)
    {
        trace?.Info($"xTranslator import start: importDir={importDir}");
        var result = new List<CorpusEntry>();
        if (!Directory.Exists(importDir))
        {
            log.Line($"importフォルダが存在しない: {importDir}（xTranslatorインポート分なし）", $"Import folder does not exist: {importDir} (no xTranslator import)");
            trace?.Info($"Import folder does not exist: {importDir} — 0 entries");
            return result;
        }

        // v0.24.0: process oldest-file-first, MO2-style — a later (newer) file's
        // entries overwrite an earlier IMPORT's, on the theory that a more recently
        // published community translation supersedes an older one for the same
        // mod. Sort by last-write time, not filename, since download filenames
        // ("(1)", "(2)") don't reliably encode which is newer.
        // Recursive (v0.26.0): a zip extracted straight into the import folder
        // routinely puts the actual XML one or more subfolders down, alongside
        // readme/license files this scan simply never matches (*.xml only).
        var files = Directory.EnumerateFiles(importDir, "*.xml", SearchOption.AllDirectories)
            .OrderBy(f => File.GetLastWriteTimeUtc(f))
            .ToList();
        if (files.Count == 0)
        {
            log.Line($"importフォルダにXMLファイルなし: {importDir}（xTranslatorインポート分なし）", $"No XML files in import folder: {importDir} (no xTranslator import)");
            trace?.Info($"No XML files in import folder: {importDir} — 0 entries");
            return result;
        }
        trace?.Info($"Found {files.Count} XML file(s) ({importDir}, AllDirectories)");

        Console.WriteLine($"Found {files.Count} xTranslator XML file(s) in {importDir}");
        log.Section("xTranslatorインポート", "xTranslator import");
        log.Line($"importフォルダ: {importDir}", $"Import folder: {importDir}");
        log.Line($"XMLファイル: {files.Count}件", $"XML files: {files.Count}");
        log.Line("適用順（古い順。同じ行を複数ファイルが訳している場合、後段＝新しい方で上書き）:",
            "Application order (oldest first. When multiple files translate the same row, the later/newer one wins):");
        foreach (var file in files)
            log.Line($"  {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm:ss}  {Path.GetFileName(file)}",
                $"  {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm:ss}  {Path.GetFileName(file)}");

        var candidateKeys = new HashSet<(string, string)>(TupleComparer.Instance);
        foreach (var c in allCandidates)
            candidateKeys.Add((c.RecordType, c.CurrentText));

        // plugin -> (RecordType, EnglishText) -> Japanese. A later file's entry for
        // the same key simply overwrites the earlier one — plain dictionary
        // assignment reproduces the old "MO2-style" overwrite policy exactly,
        // since files are processed oldest-first.
        var byPlugin = new Dictionary<string, Dictionary<(string, string), string>>();

        var totalMatched = 0;
        var totalInvalidJapanese = 0;
        var totalNotFound = 0;

        foreach (var file in files)
        {
            trace?.Debug($"Read start: {file}");
            var (plugin, entries) = ParseFile(file, log, trace);
            if (plugin == null) { trace?.Warning($"Read skipped (plugin unresolved or parse failure): {file}"); continue; }
            trace?.Debug($"Read done: {file} (plugin={plugin}, entries={entries.Count})");

            if (!byPlugin.TryGetValue(plugin, out var map))
                byPlugin[plugin] = map = new Dictionary<(string, string), string>(TupleComparer.Instance);

            var matched = 0;
            var invalidJapanese = 0;
            var notFound = 0;

            foreach (var (recordType, english, japanese) in entries)
            {
                if (!LanguageDetector.ContainsJapanese(japanese))
                {
                    invalidJapanese++;
                    log.Detail("除外: xTranslator側のDestが日本語と判定できない（貼り付けミス・未翻訳残りの可能性）",
                        "Excluded: xTranslator's Dest doesn't look like Japanese (possibly a paste mistake or leftover untranslated text)",
                        $"[{recordType}] \"{english}\" -> \"{japanese}\"");
                    continue;
                }

                if (!candidateKeys.Contains((recordType, english)))
                {
                    notFound++;
                    continue; // normal case (already covered by DSD, mod not installed, or text drifted) — not logged per-item to avoid noise; totals are in the summary
                }

                map[(recordType, english)] = japanese;
                matched++;
            }

            Console.WriteLine($"[{Path.GetFileName(file)}] '{plugin}': {entries.Count} entries parsed -> " +
                $"{matched} matched this environment's candidates, {invalidJapanese} not valid Japanese (skipped), {notFound} not found");

            totalMatched += matched;
            totalInvalidJapanese += invalidJapanese;
            totalNotFound += notFound;
            trace?.Trace($"{Path.GetFileName(file)}: matched={matched} invalidJapanese={invalidJapanese} notFound={notFound}");

            WarnIfLikelyVersionMismatch(file, plugin, entries.Count, matched, notFound, log);
        }

        foreach (var (plugin, map) in byPlugin)
            foreach (var ((recordType, english), japanese) in map)
                result.Add(new CorpusEntry(english, japanese, plugin, "imported", recordType));

        Console.WriteLine($"xTranslator import: {result.Count} translation(s) loaded as corpus precedent (imported tier)");
        log.Section("xTranslatorインポート サマリ", "xTranslator import summary");
        log.Line($"コーパスへ取り込んだ訳（同一キーの重複を後発ファイルで解消した後）: {result.Count}件", $"Translations merged into corpus (after resolving same-key duplicates by newest file): {result.Count}");
        log.Line($"この環境の候補に一致した延べ件数: {totalMatched}", $"Total matched to this environment's candidates: {totalMatched}");
        log.Line($"日本語と判定できずスキップ: {totalInvalidJapanese}", $"Skipped, not recognized as Japanese: {totalInvalidJapanese}");
        log.Line($"この環境の候補に一致しなかった延べ件数（既存DSDでカバー済み・MOD未導入・原文相違等）: {totalNotFound}", $"Total not matched to this environment's candidates (already DSD-covered / mod not installed / original text drifted, etc.): {totalNotFound}");
        trace?.Info($"xTranslator import done: {result.Count} entries (matched={totalMatched} invalidJapanese={totalInvalidJapanese} notFound={totalNotFound})");

        return result;
    }

    /// <summary>Below this match rate (entries that found a matching candidate this
    /// run, whether or not a later file went on to overwrite them, divided by the
    /// file's total entry count) a file is flagged as a likely wrong-version
    /// import, not because the match rate is meaningful in isolation — "not
    /// found" is the EXPECTED, healthy outcome for most entries of an
    /// already-well-covered plugin (see DESIGN_NOTES.md v0.23.0: 1771/2032 "not
    /// found" on the very first, entirely legitimate Book Covers Skyrim import)
    /// — but because a genuinely version-matched file should still land on SOME
    /// candidate. A file that can't find almost ANY of them is the actual
    /// anomaly: text drifted out from under it, i.e. a different plugin version
    /// than this load order's.</summary>
    private const double VersionMismatchWarningThreshold = 0.10;

    /// <summary>Below this many parsed entries, a low match rate is just as
    /// likely to mean "this file only covers a handful of specific strings"
    /// as "wrong version" — not enough signal either way, so it's not worth
    /// warning about.</summary>
    private const int VersionMismatchMinEntries = 20;

    private static void WarnIfLikelyVersionMismatch(string file, string plugin, int totalEntries, int matched, int notFound, RunLog log)
    {
        if (totalEntries < VersionMismatchMinEntries) return;
        var matchRatio = (double)matched / totalEntries;
        if (matchRatio >= VersionMismatchWarningThreshold) return;

        var message = $"'{Path.GetFileName(file)}'（対象: {plugin}）は {totalEntries} 件中 {matched} 件しかこの環境の候補と一致しませんでした" +
            $"（一致率 {matchRatio:P0}）。導入しているMODのバージョンと、この翻訳ファイルが作られたバージョンが異なっている可能性があります。";
        Console.Error.WriteLine($"[WARNING] {message}");
        log.Line($"[WARNING] {message}", $"[WARNING] {message}");
    }

    /// <summary>Parses one SSTXMLRessources file. Returns (null, []) and logs a
    /// warning on any structural problem — a malformed input file must never
    /// crash the whole batch import.</summary>
    private static (string? Plugin, List<(string RecordType, string English, string Japanese)> Entries) ParseFile(string path, RunLog log, TraceLog? trace = null)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] failed to parse '{path}': {ex.Message}");
            log.Line($"パース失敗: {path} — {ex.Message}", $"Parse failed: {path} — {ex.Message}");
            trace?.Error($"XML parse failed: {path}", ex);
            return (null, new());
        }

        var plugin = doc.Root?.Element("Params")?.Element("Addon")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(plugin))
        {
            Console.Error.WriteLine($"[warn] '{path}' has no <Params><Addon> — skipped (not a recognizable SSTXMLRessources file)");
            log.Line($"対象プラグイン不明のためスキップ: {path}（<Params><Addon>が見つからない）", $"Skipped, target plugin unresolved: {path} (<Params><Addon> not found)");
            trace?.Warning($"<Params><Addon> not found: {path}");
            return (null, new());
        }

        var entries = new List<(string, string, string)>();
        foreach (var str in doc.Root?.Element("Content")?.Elements("String") ?? Enumerable.Empty<XElement>())
        {
            var rec = str.Element("REC")?.Value.Trim();
            var source = str.Element("Source")?.Value;
            var dest = str.Element("Dest")?.Value;
            if (string.IsNullOrWhiteSpace(rec) || string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest)) continue;

            // xTranslator's "BOOK:FULL" -> this tool's "BOOK FULL".
            // v0.59.x (GitHub issue #2): a SwapBookDescCnam step used to live
            // here, compensating for what was believed to be xTranslator
            // labeling BOOK's DESC/CNAM the opposite way from this tool. That
            // was backwards — xTranslator's DESC/CNAM already matched
            // Mutagen/DSD's real subrecord signatures (BOOK DESC = the book's
            // body, BOOK CNAM = its separate short description); this tool's
            // OWN PickUpTarget/ExtraTranslatableFields.cs had them swapped.
            // Now that the root mapping is fixed there, xTranslator's labels
            // need no adjustment on import.
            var recordType = rec.Replace(':', ' ');
            entries.Add((recordType, source, dest));
        }
        return (plugin, entries);
    }

    // (RecordType, EnglishText) equality must be exact (not culture-sensitive) — this
    // has to match what Mutagen actually read, not a fuzzy human-similarity notion.
    private sealed class TupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly TupleComparer Instance = new();
        public bool Equals((string, string) a, (string, string) b) => a.Item1 == b.Item1 && a.Item2 == b.Item2;
        public int GetHashCode((string, string) t) => HashCode.Combine(t.Item1, t.Item2);
    }
}
