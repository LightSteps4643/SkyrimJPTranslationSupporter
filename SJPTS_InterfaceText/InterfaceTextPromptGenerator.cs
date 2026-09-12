using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SJPTS_InterfaceText;

/// <summary>
/// LLM batch-translation step for Interface\Translations entries — copied from
/// (and trimmed down against) <c>SkyrimJPStringPatcher.Translation.PromptGenerator.
/// ApplyLlmStep</c> (2026-09-11; see design/interface_translations.md), which is
/// deliberately NOT reused by reference: it's `private` inside a class built
/// around ESP candidates, and its public API (RunOne/RunMany/RunAll,
/// WritePrompt, corpus/candidate TSV loading) is entirely ESP-specific.
///
/// Copied over and KEPT as-is (this is the actual hard-won engineering — batch
/// splitting by char limit not item count, per-batch circuit-breaker checks,
/// dedup-by-identical-text before sending, defensive response-side
/// quote/tag stripping, per-candidate "not found" vs "no Japanese in answer"
/// logging): the core loop shape of ApplyLlmStep, ExtractTaggedSource,
/// StripSurroundingQuotes, StripTargetTags.
///
/// Removed as ESP-specific (does not apply to plain $Key/English UI strings):
/// RecordType/Context display, corpus "Reference examples"/"Known translations"
/// hints (no vanilla corpus for UI text — design decision), mod_glossary word
/// decomposition, and MultilineBreakMarker handling — Interface\Translations
/// values cannot contain a literal newline in the first place (the format
/// itself is one value per line), so the multiline-candidate problem this
/// existed for cannot occur here.
///
/// 2026-09-12: now matches the original's full 3-way outcome (auto-resolved /
/// low-confidence "NoJapanese" tag for human review / unresolved) — the
/// earlier 2-way simplification (documented here until now) was undone once
/// <see cref="InterfaceTranslationRow"/> gained its own Notes column and
/// <c>InterfaceTextDetailForm</c> (the GUI review grid) existed to act as the
/// review surface. "Response came back but wasn't Japanese" is accepted as
/// Resolved=true with a dedicated Notes tag (methodTag+"NoJapanese"), not
/// treated the same as "not found at all" anymore.
/// </summary>
public static class InterfaceTextPromptGenerator
{
    /// <summary>Mirrors <c>PromptGenerator.DefaultLlmBatchCharLimit</c> (cloud AI).</summary>
    public const int DefaultLlmBatchCharLimit = 12_000;

    /// <summary>Mirrors <c>PromptGenerator.DefaultLocalLlmBatchCharLimit</c> (local LLM).</summary>
    public const int DefaultLocalLlmBatchCharLimit = 3_000;

    /// <summary>Wraps each unique English text on the "Target:" line so the
    /// model's answer can be matched back by exact content (not position) —
    /// same tag pair and same rationale as the original (unlikely to collide
    /// with real game/UI text; confirmed the model reliably distinguishes it
    /// from a candidate's own embedded markup).</summary>
    private const string TargetTagOpen = "<SJPTS_TARGET>";
    private const string TargetTagClose = "</SJPTS_TARGET>";

    private const string Instruction =
        "Below are UI strings from a Skyrim SE mod's settings menu (MCM or similar), not yet translated into Japanese.\n" +
        "These are the mod's OWN original UI text — do not assume they match any vanilla Skyrim terminology; judge each\n" +
        "string on its own meaning. Many are short (toggle labels, headers, option names) with little surrounding\n" +
        "context — the \"Key(s)\" line for each string lists the $Key name(s) it's used under, which often encode a\n" +
        "feature/page hierarchy (e.g. $ModName_FeatureName_OptionText); use that as a hint to what the string is for.\n\n" +
        "Translate EVERY word into Japanese, including simple/common ones — do not leave any English word in your\n" +
        "answer, and do not add the original English in parentheses. For a proper noun you don't recognize, give your\n" +
        "best phonetic katakana rendering rather than leaving it in English.\n\n" +
        "Some strings contain a placeholder token — for example {0}, %s, %.1f, or an angle-bracket tag like <font>.\n" +
        "Copy any such token EXACTLY unchanged (same characters, same position in the sentence) — never translate,\n" +
        "reword, or drop it.\n\n" +
        "Each string to translate below is wrapped in " + TargetTagOpen + " and " + TargetTagClose + " tags, like\n" +
        "this: - Target: " + TargetTagOpen + "example text" + TargetTagClose + "\n" +
        "Translate ONLY the text between these tags. Copy that exact text (unchanged, including any punctuation,\n" +
        "quotes, or markup it may contain, and including case).\n\n" +
        "Output ONE line per string below: the English source WRAPPED IN THE SAME " + TargetTagOpen + "..." + TargetTagClose + "\n" +
        "tags shown above (e.g. " + TargetTagOpen + "example text" + TargetTagClose + "), then a single actual tab\n" +
        "character (press Tab — do NOT write the four characters \"<TAB>\" as literal text), then the Japanese\n" +
        "translation. No other lines, no header row, no numbering, no preamble or explanation, and nothing other\n" +
        "than whitespace before the opening tag or after the closing tag — no leading \"- \" or the word \"Target:\".\n" +
        "Keeping the tags in your answer's source column is required: it is how your answer is matched back to the\n" +
        "string you translated, even if that string itself happens to contain the word \"Target:\" or look similar\n" +
        "to this instruction's own formatting.\n\n";

