using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// 2026-09-16: shared "hard part" engine extracted from
/// <c>Translation/PromptGenerator.cs</c>'s and
/// <c>SJPTS_InterfaceText/InterfaceTextPromptGenerator.cs</c>'s own, near-identical
/// <c>ApplyLlmStep</c> methods. The Interface pipeline started life as a copy-paste
/// of the ESP pipeline's batch-splitting/circuit-breaker/response-parsing logic
/// (documented in Interface's own class doc comment), which caused real
/// maintenance drift twice — a debug feature once landed in only one file, and the
/// 2026-09-16 round/batch → "pass" restructure had to be hand-implemented in both
/// files because there was no shared component. An independent code review done
/// specifically to prepare this extraction found 4 further unintentional
/// divergences that had crept in from the duplication: ESP-only trace.log warnings
/// for 3 failure classes, an ESP-only "retry record" diagnostic log, Interface-only
/// same-mod-hint-pool dedup by text, and Interface's log headers hardcoding
/// "生成AI翻訳" (cloud) even when running the local-LLM step. This engine now owns
/// all of that logic exactly once, fixing all 4 in the process — see the fixed
/// <see cref="Options{TItem}.StepNumber"/>/<see cref="Options{TItem}.StepLabelJa"/>/
/// <see cref="Options{TItem}.StepLabelEn"/> handling, the unconditional trace/retry
/// logging, and the pool dedup in <see cref="Run{TItem}"/> below.
///
/// What stays OUTSIDE this engine (each pipeline supplies via
/// <see cref="Options{TItem}"/>): how to represent one translation unit (ESP:
/// <c>Candidate</c>; Interface: a <c>(Key, English)</c> pair), how to build that
/// unit's own prompt block (ESP: corpus "b" reference examples, NPC-name/glossary
/// hints, multiline flattening; Interface: just "Target:"/"Key(s):"), the fixed
/// instruction text, and — after this engine returns — how a resolved text fans
/// out to the caller's own data model (ESP: every <c>Candidate</c> sharing that
/// text; Interface: every <c>$Key</c> sharing that English string).
/// </summary>
public static class LlmBatchTranslationEngine
{
    /// <summary>Wraps each unique English text on the "Target:" line so the
    /// model's answer can be matched back by exact content, not position —
    /// shared by both pipelines' instruction text and block-building.</summary>
    public const string TargetTagOpen = "<SJPTS_TARGET>";
    public const string TargetTagClose = "</SJPTS_TARGET>";

    public sealed class Options<TItem>
    {
        /// <summary>Everything still unresolved when this call starts.</summary>
        public required IReadOnlyList<TItem> Items { get; init; }

        /// <summary>The text used both to group items (identical text asked about
        /// once) and to match the model's echoed source back (ESP: CurrentText;
        /// Interface: English).</summary>
        public required Func<TItem, string> TextOf { get; init; }

        /// <summary>The DSD record type (or "" if the pipeline has no such concept)
        /// used for issue #4's same-mod-hint record-type-affinity bonus.</summary>
        public required Func<IGrouping<string, TItem>, string> RecordTypeOf { get; init; }

        /// <summary>Builds one group's own prompt block text, given the group and
        /// the exact "matchKey" that will appear on its "Target:" line (equal to
        /// <c>group.Key</c> unless <see cref="NeedsMatchKeyFlatten"/>/<see
        /// cref="FlattenForMatching"/> changed it) — plus the list of "Reference
        /// examples" English texts it actually included, for same-mod-hint dedup
        /// (b/c overlap). A pipeline with no "b" concept returns an empty list for
        /// the second element.</summary>
        public required Func<IGrouping<string, TItem>, string, (string Block, IReadOnlyList<string> ShownReferenceEnglish)> BuildBlock { get; init; }

        /// <summary>ESP-only: when this returns true for a group's key, <see
        /// cref="FlattenForMatching"/> is applied to produce a one-line "matchKey"
        /// for the TSV round-trip. Null (default) means no group ever needs
        /// flattening — every pipeline without ESP's multiline-candidate concept
        /// (e.g. Interface, whose values are always one line).</summary>
        public Func<string, bool>? NeedsMatchKeyFlatten { get; init; }
        public Func<string, string>? FlattenForMatching { get; init; }

