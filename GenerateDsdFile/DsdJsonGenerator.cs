using SkyrimJPStringPatcher.Core;
using static SkyrimJPStringPatcher.Core.TsvEscaping;

namespace SkyrimJPStringPatcher.GenerateDsdFile;

/// <summary>
/// GenerateDsdFile: 翻訳結果をDSDのjsonファイルとして生成する。Translationが出力した
/// 「翻訳テンプレートTSV」（ユーザーが Japanese 列を埋めたもの）だけを入力とし、
/// Mutagen/MO2には一切依存しない。空欄（まだ翻訳していない行）はスキップする。
/// 入力は単一のTSVファイル、または複数のtranslations.tsvを含むディレクトリの
/// どちらでも受け付ける（後者は再帰的に探索し、まとめて1つのOutputに統合する）。
/// </summary>
public static class DsdJsonGenerator
{
    public static void Run(string translationsInputPath, string outputDir, RunLog log, TraceLog? trace = null)
    {
        trace?.Info($"Input file resolution start: {translationsInputPath}");
        var files = ResolveInputFiles(translationsInputPath);
        Console.WriteLine($"Found {files.Count} translation file(s) to process.");
        trace?.Info($"Input file resolution done: {files.Count} file(s)");

        var allRows = new List<TranslationRow>();
        foreach (var file in files)
        {
            trace?.Trace($"Read start: {file}");
            var rows = ReadTranslations(file);
            allRows.AddRange(rows);
            trace?.Trace($"Read done: {file} ({rows.Count} rows)");
        }

        var translated = allRows.Where(r => !string.IsNullOrWhiteSpace(r.Japanese)).ToList();
        var skipped = allRows.Count - translated.Count;
        Console.WriteLine($"Read {allRows.Count} rows total ({translated.Count} translated, {skipped} still blank — skipped)");
        trace?.Info($"All files read: {allRows.Count} rows total (translated {translated.Count} / blank {skipped})");
        if (trace != null)
            foreach (var r in allRows.Where(r => string.IsNullOrWhiteSpace(r.Japanese)))
                trace.Trace($"Skip {r.FormId} [{r.RecordType}]: untranslated (\"{r.EnglishText}\")");

        log.Section("入力", "Input");
        log.Line($"入力パス: {translationsInputPath}", $"Input path: {translationsInputPath}");
        log.Line($"読み込んだ翻訳ファイル: {files.Count}件", $"Translation files read: {files.Count}");
        log.Line($"読み込んだ行: {allRows.Count}（訳あり {translated.Count} / 未翻訳のため対象外 {skipped}）",
            $"Rows read: {allRows.Count} (translated {translated.Count} / excluded as untranslated {skipped})");

        // Untranslated rows are the NORMAL state for work in progress, not an
        // anomaly, so they're counted rather than listed — but which plugins are
        // still outstanding is exactly what a person wants to know afterwards.
        foreach (var g in allRows.Where(r => string.IsNullOrWhiteSpace(r.Japanese))
                     .GroupBy(r => r.WinningPlugin)
                     .OrderByDescending(g => g.Count()))
        {
            log.Detail("対象外: Japanese列が空（未翻訳のためDSDに含めていない）", "Excluded: Japanese column is empty (untranslated, not included in DSD)", $"{g.Key}（{g.Count()}件）");
        }

        var entriesByPlugin = new Dictionary<string, List<DsdEntry>>();
        var notJapaneseWarnings = 0;
        var badFormIdWarnings = 0;
        var modifiedByUserNonJapaneseNotices = 0;

        foreach (var row in translated)
        {
            // v0.50.1: AutoCorpusOverride rows come from Data/phrase_overrides.tsv —
            // a human-curated table that DELIBERATELY keeps some values non-Japanese
            // (v0.48.1: "OK"->"OK", "pts"->"pt", matching Japanese UI convention for
            // short game-UI vocabulary). The check below exists to catch xTranslator
            // paste mistakes/missed translations, not to second-guess a curated
            // override — without this exemption, e.g. "pts"->"pt" was silently
            // dropped from the DSD output, leaving the untranslated "pts" on screen
            // despite phrase_overrides.tsv having the correct answer all along.
            //
            // v0.56.0: ModifiedByUser (a human's own edit via TranslationDetailForm)
            // gets the same exemption, for the same reason — a person who typed
            // this value in and saved it decided it belongs there as-is, even if it
            // doesn't look like Japanese (e.g. a proper noun, a number, "OK"). This
            // check exists to catch machine mistakes, not to second-guess a
            // deliberate human decision — so it's included as-is either way. Unlike
            // AutoCorpusOverride (a curated table where a non-Japanese value is
            // fully expected, nothing to flag), a ModifiedByUser row still gets a
            // logged NOTE (not an exclusion, not a [warn]) — a manual edit could
            // just as easily be a genuine oversight as a deliberate choice, so it's
            // worth surfacing for the user to spot-check later without second-
            // guessing it now.
            if (!LanguageDetector.ContainsJapanese(row.Japanese) && row.Notes is not ("AutoCorpusOverride" or "ModifiedByUser"))
            {
                notJapaneseWarnings++;
                if (notJapaneseWarnings <= 20)
                    Console.Error.WriteLine($"[warn] '{row.FormId}' Japanese column doesn't look like Japanese — skipping: \"{row.Japanese}\"");
                log.Detail("除外: Japanese列に日本語が含まれていない（訳し忘れ・貼り付けミスの可能性）",
                    "Excluded: the Japanese column doesn't contain Japanese (possibly a missed translation or a paste mistake)",
                    $"{row.FormId} [{row.RecordType}] \"{row.EnglishText}\" → \"{row.Japanese}\"");
                trace?.Trace($"Exclude {row.FormId} [{row.RecordType}]: Japanese column doesn't look like Japanese (\"{row.Japanese}\")");
                continue;
            }

            if (!LanguageDetector.ContainsJapanese(row.Japanese) && row.Notes == "ModifiedByUser")
            {
                // v0.55.2: promoted to a console [warn] too (previously log-file-
                // only) — a user who ran generatedsdfile and never opened
                // generatedsdfile.log had no way to notice this at all.
                modifiedByUserNonJapaneseNotices++;
                if (modifiedByUserNonJapaneseNotices <= 20)
                    Console.Error.WriteLine($"[warn] '{row.FormId}' manually-edited translation doesn't look like Japanese — keeping as-is (not excluded): \"{row.Japanese}\"");
                log.Detail("情報: 手動編集の訳文に日本語が含まれていないが、そのまま出力する（意図的な可能性があるため除外しない）",
                    "Note: a manually-edited translation doesn't contain Japanese — included as-is (not excluded, since this may be intentional)",
                    $"{row.FormId} [{row.RecordType}] \"{row.EnglishText}\" → \"{row.Japanese}\"");
                trace?.Trace($"Note {row.FormId} [{row.RecordType}]: ModifiedByUser translation doesn't look like Japanese, including as-is (\"{row.Japanese}\")");
            }

            if (!TryConvertFormId(row.FormId, out var dsdFormId))
            {
                badFormIdWarnings++;
                if (badFormIdWarnings <= 20)
                    Console.Error.WriteLine($"[warn] could not parse FormId '{row.FormId}' — skipping");
                log.Detail("除外: FormIdを解釈できない（行が壊れている可能性）",
                    "Excluded: could not parse the FormId (the row may be corrupted)",
                    $"\"{row.FormId}\" [{row.RecordType}] \"{row.EnglishText}\"");
                trace?.Trace($"Exclude [{row.RecordType}]: could not parse FormId \"{row.FormId}\"");
                continue;
            }

            if (!entriesByPlugin.TryGetValue(row.WinningPlugin, out var list))
            {
                list = new List<DsdEntry>();
                entriesByPlugin[row.WinningPlugin] = list;
            }

            trace?.Trace($"Include {dsdFormId} [{row.RecordType}] -> {row.WinningPlugin}: \"{row.EnglishText}\" -> \"{row.Japanese}\" (status={(string.IsNullOrWhiteSpace(row.Notes) ? "TranslationProposed" : row.Notes)})");
            list.Add(new DsdEntry
            {
                EditorId = row.EditorId,
                FormId = dsdFormId,
                Index = row.Index,
                Type = row.RecordType,
                Original = row.EnglishText,
                String = row.Japanese,
                Status = string.IsNullOrWhiteSpace(row.Notes) ? "TranslationProposed" : row.Notes,
            });
        }

        if (notJapaneseWarnings > 20) Console.Error.WriteLine($"[warn] ...and {notJapaneseWarnings - 20} more non-Japanese rows skipped");
        if (badFormIdWarnings > 20) Console.Error.WriteLine($"[warn] ...and {badFormIdWarnings - 20} more unparseable FormId rows skipped");
        if (modifiedByUserNonJapaneseNotices > 20) Console.Error.WriteLine($"[warn] ...and {modifiedByUserNonJapaneseNotices - 20} more manually-edited non-Japanese rows kept as-is");
        trace?.Debug($"Conversion done: {entriesByPlugin.Values.Sum(l => l.Count)} entries / excluded: non-Japanese {notJapaneseWarnings} bad-FormId {badFormIdWarnings}");

        trace?.Info($"Write start: {outputDir} ({entriesByPlugin.Count} plugin(s))");
        DsdWriter.WriteAll(outputDir, entriesByPlugin, trace);

        var totalWritten = entriesByPlugin.Values.Sum(l => l.Count);
        Console.WriteLine($"Wrote {totalWritten} DSD entries across {entriesByPlugin.Count} plugin folder(s) to: {outputDir}");
        trace?.Info($"Write done: {outputDir} (total {totalWritten} DSD entries, {entriesByPlugin.Count} plugin folder(s))");

        log.Section("処理サマリ", "Processing summary");
        log.Line($"出力したDSDエントリ: {totalWritten}", $"DSD entries written: {totalWritten}");
        log.Line($"出力先プラグインフォルダ数: {entriesByPlugin.Count}", $"Output plugin folders: {entriesByPlugin.Count}");
        log.Line($"出力先: {outputDir}", $"Output path: {outputDir}");
        if (modifiedByUserNonJapaneseNotices > 0)
            log.Line($"手動編集で日本語以外の訳文のまま出力: {modifiedByUserNonJapaneseNotices}件（除外はしていない。詳細は下記セクション）",
                $"Manually-edited rows output as-is despite not containing Japanese: {modifiedByUserNonJapaneseNotices} (not excluded — see the section below for details)");
        log.Line("※出力された各エントリの内容は、出力先の .json を参照",
            "* See the output .json for each entry's actual content");

        log.Section("特殊な出力処理の記録", "Special output handling notes");
        log.Line("ファイル名: プラグイン名ではなく固定名 SkyrimJPStringPatcher.json で出力している。",
            "File name: a fixed name, SkyrimJPStringPatcher.json, not the plugin name.");
        log.Line("           既存の翻訳MODが同名ファイルを持つ場合にVFSで上書きし、既存訳を消してしまう",
            "           An existing translation mod with a same-named file would get overwritten in the VFS,");
        log.Line("           事故が実際に起きたため（DESIGN_NOTESの該当節を参照）",
            "           erasing its translation — this actually happened once (see the DESIGN_NOTES section)");
        log.Line("status欄 : Notes列が空の行は TranslationProposed、埋まっている行はその値をそのまま使用",
            "status   : rows with an empty Notes column get TranslationProposed; otherwise the Notes value is used as-is");
        log.Line("FormId   : Mutagenの \"XXXXXX:Plugin.esp\" 形式を、DSDが要求する \"XXXXXX|Plugin.esp\" に変換",
            "FormId   : converted from Mutagen's \"XXXXXX:Plugin.esp\" form to the \"XXXXXX|Plugin.esp\" DSD requires");
    }