    /// <summary>
    /// Translates every not-yet-resolved (Key, English) pair, deduplicated by
    /// English text (an identical string used under several keys is asked
    /// about once and the answer applied to all of them — real cost savings,
    /// confirmed against TrueHUD which reuses short labels across many keys),
    /// batched under <paramref name="batchCharLimit"/> characters per call,
    /// with the same circuit-breaker/no-auto-retry/per-candidate-logging
    /// behavior as the ESP pipeline's own ⑤/⑥ step. Mirrors
    /// <c>PromptGenerator.ApplyLlmStep</c> almost line-for-line; see that
    /// method's remarks for the reasoning behind char-limit (not item-count)
    /// batching.
    /// </summary>
    /// <returns>Key -&gt; (Japanese, Notes) for every key accepted as resolved
    /// (across every key sharing a resolved text, not just the one used in
    /// the prompt) — Notes is "Translation{Local,Cloud}Llm" for a normal
    /// resolution, or that plus "NoJapanese" for a response that came back
    /// but contained no Japanese (still accepted, flagged for review). A key
    /// not present in the result stays unresolved — never guessed.</returns>
    /// <param name="modWorkDir">Where to write each batch's actual sent prompt
    /// (instruction + candidate blocks, byte-for-byte what goes into
    /// <see cref="ITextTranslator.TryTranslate"/>) as "prompt_{providerLabel}
    /// _batch{N}_of_{total}.txt" — a debugging aid (2026-09-12 design
    /// discussion) distinct from the ESP CLI's own prompt.txt (that one is a
    /// human AI-chat handoff for whatever's STILL unresolved after the LLM
    /// step; this one is a record of what WAS actually sent, win or lose).
    /// Stale files under this SAME provider's prefix are cleared first — not
    /// the other provider's — so a translate run that does ローカルLLM then
    /// 生成AI（クラウド） in two calls doesn't have the second call wipe out
    /// the first's files (both would otherwise match a shared "prompt_batch*"
    /// pattern).</param>
    /// <param name="providerLabel">"localLLM" or "cloudLLM" — which of this
    /// mod's two independent translate calls this is, mirroring the ESP CLI's
    /// own step 5 vs step 6 distinction (Translation/PromptGenerator.cs).</param>
    public static Dictionary<string, (string Japanese, string Notes)> ApplyLlmStep(
        IReadOnlyList<(string Key, string English)> pending, ITextTranslator translator,
        string modName, RunLog log, TraceLog? trace, int batchCharLimit, string modWorkDir, string providerLabel)
    {
        var result = new Dictionary<string, (string Japanese, string Notes)>(StringComparer.Ordinal);
        if (pending.Count == 0) return result;

        // Notes値はESP側（Translation/PromptGenerator.cs）の命名規則をそのまま
        // 踏襲——"TranslationLocalLlm"/"TranslationCloudLlm"。
        var methodTag = providerLabel == "localLLM" ? "TranslationLocalLlm" : "TranslationCloudLlm";

        var promptFilePrefix = $"prompt_{providerLabel}_batch";
        foreach (var stale in Directory.Exists(modWorkDir) ? Directory.EnumerateFiles(modWorkDir, $"{promptFilePrefix}*_of_*.txt") : Enumerable.Empty<string>())
            File.Delete(stale);

        // Dedup by identical English text — same rationale as PromptGenerator's
        // own byText grouping: a meaningful share of a mod's strings repeat
        // (short toggle/option labels), and asking once costs less while
        // making a divergent translation of the same text structurally
        // impossible within this mod.
        var byText = pending.GroupBy(e => e.English, StringComparer.Ordinal).ToList();

        var blocks = byText.Select(g => (Group: g, Block: BuildBlock(g))).ToList();

        var batches = new List<List<(IGrouping<string, (string Key, string English)> Group, string Block)>>();
        var current = new List<(IGrouping<string, (string Key, string English)> Group, string Block)>();
        var currentLength = 0;
        foreach (var item in blocks)
        {
            if (current.Count > 0 && currentLength + item.Block.Length > batchCharLimit)
            {
                batches.Add(current);
                current = new List<(IGrouping<string, (string Key, string English)> Group, string Block)>();
                currentLength = 0;
            }
            current.Add(item);
            currentLength += item.Block.Length;
        }
        if (current.Count > 0) batches.Add(current);

        log.DetailAndReport("生成AI翻訳のバッチ呼び出し件数", "batched call count",
            $"[{modName}]  未解決{byText.Count}件（重複排除後）を{batches.Count}回のバッチ呼び出しに分割（1回あたりの文字数上限: {batchCharLimit}）",
            $"[{modName}] {byText.Count} unique unresolved string(s), {batches.Count} batched call(s), char limit {batchCharLimit}");

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            var batchLabel = batches.Count > 1 ? $"バッチ{batchIndex + 1}/{batches.Count}" : "バッチ";

            if (translator.CircuitOpen)
            {
                var remainingBatches = batches.Count - batchIndex;
                var remainingCandidates = batches.Skip(batchIndex).Sum(b => b.Count);
                log.DetailAndReport("生成AI翻訳のサーキットブレーカー作動", "circuit breaker open",
                    $"[{modName}]  連続失敗のため残り{remainingBatches}バッチ（{remainingCandidates}件）をまとめてスキップしました",
                    $"[{modName}] circuit breaker open — skipping remaining {remainingBatches} batch(es) ({remainingCandidates} candidate(s))");
                break;
            }

            var promptBuilder = new System.Text.StringBuilder(Instruction);
            foreach (var (_, block) in batch) promptBuilder.Append(block);
            var promptText = promptBuilder.ToString();

            Directory.CreateDirectory(modWorkDir);
            var promptBatchPath = Path.Combine(modWorkDir, $"{promptFilePrefix}{batchIndex + 1}_of_{batches.Count}.txt");
            File.WriteAllText(promptBatchPath, promptText, new System.Text.UTF8Encoding(false));

            var response = translator.TryTranslate(promptText, out var error);
            if (response == null)
            {
                log.DetailAndReport("生成AI翻訳のバッチが失敗（エラー理由）", "a batch failed (error reason)",
                    $"[{modName}]  {batchLabel}（{batch.Count}件）が失敗しました  ({error})",
                    $"[{modName}] {batchLabel} failed ({batch.Count} candidate(s)): {error}");
                continue; // 自動リトライしない — 次のバッチへ（既存と同じ方針）
            }

            var byLine = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in response.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                var tabIndex = line.IndexOf('\t');
                if (tabIndex < 0) continue;
                var sourceColumnRaw = line[..tabIndex];
                var source = ExtractTaggedSource(sourceColumnRaw);
                if (source.Length == 0)
                {
                    var issue = ClassifyTaggedSourceIssue(sourceColumnRaw);
                    log.Detail("応答の1行がタグ形式を満たさずスキップ",
                        "a response line didn't satisfy the tag format and was skipped",
                        $"[{modName}]  理由: {issue} / 該当行: \"{sourceColumnRaw.Trim()}\"");
                }
                var japanese = StripSurroundingQuotes(StripTargetTags(line[(tabIndex + 1)..].Trim()));
                if (source.Length > 0 && japanese.Length > 0)
                    byLine[source] = japanese; // 同じキーが複数行あれば最後の行を採用
            }