        /// <summary>Reverses <see cref="FlattenForMatching"/>'s marker back into the
        /// resolved Japanese text (e.g. the marker back to '\n'). Null = identity.</summary>
        public Func<string, string>? UnflattenAnswer { get; init; }

        /// <summary>ESP-only rescue for a model that appends one extra copy of the
        /// multiline marker to an otherwise-correct echo. Null = this fallback tier
        /// is skipped entirely (Interface has no multiline marker to strip).</summary>
        public Func<string, string>? StripSpuriousBoundaryMarker { get; init; }

        public required string InstructionText { get; init; }

        /// <summary>Already-resolved, same-mod-hint-eligible entries from BEFORE
        /// this call (ESP: steps 1-4, or an earlier step 5 when this call is for
        /// step 6; Interface: none — empty). Merged with this call's own growing
        /// answers each pass so cross-call and cross-pass consistency both work.</summary>
        public IReadOnlyList<CorpusEntry> ExternalSameModBaseline { get; init; } = Array.Empty<CorpusEntry>();

        /// <summary>The same-mod pool <see cref="CorpusEntry"/>'s "Source" field
        /// (plugin name or mod name) — purely informational, never compared against
        /// anything.</summary>
        public required string SameModSourceLabel { get; init; }

        public required ITextTranslator Translator { get; init; }
        public required int BatchCharLimit { get; init; }
        public required string DebugDir { get; init; }

        /// <summary>Prompt debug file prefix, e.g. "prompt_localLLM_call" — files
        /// under this exact prefix are cleared before the first pass.</summary>
        public required string DebugFilePrefix { get; init; }

        public required string MethodTag { get; init; }

        public required RunLog Log { get; init; }
        public TraceLog? Trace { get; init; }

        /// <summary>The "[xxx]" bracketed scope name in every log line (plugin name
        /// or mod name).</summary>
        public required string LogScope { get; init; }

        /// <summary>"5"/"6" for ESP (rendered as "{StepNumber}." in every Japanese
        /// log header) or "" for Interface (omitted entirely). Interface previously
        /// hardcoded "生成AI翻訳" (cloud) in three log headers regardless of which
        /// provider was actually running — that bug is fixed by threading
        /// <see cref="StepLabelJa"/>/<see cref="StepLabelEn"/> (derived from
        /// providerLabel) through every log call site in this engine instead of
        /// duplicating the headers per caller.</summary>
        public required string StepNumber { get; init; }
        public required string StepLabelJa { get; init; }
        public required string StepLabelEn { get; init; }
    }

