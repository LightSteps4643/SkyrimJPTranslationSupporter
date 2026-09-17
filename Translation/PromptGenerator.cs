using SkyrimJPStringPatcher.Core;
using static SkyrimJPStringPatcher.Core.TsvEscaping;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Translation: 対象を翻訳する（AIチャット活用パス）。PickUpTargetが出力したTSV
/// （候補・コーパス）だけを入力とし、Mutagen/MO2には一切依存しない。
/// 「AIに渡すプロンプト」と「翻訳結果を書き込むためのテンプレート（GenerateDsdFileの
/// 入力になる）」の2つを、プラグインごとに出力する。
///
/// v0.33.0: each run is now fully self-contained and stateless across runs — no
/// reflux, no cross-plugin ownership/deferral. Every candidate's own plugin gets
/// its own prompt for its own text, every time, unaffected by anything typed
/// into a previous run's translations.tsv. See DESIGN_HISTORY.md's v0.33.0
/// section for why the removed reflux/owner mechanism's remaining value turned
/// out too small to justify keeping.
///
/// v0.50.1a〜v0.52.1a: a deliberate exception to "fully stateless" — any row
/// that already has a translation in the existing translations.tsv (whatever
/// method produced it — ①〜⑥ auto-resolution, or a human's SJPTS_ModifiedByUser edit)
/// is carried forward into the freshly-regenerated file as-is, never re-run.
/// This is NOT the old reflux mechanism back from the dead — reflux propagated
/// the pipeline's OWN unverified output across DIFFERENT plugins/sessions
/// (measured, in DESIGN_HISTORY.md's v0.34.0 section, to matter for all of two
/// strings) — this instead preserves a candidate's OWN prior answer within the
/// SAME plugin, whether that answer was a human's explicit judgment call
/// (matching the same "a person's decision outranks automated output"
/// precedent as xTranslator imports and Data/phrase_overrides.tsv) or a costly
/// ⑤/⑥ AI call that should never be silently redone (and re-billed) just
/// because `translation` was run again — see ReadExistingTranslations's
/// remarks for the full history of this widening. `--discard-user-edits` opts
/// out per-run when a clean ①のみ slate is actually wanted (despite the name,
/// it now discards every prior resolution, not just human edits — matching
/// what the GUI's "初期化"/"MO2再読込＆初期化" actions have always meant).
/// </summary>
public static class PromptGenerator
{
    /// <summary>
    /// v0.46.1: the pipeline-construction wiring RunOne and RunAll both need
    /// (read candidates/corpus TSVs, merge in the xTranslator import and
    /// reference glossary, build the precedent index / AutoTranslator /
    /// NameFallbackTranslator, dump the derived word tables) — factored out
    /// after the two copies were found to have drifted (RunAll had gained extra
    /// trace/console instrumentation RunOne never got). Everything else about
    /// each method (its own "入力" log section, its own candidate selection,
    /// its own per-plugin vs. single-plugin write path) stays in the caller,
    /// since that's genuinely where the two modes differ.
    /// </summary>
    private readonly record struct TranslationContext(
        List<Candidate> AllCandidates, List<CorpusEntry> Corpus, List<CorpusEntry> Imported, List<CorpusEntry> Reference,
        PrecedentRetriever Retriever, AutoTranslator Auto, NameFallbackTranslator NameFallback,
        IReadOnlySet<string> NpcNames, ModPhraseGlossary.GlobalNgramFrequency GlobalPhraseFrequency);