            var anyUnresolvedInBatch = false;
            foreach (var (group, _) in batch)
            {
                if (!byLine.TryGetValue(group.Key.Trim(), out var japanese))
                {
                    foreach (var (key, english) in group)
                    {
                        log.DetailAndReport("解決できなかった候補（モデルの応答形式を解釈できず）",
                            "could not resolve this candidate (model's response wasn't in an interpretable format)",
                            $"[{modName}]  \"{key}\": \"{english}\"",
                            $"[{modName}] could not resolve \"{key}\" (model's response wasn't in an interpretable format)");
                    }
                    anyUnresolvedInBatch = true;
                    continue;
                }

                if (!LanguageDetector.ContainsJapanese(japanese))
                {
                    // 2026-09-12: ESP側（DsdJsonGenerator.cs）と同じ結論——モデルが
                    // 「翻訳不要」と正しく判断した結果（例: "Ok"→"OK"）と、本当に
                    // 翻訳を誤っただけのケースを、この段階では機械的に区別できない。
                    // 未解決のまま毎回リトライさせ続けるより、専用タグ（methodTag+
                    // "NoJapanese"）を付けてResolved=trueとして採用し、間違って
                    // いれば人間がInterfaceTextDetailFormで見つけて直せるようにする
                    // （除外せず、情報ログのみ残す——ModifiedByUserと同じ「人間が
                    // 後から見分けられる形で受理する」扱い）。
                    var noJapaneseTag = methodTag + "NoJapanese";
                    foreach (var (key, english) in group)
                    {
                        log.DetailAndReport("応答は得られたが訳文に日本語が含まれない（要レビューとして受理）",
                            "response parsed but the translation contains no Japanese (accepted, flagged for review)",
                            $"[{modName}]  \"{key}\": \"{english}\" → \"{japanese}\"",
                            $"[{modName}] \"{key}\": response contained no Japanese — accepted, flagged for review: \"{japanese}\"");
                        result[key] = (japanese, noJapaneseTag);
                    }
                    continue;
                }

                foreach (var (key, _) in group) result[key] = (japanese, methodTag);
            }