    /// <summary>Accepts a single .tsv file, or a directory to search recursively
    /// for "translations.tsv" files — the only name <see cref="PromptGenerator"/>
    /// ever writes, whether from RunOne (single-plugin) or RunAll (whole load
    /// order).</summary>
    private static List<string> ResolveInputFiles(string path)
    {
        if (File.Exists(path)) return new List<string> { path };

        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "translations.tsv", SearchOption.AllDirectories)
                .OrderBy(f => f).ToList();

        throw new FileNotFoundException($"Input path not found (not a file or directory): {path}");
    }

    /// <summary>Candidate.FormId is Mutagen's "XXXXXX:Plugin.esp" (colon) format;
    /// DSD's own json wants "XXXXXX|Plugin.esp" (pipe).</summary>
    private static bool TryConvertFormId(string mutagenFormId, out string dsdFormId)
    {
        dsdFormId = "";
        var idx = mutagenFormId.IndexOf(':');
        if (idx <= 0) return false;
        dsdFormId = $"{mutagenFormId[..idx]}|{mutagenFormId[(idx + 1)..]}";
        return true;
    }

    private sealed record TranslationRow(string FormId, string WinningPlugin, string RecordType, string EnglishText, string Japanese, string Notes, int Index, string EditorId);

    private static List<TranslationRow> ReadTranslations(string path)
    {
        var result = new List<TranslationRow>();
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 6) continue;
            var index = parts.Length > 6 && int.TryParse(parts[6], out var i) ? i : 0;
            var editorId = parts.Length > 7 ? Unescape(parts[7]) : "";
            result.Add(new TranslationRow(parts[0], parts[1], parts[2], Unescape(parts[3]), Unescape(parts[4]), Unescape(parts[5]), index, editorId));
        }
        return result;
    }
}