    private static TranslationContext BuildContext(string candidatesTsvPath, string corpusTsvPath, string importDir, string outputDir, RunLog log, TraceLog? trace, TranslationStageOptions stages)
    {
        var allCandidates = CandidateIo.ReadTsv(candidatesTsvPath);
        var corpus = CorpusIo.ReadTsv(corpusTsvPath);
        trace?.Debug($"candidates.tsv read: {allCandidates.Count} entries / corpus.tsv read: {corpus.Count} entries");

        // xTranslator's community/human translations are loaded fresh from
        // Translation/import/ every run and merged in right after the permanent
        // vanilla/DSD corpus — i.e. checked at step ①, ahead of ④/③ and ahead of
        // NameFallbackTranslator entirely (both consult the corpus this builds).
        // See XTranslatorImporter's remarks (v0.33.0).
        var imported = XTranslatorImporter.Load(importDir, allCandidates, log, trace);
        corpus.AddRange(imported);
        var reference = LoadReferenceGlossary();
        corpus.AddRange(reference);
        trace?.Debug($"Merged xTranslator import {imported.Count} + reference glossary {reference.Count}, corpus total {corpus.Count}");

        Console.WriteLine($"Building precedent index from {corpus.Count} corpus entries...");
        var retriever = new PrecedentRetriever(corpus); // built ONCE, reused for every candidate below
        trace?.Info("PrecedentRetriever build done");
        var auto = new AutoTranslator(corpus, trace, stages.EnableMeaning, stages.EnableTransliteration, stages.EnableNameFallback);
        trace?.Info($"AutoTranslator build done: head={auto.MeaningTable.HeadCount} modifier={auto.MeaningTable.ModifierCount}");
        var nameFallback = NameFallbackTranslator.Build(LoadNameGlossary(), auto, auto.MeaningTable, auto.TransliterationTable);

        // v0.49.1: 1.完全一致には無効化オプションが無い（正解データそのものであり、
        // 無効化すべき状況が無いため）。2.3.4.はここで無効化されていればログに残す——
        // 自動解決件数が想定より少なく見えたとき、原因調査の起点になる。
        if (!stages.EnableMeaning)
            log.Line("2.意味合成: 無効化（--no-meaning）", "Step 2 (meaning composition): disabled (--no-meaning)");
        if (!stages.EnableTransliteration)
            log.Line("3.音訳分解: 無効化（--no-translit）", "Step 3 (transliteration decomposition): disabled (--no-translit)");
        if (!stages.EnableNameFallback)
            log.Line("4.NameFallbackTranslator: 無効化（--no-namefallback）", "Step 4 (NameFallbackTranslator): disabled (--no-namefallback)");

        WriteDerivedTables(outputDir, auto, log, trace);

        // v0.48.1: this load order's own NPC_ FULL display names (this exact
        // reader's own pets/characters), used to hint DIAL FULL/INFO NAM1
        // candidates when one of these names appears embedded in an ordinary
        // sentence ("Go home, Scooby.") — a spot where NameFallbackTranslator's
        // NPC_ FULL exclusion doesn't help, since the risk here is a downstream
        // AI-chat/local-LLM pass reading the name as a common word, not this
        // tool's own word-chain composition. See DESIGN_NOTES.md's "残存リスク".
        var npcNames = allCandidates
            .Where(c => c.RecordType.Equals("NPC_ FULL", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.CurrentText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2026-09-17: ModPhraseGlossaryのグローバルn-gram文書頻度表はロード
        // オーダー全体（allCandidates）から決まり、どのプラグインを処理するかに
        // 一切依存しないので、npcNames同様ここで実行全体につき1回だけ構築する
        // （GlobalNgramFrequencyの remarks 参照。以前はプラグインごとに毎回
        // 再構築しており、--all実行が約6倍（10秒→62秒、183プラグインで実測）
        // 遅くなっていた）。
        var globalPhraseFrequency = ModPhraseGlossary.GlobalNgramFrequency.Build(allCandidates.Select(c => c.CurrentText).ToList());

        return new TranslationContext(allCandidates, corpus, imported, reference, retriever, auto, nameFallback, npcNames, globalPhraseFrequency);
    }

    /// <summary>1つのプラグインだけを対象に実行する。</summary>
    public static void RunOne(string candidatesTsvPath, string corpusTsvPath, string importDir, string targetPlugin, string outputDir, RunLog log, TraceLog? trace = null, ITextTranslator? llmLocal = null, ITextTranslator? llmCloud = null, TranslationStageOptions? stageOptions = null, bool discardUserEdits = false, int llmLocalBatchCharLimit = DefaultLocalLlmBatchCharLimit, int llmCloudBatchCharLimit = DefaultLlmBatchCharLimit)
    {
        var stages = stageOptions ?? TranslationStageOptions.Default;
        var ctx = BuildContext(candidatesTsvPath, corpusTsvPath, importDir, outputDir, log, trace, stages);

        var targetCandidates = ctx.AllCandidates
            .Where(c => c.WinningPlugin.Equals(targetPlugin, StringComparison.OrdinalIgnoreCase))
            .ToList();

        log.Section("入力", "Input");
        log.Line($"対象プラグイン: {targetPlugin}（単一指定）", $"Target plugin: {targetPlugin} (single-plugin mode)");
        log.Line($"候補: {targetCandidates.Count}件 / 全体{ctx.AllCandidates.Count}件", $"Candidates: {targetCandidates.Count} / total {ctx.AllCandidates.Count}");
        log.Line($"コーパス: {ctx.Corpus.Count - ctx.Imported.Count - ctx.Reference.Count}件、xTranslatorインポート: {ctx.Imported.Count}件",
            $"Corpus: {ctx.Corpus.Count - ctx.Imported.Count - ctx.Reference.Count}, xTranslator import: {ctx.Imported.Count}");

        if (targetCandidates.Count == 0)
        {
            Console.WriteLine($"No candidates found for '{targetPlugin}' in {candidatesTsvPath}.");
            log.Line("該当候補が0件のため、何も生成していない", "0 matching candidates, nothing generated");
            return;
        }

        var (promptPath, templatePath, _, autoCount, unique, _, _, _, methodCounts) = WritePluginFilesWithDir(outputDir, targetPlugin, targetCandidates, ctx.GlobalPhraseFrequency, ctx.Retriever, ctx.Auto, ctx.NameFallback, ctx.NpcNames, llmLocal, llmCloud, stages.EnableNameFallback, log, trace, discardUserEdits, llmLocalBatchCharLimit, llmCloudBatchCharLimit);
        Console.WriteLine($"Target: {targetPlugin} ({targetCandidates.Count} candidates, {autoCount} resolved (①〜⑥))");
        Console.WriteLine($"Wrote AI-chat prompt: {promptPath}");
        Console.WriteLine($"Wrote translation template: {templatePath}");

        log.Section("処理サマリ", "Processing summary");
        // v0.53.0: 「AI不要」という表記は、⑤ローカルLLM・⑥生成AI翻訳による解決も
        // 含んだ件数であるにもかかわらず「AIなしで解決した件数」であるかのように
        // 読めてしまっていたため、単純に「解決」に修正した（実データからユーザーが
        // 指摘・確認済み）。内訳の内側にローカルLLM/生成AIの件数が既に出ているので、
        // 情報量自体は変わらない。
        log.Line($"解決（①〜⑥）: {autoCount} / {targetCandidates.Count}", $"Resolved (steps 1-6): {autoCount} / {targetCandidates.Count}");
        log.Line($"  内訳: コーパス完全一致{methodCounts.Corpus}件 / 意味合成{methodCounts.Meaning}件 / 音訳分解{methodCounts.Transliteration}件 / NameFallbackTranslator{methodCounts.NameFallback}件 / ローカルLLM{methodCounts.Llm}件 生成AI{methodCounts.CloudLlm}件",
            $"  Breakdown: exact corpus match {methodCounts.Corpus} / meaning composition {methodCounts.Meaning} / transliteration decomposition {methodCounts.Transliteration} / NameFallbackTranslator {methodCounts.NameFallback} / local LLM {methodCounts.Llm} cloud AI {methodCounts.CloudLlm}");
        log.Line($"AIチャット対象: {targetCandidates.Count - autoCount} → 同一英文の重複排除後 {unique}",
            $"AI-chat target: {targetCandidates.Count - autoCount} -> {unique} after deduplicating identical English text");
        log.Line($"※各件の訳は {templatePath} を参照", $"* See {templatePath} for each entry's translation");
    }

    /// <summary>
    /// v0.50.1a: 複数プラグインを指定して、1回の起動でまとめて処理する。GUIの
    /// 「選択したプラグインだけ翻訳」機能のために新設——`BuildContext`（candidates.tsv/
    /// corpus.tsvの読み込み、xTranslatorインポート、コーパス辞書の構築）はどのプラグインを
    /// 処理するかに一切依存しない、ロードオーダー全体に対して1回で済む前処理であるにも
    /// かかわらず、従来はプラグインごとに`RunOne`を個別プロセスとして呼ぶしかなく、
    /// 選択件数分（最大175回）この重い初期化が繰り返されていた（実測: xTranslator
    /// インポート38ファイル・コーパス12万件超の再構築が毎回発生）。このメソッドは
    /// `RunAll`のループ本体をプラグイン集合でフィルタしただけで、初期化は1回のみ。
    /// `RunOne`と同じく、集計ファイル（auto_resolve_by_plugin.tsv・plugin_summary.txt・
    /// translation_index.txt）は書き出さない——これらは「ロードオーダー全体」を前提に
    /// 集計する契約のファイルであり、一部プラグインだけの実行結果で上書きすると、
    /// 直前の`--all`スキャンが持っていた全体像の可視性を失わせてしまうため。
    /// </summary>
    /// <param name="cancelFlagPath">v0.53.0a: GUIの「キャンセル」ボタン用。1プラグイン
    /// 処理し終えるたびにこのパスの存在を確認し、あればそこで残りのプラグインを処理せず
    /// 正常終了する（DESIGN_NOTES.md既知の課題15.）。null／未指定なら一切チェックしない。</param>
    public static void RunMany(string candidatesTsvPath, string corpusTsvPath, string importDir, IReadOnlyList<string> targetPlugins, string outputDir, RunLog log, TraceLog? trace = null, ITextTranslator? llmLocal = null, ITextTranslator? llmCloud = null, TranslationStageOptions? stageOptions = null, bool discardUserEdits = false, int llmLocalBatchCharLimit = DefaultLocalLlmBatchCharLimit, int llmCloudBatchCharLimit = DefaultLlmBatchCharLimit, string? cancelFlagPath = null)
    {
        var stages = stageOptions ?? TranslationStageOptions.Default;
        var ctx = BuildContext(candidatesTsvPath, corpusTsvPath, importDir, outputDir, log, trace, stages);

        log.Section("入力", "Input");
        log.Line($"対象プラグイン: {targetPlugins.Count}件（複数指定）", $"Target plugins: {targetPlugins.Count} (multi-plugin mode)");
        log.Line($"候補: {ctx.AllCandidates.Count}件", $"Candidates: {ctx.AllCandidates.Count}");
        log.Line($"コーパス: {ctx.Corpus.Count - ctx.Imported.Count - ctx.Reference.Count}件、xTranslatorインポート: {ctx.Imported.Count}件",
            $"Corpus: {ctx.Corpus.Count - ctx.Imported.Count - ctx.Reference.Count}, xTranslator import: {ctx.Imported.Count}");

        var targetSet = new HashSet<string>(targetPlugins, StringComparer.OrdinalIgnoreCase);
        var byPlugin = ctx.AllCandidates
            .Where(c => targetSet.Contains(c.WinningPlugin))
            .GroupBy(c => c.WinningPlugin)
            .ToList();

        var found = new HashSet<string>(byPlugin.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var missing in targetPlugins.Where(p => !found.Contains(p)))
            Console.WriteLine($"No candidates found for '{missing}' in {candidatesTsvPath}.");

        Console.WriteLine($"Generating Translation prompt packages for {byPlugin.Count} plugin(s)...");

        var totalCandidates = 0;
        var totalAuto = 0;
        var processedCount = 0;
        foreach (var group in byPlugin)
        {
            processedCount++;
            var candidates = group.ToList();
            var (promptPath, templatePath, _, autoCount, _, _, _, _, methodCounts) =
                WritePluginFilesWithDir(outputDir, group.Key, candidates, ctx.GlobalPhraseFrequency, ctx.Retriever, ctx.Auto, ctx.NameFallback, ctx.NpcNames, llmLocal, llmCloud, stages.EnableNameFallback, log, trace, discardUserEdits, llmLocalBatchCharLimit, llmCloudBatchCharLimit);
            totalCandidates += candidates.Count;
            totalAuto += autoCount;
            trace?.Debug($"{group.Key}: target {candidates.Count} entries, auto-resolved {autoCount} -> {templatePath}");

            Console.WriteLine($"Target: {group.Key} ({candidates.Count} candidates, {autoCount} resolved (①〜⑥))");
            log.Line(
                $"{group.Key}: 対象{candidates.Count}件 " +
                $"コーパス{methodCounts.Corpus}件 意味合成{methodCounts.Meaning}件 音訳{methodCounts.Transliteration}件 " +
                $"NameFallback{methodCounts.NameFallback}件 ローカルLLM{methodCounts.Llm}件 生成AI{methodCounts.CloudLlm}件 未解決{candidates.Count - autoCount}件",
                $"{group.Key}: target {candidates.Count} " +
                $"corpus {methodCounts.Corpus} meaning {methodCounts.Meaning} transliteration {methodCounts.Transliteration} " +
                $"NameFallback {methodCounts.NameFallback} local LLM {methodCounts.Llm} cloud AI {methodCounts.CloudLlm} unresolved {candidates.Count - autoCount}");

            // v0.53.0a: このプラグインの書き込みまで完了した直後（区切りの良い
            // タイミング）でのみチェックする——バッチ呼び出しの途中で打ち切ると
            // そのプラグインの計算結果が丸ごと失われる上、書きかけの
            // translations.tsvが残るリスクがあるため、あえてプロセスをkillせず
            // ここで自発的に止まる設計にしている（DESIGN_NOTES.md既知の課題15.）。
            if (cancelFlagPath != null && File.Exists(cancelFlagPath))
            {
                var remaining = byPlugin.Count - processedCount;
                Console.WriteLine($"Cancelled by user after [{group.Key}] — {remaining} plugin(s) left unprocessed (re-run to continue; already-resolved entries are preserved).");
                log.DetailAndReport("キャンセル", "Cancelled",
                    log.Lang == RunLogLang.Ja
                        ? $"ユーザーの中断要求により[{group.Key}]の完了後に処理を打ち切りました（未処理{remaining}件、再実行で続きから対応可能）"
                        : $"Cancelled by user after [{group.Key}] — {remaining} plugin(s) left unprocessed (re-run to continue).",
                    $"Cancelled by user after [{group.Key}] — {remaining} plugin(s) left unprocessed.");
                trace?.Info($"Cancelled by user after [{group.Key}] ({remaining} plugin(s) left unprocessed)");
                return;
            }
        }

        Console.WriteLine($"Done. Resolved (①〜⑥): {totalAuto} / {totalCandidates}");
        trace?.Info($"Done (multi-plugin): resolved {totalAuto}/{totalCandidates} across {byPlugin.Count} plugin(s)");
    }

    /// <summary>ロードオーダー全体の候補を、勝者プラグインごとに一括生成する。</summary>
    public static void RunAll(string candidatesTsvPath, string corpusTsvPath, string importDir, string outputDir, RunLog log, TraceLog? trace = null, ITextTranslator? llmLocal = null, ITextTranslator? llmCloud = null, TranslationStageOptions? stageOptions = null, bool discardUserEdits = false, int llmLocalBatchCharLimit = DefaultLocalLlmBatchCharLimit, int llmCloudBatchCharLimit = DefaultLlmBatchCharLimit)
    {
        var stages = stageOptions ?? TranslationStageOptions.Default;
        var ctx = BuildContext(candidatesTsvPath, corpusTsvPath, importDir, outputDir, log, trace, stages);

        log.Section("入力", "Input");
        log.Line($"候補: {candidatesTsvPath}（{ctx.AllCandidates.Count}件）", $"Candidates: {candidatesTsvPath} ({ctx.AllCandidates.Count} entries)");
        log.Line($"コーパス: {corpusTsvPath}（{ctx.Corpus.Count - ctx.Imported.Count - ctx.Reference.Count}件）", $"Corpus: {corpusTsvPath} ({ctx.Corpus.Count - ctx.Imported.Count - ctx.Reference.Count} entries)");
        log.Line($"xTranslatorインポート: {ctx.Imported.Count}件（{importDir}）", $"xTranslator import: {ctx.Imported.Count} entries ({importDir})");
        log.Line($"参照用語集: {ctx.Reference.Count}件（Data/skyrim_taiyaku_reference.tsv、v0.29.0）", $"Reference glossary: {ctx.Reference.Count} entries (Data/skyrim_taiyaku_reference.tsv, v0.29.0)");
        log.Line($"チューニング: {TuningProfile.Current.Name}", $"Tuning: {TuningProfile.Current.Name}");

        var byPlugin = ctx.AllCandidates
            .GroupBy(c => c.WinningPlugin)
            .OrderByDescending(g => g.Count())
            .ToList();

        Console.WriteLine($"Generating Translation prompt packages for {byPlugin.Count} plugin(s), {ctx.AllCandidates.Count} candidates total...");

        var index = new List<(string Plugin, int Count, int AutoResolved, string Dir)>();
        var autoResolveByPlugin = new List<(string Plugin, int Count, int AutoResolved, long AutoResolvedChars, long RemainingChars, List<string> SampleRemaining)>();
        var uniqueForAi = 0;

        // v0.35.0: one line per plugin — 対象数/コーパス/意味合成/音訳/NameFallback/未解決
        // — deliberately a count table, not a per-word decomposition log (which would
        // bloat the log far more). Lets a person spot-check which plugins lean on the
        // lower-confidence steps (②意味合成/③音訳分解/④NameFallbackTranslator) before
        // opening that plugin's translations.tsv to actually read the results.
        log.Section("MOD別 手法別 自動解決件数（②③④の精度確認の入口・translations.tsvのNotes列と突き合わせて確認）",
            "Auto-resolved counts by MOD and method (a starting point for checking steps 2/3/4's accuracy — cross-check against translations.tsv's Notes column)");
        foreach (var group in byPlugin)
        {
            var candidates = group.ToList();
            var (_, _, pluginDir, autoCount, unique, autoResolvedChars, remainingChars, sampleRemaining, methodCounts) =
                WritePluginFilesWithDir(outputDir, group.Key, candidates, ctx.GlobalPhraseFrequency, ctx.Retriever, ctx.Auto, ctx.NameFallback, ctx.NpcNames, llmLocal, llmCloud, stages.EnableNameFallback, log, trace, discardUserEdits, llmLocalBatchCharLimit, llmCloudBatchCharLimit);
            index.Add((group.Key, candidates.Count, autoCount, pluginDir));
            autoResolveByPlugin.Add((group.Key, candidates.Count, autoCount, autoResolvedChars, remainingChars, sampleRemaining));
            uniqueForAi += unique;
            trace?.Debug($"{group.Key}: target {candidates.Count} entries, auto-resolved {autoCount} -> {pluginDir}");

            log.Line(
                $"{group.Key}: 対象{candidates.Count}件 " +
                $"コーパス{methodCounts.Corpus}件 意味合成{methodCounts.Meaning}件 音訳{methodCounts.Transliteration}件 " +
                $"NameFallback{methodCounts.NameFallback}件 ローカルLLM{methodCounts.Llm}件 生成AI{methodCounts.CloudLlm}件 未解決{candidates.Count - autoCount}件",
                $"{group.Key}: target {candidates.Count} " +
                $"corpus {methodCounts.Corpus} meaning {methodCounts.Meaning} transliteration {methodCounts.Transliteration} " +
                $"NameFallback {methodCounts.NameFallback} local LLM {methodCounts.Llm} cloud AI {methodCounts.CloudLlm} unresolved {candidates.Count - autoCount}");
        }

        WriteIndex(outputDir, index);
        var autoResolveReportPath = Path.Combine(outputDir, "auto_resolve_by_plugin.tsv");
        AutoResolveReportWriter.WriteTsv(autoResolveReportPath, autoResolveByPlugin);
        Console.WriteLine($"Wrote: {autoResolveReportPath}");

        var pluginSummaryPath = Path.Combine(outputDir, "plugin_summary.txt");
        PluginSummaryWriter.Write(pluginSummaryPath, Path.GetDirectoryName(candidatesTsvPath) ?? ".", autoResolveByPlugin);
        Console.WriteLine($"Wrote: {pluginSummaryPath}");
        var totalAuto = index.Sum(i => i.AutoResolved);
        // v0.53.0a: 「AI不要」という表記は、⑤ローカルLLM・⑥生成AI翻訳による解決も
        // 含んだ件数であるにもかかわらず「AIなしで解決した件数」であるかのように
        // 読めてしまっていたため、単純に「解決（①〜⑥）」に修正した（実データから
        // ユーザーが指摘・確認済み——DESIGN_NOTES.md「既知の課題」11.参照）。
        Console.WriteLine($"Resolved via steps ①〜⑥: {totalAuto} / {ctx.AllCandidates.Count}");
        Console.WriteLine($"Remaining for AI-chat pass: {ctx.AllCandidates.Count - totalAuto}");
        Console.WriteLine($"Done. Index written to: {Path.Combine(outputDir, "translation_index.txt")}");
        trace?.Info($"Done: resolved {totalAuto}/{ctx.AllCandidates.Count}, AI-chat target {ctx.AllCandidates.Count - totalAuto}");

        log.Section("処理サマリ", "Processing summary");
        log.Line($"対象プラグイン: {byPlugin.Count}", $"Target plugins: {byPlugin.Count}");
        log.Line($"解決（①〜⑥）: {totalAuto} / {ctx.AllCandidates.Count}", $"Resolved (steps 1-6): {totalAuto} / {ctx.AllCandidates.Count}");
        log.Line($"AIチャット対象: {ctx.AllCandidates.Count - totalAuto} → 同一英文の重複排除後 {uniqueForAi}",
            $"AI-chat target: {ctx.AllCandidates.Count - totalAuto} -> {uniqueForAi} after deduplicating identical English text");
        log.Line("※プラグインごとの内訳は translation_index.txt を、各件の訳は各フォルダの translations.tsv を参照",
            "* See translation_index.txt for the per-plugin breakdown, each folder's translations.tsv for individual translations");
    }

    /// <summary>
    /// Writes the two word-level tables the AutoTranslator DERIVES from the corpus,
    /// every run, so they can be eyeballed (v0.12.0).
    ///
    /// These are the only parts of the automatic pipeline that are inferred rather
    /// than looked up: ①・xTranslatorインポート・参照用語集はファイルをそのまま読むだけ
    /// だが、③の音訳辞書と④の意味辞書はメモリ上でのみ存在する。ここが誤りが最も
    /// 波及しやすい箇所でもある——1つの誤った単語レベルの対応が、その語を含む
    /// すべての候補に静かに伝播する（"Knockback" → "ノックダウン"、v0.7.1）。
    /// 無条件にダンプすることで、別コマンドの実行を覚えていなくてもレビューできる。
    ///
    /// The transliteration table carries its Origin column (official = the pair
    /// exists verbatim in the corpus; derived = this tool sliced it out of a longer
    /// phrase), which is the column to read first when checking for errors.
    /// </summary>
    private static void WriteDerivedTables(string outputDir, AutoTranslator auto, RunLog log, TraceLog? trace = null)
    {
        Directory.CreateDirectory(outputDir);

        var transliteration = auto.TransliterationTable.AllWords
            .OrderBy(w => w.English, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var transliterationPath = Path.Combine(outputDir, "derived_transliteration_dict.tsv");
        trace?.Debug($"Write start: {transliterationPath} ({transliteration.Count} entries)");
        using (var w = new StreamWriter(transliterationPath, false, System.Text.Encoding.UTF8))
        {
            w.WriteLine(string.Join('\t', "English", "Japanese", "Origin", "Source"));
            foreach (var (english, japanese, origin, source) in transliteration)
                w.WriteLine($"{english}\t{japanese}\t{origin}\t{source}");
        }
        trace?.Debug($"Write done: {transliterationPath}");

        var meaning = auto.MeaningTable.AllEntries
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.English, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var meaningPath = Path.Combine(outputDir, "derived_meaning_dict.tsv");
        trace?.Debug($"Write start: {meaningPath} ({meaning.Count} entries)");
        using (var w = new StreamWriter(meaningPath, false, System.Text.Encoding.UTF8))
        {
            w.WriteLine(string.Join('\t', "English", "Japanese", "Kind", "Source"));
            foreach (var (english, japanese, kind, source) in meaning)
                w.WriteLine($"{english}\t{japanese}\t{kind}\t{source}");
        }
        trace?.Debug($"Write done: {meaningPath}");

        var byOrigin = transliteration.GroupBy(t => t.Origin).ToDictionary(g => g.Key, g => g.Count());
        int Count(string origin) => byOrigin.GetValueOrDefault(origin);
        Console.WriteLine($"Derived tables: transliteration {transliteration.Count} " +
                          $"(official {Count("official")}, derived {Count("derived")}, sentence {Count("sentence")}), " +
                          $"meaning {auto.MeaningTable.HeadCount} head + {auto.MeaningTable.ModifierCount} modifier");

        log.Section("コーパスから導出した単語レベルの対応表（③④が使用・人手確認用）",
            "Word-level tables derived from the corpus (used by steps 3/4 — for manual review)");
        log.Line($"③音訳: {transliteration.Count}語", $"Step 3 transliteration: {transliteration.Count} words");
        log.Line($"    official {Count("official")}（コーパスにそのまま存在）", $"    official {Count("official")} (exists verbatim in the corpus)");
        log.Line($"    derived  {Count("derived")}（名前フィールドから切り出し）", $"    derived  {Count("derived")} (sliced out of a name field)");
        log.Line($"    sentence {Count("sentence")}（文章から共起統計で対応付け）", $"    sentence {Count("sentence")} (aligned from prose via co-occurrence)");
        log.Line($"④意味訳: 語尾 {auto.MeaningTable.HeadCount}語 / 修飾語 {auto.MeaningTable.ModifierCount}語",
            $"Step 4 meaning translation: head {auto.MeaningTable.HeadCount} words / modifier {auto.MeaningTable.ModifierCount} words");
        log.Line($"→ {transliterationPath}", $"-> {transliterationPath}");
        log.Line($"→ {meaningPath}", $"-> {meaningPath}");
        log.Line("※①はcorpus.tsvがそのまま対応表なので、ここには出していない",
            "* Step 1 is not listed here — corpus.tsv itself already is that table");

        // v0.44.2: flag learned head/modifier entries whose Japanese is just
        // 1-2 kanji characters — the exact shape of every bound-stem/homograph
        // bug found by hand this session ("Heavy"→"重", "Light"→"光", "Fall"→
        // "秋", "Farm"→"会話"...). Not every hit here is wrong — most 1-2 kanji
        // readings ("鎧", "盾", "剣") are perfectly ordinary standalone nouns —
        // but this turns "read the whole log by hand" into "skim a short
        // candidate list", and already caught "Farm"/"Locks" during this
        // session's own review that random sampling had missed. Only words NOT
        // already in the exclusion lists appear here (an excluded word never
        // reaches `meaning` at all), so this list only ever shrinks as entries
        // get confirmed and added to Data/corpus_exact_exclusions.txt or
        // Data/meaning_mining_exclusions.txt.
        foreach (var (english, japanese, kind, source) in meaning)
        {
            if (japanese.Length is < 1 or > 2) continue;
            if (!japanese.All(c => c is >= '一' and <= '鿿')) continue;
            log.Detail("2.意味合成の短い訳語（要レビュー・同綴異義語/連体詞化できない語幹の可能性）",
                "2. Short meaning-composed translation (needs review — possibly a homograph or a bound stem that can't stand alone)",
                $"\"{english}\"→\"{japanese}\"（{kind}）[{source}]");
        }
    }

    /// <summary>v0.29.0: the hand-curated, name-only word list
    /// (Data/name_glossary.tsv), loaded with the same simple "word\tjapanese"
    /// reader the (now-removed, v0.29.5) JMdict dictionary used to share.
    /// Consulted by <see cref="NameFallbackTranslator"/> after the corpus-mined
    /// sources — see that class's remarks for why that order.</summary>
    private static EnJaDictionary LoadNameGlossary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "name_glossary.tsv");
        return EnJaDictionary.Load(path);
    }

    /// <summary>v0.29.0: a load-order-independent official EN/JA reference —
    /// see DESIGN_NOTES.md. Same 5-column shape as corpus.tsv, so it's read with
    /// the existing <see cref="CorpusIo.ReadTsv"/> and simply appended to the
    /// corpus; every downstream consumer (exact match, transliteration mining,
    /// meaning mining) already keys its trust level off SourceKind, so nothing
    /// else needs to know this data came from a file rather than a live scan.</summary>
    private static List<CorpusEntry> LoadReferenceGlossary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "skyrim_taiyaku_reference.tsv");
        return File.Exists(path) ? CorpusIo.ReadTsv(path) : new List<CorpusEntry>();
    }

    private static (string PromptPath, string TemplatePath, string PluginDir, int AutoResolvedCount, int UniqueForAi,
        long AutoResolvedChars, long RemainingChars, List<string> SampleRemaining,
        (int Corpus, int Meaning, int Transliteration, int NameFallback, int Llm, int CloudLlm) MethodCounts) WritePluginFilesWithDir(
        string outputDir, string plugin, List<Candidate> candidates, ModPhraseGlossary.GlobalNgramFrequency globalPhraseFrequency, PrecedentRetriever retriever, AutoTranslator auto,
        NameFallbackTranslator nameFallback, IReadOnlySet<string> npcNames, ITextTranslator? llmLocal, ITextTranslator? llmCloud, bool enableNameFallback,
        RunLog log, TraceLog? trace = null, bool discardUserEdits = false,
        int llmLocalBatchCharLimit = DefaultLocalLlmBatchCharLimit, int llmCloudBatchCharLimit = DefaultLlmBatchCharLimit)
    {
        trace?.Trace($"Plugin processing start: {plugin} ({candidates.Count} candidates)");
        var safeName = MakeSafeFolderName(plugin);
        var pluginDir = Path.Combine(outputDir, safeName);
        Directory.CreateDirectory(pluginDir);

        // 2026-09-17: MOD特有語彙ヒント（mod_glossary.tsv、⑤/⑥のissue #4「c」に
        // 合流させる。④専用のData/mod_glossary/*.tsvとは別物）——このMOD自身の
        // 候補と、ロード順全体の候補を比較し、このMODに特徴的な未解決フレーズを
        // 検出してテンプレート出力する。既存の記入済み内容はWriteTemplate側で
        // 保持される（ModPhraseGlossary.WriteTemplateの remarks 参照）。
        var localTexts = candidates.Select(c => c.CurrentText).Distinct(StringComparer.Ordinal).ToList();
        var detectedPhrases = ModPhraseGlossary.DetectCandidatePhrases(localTexts, globalPhraseFrequency, auto);
        ModPhraseGlossary.WriteTemplate(pluginDir, plugin, detectedPhrases);

        var promptPath = Path.Combine(pluginDir, "prompt.txt");
        var templatePath = Path.Combine(pluginDir, "translations.tsv");
        var ordered = candidates.OrderBy(c => c.FormId).ToList();

        // v0.52.1a: a candidate that already has SOME translation in the
        // existing translations.tsv (any method — ①〜⑥ auto-resolution or a
        // human's SJPTS_ModifiedByUser edit) is carried straight through and never
        // re-run — see ReadExistingTranslations's remarks for why this changed
        // from "only preserve SJPTS_ModifiedByUser" to "preserve anything already
        // resolved" (⑤/⑥ cost real tokens; silently redoing them on every
        // `translation` run was the bug). discardUserEdits (the GUI's
        // "初期化"/"MO2再読込＆初期化") skips this entirely for a clean ①のみ
        // reset.
        var existing = discardUserEdits
            ? new Dictionary<(string, string, int), (string Japanese, string Method, string TranslationCheck)>()
            : ReadExistingTranslations(templatePath);
        if (existing.Count > 0)
            trace?.Debug($"{plugin}: carrying forward {existing.Count} already-translated row(s) from existing {templatePath}");

        var resolved = ordered
            .Select(c => existing.TryGetValue((c.FormId, c.RecordType, c.Index), out var preserved)
                ? (Candidate: c, Auto: (AutoTranslationResult?)new AutoTranslationResult(preserved.Japanese, preserved.Method, "", preserved.TranslationCheck))
                : !string.IsNullOrEmpty(c.CrossModPrecedentJapanese)
                    // v0.56.0: a cross-mod precedent (PickUpTargetRunner.cs's
                    // FindCrossModPrecedent) is keyed on record identity, not
                    // text -- it takes priority even over ①コーパス完全一致.
                    ? (Candidate: c, Auto: (AutoTranslationResult?)new AutoTranslationResult(c.CrossModPrecedentJapanese, "SJPTS_AutoCrossModPrecedent", ""))
                    : (Candidate: c, Auto: auto.TryTranslate(c.CurrentText, c.RecordType, trace)))
            .ToList();

        // v0.36.0: step 1（コーパス完全一致）は正解データそのものなのでログしない。
        // 2.意味合成・3.音訳分解は「組み合わせ」の結果なので、内訳（Detail）込みで
        // 全件ログする——146件程度（v0.35.0時点の実測）であればログの肥大化にならず、
        // かつ「なぜその訳になったか」を都度手で追わずに済む。
        foreach (var (candidate, autoResult) in resolved)
        {
            switch (autoResult?.Method)
            {
                case "SJPTS_AutoCorpusMeaning" or "SJPTS_AutoCorpusMeaningTranslit":
                    log.Detail("2.意味合成による自動解決（要レビュー）",
                        "2. Auto-resolved via meaning composition (needs review)",
                        $"[{plugin}]  \"{candidate.CurrentText}\" → \"{autoResult.Japanese}\"  [{candidate.RecordType}]  {autoResult.Detail}");
                    break;
                case "SJPTS_AutoCorpusTransliterate":
                    log.Detail("3.音訳分解による自動解決（要レビュー）",
                        "3. Auto-resolved via transliteration decomposition (needs review)",
                        $"[{plugin}]  \"{candidate.CurrentText}\" → \"{autoResult.Japanese}\"  [{candidate.RecordType}]  {autoResult.Detail}");
                    break;
                case "SJPTS_AutoCrossModPrecedent" when candidate.CrossModPrecedentNeedsReview:
                    // v0.56.0: this tool doesn't adjudicate whether a
                    // precedent translation is still objectively correct for
                    // the current text (mirrors the existing DSD stale-
                    // coverage handling) -- the precedent is applied either
                    // way, this warning only flags it for human review.
                    log.Detail("要レビュー: 別MOD由来の過去訳を適用したが、原文の一致を確認できなかった（staleの可能性）",
                        "Needs review: applied a cross-mod precedent translation, but could not confirm the original text still matches (may be stale)",
                        $"[{plugin}]  \"{candidate.CurrentText}\" → \"{autoResult.Japanese}\"  [{candidate.RecordType}]");
                    break;
            }
        }

        // v0.28.0: for whatever AutoTranslator gave up on, try the lower-confidence
        // Translation-stage-only name fallback (word-by-word modifier
        // decomposition, English left standing for genuinely unknown words). Every
        // hit is logged in full — this is explicitly lower-quality output and
        // needs to stay reviewable.
        // v0.31.0: this plugin's own scoped glossary, and the blocking words the
        // chain reports on the way through — see ModGlossary's remarks.
        var modGlossary = ModGlossary.LoadFor(plugin);
        var blockedBy = new Dictionary<string, (int Count, string Example)>(StringComparer.OrdinalIgnoreCase);

        // v0.49.1: --no-namefallback skips 4. entirely — candidates it would have
        // resolved simply stay unresolved (fall to 5./prompt.txt like any other
        // gap), and blockedBy stays empty (nothing was attempted, so nothing was
        // blocked; ModGlossary.WriteTemplate below gets an empty list, which is
        // correct — there's no MOD-glossary decision to ask for if 4. never ran).
        if (enableNameFallback)
        {
            resolved = resolved
                .Select(r =>
                {
                    if (r.Auto != null) return r;
                    var fallback = nameFallback.TryTranslate(r.Candidate.CurrentText, r.Candidate.RecordType, modGlossary, out var blockers);
                    foreach (var word in blockers.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        blockedBy[word] = blockedBy.TryGetValue(word, out var seen)
                            ? (seen.Count + 1, seen.Example)
                            : (1, r.Candidate.CurrentText);
                    }
                    if (fallback == null) return r;
                    log.Detail("4.NameFallbackTranslatorによる自動解決（低精度・要レビュー）",
                        "4. Auto-resolved via NameFallbackTranslator (low confidence, needs review)",
                        $"[{plugin}]  \"{r.Candidate.CurrentText}\" → \"{fallback.Japanese}\"  [{r.Candidate.RecordType}]  {fallback.Detail}");
                    return (r.Candidate, Auto: (AutoTranslationResult?)new AutoTranslationResult(fallback.Japanese, fallback.Method, fallback.Detail));
                })
                .ToList();
        }

        ModGlossary.WriteTemplate(plugin, blockedBy
            .Select(kv => new ModGlossary.Blocker(kv.Key, kv.Value.Count, kv.Value.Example))
            .ToList());
        if (modGlossary.FilledCount > 0)
            log.Line($"MOD用語集: {plugin} → {modGlossary.FilledCount}語を適用", $"MOD glossary: {plugin} -> applied {modGlossary.FilledCount} word(s)");

        // v0.49.0/v0.52.1a: step 5 — whatever 1.〜4. still couldn't resolve, try a
        // locally-running LLM IF the user opted in (see LocalLlmTranslator's
        // remarks for why this is deliberately last among the free/automatic
        // steps, not a replacement for any earlier one). step 6 then gets
        // whatever's STILL unresolved after step 5 (including step 5 disabled
        // entirely) and tries a cloud AI backend — the two are independent
        // opt-ins that chain, exactly like ①〜④ already fall through to each
        // other. See ApplyLlmStep for the shared per-candidate logic.
        resolved = ApplyLlmStep(resolved, llmLocal, "5", "ローカルLLM", "local LLM", "SJPTS_TranslationLocalLlm", plugin, retriever, auto, npcNames, log, trace, pluginDir, llmLocalBatchCharLimit);
        resolved = ApplyLlmStep(resolved, llmCloud, "6", "生成AI翻訳", "cloud AI", "SJPTS_TranslationCloudLlm", plugin, retriever, auto, npcNames, log, trace, pluginDir, llmCloudBatchCharLimit);

        var unresolved = resolved.Where(r => r.Auto == null).Select(r => r.Candidate).ToList();

        trace?.Trace($"Write start: {promptPath} (unresolved {unresolved.Count})");
        var uniqueForAi = WritePrompt(promptPath, plugin, unresolved, retriever, auto, npcNames,
            blockedBy.Select(kv => new ModGlossary.Blocker(kv.Key, kv.Value.Count, kv.Value.Example))
                .OrderByDescending(b => b.BlockedCount).ThenBy(b => b.Word, StringComparer.OrdinalIgnoreCase).ToList());
        trace?.Trace($"Write done: {promptPath} (deduplicated for AI chat: {uniqueForAi})");

        trace?.Trace($"Write start: {templatePath} ({resolved.Count} entries)");
        WriteTranslationTemplate(templatePath, resolved);
        trace?.Trace($"Write done: {templatePath}");

        // v0.19.0: per-plugin AutoTranslator-solvability stats, for
        // AutoResolveReportWriter — "would this MOD likely need a human/AI at
        // all, or does the corpus/dictionary/transliteration pipeline already
        // cover most of it?" Char counts (not just counts) matter for the same
        // reason CoverageReportWriter's do: a handful of remaining CANDIDATES can
        // still mean a huge amount of untranslated TEXT.
        var autoResolvedChars = resolved.Where(r => r.Auto != null).Sum(r => (long)r.Candidate.CurrentText.Length);
        var remainingChars = unresolved.Sum(c => (long)c.CurrentText.Length);
        var sampleRemaining = unresolved.Take(3).Select(c => TextPreview.Truncate(c.CurrentText, 40)).ToList();
        var methodCounts = CountByMethod(resolved);

        return (promptPath, templatePath, pluginDir, resolved.Count - unresolved.Count, uniqueForAi,
            autoResolvedChars, remainingChars, sampleRemaining, methodCounts);
    }

    /// <summary>⑥生成AI翻訳（クラウド）のプラグイン単位バッチの上限に、CLIから
    /// <c>--llm-cloud-batch-char-limit=</c>で明示指定が無いときのデフォルト値。実測
    /// （Light Greatswords.esp: 候補32件・原文計4,438文字→出力12,346トークン）
    /// から逆算した安全マージン込みの値——生成AIサービスや契約プランによって
    /// 妥当な値は変わりうる（Claude Code CLIのProプランは余裕があるが、無料枠や
    /// 他サービスではもっと小さくする必要があるかもしれない）ため、固定値では
    /// なく呼び出し側から上書きできるようにしてある。</summary>
    public const int DefaultLlmBatchCharLimit = 12_000;

    /// <summary>v0.58.1: ⑤ローカルLLM翻訳の既定値。⑥（上記）と共通だったが、
    /// ⑤専用の実機検証（`Cloaks_SMP_Patch.esp`、gemma3:12b・gemma4:26b双方）で、
    /// 12000のままだと大きすぎて成功率が大幅に下がり（例: gemma3で7/51件→2000
    /// 文字にしただけで52/61件）、逆に500まで下げても2000と解決件数は変わらず
    /// 実行時間だけ3倍に悪化することを確認し、実測で頭打ちだった2000よりやや
    /// 余裕を持たせた3000を既定値としていた。
    ///
    /// 2026-09-16: `PrecedentRetriever`をTF-IDF＋コサイン類似度に刷新した新方式
    /// で改めて実機検証（gemma4:26b、`reasoning_effort: "none"`）したところ、
    /// 実際の指示文（2,692字）込みの合計で7,000字（b+d予算）までは安定して
    /// 完璧な形式で応答したが、8,000字（合計10,453字）では`finish_reason=length`
    /// で打ち切りが発生した。issue #4（同一MOD既訳ヒント）実装時にさらに文字数
    /// が上乗せされる余地を残すため、7,000より余裕を持たせた6,000を新しい
    /// 既定値とする。GUI側の既定値（`InterfaceTextPanel.cs`・`MainForm.cs`の
    /// `DefaultLlmLocalBatchCharLimit`、既に6,000）とこれで一致する。</summary>
    public const int DefaultLocalLlmBatchCharLimit = 6_000;

    /// <summary>v0.53.0a: 既知の課題13.の対応——改行を含む原文をバッチ送信する際、
    /// `\n`をこの目印タグへ一時的に置き換えて1行に収める（実データ・67件超の
    /// タグ付き単一行候補が正確に翻訳・保持されている実績から、モデルは既存の
    /// `&lt;mag&gt;`等のプレースホルダータグと同様にこれも変更・削除せず維持できる
    /// と見込める）。実在のMOD文字列・DSDタグと衝突しないよう、ツール専用の
    /// プレフィックス（一時ファイル名で使っているsjpts_と同じ由来）を使う。</summary>
    private const string MultilineBreakMarker = "<SJPTS_BR>";

    /// <summary>v0.58.5: wraps each candidate's original text on the
    /// "Target:" line (see <see cref="BuildCandidateBlock"/>) instead of the
    /// former <c>Target: "..."</c> double-quote wrapping — see FlattenMultiline's
    /// neighboring remarks for why the quote-based approach was replaced.
    /// A tag pair unlikely to collide with real game text was chosen
    /// deliberately (as opposed to, say, wrapping in <c>[...]</c> — real
    /// Skyrim mod data commonly uses both bracket styles already, e.g.
    /// <c>&lt;font face='...'&gt;</c>/<c>&lt;Global=...&gt;</c> and <c>[E] ...</c>/
    /// <c>[ELLE] ...</c> mod-name prefixes; confirmed via real gemma4 testing
    /// that the model correctly distinguishes this wrapper from a candidate's
    /// own embedded angle-bracket markup rather than getting confused by the
    /// visual similarity).</summary>
    private const string TargetTagOpen = LlmBatchTranslationEngine.TargetTagOpen;
    private const string TargetTagClose = LlmBatchTranslationEngine.TargetTagClose;

    /// <summary>改行を含む候補だけに適用する——単一行の候補はそのまま
    /// <see cref="ApplyLlmStep"/>に渡す（無駄な変換を増やさない）。</summary>
    private static string FlattenMultiline(string text) => text.Replace("\r\n", "\n").Replace("\n", MultilineBreakMarker);

    // v0.58.5: 既知の課題——旧方式（v0.58.4まで）は"Target: \"...\""のように
    // 原文を引用符で囲んで送っていたため、原文自体が引用符を含む台詞（例:
    // "Do you take me for a fool, Ulfrand?" she snapped. "..."）だと境界が
    // 四重引用符になり照合が恒久的に失敗する、あるいは応答側の引用符除去が
    // 「」で既に自然に訳出済みの引用を末尾の"としてもう一度表現してしまう、
    // といった問題が実機で確認された（DoubleQuoteMarker/MarkBoundaryQuotesと
    // いう目印タグでの後始末を試みたが、根本原因である「区切り文字に"を使う
    // こと自体」は解消できなかった）。実機検証（HTMLタグ<font>や[E]接頭辞を
    // 含む候補を含む）の結果、区切りを"Target: <SJPTS_TARGET>...</SJPTS_TARGET>"
    // というゲーム内テキストと衝突しないタグへ変更することで、原文の引用符を
    // 一切加工せずそのまま送れることを確認したため、この対応（＋その後始末の
    // ためだけに存在したDoubleQuoteMarker/MarkBoundaryQuotes/
    // StripOuterQuoteIndependently）は丸ごと不要になり削除した——詳細は
    // BuildCandidateBlockのTarget行、LlmBatchInstruction参照。

    /// <summary>
    /// v0.52.1a: shared body for step 5 (ローカルLLM) and step 6 (生成AI翻訳・クラウド) —
    /// both are "whatever's still unresolved, try this LLM backend" with identical
    /// mechanics. The only difference between the two calls is which
    /// <paramref name="llm"/> instance (or null — step disabled) and which step
    /// number/label/method tag to log under. Called twice in sequence (5 then 6)
    /// so 6 only ever sees candidates 5 left behind — a chain, not two
    /// independent passes.
    ///
    /// 2026-09-16: this is now a thin ESP-specific wrapper around <see
    /// cref="LlmBatchTranslationEngine.Run{TItem}"/>, which owns the actual
    /// pass/circuit-breaker/send/parse/match loop shared with <c>SJPTS_
    /// InterfaceText/InterfaceTextPromptGenerator.cs</c>'s own ApplyLlmStep —
    /// see that engine's own class remarks for why this extraction happened
    /// (repeated maintenance drift from hand-copying the same "hard part" logic
    /// into both files) and what it fixed. What stays here, ESP-specific: the
    /// <see cref="Candidate"/> data model, <see cref="BuildCandidateBlock"/>
    /// ("b" reference examples, NPC-name/glossary hints), multiline-candidate
    /// flattening (<see cref="FlattenMultiline"/>/<see
    /// cref="StripSpuriousBoundaryMarker"/>), and fanning the engine's
    /// per-text answers back out onto every <see cref="Candidate"/> sharing
    /// that text (<paramref name="resolved"/> may hold several).
    /// </summary>
    /// <param name="pluginDir">Where to write each call's actual sent prompt
    /// (instruction + candidate block(s), byte-for-byte what goes into
    /// <see cref="ITextTranslator.TryTranslate"/>) as "prompt_{localLLM|
    /// cloudLLM}_call{N}.txt". Distinct from this method's own
    /// <c>WritePrompt</c>-written prompt.txt (a human AI-chat handoff for
    /// whatever's STILL unresolved after THIS step; this one is a record of
    /// what WAS actually sent, win or lose). Only this step's own prefix
    /// (localLLM vs cloudLLM, from <paramref name="stepNumber"/>) is cleared
    /// first, so step 6 doesn't wipe out step 5's files from the same run.</param>
    private static List<(Candidate Candidate, AutoTranslationResult? Auto)> ApplyLlmStep(
        List<(Candidate Candidate, AutoTranslationResult? Auto)> resolved, ITextTranslator? llm,
        string stepNumber, string stepLabelJa, string stepLabelEn, string methodTag,
        string plugin, PrecedentRetriever retriever, AutoTranslator auto, IReadOnlySet<string> npcNames,
        RunLog log, TraceLog? trace, string pluginDir, int batchCharLimit = DefaultLlmBatchCharLimit)
    {
        if (llm == null) return resolved;

        var beforeStep = resolved.Where(r => r.Auto == null).Select(r => r.Candidate).ToList();
        if (beforeStep.Count == 0) return resolved;

        var providerLabel = stepNumber == "5" ? "localLLM" : "cloudLLM";
        var referenceLimits = stepNumber == "5" ? LocalLlmReferenceLimits : CloudLlmReferenceLimits;

        var answers = LlmBatchTranslationEngine.Run(new LlmBatchTranslationEngine.Options<Candidate>
        {
            Items = beforeStep,
            TextOf = c => c.CurrentText,
            RecordTypeOf = g => g.First().RecordType,
            BuildBlock = (g, matchKey) => BuildCandidateBlock(g, retriever, auto, npcNames, batchCharLimit, referenceLimits,
                targetTextOverride: matchKey != g.Key ? matchKey : null),
            NeedsMatchKeyFlatten = key => key.IndexOf('\n') >= 0,
            FlattenForMatching = FlattenMultiline,
            UnflattenAnswer = s => s.Replace(MultilineBreakMarker, "\n"),
            StripSpuriousBoundaryMarker = StripSpuriousBoundaryMarker,
            InstructionText = LlmBatchInstruction,
            ExternalSameModBaseline = BuildSameModHintPool(resolved, plugin)
                .Concat(ModPhraseGlossary.LoadFilled(pluginDir)
                    .Select(kv => new CorpusEntry(kv.Key, kv.Value, plugin, "SJPTS_ModifiedByUser", "")))
                .ToList(),
            SameModSourceLabel = plugin,
            Translator = llm,
            BatchCharLimit = batchCharLimit,
            DebugDir = pluginDir,
            DebugFilePrefix = $"prompt_{providerLabel}_call",
            MethodTag = methodTag,
            Log = log,
            Trace = trace,
            LogScope = plugin,
            StepNumber = stepNumber,
            StepLabelJa = stepLabelJa,
            StepLabelEn = stepLabelEn,
        });

        if (answers.Count == 0) return resolved;
        return resolved.Select(r => r.Auto == null && answers.TryGetValue(r.Candidate.CurrentText, out var a)
            ? (r.Candidate, Auto: (AutoTranslationResult?)a)
            : r).ToList();
    }

    /// <summary>issue #4 (c): builds the pool <see cref="SameModHintBlockBuilder.BuildBlock"/>
    /// draws hints from — this plugin's own candidates already resolved this
    /// session, restricted to <see cref="SameModHintBlockBuilder.IsEligibleMethod"/>'s
    /// trust tier (excludes the `SJPTS_AutoCorpus`* family, already reachable via
    /// "Reference examples", and the `*NoJapanese` variants, a possible
    /// translation failure).</summary>
    private static List<CorpusEntry> BuildSameModHintPool(
        IEnumerable<(Candidate Candidate, AutoTranslationResult? Auto)> resolved, string plugin) =>
        resolved
            .Where(r => r.Auto != null && SameModHintBlockBuilder.IsEligibleMethod(r.Auto.Method))
            .Select(r => new CorpusEntry(r.Candidate.CurrentText, r.Auto!.Japanese, plugin, r.Auto.Method, r.Candidate.RecordType))
            .ToList();

    /// <summary>v0.58.6: 既知の課題26.関連の実機調査（unofficial skyrim special
    /// edition patch.espの"Heretical Thoughts"、gemma4:26b/gemma3:12b/
    /// qwen2.5:14b-instructの3モデル全てで再現）で判明した挙動への救済——
    /// 複数行候補（原文にMultilineBreakMarkerを含む）の英文再掲で、モデルが
    /// 内容自体は完璧に再掲しつつ、末尾に余分なMultilineBreakMarkerを1つ
    /// （まれに閉じタグ風に崩れた"&lt;/SJPTS_BR&gt;"として）付け足してから
    /// タブへ続ける、という完全一致を壊す振る舞いを高確率で取ることを確認した
    /// （候補原文が閉じタグ等の目印で終わらず地の文のまま終わる場合に特に
    /// 起きやすい）。このマーカーは意味を持たない自前の目印記号なので、
    /// 末尾/先頭から1個だけ剥がすのは翻訳内容に一切影響しない。
    ///
    /// ただしPickUpTarget/out_temp/candidates.tsvの実データには、原文が
    /// 正当に改行で始まる/終わる候補が315+706件存在し（例:
    /// "10F786:Skyrim.esm"のBOOK CNAM、末尾が実際に"&lt;/font&gt;&lt;/p&gt;\n"）、
    /// matchKey自体が正当にこのマーカーで始まる/終わることがある。そのため
    /// 呼び出し側（ApplyLlmStep）では、この関数の戻り値を通常のbyLine辞書とは
    /// 別のフォールバック辞書に格納し、通常の完全一致が失敗した場合だけ参照する
    /// ——正しくマーカーごと再掲された場合の一致を壊さないための設計。</summary>
    private static string StripSpuriousBoundaryMarker(string source)
    {
        var t = source;
        const string malformedClose = "</SJPTS_BR>";
        if (t.EndsWith(MultilineBreakMarker, StringComparison.Ordinal))
            t = t[..^MultilineBreakMarker.Length];
        else if (t.EndsWith(malformedClose, StringComparison.Ordinal))
            t = t[..^malformedClose.Length];
        if (t.StartsWith(MultilineBreakMarker, StringComparison.Ordinal))
            t = t[MultilineBreakMarker.Length..];
        return t;
    }

    /// <summary>
    /// v0.35.0: tallies how many of this plugin's candidates were auto-resolved by
    /// EACH resolution step, keyed off the Notes/Method tag that already labels
    /// every row. Exists so the log can report per-plugin accuracy-relevant volume
    /// (①コーパス完全一致 has ground truth behind it; ②意味合成/③音訳分解 are
    /// corpus-corroborated inference; ④NameFallbackTranslator is the lowest-
    /// confidence, uncorroborated fallback — see DESIGN_NOTES.md's renumbering)
    /// WITHOUT logging every individual word-level decomposition, which would
    /// bloat the log far more than a handful of numbers per plugin does.
    /// </summary>
    private static (int Corpus, int Meaning, int Transliteration, int NameFallback, int Llm, int CloudLlm) CountByMethod(
        List<(Candidate Candidate, AutoTranslationResult? Auto)> resolved)
    {
        int corpus = 0, meaning = 0, transliteration = 0, nameFallback = 0, llm = 0, cloudLlm = 0;
        foreach (var (_, auto) in resolved)
        {
            switch (auto?.Method)
            {
                case "SJPTS_AutoCorpus" or "SJPTS_AutoCorpusDsd" or "SJPTS_AutoCorpusImported" or "SJPTS_AutoCorpusReferenceTaiyaku" or "SJPTS_AutoCorpusOverride" or "SJPTS_AutoCrossModPrecedent":
                    corpus++;
                    break;
                case "SJPTS_AutoCorpusMeaning" or "SJPTS_AutoCorpusMeaningTranslit":
                    meaning++;
                    break;
                case "SJPTS_AutoCorpusTransliterate":
                    transliteration++;
                    break;
                case "SJPTS_TranslationNameFallback":
                    nameFallback++;
                    break;
                case "SJPTS_TranslationLocalLlm":
                    llm++;
                    break;
                case "SJPTS_TranslationCloudLlm":
                    cloudLlm++;
                    break;
            }
        }
        return (corpus, meaning, transliteration, nameFallback, llm, cloudLlm);
    }

    private static void WriteIndex(string outputDir, List<(string Plugin, int Count, int AutoResolved, string Dir)> index)
    {
        using var w = new StreamWriter(Path.Combine(outputDir, "translation_index.txt"), false, System.Text.Encoding.UTF8);
        w.WriteLine("# Translation 一括生成インデックス（候補数の多い順）");
        w.WriteLine($"# 生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"# プラグイン数: {index.Count}, 候補総数: {index.Sum(i => i.Count)}, 自動解決: {index.Sum(i => i.AutoResolved)}");
        w.WriteLine();
        foreach (var (plugin, count, autoResolved, dir) in index)
            w.WriteLine($"{count,6}件（自動解決{autoResolved,5}件）\t{plugin}\t{dir}");
    }

    /// <summary>Established renderings for the individual words of a candidate,
    /// in the order they appear. Duplicates are dropped, and words with no attested
    /// rendering are simply absent — an empty result means the line is omitted
    /// rather than a "none found" note, since this is supplementary.
    ///
    /// v0.47.0: widened from ③音訳のみ（かつ<see cref="CorpusTransliterator.StandsAlone"/>
    /// が課す「derivedは単独では信頼しない」制約つき）to also check ①完全一致
    /// （<see cref="AutoTranslator.TryExactWord"/>）・②意味（<see cref="AutoTranslator.MeaningTable"/>）
    /// and, for ③, <see cref="CorpusTransliterator.TryLookupWordForHint"/>
    /// （StandsAlone制約なし版）. 実データで見つかったギャップに対応: `"Nirn"`
    /// （惑星名）はこのロードオーダーのコーパス/参照対訳表のどこにも単体では
    /// 存在せず（すべて`"Nirnroot"`という無関係な複合語の中）、
    /// `CorpusTransliterator`の反復ブートストラップが`derived`（複合語からの
    /// 切り出し）として`"Nirn"`→`"ニルン"`を既に学習済みだったにもかかわらず、
    /// 旧実装（StandsAlone制約つき）はこれを拾えなかった。ここはヒント表示用
    /// （AIチャット/ローカルLLM向けの参考情報）であって自動確定ではないため、
    /// 「候補全体の訳として単独では信用しない」というStandsAloneの安全策を
    /// 適用する理由がない。</summary>
    private static List<(string English, string Japanese)> WordGlossary(string candidateText, AutoTranslator auto)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();

        foreach (var raw in candidateText.Split(new[] { ' ', '-', '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new string(raw.Where(c => char.IsAsciiLetter(c) || c == '\'').ToArray());
            if (word.Length < 3 || !seen.Add(word)) continue;

            if (auto.TryExactWord(word, out var exactJapanese, out _))
                result.Add((word, exactJapanese));
            else if (auto.MeaningTable.TryTranslateWord(word, out var meaningJapanese, out _))
                result.Add((word, meaningJapanese));
            else if (auto.TransliterationTable.TryLookupWordForHint(word, out var translitJapanese, out _))
                result.Add((word, translitJapanese));
        }
        return result;
    }

    /// <summary>v0.48.1: shared tokenizer for the NPC-name and plugin-brand-name
    /// hints below — same punctuation-splitting/ASCII-letter-only convention as
    /// <see cref="WordGlossary"/>, factored out since both new hints need it.</summary>
    private static IEnumerable<string> SplitToWords(string text) =>
        text.Split(new[] { ' ', '-', '(', ')', '[', ']', ',', '.', '_', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(raw => new string(raw.Where(char.IsAsciiLetter).ToArray()))
            .Where(w => w.Length > 0);

    /// <summary>
    /// v0.31.0: the "decide these words" ask, emitted AHEAD of the per-string
    /// list because answering it is what makes most of that list unnecessary —
    /// mod item names are combinatorial, so a handful of words releases dozens
    /// of names (see <see cref="ModGlossary"/>). Only the words carrying real
    /// leverage are listed; a long tail of one-off words is better handled by
    /// just translating those names directly further down.
    /// </summary>
    private static void WriteGlossarySection(TextWriter writer, string plugin, IReadOnlyList<ModGlossary.Blocker> blockers)
    {
        var worthwhile = blockers.Where(b => b.BlockedCount >= 2).ToList();
        if (worthwhile.Count == 0) return;

        var released = worthwhile.Sum(b => b.BlockedCount);
        writer.WriteLine("=========================================================================");
        writer.WriteLine("[PLEASE DO THIS FIRST] Decide translations for words repeated across this mod");
        writer.WriteLine("=========================================================================");
        writer.WriteLine($"The {worthwhile.Count} word(s) below appear in common across multiple item names in \"{plugin}\".");
        writer.WriteLine("Just deciding a translation for each word lets the tool mechanically assemble every name that");
        writer.WriteLine("contains it (faster and more consistent than translating every name below one by one).");
        writer.WriteLine();
        writer.WriteLine("- Series names / coined terms (mod-specific proper nouns) should be rendered phonetically in katakana.");
        writer.WriteLine("- This glossary applies ONLY to this mod and never affects any other mod. It doesn't need to be the");
        writer.WriteLine("  single universally-correct translation — it only needs to fit this mod's own item lineup.");
        writer.WriteLine($"- If the word itself is an internal tag that shouldn't be translated at all (e.g. \"SMP\"), answer \"{ModGlossary.PassThroughMarker}\" (it will be output as-is).");
        writer.WriteLine("- Leaving a word blank is fine — any name containing it just stays untranslated (in English).");
        writer.WriteLine();
        writer.WriteLine("Output format is the same TSV, \"English word<TAB>Japanese translation\".");
        writer.WriteLine($"(These {worthwhile.Count} word(s) bring a combined {released} name(s) closer to being resolved.)");
        writer.WriteLine();
        foreach (var b in worthwhile)
            writer.WriteLine($"- Word: \"{b.Word}\"  (used in {b.BlockedCount} name(s))  example: \"{b.Example}\"");
        writer.WriteLine();
        writer.WriteLine($"Please write your answers into Data/mod_glossary/{MakeSafeFolderName(plugin)}.tsv.");
        writer.WriteLine("=========================================================================");
        writer.WriteLine();
    }

    /// <summary>v0.49.0: the single-candidate counterpart to WritePrompt's header —
    /// same instructions, minus the "how to answer many strings in one batch" TSV
    /// framing (step 5 sends one candidate per HTTP request, so that framing
    /// doesn't apply), plus an explicit "one line, no preamble" instruction so the
    /// response can be used as the Japanese column directly.</summary>
    /// <summary>v0.52.1a: batched form of the old single-candidate instruction —
    /// see <see cref="ApplyLlmStep"/>'s remarks for why calls are now batched per
    /// plugin. The output-format paragraph is the same "match by literal source
    /// text" contract <see cref="WritePrompt"/> already uses for the manual
    /// AI-chat prompt.txt.</summary>
    private const string LlmBatchInstruction =
        "Below are multiple strings from a Skyrim SE mod that are not yet translated into Japanese.\n" +
        "Each entry's \"Type\" says what the string actually is in-game.\n" +
        "Translate in a style that fits the type (a noun phrase for an item name, a short verb for an action prompt, etc.).\n" +
        "\"Reference examples\" are similar English/Japanese translation pairs already established in this user's\n" +
        "environment. Match your terminology to them for consistency. Proper nouns (personal/organization names, etc.)\n" +
        "are sometimes best rendered phonetically in katakana.\n" +
        "\"Known translations for words in this candidate\" are per-word translations already established in this\n" +
        "user's environment, for individual words that literally appear in the string. Prefer these where listed.\n" +
        "For any word NOT listed, translate it normally using your own judgment regardless of this hint (never skip it).\n" +
        "Translate EVERY word into Japanese, including simple/common ones — do not leave any English word in your answer\n" +
        "even if you're not fully confident in the translation, and do not add the original English in parentheses next to\n" +
        "your translation. For a proper noun you don't recognize, give your best phonetic katakana rendering rather than\n" +
        "leaving it in English.\n\n" +
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
        "to this instruction's own formatting.\n\n" +
        "Some Target strings contain the literal marker " + MultilineBreakMarker + ". Treat it exactly like any other\n" +
        "in-string placeholder tag (e.g. <mag>, <dur>): copy it unchanged, do not translate or remove it, and place it\n" +
        "at the corresponding point in your Japanese translation too — it stands in for a line break that was removed\n" +
        "before sending you this text, purely so your whole answer for that string fits on one output line.\n\n";

    internal static string MakeSafeFolderName(string plugin)
    {
        var name = Path.GetFileNameWithoutExtension(plugin);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }

    /// <summary>
    /// v0.5.0: the prompt groups candidates by their English text rather than
    /// emitting one block per record. Two reasons, both measured on real data:
    /// a meaningful share of a plugin's unresolved candidates are exact repeats
    /// of a string already listed elsewhere in the SAME plugin, and — more
    /// importantly — the SAME name legitimately appears under several record
    /// types at once (a spell, its magic effect and the perk that grants it all
    /// share one name; "Trained Rabbit" spans MGEF/SPEL/NPC_/QUST/PERK). Asking
    /// for one translation per unique string therefore both costs less and makes
    /// divergent translations of one concept structurally impossible within this
    /// plugin. Each block states every record type the string appears as, so the
    /// translator can pick wording that works for all of them, and answers come
    /// back keyed by the English text (see the instructions written below).
    ///
    /// v0.33.0: scoped strictly to THIS plugin — the cross-plugin
    /// ownership/deferral this docstring used to describe (v0.8.1) was retired
    /// along with the reflux mechanism it depended on to deliver an answer back
    /// to the deferred plugins (see DESIGN_HISTORY.md's v0.33.0 section). A
    /// string shared across mods is now simply asked about in every mod that
    /// has it — measured to be a small cost (2 of 332 unique remaining strings
    /// in one real load order), far cheaper than the owner/reflux machinery it
    /// would take to avoid it.
    /// </summary>
    /// <returns>How many UNIQUE strings the AI is actually being asked to translate,
    /// after grouping — the number that governs the real cost.</returns>
    private static int WritePrompt(
        string path, string targetPlugin, List<Candidate> targetCandidates, PrecedentRetriever retriever,
        AutoTranslator auto, IReadOnlySet<string> npcNames, IReadOnlyList<ModGlossary.Blocker> blockers)
    {
        var groups = targetCandidates
            .GroupBy(c => c.CurrentText, StringComparer.Ordinal)
            .ToList();

        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        writer.WriteLine("# Translation prompt package (AI-chat pass)");
        writer.WriteLine($"# Target plugin: {targetPlugin}");
        writer.WriteLine($"# Untranslated candidates: {targetCandidates.Count} ({groups.Count} unique after deduplication)");
        writer.WriteLine();
        writer.WriteLine("Below are strings from the Skyrim SE mod \"" + targetPlugin + "\" that are not yet translated into Japanese.");
        writer.WriteLine("Each entry's \"Type\" says what the string actually is in-game.");
        writer.WriteLine("Translate in a style that fits the type (a noun phrase for an item name, a short verb for an action prompt, etc.).");
        writer.WriteLine("\"Reference examples\" are similar English/Japanese translation pairs already established in this user's");
        writer.WriteLine("environment. Match your terminology to them for consistency. Proper nouns (personal/organization names, etc.)");
        writer.WriteLine("are sometimes best rendered phonetically in katakana. If a translation is difficult or you're not confident,");
        writer.WriteLine("say so explicitly.");
        writer.WriteLine("\"Known translations for words in this candidate\" are per-word translations already established in this");
        writer.WriteLine("user's environment, for individual words that literally appear in the string. Prefer these where listed.");
        writer.WriteLine("For any word NOT listed, translate it normally using your own judgment regardless of this hint (never skip it).");
        writer.WriteLine();
        writer.WriteLine("Output one line per string, in the format \"English source<TAB>Japanese translation\" (TSV).");
        writer.WriteLine($"Use the exact text inside the {TargetTagOpen}{TargetTagClose} tags after \"Target:\" below as the English source");
        writer.WriteLine("(this is the matching key) — do not include the tags themselves in your answer.");
        writer.WriteLine();

        WriteGlossarySection(writer, targetPlugin, blockers);

        foreach (var group in groups)
        {
            // 2026-09-16: prompt.txt（AI-chat向け、人間が1個ずつコピペする想定）は
            // バッチ分割の概念自体がないため、Reference examplesの予算計算には
            // 実際のLLM APIバッチ上限ではなくDefaultLlmBatchCharLimitを代用の
            // 基準値として使う（何らかの上限がないと無制限に肥大しうるため）。
            var (block, _) = BuildCandidateBlock(group, retriever, auto, npcNames, DefaultLlmBatchCharLimit, LocalLlmReferenceLimits);
            writer.Write(block);
        }

        return groups.Count;
    }

    /// <summary>参考例（Reference examples）1候補あたりの上限——「件数」と
    /// 「batchCharLimitに対する割合」の**どちらか先に達した方**で打ち切る
    /// （2026-09-17、実データ・議論を経て確定）。
    ///
    /// 経緯: 当初「候補ごとにbatchCharLimitの25%」という固定上限だった。実データ
    /// （Light Greatswords.esp）で、似た短い候補が並ぶMODではこれが1回のバッチに
    /// 詰め込める候補数を3件程度まで圧迫することが判明し、一度は「パス全体の
    /// 合計に対する共有スケール係数」方式に置き換えたが、今度は長文・汎用的な
    /// 候補（例: 武器の由来を説明する一文）が数百〜900件以上の類似実例を抱える
    /// ケースがあり、この外れ値がパス全体の合計を支配してしまい、他の候補の
    /// 参考例までほぼゼロに潰れてしまうことが判明した。相対比率0.4の足切り
    /// （PrecedentRetriever）は件数を一切制限しないため、この種の外れ値を
    /// 生み出しうる。
    ///
    /// 結論として、パス全体の集計という複雑な仕組みをやめ、**候補ごとに
    /// 独立した「件数」上限を追加**することで外れ値を直接抑え、かつ「割合」
    /// 上限も残すことで、候補ごとの文字数バランスも保つ。⑤ローカルLLM
    /// （実行コストが低く、時間も数時間程度まで許容できるため精度優先で
    /// やや緩め）と⑥クラウドLLM（呼び出し1回ごとに実費用が発生し、かつ
    /// 元々の精度が高いためヒントへの依存度も低いので、呼び出し回数の
    /// 最小化を優先してやや厳しめ）で別々の値を持つ。</summary>
    private readonly record struct ReferenceExampleLimits(int TopN, double BudgetRatio);

    private static readonly ReferenceExampleLimits LocalLlmReferenceLimits = new(TopN: 15, BudgetRatio: 0.15);
    private static readonly ReferenceExampleLimits CloudLlmReferenceLimits = new(TopN: 10, BudgetRatio: 0.10);

    /// <summary>
    /// The per-candidate detail block ("- Target: ..." through the trailing blank
    /// line) shared between the AI-chat prompt.txt (many blocks, one file) and
    /// step 5's local-LLM calls (one block per HTTP request). Factored out in
    /// v0.49.0 so both consumers stay byte-for-byte identical in what context they
    /// give the translator — the whole point of step 5 reusing this is that it
    /// sees exactly what a human answering prompt.txt would have seen.
    /// </summary>
    /// <param name="targetTextOverride">v0.53.0a: ⑤⑥のバッチ送信時、改行を含む
    /// 候補だけ"Target:"行の表示をMultilineBreakMarker適用済みの1行テキストに
    /// 差し替えるために使う（既知の課題13.）。null（既定）なら従来通り
    /// <c>first.CurrentText</c>をそのまま使う——Reference examples・Known
    /// translations等、他の文脈情報は本来の（改行を含む）原文のまま解析する
    /// （精度への影響を避けるため、"Target:"行の表示だけを差し替える）。
    /// prompt.txt（<see cref="WritePrompt"/>）はこの引数を渡さないため、
    /// 従来通り常にnullで、挙動は一切変わらない。v0.58.5:
    /// <c>&lt;SJPTS_TARGET&gt;</c>タグ方式への移行に伴い、境界引用符
    /// （MarkBoundaryQuotes）用の差し替えは不要になった——原文自身の引用符は
    /// タグの外側と衝突しないため、一切加工せずそのまま送れる。</param>
    /// <returns>The block text, plus the English text of every "Reference
    /// examples" entry actually included (after budget trimming) — the
    /// caller aggregates this across a batch to dedup issue #4's "c" block
    /// against anything already shown here.</returns>
    private static (string Block, IReadOnlyList<string> ShownReferenceEnglish) BuildCandidateBlock(
        IGrouping<string, Candidate> group, PrecedentRetriever retriever, AutoTranslator auto, IReadOnlySet<string> npcNames,
        int batchCharLimit, ReferenceExampleLimits referenceLimits, string? targetTextOverride = null)
    {
        var sb = new System.Text.StringBuilder();
        var first = group.First();
        var types = group.Select(c => c.RecordType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // v0.58.6: 全行を明示的な"\n"終端のAppendで統一している——AppendLineは
        // Environment.NewLine（Windowsでは"\r\n"）を使うため、LlmBatchInstruction
        // 側（文字列リテラルの"\n"のみ）と混在すると、同じプロンプト内でCRLFと
        // LFが入り混じった状態になっていた。実機検証（gemma4:26b）で、この
        // 混在した実際のプロンプトをそのまま再送したところ、LFのみに正規化した
        // 版よりタブ区切り形式の省略（既知の課題「単独バッチのタブ抜け」参照）が
        // 明確に高頻度で再現したため、送信するプロンプト全体をLFのみに統一した。
        sb.Append($"- Target: {TargetTagOpen}{targetTextOverride ?? first.CurrentText}{TargetTagClose}\n");

        var descriptions = types
            .Select(t => DsdTypeDescriptions.Describe(t) is { } d ? $"{d} [{t}]" : t)
            .ToList();
        sb.Append($"  Type: {string.Join(" / ", descriptions)}\n");

        // v0.6.0: per-record context PickUpTarget read off the Mutagen record —
        // "light armor, slot: head", "one-handed axe", "female, race: Nord".
        // Not derivable from the type string, and exactly what disambiguates a
        // name that is ambiguous on its own.
        var contexts = group.Select(c => c.Context).Where(c => c.Length > 0).Distinct().ToList();
        if (contexts.Count > 0)
            sb.Append($"  Context: {string.Join(" / ", contexts)}\n");

        // v0.8.0: a --include-stale candidate already HAS a shipped translation;
        // it was just written for different source text. Showing both lets the
        // translator adjust the existing wording instead of starting over, which
        // also keeps the result consistent with whatever else that DSD file
        // translated.
        var stale = group.FirstOrDefault(c => c.StaleTranslation.Length > 0);
        if (stale != null)
        {
            sb.Append($"  Existing translation (for the original text before it changed — use as a starting point): \"{stale.StaleTranslation}\"\n");
            sb.Append($"  The original text that existing translation was for: \"{stale.StaleOriginal}\"\n");
        }

        if (group.Count() > 1)
            sb.Append($"  (This string appears {group.Count()} times in this plugin. Answer once — the same translation applies to all occurrences.)\n");

        // 2026-09-17: PrecedentRetriever自体は500文字キャップ・相対比率0.4の
        // 足切りは適用済みだが、これは件数を一切制限しないため、長文・汎用的な
        // 候補では数百件を超えることがある（実データで確認）。まず完全一致する
        // (English, Japanese)ペアを1件にまとめ（同じ実例が出典違いで重複登録
        // されているケースの無駄を省く）、そのうえで「件数」と「割合」の
        // どちらか先に達した方で打ち切る——ReferenceExampleLimits参照。
        // フロアなし: 1件も収まらなければ0件（"none"表示）。
        var allPrecedents = retriever.FindPrecedents(first.CurrentText, first.RecordType, first.WinningPlugin);
        var seenPairs = new HashSet<(string English, string Japanese)>();
        var dedupedPrecedents = new List<CorpusEntry>();
        foreach (var p in allPrecedents)
            if (seenPairs.Add((p.English, p.Japanese)))
                dedupedPrecedents.Add(p);

        var budget = (int)(batchCharLimit * referenceLimits.BudgetRatio);
        var precedents = new List<CorpusEntry>();
        var precedentsLength = 0;
        foreach (var p in dedupedPrecedents)
        {
            if (precedents.Count >= referenceLimits.TopN) break;
            var line = $"    \"{p.English}\" → \"{p.Japanese}\"\n";
            if (precedentsLength + line.Length > budget) break;
            precedents.Add(p);
            precedentsLength += line.Length;
        }

        if (precedents.Count > 0)
        {
            sb.Append("  Reference examples:\n");
            foreach (var p in precedents)
                sb.Append($"    \"{p.English}\" → \"{p.Japanese}\"\n");
        }
        else
        {
            sb.Append("  Reference examples: none (no related existing translation found in the corpus)\n");
        }

        // v0.14.0: the words of THIS candidate that the corpus already has an
        // established rendering for. Unlike 参考例 (whole strings retrieved by
        // overlap), this is word-level and exact — it tells the translator that
        // "Bosmer" is ボズマー in this install, which is the single most common
        // way a translation drifts out of line with the rest of the load order.
        // Only entries the corpus attests are offered, so nothing here is a guess.
        var glossary = WordGlossary(first.CurrentText, auto);
        if (glossary.Count > 0)
            sb.Append($"  Known translations for words in this candidate: {string.Join(", ", glossary.Select(g => $"{g.English}={g.Japanese}"))}\n");

        // v0.48.1: does this candidate's text embed one of this load order's own
        // NPC_ FULL display names? Closes a gap found in DIAL FULL/INFO NAM1: a
        // pet/character name used mid-sentence ("Go home, Scooby.") carries no
        // signal on its own that it's an identity, not a common word — unlike
        // NPC_ FULL's own Context (race/gender), which already disambiguates this
        // for the name field itself. Word-level, case-insensitive; only shown on
        // an actual hit.
        var nameHits = SplitToWords(first.CurrentText)
            .Where(w => w.Length >= 3 && npcNames.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (nameHits.Count > 0)
            sb.Append($"  Known character/creature names in this mod's load order (proper nouns — render phonetically, don't translate by meaning): {string.Join(", ", nameHits)}\n");

        // v0.48.1: does a word in this candidate also appear in its own plugin's
        // filename? A recurring modifier that matches the mod's own name is often
        // a brand/set/character name (e.g. "[FB] Bishop Armor.esp"'s "Bishop
        // Belt"), not the word's common dictionary sense — the same trap
        // NameFallbackTranslator's NPC_ FULL exclusion exists for (Silver/Steel,
        // v0.29.10), generalized here as a soft hint rather than a hard rule
        // since a false positive (a genuinely material-named plugin, e.g. "Iron
        // Armor Pack.esp") costs nothing worse than a redundant note.
        var pluginWords = SplitToWords(first.WinningPlugin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var brandHits = SplitToWords(first.CurrentText)
            .Where(w => w.Length >= 3 && pluginWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (brandHits.Count > 0)
        {
            var verb = brandHits.Count == 1 ? "also appears" : "also appear";
            sb.Append($"  Note: {string.Join(", ", brandHits)} {verb} in this mod's own filename ({first.WinningPlugin}) — likely a set/brand/character name here rather than the word's usual dictionary meaning.\n");
        }

        sb.Append('\n');
        return (sb.ToString(), precedents.Select(p => p.English).ToList());
    }

    /// <summary>The GenerateDsdFile input format: same rows as candidates.tsv, plus a
    /// "Japanese" column. Rows the AutoTranslator (①コーパス一致/②辞書一致)
    /// could resolve come pre-filled with a Notes tag identifying the method,
    /// so they're still easy to spot-check/correct by hand before GenerateDsdFile runs;
    /// everything else is left blank for the user (or AI, pasted in) to fill in.
    /// Fields are escaped the same way as CandidateIo/CorpusIo — some source
    /// text contains literal tab/newline characters (seen in real ESP data,
    /// e.g. a name with an embedded newline), which would otherwise corrupt
    /// the row structure.
    ///
    /// v0.54.2 (DESIGN_NOTES.md known issue 21): an UNRESOLVED row whose
    /// Candidate carries a PickUpTarget-side Warning (fail-open classification)
    /// gets that warning written into Notes instead of leaving it blank — this
    /// never collides with a resolution-method tag, since those only ever
    /// appear on resolved (auto != null) rows.</summary>
    private static void WriteTranslationTemplate(string path, List<(Candidate Candidate, AutoTranslationResult? Auto)> rows)
    {
        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine(string.Join('\t', "FormId", "WinningPlugin", "RecordType", "EnglishText", "Japanese", "Notes", "Index", "EditorId", "TranslationCheck"));
        foreach (var (c, auto) in rows)
        {
            var notes = auto?.Method ?? c.Warning;
            w.WriteLine(string.Join('\t', c.FormId, c.WinningPlugin, c.RecordType, Escape(c.CurrentText),
                Escape(auto?.Japanese ?? ""), Escape(notes), c.Index, Escape(c.EditorId), auto?.TranslationCheck ?? ""));
        }
    }

    /// <summary>v0.52.1a: reads an existing translations.tsv (if any) and returns
    /// every row that already has SOME Japanese translation, whatever method
    /// produced it (①〜⑥ auto-resolution, SJPTS_ModifiedByUser, or a translation
    /// carried forward from a still-earlier run by this same method) — keyed the
    /// same way the GUI's own editor identifies a row (FormId+RecordType+Index —
    /// FormId alone repeats within a plugin whenever one FormKey carries multiple
    /// translatable fields, e.g. a WEAP's FULL and DESC).
    ///
    /// v0.50.1a〜v0.52.1a: originally scoped to ONLY "Notes == SJPTS_ModifiedByUser"
    /// (a human's manual edit) — every other row, INCLUDING a candidate ⑤/⑥ had
    /// just spent real tokens resolving, was silently discarded and recomputed
    /// from scratch on the next run (the "fully stateless" design from v0.33.0).
    /// That was fine while ①〜④ were the only resolution methods (cheap, local,
    /// instant to redo) — it stopped being fine once ⑤/⑥ started costing real
    /// money/time per call: re-running `translation` (e.g. to pick up a newly
    /// collected xTranslator file, or just to refresh the GUI's status view)
    /// silently re-billed every already-AI-resolved candidate. Widened to
    /// "already has ANY translation" so a resolved candidate is never redone
    /// unless the caller explicitly asks for a full reset (the CLI's
    /// --discard-user-edits, checked by WritePluginFilesWithDir before even
    /// calling this method — despite the flag's name, it now discards ALL
    /// prior resolutions, matching what the GUI's "初期化"/"MO2再読込＆初期化"
    /// actions have always meant by "reset to ① only").
    ///
    /// Missing file or no such rows both just return empty — this is a
    /// best-effort carry-forward, not a required input.</summary>
    private static Dictionary<(string FormId, string RecordType, int Index), (string Japanese, string Method, string TranslationCheck)> ReadExistingTranslations(string path)
    {
        var result = new Dictionary<(string, string, int), (string, string, string)>();
        if (!File.Exists(path)) return result;

        using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
        var headerLine = reader.ReadLine();
        if (headerLine == null) return result;
        var headers = headerLine.Split('\t');
        int Col(string name) => Array.IndexOf(headers, name);
        var formIdCol = Col("FormId");
        var recordTypeCol = Col("RecordType");
        var japaneseCol = Col("Japanese");
        var notesCol = Col("Notes");
        var indexCol = Col("Index");
        // 2026-09-18: 古いtranslations.tsv（この列が存在する前に生成されたもの）
        // との後方互換のため、無ければ""として扱う（他の必須列と違い読み込み
        // 自体を諦めない）。
        var translationCheckCol = Col("TranslationCheck");
        if (formIdCol < 0 || recordTypeCol < 0 || japaneseCol < 0 || notesCol < 0 || indexCol < 0) return result;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var cells = line.Split('\t');
            if (cells.Length <= Math.Max(Math.Max(formIdCol, recordTypeCol), Math.Max(japaneseCol, Math.Max(notesCol, indexCol)))) continue;
            var japanese = Unescape(cells[japaneseCol]);
            if (japanese.Length == 0) continue;
            if (!int.TryParse(cells[indexCol], out var index)) continue;
            var translationCheck = translationCheckCol >= 0 && cells.Length > translationCheckCol ? Unescape(cells[translationCheckCol]) : "";
            result[(cells[formIdCol], cells[recordTypeCol], index)] = (japanese, Unescape(cells[notesCol]), translationCheck);
        }
        return result;
    }
}