            // 2026-09-12: このバッチで1件でも未解決が残った場合のみ、モデルからの
            // 生レスポンス全文をtrace.logへ1回だけ残す（バッチ全体で1回、候補ごとに
            // 重複させない）——上のper-line診断（ClassifyTaggedSourceIssue）で
            // 大抵の原因は分かるが、それでも特定できない場合の最終手段として。
            if (anyUnresolvedInBatch)
                trace?.Warning($"[{modName}] {batchLabel}: raw response for the batch with unresolved candidate(s):\n{response}");
        }

        return result;
    }

    private static string BuildBlock(IGrouping<string, (string Key, string English)> group)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"- Target: {TargetTagOpen}{group.Key}{TargetTagClose}\n");
        sb.Append($"  Key(s): {string.Join(" / ", group.Select(e => e.Key))}\n\n");
        return sb.ToString();
    }

    /// <summary>2026-09-12: copied from <c>PromptGenerator.ExtractTaggedSource</c>
    /// (renamed from NormalizeBatchResponseSource after a real-data bug —
    /// HeelsFix mod, "$HEELSFIX_TARGET_ACTOR" = the literal string "Target:" —
    /// proved the old guess-and-strip approach unsound: unconditionally
    /// stripping a literal "Target:" prefix destroys a candidate whose OWN
    /// text starts with that word). The prompt now requires the model to echo
    /// the source wrapped in the same &lt;SJPTS_TARGET&gt;/&lt;/SJPTS_TARGET&gt;
    /// tags it was sent, so this requires an exact tag-delimited match instead
    /// of guessing at surrounding text — anything else (no tags, a missing
    /// tag, or extra text around an otherwise well-formed pair) returns ""
    /// and is treated as an unparseable line by the caller.</summary>
    private static string ExtractTaggedSource(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith(TargetTagOpen, StringComparison.Ordinal)) return "";
        if (!t.EndsWith(TargetTagClose, StringComparison.Ordinal)) return "";
        return t[TargetTagOpen.Length..^TargetTagClose.Length];
    }

    /// <summary>2026-09-12: mirrors PromptGenerator.ClassifyTaggedSourceIssue
    /// — diagnostic-only classification of WHY a response line's source
    /// column failed <see cref="ExtractTaggedSource"/>, for translation.log.
    /// See that method's own remarks for the real-data investigation this
    /// came from.</summary>
    private enum TaggedSourceIssue { NoTags, MissingOpeningTag, MissingClosingTag, ExtraTextOutsideTags }

    private static TaggedSourceIssue ClassifyTaggedSourceIssue(string text)
    {
        var t = text.Trim();
        var hasOpen = t.Contains(TargetTagOpen, StringComparison.Ordinal);
        var hasClose = t.Contains(TargetTagClose, StringComparison.Ordinal);
        if (!hasOpen && !hasClose) return TaggedSourceIssue.NoTags;
        if (!hasOpen) return TaggedSourceIssue.MissingOpeningTag;
        if (!hasClose) return TaggedSourceIssue.MissingClosingTag;
        return TaggedSourceIssue.ExtraTextOutsideTags;
    }

    /// <summary>Copied verbatim from <c>PromptGenerator.StripSurroundingQuotes</c>.</summary>
    private static string StripSurroundingQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    /// <summary>Copied verbatim from <c>PromptGenerator.StripTargetTags</c>.</summary>
    private static string StripTargetTags(string text) =>
        text.StartsWith(TargetTagOpen, StringComparison.Ordinal) && text.EndsWith(TargetTagClose, StringComparison.Ordinal)
            ? text[TargetTagOpen.Length..^TargetTagClose.Length]
            : text;
}