    /// <summary>Runs the pass/circuit-breaker/send/parse/match loop described in the
    /// class remarks above, returning every newly-resolved text (keyed by <see
    /// cref="Options{TItem}.TextOf"/>'s value) this call itself resolved. Callers
    /// with a null translator or empty item list should short-circuit before calling
    /// this (each pipeline's own "step disabled"/"nothing to do" semantics differ).</summary>
    public static Dictionary<string, AutoTranslationResult> Run<TItem>(Options<TItem> o)
    {
        var answers = new Dictionary<string, AutoTranslationResult>(StringComparer.Ordinal);
        if (o.Items.Count == 0) return answers;
        // 2026-09-16: the same-mod pool needs each already-resolved text's own
        // record type (for RecordTypeAffinity) on later passes/calls, once the
        // IGrouping that resolved it no longer exists — cached alongside answers.
        var answerRecordTypes = new Dictionary<string, string>(StringComparer.Ordinal);

        var stepPrefixJa = o.StepNumber.Length > 0 ? $"{o.StepNumber}." : "";
        var stepPrefixEn = o.StepNumber.Length > 0 ? $"{o.StepNumber}. " : "";

        foreach (var stale in Directory.Exists(o.DebugDir) ? Directory.EnumerateFiles(o.DebugDir, $"{o.DebugFilePrefix}*.txt") : Enumerable.Empty<string>())
            File.Delete(stale);

        var callIndex = 0;
        var circuitOpened = false;
        while (true) // パス: この時点で未解決な候補全部に最低1回の送信機会を与える一続き
        {
            var passStart = o.Items.Where(i => !answers.ContainsKey(o.TextOf(i))).ToList();
            if (passStart.Count == 0) break;

            var resolvedCountBeforePass = answers.Count;
            // このパスの間だけ有効な、固定の送信待ちキュー。ある呼び出しが失敗
            // しても「送った」候補はここから外す（解決できたかどうかは問わない）
            // ——そうしないと、先頭の候補が送るたびに失敗し続ける限り、後ろに
            // 控えている「まだ一度も送っていない」候補に永遠にたどり着けない。
            var remainingThisPass = passStart.GroupBy(o.TextOf, StringComparer.Ordinal).ToList();

            while (remainingThisPass.Count > 0)
            {
                // v0.52.1a: ClaudeCodeTranslator/LocalLlmTranslatorは連続失敗が
                // 一定回数続くとCircuitOpenを立てる。この状態はtranslator自身が
                // 保持しているため、パス・呼び出しをまたいでも連続失敗回数は
                // 継続してカウントされる。呼び出しごとに確認し、開いていれば
                // 残りの未解決分をまとめてスキップして完全に終了する。
                if (o.Translator.CircuitOpen)
                {
                    var stillUnresolvedTotal = o.Items.Count(i => !answers.ContainsKey(o.TextOf(i)));
                    o.Trace?.Warning($"{o.StepLabelEn}: circuit breaker open, skipping remaining {stillUnresolvedTotal} candidate(s) for [{o.LogScope}]");
                    o.Log.DetailAndReport($"{stepPrefixJa}{o.StepLabelJa}のサーキットブレーカー作動（残りをまとめてスキップ）",
                        $"{stepPrefixEn}{o.StepLabelEn} circuit breaker open (remaining candidates skipped)",
                        o.Log.Lang == RunLogLang.Ja
                            ? $"[{o.LogScope}]  連続失敗のため残り{stillUnresolvedTotal}件をまとめてスキップしました"
                            : $"[{o.LogScope}]  circuit breaker open — skipping remaining {stillUnresolvedTotal} candidate(s)",
                        $"[{o.LogScope}] {o.StepLabelEn}: circuit breaker open — skipping remaining {stillUnresolvedTotal} candidate(s)");
                    circuitOpened = true;
                    break;
                }

                // issue #4 (c): 既に解決済みの中から信頼できる手法のものだけを
                // プールにする。同一テキストが複数回登場しうる（answers自体は
                // テキストごとに1件だが、外部ベースラインとの合算で重複しうる）
                // ため、テキストで重複排除する。
                var sameModPool = answers
                    .Where(kv => SameModHintBlockBuilder.IsEligibleMethod(kv.Value.Method))
                    .Select(kv => new CorpusEntry(kv.Key, kv.Value.Japanese, o.SameModSourceLabel, kv.Value.Method, answerRecordTypes.GetValueOrDefault(kv.Key, "")))
                    .Concat(o.ExternalSameModBaseline)
                    .GroupBy(e => e.English, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList();

                // 各グループのブロック本文を先に1回だけ組み立て、その文字数を
                // 見ながらこのイテレーション1回分（＝1回のLLM呼び出し）に収まる
                // 分だけ先頭から詰める。1件だけで上限を超えるグループも、単独で
                // 必ず含める（無限にスキップされ続けることがないように）。
                var remainingThisPassCountBeforeBatch = remainingThisPass.Count;
                var blocks = remainingThisPass.Select(g =>
                {
                    var needsFlatten = o.NeedsMatchKeyFlatten?.Invoke(g.Key) ?? false;
                    var matchKey = needsFlatten ? o.FlattenForMatching!(g.Key) : g.Key;
                    var (block, shownReferenceEnglish) = o.BuildBlock(g, matchKey);
                    return (Group: g, Block: block, MatchKey: matchKey, ShownReferenceEnglish: shownReferenceEnglish);
                }).ToList();

                // bは各候補ブロックに個別に埋め込まれる実測値（呼び出し側の
                // BuildBlockで既にキャップ済み）なので、このパッキングの積算
                // （item.Block.Length）に既に正しく反映されている。まず
                // batchCharLimitいっぱいまで詰めてから、issue #4のc（バッチ
                // 全体で共有1個、実際にこの回に含まれる候補だけを対象に計算
                // ——中身が確定するまで実サイズが分からない）を実際に計算し、
                // 合計がbatchCharLimitを超える場合だけ末尾の候補を1件ずつ
                // 外して再計算する（外された候補は次のイテレーションで自然に
                // 再度対象になる）。候補が1件だけになってもなおcを含めると
                // 超える場合は、その候補自体は外さずcの方を諦める。
                var currentBatch = new List<(IGrouping<string, TItem> Group, string Block, string MatchKey, IReadOnlyList<string> ShownReferenceEnglish)>();
                var currentLength = 0;
                foreach (var item in blocks)
                {
                    if (currentBatch.Count > 0 && currentLength + item.Block.Length > o.BatchCharLimit) break;
                    currentBatch.Add(item);
                    currentLength += item.Block.Length;
                }

                string sameModBlock;
                while (true)
                {
                    var alreadyShownInBatch = currentBatch.SelectMany(b => b.ShownReferenceEnglish).ToHashSet();
                    sameModBlock = SameModHintBlockBuilder.BuildBlock(
                        sameModPool,
                        currentBatch.Select(b => (b.Group.Key, o.RecordTypeOf(b.Group))).ToList(),
                        o.BatchCharLimit,
                        alreadyShown: alreadyShownInBatch);
                    if (currentLength + sameModBlock.Length <= o.BatchCharLimit) break;
                    if (currentBatch.Count <= 1) { sameModBlock = ""; break; }
                    var last = currentBatch[^1];
                    currentBatch.RemoveAt(currentBatch.Count - 1);
                    currentLength -= last.Block.Length;
                }

                // このパスの送信待ちキューから、今回送る分を外す——解決できたか
                // どうかは問わない（このパス内での再送はしない。パス自体を
                // 再度回すかどうかはパス終了時の進捗判定に委ねる）。
                var currentBatchKeys = currentBatch.Select(b => b.Group.Key).ToHashSet(StringComparer.Ordinal);
                remainingThisPass = remainingThisPass.Where(g => !currentBatchKeys.Contains(g.Key)).ToList();

                callIndex++;
                var batchLabel = $"呼び出し{callIndex}";
                var batchLabelEn = $"call {callIndex}";

                o.Log.DetailAndReport($"{stepPrefixJa}{o.StepLabelJa}のバッチ呼び出し",
                    $"{stepPrefixEn}{o.StepLabelEn} batch call",
                    o.Log.Lang == RunLogLang.Ja
                        ? $"[{o.LogScope}]  {batchLabel}: 未解決{remainingThisPassCountBeforeBatch}件のうち{currentBatch.Count}件を送信（1回あたりの文字数上限: {o.BatchCharLimit}）"
                        : $"[{o.LogScope}]  {batchLabelEn}: sending {currentBatch.Count} of {remainingThisPassCountBeforeBatch} unresolved string(s) (char limit: {o.BatchCharLimit})",
                    $"[{o.LogScope}] {stepPrefixEn}{o.StepLabelEn} {batchLabelEn}: sending {currentBatch.Count} of {remainingThisPassCountBeforeBatch} unresolved string(s)...");

                var promptBuilder = new System.Text.StringBuilder(o.InstructionText);
                // issue #4 (c): a single shared block for this one call, placed
                // right after the fixed instruction and before the candidate
                // blocks — empty on the very first call (nothing resolved yet this
                // session for this scope) and grows as later calls/steps add to
                // the pool.
                if (sameModBlock.Length > 0) promptBuilder.Append(sameModBlock);
                foreach (var (_, block, _, _) in currentBatch)
                    promptBuilder.Append(block);
                var promptText = promptBuilder.ToString();

                Directory.CreateDirectory(o.DebugDir);
                var promptBatchPath = Path.Combine(o.DebugDir, $"{o.DebugFilePrefix}{callIndex}.txt");
                File.WriteAllText(promptBatchPath, promptText, new System.Text.UTF8Encoding(false));

                var response = o.Translator.TryTranslate(promptText, out var error);
                if (response == null)
                {
                    o.Trace?.Warning($"{o.StepLabelEn} {batchLabelEn} failed [{o.LogScope}] ({currentBatch.Count} candidate(s)): {error}");
                    o.Log.DetailAndReport($"{stepPrefixJa}{o.StepLabelJa}のバッチが失敗（エラー理由）",
                        $"{stepPrefixEn}{o.StepLabelEn} a batch failed (error reason)",
                        o.Log.Lang == RunLogLang.Ja
                            ? $"[{o.LogScope}]  {batchLabel}（{currentBatch.Count}件）が失敗しました  ({error})"
                            : $"[{o.LogScope}]  {batchLabelEn} ({currentBatch.Count} candidate(s)) failed ({error})",
                        $"[{o.LogScope}] {o.StepLabelEn} {batchLabelEn} failed ({currentBatch.Count} candidate(s)): {error}");
                }
                else
                {
                    // 2026-09-13: real-data investigation (gemma4:26b/Ollama,
                    // finish_reason == "length") — a batch whose response was cut
                    // off by an output-token limit ends mid-tag, silently dropping
                    // every candidate after the cutoff. Log this as a named reason
                    // instead of leaving an operator to infer truncation by eye
                    // from the raw trace-log dump below.
                    if (o.Translator.LastResponseTruncated)
                        o.Log.DetailAndReport($"{stepPrefixJa}{o.StepLabelJa}: モデルの応答が出力トークン数の上限で打ち切られた可能性があります",
                            $"{stepPrefixEn}{o.StepLabelEn}: the model's response may have been cut off by an output token limit",
                            o.Log.Lang == RunLogLang.Ja
                                ? $"[{o.LogScope}]  {batchLabel}（finish_reason=length）"
                                : $"[{o.LogScope}]  {batchLabelEn} (finish_reason=length)",
                            $"[{o.LogScope}] {o.StepLabelEn} {batchLabelEn}: response may have been cut off by an output token limit (finish_reason=length)");

                    // レスポンスを「English<TAB>Japanese」のTSV行として解析し、
                    // 元の英文（"Target:"に書いた原文そのもの）をキーに突き合わせる
                    // ——手動のAI-chat向けprompt.txtと同じ、位置ではなく内容一致で
                    // マッチングする方式。行の欠落・順序の入れ替わりがあっても
                    // 対応関係が崩れず、見つからなかった候補は単に未解決のまま
                    // 残る。
                    var byLine = new Dictionary<string, string>(StringComparer.Ordinal);
                    var byLineTrimmed = new Dictionary<string, string>(StringComparer.Ordinal);
                    // ESP-only rescue tier (StripSpuriousBoundaryMarker supplied):
                    // a model that echoes a multiline candidate's original text
                    // perfectly but appends one spurious extra marker before the
                    // tab. Only built/consulted when the caller supplied the hook.
                    var byLineMarkerTrimmed = o.StripSpuriousBoundaryMarker != null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : null;
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
                            o.Log.Detail($"{stepPrefixJa}{o.StepLabelJa}: 応答の1行がタグ形式を満たさずスキップ",
                                $"{stepPrefixEn}{o.StepLabelEn}: a response line didn't satisfy the tag format and was skipped",
                                $"[{o.LogScope}]  理由: {issue} / 該当行: \"{sourceColumnRaw.Trim()}\"");
                        }
                        var japanese = StripSurroundingQuotes(StripTargetTags(line[(tabIndex + 1)..].Trim()));
                        if (source.Length > 0 && japanese.Length > 0)
                        {
                            byLine[source] = japanese; // 同じキーが複数行あれば最後の行を採用
                            byLineTrimmed[source.Trim()] = japanese;
                            if (byLineMarkerTrimmed != null)
                            {
                                var trimmedSource = o.StripSpuriousBoundaryMarker!(source);
                                if (trimmedSource != source)
                                    byLineMarkerTrimmed[trimmedSource] = japanese;
                            }
                        }
                    }

                    var anyUnresolvedInBatch = false;
                    foreach (var (group, _, matchKey, _) in currentBatch)
                    {
                        // まず未Trimの完全一致（byLine）を試す——モデルが指示通り
                        // 正確に再掲したケースはここで一致する。それでも見つから
                        // ない場合のみ、双方をTrimしたbyLineTrimmed（モデルが
                        // 自発的に前後の空白を削ってしまったケースの救済）、
                        // さらにbyLineMarkerTrimmed（ESP限定、末尾の余分な
                        // マーカーを剥がした版）の順にフォールバックする。
                        string? japaneseRaw = null;
                        if (!byLine.TryGetValue(matchKey, out japaneseRaw)
                            && !byLineTrimmed.TryGetValue(matchKey.Trim(), out japaneseRaw)
                            && byLineMarkerTrimmed != null)
                            byLineMarkerTrimmed.TryGetValue(matchKey.Trim(), out japaneseRaw);

                        if (japaneseRaw != null)
                        {
                            // ESP限定: 改行を含む候補（matchKeyがgroup.Keyと異なる）
                            // の場合、送信時に埋め込んだマーカーをここで実際の
                            // 改行へ戻す。Interfaceはこのフックを渡さないため
                            // no-op。
                            var japanese = o.UnflattenAnswer?.Invoke(japaneseRaw) ?? japaneseRaw;

                            // v0.58.5: 応答は得られたが日本語が含まれない場合、
                            // 「未解決として捨てる」のでも「翻訳成功として無条件
                            // 受理する」のでもなく、専用タグ（methodTag+
                            // "NoJapanese"）を付けて保存する——「翻訳不要だった」
                            // のか「モデルが本当に翻訳を誤っただけ」なのかは
                            // 機械的に区別できないため、人間がレビューできる
                            // ようにする。
                            if (LanguageDetector.ContainsJapanese(japanese))
                            {
                                answers[group.Key] = new AutoTranslationResult(japanese, o.MethodTag, "");
                                answerRecordTypes[group.Key] = o.RecordTypeOf(group);
                                o.Log.Detail($"{stepPrefixJa}{o.StepLabelJa}による自動解決（低精度・要レビュー）",
                                    $"{stepPrefixEn}Auto-resolved via {o.StepLabelEn} (low confidence, needs review)",
                                    $"[{o.LogScope}]  \"{group.Key}\" → \"{japanese}\"");
                            }
                            else
                            {
                                var noJapaneseTag = o.MethodTag + "NoJapanese";
                                answers[group.Key] = new AutoTranslationResult(japanese, noJapaneseTag, "");
                                answerRecordTypes[group.Key] = o.RecordTypeOf(group);
                                o.Trace?.Warning($"{o.StepLabelEn} [{o.LogScope}] \"{group.Key}\": response parsed but contains no Japanese — saved as \"{noJapaneseTag}\" for review");
                                o.Log.DetailAndReport($"{stepPrefixJa}{o.StepLabelJa}: 応答は得られたが訳文に日本語が含まれない（翻訳不要な文字列か、翻訳失敗かは要レビュー）",
                                    $"{stepPrefixEn}{o.StepLabelEn}: response parsed but the translation contains no Japanese (needs review — may be untranslatable content, or a genuine translation failure)",
                                    $"[{o.LogScope}]  \"{group.Key}\" → \"{japanese}\"",
                                    $"[{o.LogScope}] {o.StepLabelEn}: response contained no Japanese — saved as-is for review: \"{group.Key}\" -> \"{japanese}\"");
                            }
                        }
                        else
                        {
                            o.Trace?.Warning($"{o.StepLabelEn} skip [{o.LogScope}] \"{group.Key}\": model's response wasn't in a format this tool could interpret");
                            o.Log.DetailAndReport($"{stepPrefixJa}{o.StepLabelJa}で解決できなかった候補（モデルの応答形式を解釈できず）",
                                $"{stepPrefixEn}{o.StepLabelEn} could not resolve this candidate (model's response wasn't in an interpretable format)",
                                $"[{o.LogScope}]  \"{group.Key}\"",
                                $"[{o.LogScope}] {o.StepLabelEn}: could not resolve \"{group.Key}\" (model's response wasn't in an interpretable format)");
                            anyUnresolvedInBatch = true;
                        }
                    }

                    // このバッチで1件でも未解決が残った場合のみ、モデルからの
                    // 生レスポンス全文をtrace.logへ1回だけ残す（バッチ全体で
                    // 1回、候補ごとに重複させない）。全件成功したバッチでは
                    // 出力しない（ログの肥大化を避ける）。
                    if (anyUnresolvedInBatch)
                        o.Trace?.Warning($"{o.StepLabelEn} [{o.LogScope}] {batchLabelEn}: raw response for the batch with unresolved candidate(s):\n{response}");

                    // リトライ診断（成功はしたが1回では済まなかった旨）を可視化
                    // ——バッチ単位で1行にまとめる。
                    if (error.Length > 0)
                        o.Log.Detail($"{stepPrefixJa}{o.StepLabelJa}のリトライ記録（バッチの再試行結果）",
                            $"{stepPrefixEn}{o.StepLabelEn} retry record (batch retry outcome)",
                            o.Log.Lang == RunLogLang.Ja
                                ? $"[{o.LogScope}]  {batchLabel}（{currentBatch.Count}件）  ({error})"
                                : $"[{o.LogScope}]  {batchLabelEn} ({currentBatch.Count} candidate(s))  ({error})");
                }
            }

            if (circuitOpened) break;

            // パス単位の自己終了条件: このパス全体を通じて新規解決が1件もなければ
            // （＝再送しても改善しないと判断し）、これ以上パスを重ねず終了する。
            // 1件でも解決できていれば、残りの未解決分を対象に次のパスへ進む——
            // このパスで一度送って失敗した候補も、次のパスでは最新のsameModPool
            // を使って改めて最初から機会を得る。
            if (answers.Count == resolvedCountBeforePass) break;
        }

