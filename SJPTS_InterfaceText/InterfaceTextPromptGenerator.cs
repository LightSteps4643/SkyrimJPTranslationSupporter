using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SJPTS_InterfaceText;

/// <summary>
/// LLM batch-translation step for Interface\Translations entries. Originally
/// (2026-09-11) a copy-paste of <c>SkyrimJPStringPatcher.Translation.
/// PromptGenerator.ApplyLlmStep</c>'s batch-splitting/circuit-breaker/
/// response-parsing logic, trimmed down for plain <c>$Key</c>/English UI
/// strings (no DSD RecordType/Context, no vanilla corpus "b" reference
/// examples, no multiline-candidate handling — Interface\Translations values
/// are always one line). That duplication caused real maintenance drift twice
/// (a debug feature once landed in only one file; the 2026-09-16 round/batch
/// → "pass" restructure had to be hand-implemented in both files) — an
/// independent code review done to prepare a fix also found 4 further
/// unintentional divergences (see git history around 2026-09-16 and
/// <see cref="LlmBatchTranslationEngine"/>'s own class remarks).
///
/// 2026-09-16: the shared "hard part" (pass loop, circuit-breaker checks,
/// same-mod-hint pooling, response parsing/matching, all of the above bugs'
/// fixes) is now extracted into <see cref="LlmBatchTranslationEngine"/>, used
/// by both this file and <c>Translation/PromptGenerator.cs</c> — this class
/// is now a thin wrapper supplying only what's genuinely Interface-specific:
/// the <c>(Key, English)</c> data model, <see cref="BuildBlock"/> (just
/// "Target:"/"Key(s):", no "b"), <see cref="BuildInstruction"/>, and the
/// post-engine fan-out from a resolved English text back onto every
/// <c>$Key</c> sharing it (one text can back several keys — TrueHUD reuses
/// short labels this way).
///
/// 2026-09-12: matches the ESP pipeline's full 3-way outcome (auto-resolved /
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

    /// <summary>Mirrors <c>PromptGenerator.DefaultLocalLlmBatchCharLimit</c> (local LLM,
    /// 2026-09-16: raised to 6,000 — see that constant's remarks for the real-LLM
    /// verification behind the change).</summary>
    public const int DefaultLocalLlmBatchCharLimit = 6_000;

    /// <summary>Wraps each unique English text on the "Target:" line so the
    /// model's answer can be matched back by exact content (not position) —
    /// same tag pair and same rationale as the original (unlikely to collide
    /// with real game/UI text; confirmed the model reliably distinguishes it
    /// from a candidate's own embedded markup).</summary>
    private const string TargetTagOpen = LlmBatchTranslationEngine.TargetTagOpen;
    private const string TargetTagClose = LlmBatchTranslationEngine.TargetTagClose;

    /// <summary>2026-09-12: takes the mod's own display name (VFS-winning MOD
    /// folder name — see DetectOneMod's own remarks in Program.cs) so the
    /// model itself can recognize when a string among the candidates IS that
    /// mod's own name/title, rather than this tool trying to mechanically
    /// detect it. Mechanical detection (matching the string against `target`
    /// or the mod folder name) turned out unreliable in both directions: a
    /// real-data investigation (Floating Subtitles' "$FSUB_Title_Text" =
    /// "Floating Subtitles", HeelsFix's "$HEELSFIX_MOD_NAME" = "Heels Fix")
    /// found title-holding keys with no consistent naming convention, AND
    /// `target` is explicitly NOT guaranteed to equal the mod's real name
    /// (design/interface_translations.md) while the MOD folder name is
    /// user-renamable in MO2 — neither is a dependable ground truth. Confirmed
    /// against real gemma4:26b (reasoning off) output before implementing:
    /// the model both preserves the bare mod name AND still correctly
    /// translates a near-miss like "High Heels" for a mod named "Heels Fix"
    /// (no over-exclusion of ordinary vocabulary that merely shares a word).
    /// Scoped to Interface翻訳 only — ESP-side plugin names showed no
    /// equivalent real-data failure (a plugin name embedded in a longer
    /// record name, e.g. "Heels Fix Quest", was already handled correctly by
    /// the model without this instruction; the bug here is specific to a
    /// standalone MCM title string holding ONLY the mod's own name).</summary>
    private static string BuildInstruction(string modDisplayName) =>
        $"Below are UI strings from a Skyrim SE mod named \"{modDisplayName}\", not yet translated into Japanese.\n" +
        "These are the mod's OWN original UI text — do not assume they match any vanilla Skyrim terminology; judge each\n" +
        "string on its own meaning. Many are short (toggle labels, headers, option names) with little surrounding\n" +
        "context — the \"Key(s)\" line for each string lists the $Key name(s) it's used under, which often encode a\n" +
        "feature/page hierarchy (e.g. $ModName_FeatureName_OptionText); use that as a hint to what the string is for.\n\n" +
        $"If a string IS this mod's own name/title \"{modDisplayName}\" — exactly, or in an equivalent form with only\n" +
        "different capitalization, spacing, or punctuation — keep it EXACTLY as given in your Japanese translation\n" +
        "column too (do not translate, transliterate into katakana, or alter it in any way). Only apply this to a\n" +
        "string that clearly IS the mod's own name, not ordinary vocabulary that merely shares a word with it.\n\n" +
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
    /// <param name="modWorkDir">Where to write each call's actual sent prompt
    /// (instruction + candidate blocks, byte-for-byte what goes into
    /// <see cref="ITextTranslator.TryTranslate"/>) as "prompt_{providerLabel}
    /// _call{N}.txt" — a debugging aid (2026-09-12 design discussion) distinct
    /// from the ESP CLI's own prompt.txt (that one is a human AI-chat handoff
    /// for whatever's STILL unresolved after the LLM step; this one is a
    /// record of what WAS actually sent, win or lose). 2026-09-16: renamed
    /// from "..._batch{N}_of_{total}[_round{R}].txt" when the round/batch
    /// split was collapsed into one flat per-call loop (see ApplyLlmStep's own
    /// remarks), since "total batches" is no longer known ahead of time.
    /// Stale files under this SAME provider's prefix are cleared first — not
    /// the other provider's — so a translate run that does ローカルLLM then
    /// 生成AI（クラウド） in two calls doesn't have the second call wipe out
    /// the first's files (both would otherwise match a shared "prompt_call*"
    /// pattern).</param>
    /// <param name="providerLabel">"localLLM" or "cloudLLM" — which of this
    /// mod's two independent translate calls this is, mirroring the ESP CLI's
    /// own step 5 vs step 6 distinction (Translation/PromptGenerator.cs).</param>
    /// <param name="modDisplayName">The mod's real display name (VFS-winning
    /// MOD folder name — see <see cref="BuildInstruction"/>'s remarks), used
    /// only to tell the model what to preserve. Optional and defaults to
    /// <paramref name="modName"/> (the file-based target identifier) when not
    /// supplied — every pre-existing caller/test keeps working unchanged,
    /// just without the real display name available for this specific check.</param>
    public static Dictionary<string, (string Japanese, string Notes, string TranslationCheck)> ApplyLlmStep(
        IReadOnlyList<(string Key, string English)> pending, ITextTranslator translator,
        string modName, RunLog log, TraceLog? trace, int batchCharLimit, string modWorkDir, string providerLabel,
        string? modDisplayName = null)
    {
        modDisplayName ??= modName;
        var result = new Dictionary<string, (string Japanese, string Notes, string TranslationCheck)>(StringComparer.Ordinal);
        if (pending.Count == 0) return result;

        // Notes値はESP側（Translation/PromptGenerator.cs）の命名規則をそのまま
        // 踏襲——"SJPTS_TranslationLocalLlm"/"SJPTS_TranslationCloudLlm"。
        var methodTag = providerLabel == "localLLM" ? "SJPTS_TranslationLocalLlm" : "SJPTS_TranslationCloudLlm";
        // 2026-09-16: 独立レビューで発覚したバグの修正——以前はここのログ見出しが
        // providerLabelを無視して常に「生成AI翻訳」固定だったため、ローカルLLM
        // 実行時でもログには「生成AI翻訳」と誤表示されていた。ESP側の
        // stepLabelJa/stepLabelEnに相当する値をproviderLabelから導出し、
        // LlmBatchTranslationEngineの全ログ呼び出しに一貫して渡す。
        var stepLabelJa = providerLabel == "localLLM" ? "ローカルLLM" : "生成AI翻訳";
        var stepLabelEn = providerLabel == "localLLM" ? "local LLM" : "cloud AI translation";

        var keysByEnglish = pending.GroupBy(e => e.English, e => e.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var answers = LlmBatchTranslationEngine.Run(new LlmBatchTranslationEngine.Options<(string Key, string English)>
        {
            Items = pending,
            TextOf = e => e.English,
            RecordTypeOf = _ => "", // No DsdType concept exists for UI strings.
            BuildBlock = (g, _) => (BuildBlock(g), Array.Empty<string>()), // No "b" concept — nothing shown to dedup against.
            InstructionText = BuildInstruction(modDisplayName),
            SameModSourceLabel = modName,
            Translator = translator,
            BatchCharLimit = batchCharLimit,
            DebugDir = modWorkDir,
            DebugFilePrefix = $"prompt_{providerLabel}_call",
            MethodTag = methodTag,
            Log = log,
            Trace = trace,
            LogScope = modName,
            StepNumber = "",
            StepLabelJa = stepLabelJa,
            StepLabelEn = stepLabelEn,
            // 2026-09-18: ESP側ApplyLlmStepと同じ配線——mod_glossary.tsv
            // （InterfaceModPhraseGlossarySupport.WriteModGlossaryが書き出す）
            // に人が記入済みの訳を、issue #4「同一MOD既訳ヒント」のcプールへ
            // 合流させる。これまでInterface側にはこの配線が無く、
            // mod_glossary.tsvを作っても効果が無い状態だった。
            ExternalSameModBaseline = ModPhraseGlossary.LoadFilled(modWorkDir)
                .Select(kv => new CorpusEntry(kv.Key, kv.Value, modName, "SJPTS_ModifiedByUser", ""))
                .ToList(),
        });

        // issue #4 (c): このメソッド自身が今回解決した英文を、共有する
        // $Key全部にファンアウトする（ESP側のCandidateには無い、Interface
        // 固有の変換——1つの英文を複数の$Keyが参照しうるため）。
        foreach (var (text, auto) in answers)
        {
            if (!keysByEnglish.TryGetValue(text, out var keys)) continue;
            foreach (var key in keys)
                result[key] = (auto.Japanese, auto.Method, auto.TranslationCheck);
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
}
