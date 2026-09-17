using System.Text;
using SkyrimJPStringPatcher.Core;

namespace SJPTS_InterfaceText;

/// <summary>Key/English/Japanese/Resolved/Notes row — the intermediate working
/// file, analogous to the existing ESP pipeline's `translations.tsv`. Persists
/// across repeated `translate` runs (already-resolved rows are not re-sent to
/// the LLM) and is only reset by an explicit `detect` run, mirroring the
/// existing tool's "①のみ discardUserEdits" behavior.</summary>
/// <param name="Notes">2026-09-12: mirrors translations.tsv's own Notes column
/// (ESP side) — "SJPTS_TranslationLocalLlm"/"SJPTS_TranslationCloudLlm" for a normal LLM
/// resolution, "SJPTS_TranslationLocalLlmNoJapanese"/"SJPTS_TranslationCloudLlmNoJapanese"
/// for a response that came back but contained no Japanese (still marked
/// Resolved=true — see InterfaceTextPromptGenerator.ApplyLlmStep's remarks for
/// why treating this as a failure was wrong), "SJPTS_ModifiedByUser" for a human's
/// own edit via InterfaceTextDetailForm. Empty for a row resolved from the
/// load order's own/imported _japanese.txt, the MOD-name-exact-match
/// exclusion, or one still unresolved.</param>
/// <param name="TranslationCheck">2026-09-18: mirrors the ESP side's
/// translations.tsv "TranslationCheck" column (TranslationQualityChecker) —
/// set ONLY when ⑤ローカルLLM/⑥生成AI翻訳 actually produces this row's
/// Japanese (InterfaceTextPromptGenerator.ApplyLlmStep), left "" for every
/// other resolution path and once a human confirms/edits the row
/// (Notes=SJPTS_ModifiedByUser) — see AutoTranslationResult's own remarks for
/// the full rationale (shared with the ESP pipeline).</param>
public sealed record InterfaceTranslationRow(string Key, string English, string Japanese, bool Resolved, string Notes = "", string TranslationCheck = "");

public static class InterfaceTranslationsTsv
{
    public static List<InterfaceTranslationRow> Read(string path)
    {
        var rows = new List<InterfaceTranslationRow>();
        if (!File.Exists(path)) return rows;

        foreach (var line in File.ReadAllLines(path, Encoding.UTF8).Skip(1)) // skip header
        {
            if (line.Length == 0) continue;
            var cols = line.Split('\t');
            if (cols.Length < 4) continue;
            var notes = cols.Length >= 5 ? cols[4] : "";
            var translationCheck = cols.Length >= 6 ? cols[5] : "";
            rows.Add(new InterfaceTranslationRow(
                TsvEscaping.Unescape(cols[0]), TsvEscaping.Unescape(cols[1]), TsvEscaping.Unescape(cols[2]),
                cols[3] == "1", TsvEscaping.Unescape(notes), TsvEscaping.Unescape(translationCheck)));
        }
        return rows;
    }

    public static void Write(string path, IReadOnlyList<InterfaceTranslationRow> rows)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        writer.NewLine = "\n";
        writer.WriteLine("Key\tEnglish\tJapanese\tResolved\tNotes\tTranslationCheck");
        foreach (var row in rows)
            writer.WriteLine($"{TsvEscaping.Escape(row.Key)}\t{TsvEscaping.Escape(row.English)}\t{TsvEscaping.Escape(row.Japanese)}\t{(row.Resolved ? "1" : "0")}\t{TsvEscaping.Escape(row.Notes)}\t{TsvEscaping.Escape(row.TranslationCheck)}");
    }
}