        return answers;
    }

    /// <summary>2026-09-12: replaces the old NormalizeBatchResponseSource's
    /// guess-and-strip approach (leading "- ", leading "Target:", then a
    /// tolerant tag strip) after a real-data bug (HeelsFix mod,
    /// "$HEELSFIX_TARGET_ACTOR" = the literal string "Target:") proved that
    /// approach unsound: unconditionally stripping a literal "Target:" prefix
    /// destroys a candidate whose OWN text starts with that word. The prompt
    /// requires the model to echo the source WRAPPED IN THE SAME
    /// &lt;SJPTS_TARGET&gt;/&lt;/SJPTS_TARGET&gt; tags it was sent, so this
    /// requires an exact tag-delimited match instead of guessing at surrounding
    /// text — anything else (no tags, a missing tag, or extra text around an
    /// otherwise well-formed pair) returns "" and is treated as an unparseable
    /// line by the caller.</summary>
    public static string ExtractTaggedSource(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith(TargetTagOpen, StringComparison.Ordinal)) return "";
        if (!t.EndsWith(TargetTagClose, StringComparison.Ordinal)) return "";
        return t[TargetTagOpen.Length..^TargetTagClose.Length];
    }

    /// <summary>Diagnostic-only classification of WHY a response line's source
    /// column failed <see cref="ExtractTaggedSource"/> — used only for logging,
    /// never for resolution itself.</summary>
    public enum TaggedSourceIssue { NoTags, MissingOpeningTag, MissingClosingTag, ExtraTextOutsideTags }

    public static TaggedSourceIssue ClassifyTaggedSourceIssue(string text)
    {
        var t = text.Trim();
        var hasOpen = t.Contains(TargetTagOpen, StringComparison.Ordinal);
        var hasClose = t.Contains(TargetTagClose, StringComparison.Ordinal);
        if (!hasOpen && !hasClose) return TaggedSourceIssue.NoTags;
        if (!hasOpen) return TaggedSourceIssue.MissingOpeningTag;
        if (!hasClose) return TaggedSourceIssue.MissingClosingTag;
        return TaggedSourceIssue.ExtraTextOutsideTags;
    }

    /// <summary>v0.52.1a: a model sometimes wraps its whole TSV field in quotes as
    /// its own unrelated formatting habit, independent of whatever delimiter this
    /// project's own prompt uses. Used ONLY for the Japanese answer column — the
    /// English matching key deliberately does NOT use this (see
    /// <see cref="ExtractTaggedSource"/>'s remarks). Symmetric only (both ends
    /// must be ").</summary>
    public static string StripSurroundingQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    /// <summary>v0.59.0: a model sometimes wraps its own Japanese answer in
    /// &lt;SJPTS_TARGET&gt;...&lt;/SJPTS_TARGET&gt; too (over-generalizing the
    /// prompt's own worked example). Same "strip only if symmetric" policy as
    /// <see cref="StripSurroundingQuotes"/> so a one-sided coincidental match
    /// isn't corrupted.</summary>
    public static string StripTargetTags(string text) =>
        text.StartsWith(TargetTagOpen, StringComparison.Ordinal) && text.EndsWith(TargetTagClose, StringComparison.Ordinal)
            ? text[TargetTagOpen.Length..^TargetTagClose.Length]
            : text;
}
