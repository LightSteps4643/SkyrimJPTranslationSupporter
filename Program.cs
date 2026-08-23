using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;
using SkyrimJPStringPatcher.Translation;
using SkyrimJPStringPatcher.GenerateDsdFile;

// v0.50.1a (GUI): without this, Console.Out/Error default to the OS console
// codepage (cp932/Shift-JIS on Japanese Windows) when running interactively in
// a real terminal — fine there, since the terminal itself uses the same
// codepage. But the GUI redirects this process's stdout/stderr through a pipe
// and decodes it as UTF-8 (CliRunner.cs), so without forcing UTF-8 here too,
// every Japanese line written to the log window came out garbled. This has no
// effect on any file this tool writes (those already specify UTF-8 explicitly
// per-writer) — it's stdout/stderr only.
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// Default per-stage working directories, matching the "各Stageごとのフォルダ内にout_temp
// フォルダを作り、そこに生成物を置く" convention: every stage's OWN intermediate output
// lives inside that stage's own folder, so it's obvious at a glance which process
// produced which file. Only the FINAL DSD output (generatedsdfile) goes to the
// top-level "out" folder, since that's the actual deliverable to install into MO2.
//
// v0.32.0: both out_temp folders are DERIVATIVES of Data/ + Translation/import/ —
// every run rebuilds them from scratch, so a new version folder should never
// bother carrying Translation/out_temp forward (see DESIGN_NOTES.md's
// "バージョン管理方針"). v0.33.0 removed the in-session reflux mechanism this
// note used to explain the boundary of; there is now no cross-run state in
// out_temp at all — it is pure, disposable output every single run.
const string DefaultPickUpTargetOutDir = "PickUpTarget/out_temp";
const string DefaultTranslationOutDir = "Translation/out_temp";
const string DefaultImportDir = "Translation/import";
const string DefaultFinalOutDir = "out";

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

// Flags are pulled out first so the positional arguments keep their existing
// meaning regardless of where a flag is written.
var includeStale = args.Any(a => a.Equals("--include-stale", StringComparison.OrdinalIgnoreCase));
args = args.Where(a => !a.StartsWith("--include-stale", StringComparison.OrdinalIgnoreCase)).ToArray();

// The wide search is the default (v0.16.0): it costs a few seconds more and finds
// meaningfully more, so there is no situation in which the narrow one is the right
// thing to ship. --fast remains for when a re-run's turnaround genuinely matters.
if (args.Any(a => a.Equals("--fast", StringComparison.OrdinalIgnoreCase)))
{
    TuningProfile.Use(TuningProfile.Fast);
    Console.WriteLine("Tuning profile: fast (narrower search — for quick iteration, not for a release build)");
}
args = args.Where(a => !a.StartsWith("--fast", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--thorough", StringComparison.OrdinalIgnoreCase)).ToArray();

// v0.52.1a: step 5 (ローカルLLM) and step 6 (生成AI翻訳・クラウド) are now two
// INDEPENDENT, chainable opt-ins rather than one either/or choice — whatever ⑤
// can't resolve falls through to ⑥, exactly like ①〜④ already fall through to
// each other. Each has its own flag family so both can be enabled in the same
// run without one's endpoint/model/key clobbering the other's.
var llmLocalEnabled = args.Any(a => a.Equals("--llm-local", StringComparison.OrdinalIgnoreCase));
var llmLocalModelArg = args.FirstOrDefault(a => a.StartsWith("--llm-local-model=", StringComparison.OrdinalIgnoreCase));
var llmLocalEndpointArg = args.FirstOrDefault(a => a.StartsWith("--llm-local-endpoint=", StringComparison.OrdinalIgnoreCase));

var llmCloudEnabled = args.Any(a => a.Equals("--llm-cloud", StringComparison.OrdinalIgnoreCase));
var llmCloudProviderArg = args.FirstOrDefault(a => a.StartsWith("--llm-cloud-provider=", StringComparison.OrdinalIgnoreCase));
var llmCloudModelArg = args.FirstOrDefault(a => a.StartsWith("--llm-cloud-model=", StringComparison.OrdinalIgnoreCase));
var llmCloudEndpointArg = args.FirstOrDefault(a => a.StartsWith("--llm-cloud-endpoint=", StringComparison.OrdinalIgnoreCase));
var claudeCodeExeArg = args.FirstOrDefault(a => a.StartsWith("--claude-code-exe=", StringComparison.OrdinalIgnoreCase));

// v0.52.1a: ⑤・⑥はプラグイン単位でまとめて1回の呼び出しにする（バッチ化、
// PromptGenerator.ApplyLlmStep参照）。1バッチあたりの上限を文字数で決めており、
// 妥当な値は生成AIサービス・契約プランによって変わりうる（無料枠は既定値より
// 小さくしたい等）ため、固定値にせずここで上書きできるようにしてある。
var llmBatchCharLimitArg = args.FirstOrDefault(a => a.StartsWith("--llm-batch-char-limit=", StringComparison.OrdinalIgnoreCase));
var llmBatchCharLimit = PromptGenerator.DefaultLlmBatchCharLimit;
if (llmBatchCharLimitArg != null)
{
    var raw = llmBatchCharLimitArg["--llm-batch-char-limit=".Length..];
    if (!int.TryParse(raw, out llmBatchCharLimit) || llmBatchCharLimit <= 0)
    {
        Console.Error.WriteLine($"--llm-batch-char-limit=<正の整数> を指定してください（指定値: '{raw}'）。");
        return 1;
    }
}

args = args.Where(a => !a.Equals("--llm-local", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--llm-local-model=", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--llm-local-endpoint=", StringComparison.OrdinalIgnoreCase)
                       && !a.Equals("--llm-cloud", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--llm-cloud-provider=", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--llm-cloud-model=", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--llm-cloud-endpoint=", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--claude-code-exe=", StringComparison.OrdinalIgnoreCase)
                       && !a.StartsWith("--llm-batch-char-limit=", StringComparison.OrdinalIgnoreCase)).ToArray();

// v0.50.1a: GUI support — process several plugins in one process launch instead
// of one-per-invocation (see PromptGenerator.RunMany's remarks: BuildContext's
// setup cost is per-RUN, not per-plugin, so looping single-plugin invocations
// from outside was needlessly repeating it up to once per selected plugin).
// A file rather than an inline list because plugin names can contain almost
// any character and there can be well over a hundred of them.
var pluginsFileArg = args.FirstOrDefault(a => a.StartsWith("--plugins-file=", StringComparison.OrdinalIgnoreCase));
args = args.Where(a => !a.StartsWith("--plugins-file=", StringComparison.OrdinalIgnoreCase)).ToArray();

// v0.53.0a: GUIの「キャンセル」ボタン向け——実行のたびにGUI側が一意な一時パスを
// 生成してこれに渡す（--plugins-fileと同じ流儀）。PromptGenerator.RunManyが
// 1プラグイン処理し終えるたびにこのパスの存在を確認し、あれば残りのプラグインを
// 処理せずそこで正常終了する（プロセスをkillするのではなく、区切りの良いところで
// 自発的に止まる——DESIGN_NOTES.md既知の課題15.参照）。
var cancelFlagPathArg = args.FirstOrDefault(a => a.StartsWith("--cancel-flag-path=", StringComparison.OrdinalIgnoreCase));
var cancelFlagPath = cancelFlagPathArg?["--cancel-flag-path=".Length..];
args = args.Where(a => !a.StartsWith("--cancel-flag-path=", StringComparison.OrdinalIgnoreCase)).ToArray();

ITextTranslator? llmLocal = null;
if (llmLocalEnabled)
{
    if (llmLocalModelArg == null)
    {
        Console.Error.WriteLine("--llm-local requires --llm-local-model=<name> (e.g. --llm-local-model=gemma3:12b) — no default model is assumed.");
        return 1;
    }
    var llmLocalModel = llmLocalModelArg["--llm-local-model=".Length..];
    var llmLocalEndpoint = llmLocalEndpointArg != null ? llmLocalEndpointArg["--llm-local-endpoint=".Length..] : "http://localhost:11434/v1/chat/completions";
    // v0.52.1a: an Authorization header, for the (uncommon) case this endpoint
    // is itself an authenticated one. The key is deliberately NOT a CLI flag —
    // argv is visible to other processes on the same machine (Task Manager's
    // command-line column, WMI, etc.) for as long as this process runs, which a
    // CLI flag would expose for no benefit. An environment variable set only on
    // this one child process (see the GUI's CliRunner) is not perfectly secret
    // either, but it is at least not sitting in the process list, and it
    // matches the ubiquitous *_API_KEY convention other CLI tools use for the
    // same reason.
    var llmLocalApiKey = Environment.GetEnvironmentVariable("SKYRIMJPSP_LLM_API_KEY") ?? "";
    llmLocal = new LocalLlmTranslator(new LocalLlmOptions(llmLocalEndpoint, llmLocalModel, llmLocalApiKey));
    Console.WriteLine($"Step 5 (local LLM): ENABLED — endpoint={llmLocalEndpoint}, model={llmLocalModel}" +
        (llmLocalApiKey.Length > 0 ? ", API key: provided (via SKYRIMJPSP_LLM_API_KEY)" : ""));
}

ITextTranslator? llmCloud = null;
if (llmCloudEnabled)
{
    var llmCloudProvider = llmCloudProviderArg != null ? llmCloudProviderArg["--llm-cloud-provider=".Length..] : "claudecode";
    if (llmCloudProvider.Equals("claudecode", StringComparison.OrdinalIgnoreCase))
    {
        // No API key handling here — claude CLI is authenticated independently
        // of this tool (claude login, or its own env var), same as how the GUI
        // itself is just a thin shell around this exe.
        var claudeCodeExe = claudeCodeExeArg != null ? claudeCodeExeArg["--claude-code-exe=".Length..] : "claude";
        var claudeCodeModel = llmCloudModelArg != null ? llmCloudModelArg["--llm-cloud-model=".Length..] : ""; // optional here — claude falls back to its own default
        llmCloud = new ClaudeCodeTranslator(new ClaudeCodeOptions(claudeCodeExe, claudeCodeModel));
        Console.WriteLine($"Step 6 (cloud AI): ENABLED — provider=Claude Code CLI, exe={claudeCodeExe}" +
            (claudeCodeModel.Length > 0 ? $", model={claudeCodeModel}" : ", model=(claude default)"));
    }
    else
    {
        if (llmCloudModelArg == null || llmCloudEndpointArg == null)
        {
            Console.Error.WriteLine("--llm-cloud-provider=http requires --llm-cloud-endpoint=<url> and --llm-cloud-model=<name>.");
            return 1;
        }
        var llmCloudModel = llmCloudModelArg["--llm-cloud-model=".Length..];
        var llmCloudEndpoint = llmCloudEndpointArg["--llm-cloud-endpoint=".Length..];
        var llmCloudApiKey = Environment.GetEnvironmentVariable("SKYRIMJPSP_CLOUD_LLM_API_KEY") ?? "";
        llmCloud = new LocalLlmTranslator(new LocalLlmOptions(llmCloudEndpoint, llmCloudModel, llmCloudApiKey));
        Console.WriteLine($"Step 6 (cloud AI): ENABLED — provider=HTTP, endpoint={llmCloudEndpoint}, model={llmCloudModel}" +
            (llmCloudApiKey.Length > 0 ? ", API key: provided (via SKYRIMJPSP_CLOUD_LLM_API_KEY)" : ""));
    }
}

// v0.49.1: per-stage opt-OUT flags for "translation" — 2.意味合成/3.音訳分解/
// 4.NameFallbackTranslator. 1.完全一致 has no such flag (ground-truth data, no
// situation where disabling it is correct — see DESIGN_NOTES.md). All three
// default to enabled; each flag turns ONE step off, leaving the others as-is.
var noMeaning = args.Any(a => a.Equals("--no-meaning", StringComparison.OrdinalIgnoreCase));
var noTranslit = args.Any(a => a.Equals("--no-translit", StringComparison.OrdinalIgnoreCase));
var noNameFallback = args.Any(a => a.Equals("--no-namefallback", StringComparison.OrdinalIgnoreCase));
args = args.Where(a => !a.Equals("--no-meaning", StringComparison.OrdinalIgnoreCase)
                       && !a.Equals("--no-translit", StringComparison.OrdinalIgnoreCase)
                       && !a.Equals("--no-namefallback", StringComparison.OrdinalIgnoreCase)).ToArray();
var stageOptions = new TranslationStageOptions(
    EnableMeaning: !noMeaning, EnableTransliteration: !noTranslit, EnableNameFallback: !noNameFallback);
if (noMeaning || noTranslit || noNameFallback)
    Console.WriteLine($"Stage overrides: meaning={(noMeaning ? "OFF" : "on")}, transliteration={(noTranslit ? "OFF" : "on")}, NameFallbackTranslator={(noNameFallback ? "OFF" : "on")}");

// v0.50.1a〜v0.52.1a: opt out of carrying forward ANY already-translated row
// (see PromptGenerator's class remarks) — a clean-slate ①のみ reset. Despite
// the flag's name (kept for compatibility), this now discards every prior
// resolution, not just a human's ModifiedByUser edit — including ⑤/⑥ AI
// results, which otherwise get silently redone (and re-billed) on every run.
var discardUserEdits = args.Any(a => a.Equals("--discard-user-edits", StringComparison.OrdinalIgnoreCase));
args = args.Where(a => !a.Equals("--discard-user-edits", StringComparison.OrdinalIgnoreCase)).ToArray();
if (discardUserEdits)
    Console.WriteLine("--discard-user-edits: every existing translation (auto-resolved or human-edited) will be overwritten, not preserved");

switch (args[0])
{
    case "pickuptarget":
    {
        if (args.Length < 2) { PrintUsage(); return 1; }
        var mo2Dir = args[1];
        var outDir = args.Length > 2 ? args[2] : DefaultPickUpTargetOutDir;
        Directory.CreateDirectory(outDir);

        using var log = RunLog.Open("PickUpTarget", "PickUpTarget");
        using var trace = TraceLog.Open("PickUpTarget", "PickUpTarget");
        try
        {
            trace.Info($"Resolving MO2 instance: {mo2Dir}");
            var result = PickUpTargetRunner.Run(mo2Dir, log, includeStale, trace);
            trace.Info($"Scan complete: {result.Candidates.Count} candidates, {result.Corpus.Count} corpus entries");

            var candidatesTxt = Path.Combine(outDir, "candidates.txt");
            var candidatesTsv = Path.Combine(outDir, "candidates.tsv");
            var corpusTsv = Path.Combine(outDir, "corpus.tsv");
            var coverageTsv = Path.Combine(outDir, "coverage_by_plugin.tsv");
            var activePluginsTsv = Path.Combine(outDir, "active_plugins.tsv");

            trace.Info($"Write start: {candidatesTxt} ({result.Candidates.Count} candidates)");
            CandidateListWriter.WriteText(candidatesTxt, result);
            trace.Info($"Write done: {candidatesTxt}");

            trace.Info($"Write start: {candidatesTsv} ({result.Candidates.Count} candidates)");
            CandidateIo.WriteTsv(candidatesTsv, result.Candidates);
            trace.Info($"Write done: {candidatesTsv}");

            trace.Info($"Write start: {corpusTsv} ({result.Corpus.Count} corpus entries)");
            CorpusIo.WriteTsv(corpusTsv, result.Corpus);
            trace.Info($"Write done: {corpusTsv}");

            trace.Info($"Write start: {coverageTsv}");
            CoverageReportWriter.WriteTsv(coverageTsv, result.Candidates, result.CoveredByPlugin);
            trace.Info($"Write done: {coverageTsv}");

            // v0.50.1a (GUI): the load order's full active-plugin list, not just the
            // subset with translatable candidates — the GUI's plugin-list window needs
            // to show what actually loaded (incl. pure asset/texture mods with nothing
            // to translate), which candidates.tsv alone can't answer.
            trace.Info($"Write start: {activePluginsTsv} ({result.ActivePlugins.Count} plugins)");
            var candidatePlugins = new HashSet<string>(result.Candidates.Select(c => c.WinningPlugin), StringComparer.OrdinalIgnoreCase);
            using (var w = new StreamWriter(activePluginsTsv, false, System.Text.Encoding.UTF8))
            {
                w.WriteLine(string.Join('\t', "Index", "FileName", "HasTranslatableContent"));
                for (var i = 0; i < result.ActivePlugins.Count; i++)
                    w.WriteLine(string.Join('\t', i, result.ActivePlugins[i], candidatePlugins.Contains(result.ActivePlugins[i])));
            }
            trace.Info($"Write done: {activePluginsTsv}");

            Console.WriteLine($"Wrote: {candidatesTxt}");
            Console.WriteLine($"Wrote: {candidatesTsv}");
            Console.WriteLine($"Wrote: {corpusTsv}");
            Console.WriteLine($"Wrote: {coverageTsv}");
            Console.WriteLine($"Wrote: {activePluginsTsv}");

            log.Section("出力ファイル", "Output files");
            log.Line(candidatesTxt, candidatesTxt);
            log.Line(candidatesTsv, candidatesTsv);
            log.Line(corpusTsv, corpusTsv);
            log.Line(coverageTsv, coverageTsv);
            log.Line(activePluginsTsv, activePluginsTsv);
            return 0;
        }
        catch (Exception ex)
        {
            trace.Error("Exception during pickuptarget execution", ex);
            throw;
        }
    }

    case "translation":
    {
        var inputDir = args.Length > 1 ? args[1] : DefaultPickUpTargetOutDir;
        var outputDir = args.Length > 2 ? args[2] : DefaultTranslationOutDir;
        var targetPlugin = args.Length > 3 ? args[3] : null;
        var candidatesTsv = Path.Combine(inputDir, "candidates.tsv");
        var corpusTsv = Path.Combine(inputDir, "corpus.tsv");

        if (!File.Exists(candidatesTsv) || !File.Exists(corpusTsv))
        {
            Console.Error.WriteLine($"PickUpTarget output not found under '{inputDir}' — run pickuptarget first.");
            return 1;
        }

        Directory.CreateDirectory(outputDir);

        using var log = RunLog.Open("Translation", "Translation");
        using var trace = TraceLog.Open("Translation", "Translation");
        try
        {
            if (pluginsFileArg != null)
            {
                var pluginsFilePath = pluginsFileArg["--plugins-file=".Length..];
                if (!File.Exists(pluginsFilePath))
                {
                    Console.Error.WriteLine($"--plugins-file target not found: {pluginsFilePath}");
                    return 1;
                }
                var targetPlugins = File.ReadAllLines(pluginsFilePath)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
                trace.Info($"Input: {candidatesTsv}, {corpusTsv} / targets: {targetPlugins.Count} plugin(s) from {pluginsFilePath} / step5 local LLM: {(llmLocal != null ? "enabled" : "disabled")} / step6 cloud AI: {(llmCloud != null ? "enabled" : "disabled")}");
                PromptGenerator.RunMany(candidatesTsv, corpusTsv, DefaultImportDir, targetPlugins, outputDir, log, trace, llmLocal: llmLocal, llmCloud: llmCloud, stageOptions: stageOptions, discardUserEdits: discardUserEdits, llmBatchCharLimit: llmBatchCharLimit, cancelFlagPath: cancelFlagPath);
                LogCloudAiUsage(log, llmCloud);
                return 0;
            }

            trace.Info($"Input: {candidatesTsv}, {corpusTsv} / target: {(targetPlugin ?? "--all")} / step5 local LLM: {(llmLocal != null ? "enabled" : "disabled")} / step6 cloud AI: {(llmCloud != null ? "enabled" : "disabled")}");
            if (targetPlugin == null || targetPlugin.Equals("--all", StringComparison.OrdinalIgnoreCase))
                PromptGenerator.RunAll(candidatesTsv, corpusTsv, DefaultImportDir, outputDir, log, trace, llmLocal: llmLocal, llmCloud: llmCloud, stageOptions: stageOptions, discardUserEdits: discardUserEdits, llmBatchCharLimit: llmBatchCharLimit);
            else
                PromptGenerator.RunOne(candidatesTsv, corpusTsv, DefaultImportDir, targetPlugin, outputDir, log, trace, llmLocal: llmLocal, llmCloud: llmCloud, stageOptions: stageOptions, discardUserEdits: discardUserEdits, llmBatchCharLimit: llmBatchCharLimit);
            LogCloudAiUsage(log, llmCloud);
            return 0;
        }
        catch (Exception ex)
        {
            trace.Error("Exception during translation execution", ex);
            throw;
        }
    }

    case "generatedsdfile":
    {
        var translationsInput = args.Length > 1 ? args[1] : DefaultTranslationOutDir;
        var finalOutDir = args.Length > 2 ? args[2] : DefaultFinalOutDir;

        if (!File.Exists(translationsInput) && !Directory.Exists(translationsInput))
        {
            Console.Error.WriteLine($"Translations file/directory not found: {translationsInput}");
            return 1;
        }

        using var log = RunLog.Open("GenerateDsdFile", "GenerateDsdFile");
        using var trace = TraceLog.Open("GenerateDsdFile", "GenerateDsdFile");
        try
        {
            trace.Info($"Input: {translationsInput} / output: {finalOutDir}");
            DsdJsonGenerator.Run(translationsInput, finalOutDir, log, trace);
            trace.Info("DSD JSON generation complete");
            return 0;
        }
        catch (Exception ex)
        {
            trace.Error("Exception during generatedsdfile execution", ex);
            throw;
        }
    }

    case "worddict":
    {
        var corpusTsv = args.Length > 1 ? args[1] : Path.Combine(DefaultPickUpTargetOutDir, "corpus.tsv");
        var outputPath = args.Length > 2 ? args[2] : Path.Combine(DefaultTranslationOutDir, "word_transliteration_dict.tsv");

        if (!File.Exists(corpusTsv))
        {
            Console.Error.WriteLine($"Corpus file not found: {corpusTsv}");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) is { Length: > 0 } dir ? dir : ".");

        var corpus = CorpusIo.ReadTsv(corpusTsv);
        // Build a real CorpusTransliterator and dump ITS resolved word list, so this
        // file matches exactly what Translation actually uses (including the "meaning
        // counter-evidence" exclusion — e.g. "Eye" is dropped despite being seen
        // transliterated once, because it's translated by meaning ~88% of the time).
        var transliterator = CorpusTransliterator.Build(corpus);
        var distinct = transliterator.AllWords
            .OrderBy(p => p.English, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The Origin column is the review-critical one: "official" pairs are attested
        // verbatim in the corpus, while "derived" (sliced out of a longer name) and
        // "sentence" (aligned statistically from running text) are this tool's own
        // inferences and are the only place a mistranslation can enter.
        using (var w = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8))
        {
            w.WriteLine(string.Join('\t', "English", "Japanese", "Origin", "Source"));
            foreach (var (english, japanese, origin, source) in distinct)
                w.WriteLine($"{english}\t{japanese}\t{origin}\t{source}");
        }

        Console.WriteLine($"Corpus entries scanned: {corpus.Count}");
        Console.WriteLine($"Distinct English words written: {distinct.Count} (" +
                          string.Join(", ", distinct.GroupBy(d => d.Origin).Select(g => $"{g.Key} {g.Count()}")) + ")");
        Console.WriteLine($"Wrote: {outputPath}");
        return 0;
    }

    default:
        PrintUsage();
        return 1;
}

// v0.52.1a: after a run using step 6 (生成AI翻訳・クラウド) via Claude Code CLI
// finishes, report how much it actually cost — every call there is billed
// against the user's Claude subscription (unlike step 5's usual local/free
// model), and ClaudeCodeTranslator.Usage accumulates this across the WHOLE run
// (every candidate, every plugin) via --output-format json. Written to both
// stdout (so the GUI's log window shows it immediately) and the human-readable
// translation.log (so it's still there on review later, unlike Trace-level
// diagnostics). A no-op for the OpenAI-compatible HTTP path or when step 6 was
// never enabled — neither of those track usage this way.
static void LogCloudAiUsage(RunLog log, ITextTranslator? llmCloud)
{
    if (llmCloud is not ClaudeCodeTranslator claudeCode) return;
    var usage = claudeCode.Usage;
    if (usage.CallCount == 0) return;

    var message =
        $"生成AI（クラウド・Claude Code CLI）使用量: 呼び出し{usage.CallCount}回 / " +
        $"入力{usage.InputTokens:N0}トークン / 出力{usage.OutputTokens:N0}トークン / " +
        $"キャッシュ作成{usage.CacheCreationInputTokens:N0}トークン / キャッシュ読込{usage.CacheReadInputTokens:N0}トークン / " +
        $"概算コスト ${usage.TotalCostUsd:0.0000}";
    Console.WriteLine(message);
    log.Section("生成AI（クラウド）使用量", "Cloud AI (Claude Code CLI) usage");
    log.Line(message,
        $"Cloud AI (Claude Code CLI) usage: {usage.CallCount} call(s) / " +
        $"input {usage.InputTokens:N0} tokens / output {usage.OutputTokens:N0} tokens / " +
        $"cache creation {usage.CacheCreationInputTokens:N0} tokens / cache read {usage.CacheReadInputTokens:N0} tokens / " +
        $"est. cost ${usage.TotalCostUsd:0.0000}");
}

static void PrintUsage()
{
    Console.WriteLine("Usage (each stage's own intermediate output lives in <StageName>/out_temp by default):");
    Console.WriteLine("  SkyrimJPStringPatcher pickuptarget <MO2 instance dir> [output dir = PickUpTarget/out_temp] [--include-stale]");
    Console.WriteLine("      -> candidates.txt / candidates.tsv / corpus.tsv");
    Console.WriteLine("      --include-stale: 既存DSD訳はあるが原文が変化しているものを、再翻訳の対象に含める");
    Console.WriteLine("                       （既定では報告のみで候補にしない。DSDはFormIDだけで照合するため、");
    Console.WriteLine("                        古い訳が現在の原文に適用され続けている箇所を訳し直したい場合に使う）");
    Console.WriteLine("  SkyrimJPStringPatcher translation [input dir = PickUpTarget/out_temp] [output dir = Translation/out_temp] [target plugin filename | --all]");
    Console.WriteLine("      target plugin omitted or --all -> generates <output dir>/<plugin>/{prompt.txt,translations.tsv} for EVERY plugin with candidates, plus translation_index.txt");
    Console.WriteLine("      target plugin given            -> generates just that one plugin's files");
    Console.WriteLine("      --plugins-file=<path> : process several specific plugins in ONE process launch (one plugin name per");
    Console.WriteLine("              line in the file) — same per-plugin output as giving a single target plugin, repeated, but the");
    Console.WriteLine("              expensive corpus/dictionary setup only happens once instead of once per plugin. Overrides a");
    Console.WriteLine("              positional target plugin/--all if both are given. Does not write translation_index.txt or the");
    Console.WriteLine("              other whole-load-order summary files (auto_resolve_by_plugin.tsv, plugin_summary.txt) — those");
    Console.WriteLine("              are a --all-only contract");
    Console.WriteLine("      Translation/import/ のxTranslator XML(*.xml)は毎回自動で読み込まれ、コーパスの一部として");
    Console.WriteLine("      扱われる（①コーパス完全一致と同じ優先度）。別コマンドでの取り込みは不要");
    Console.WriteLine("      --llm-local : 5.ステップ。1.〜4.で解決できなかった候補を、ローカルLLM（OpenAI互換");
    Console.WriteLine("              /v1/chat/completions）に渡す。既定は無効。サーバー未起動時は各候補が黙って");
    Console.WriteLine("              未解決のまま残る（エラーにはしない）。--llm-local-model=<name> の指定が必須");
    Console.WriteLine("      --llm-local-model=<name> : 例 --llm-local-model=gemma3:12b（--llm-local指定時は必須）");
    Console.WriteLine("      --llm-local-endpoint=<url> : 既定 http://localhost:11434/v1/chat/completions（Ollama）。");
    Console.WriteLine("              OpenAI互換の /v1/chat/completions を実装するサーバーなら他ツールでも可");
    Console.WriteLine("              （認証が必要な場合はSKYRIMJPSP_LLM_API_KEY環境変数でAPIキーを渡す）");
    Console.WriteLine("      --llm-cloud : 6.ステップ。5.（有効な場合はそこまで）で解決できなかった候補を、");
    Console.WriteLine("              クラウドの生成AIに渡す。既定は無効。--llm-localと独立に有効化でき、両方");
    Console.WriteLine("              有効なら5→6の順に試す（5で解決済みの候補は6には回さない）");
    Console.WriteLine("      --llm-cloud-provider=<claudecode|http> : 既定 claudecode。claude Code CLI（claude");
    Console.WriteLine("              コマンド）をサブプロセス起動して使う（要ログイン済み）。httpならOpenAI互換");
    Console.WriteLine("              APIにHTTPで直接アクセス（--llm-cloud-endpoint/--llm-cloud-modelが必須、");
    Console.WriteLine("              認証が必要な場合はSKYRIMJPSP_CLOUD_LLM_API_KEY環境変数でAPIキーを渡す）");
    Console.WriteLine("      --llm-cloud-model=<name> : claudecodeでは省略可（claude自身の既定モデル）、httpでは必須");
    Console.WriteLine("      --llm-cloud-endpoint=<url> : httpのときのみ必須（例 https://api.openai.com/v1/chat/completions）");
    Console.WriteLine("      --claude-code-exe=<path> : claudeコマンドの実行ファイルパス（既定 \"claude\"、PATH上のものを使用）");
    Console.WriteLine("      --llm-batch-char-limit=<n> : 5./6.でLLMに1回でまとめて渡す候補の合計文字数の上限（既定 " + PromptGenerator.DefaultLlmBatchCharLimit + "）。");
    Console.WriteLine("              超える場合は複数回の呼び出しに分割される。生成AIサービス・契約プランによって妥当な値が");
    Console.WriteLine("              変わりうるため上書き可能（無料枠等ではより小さい値が必要な場合がある）");
    Console.WriteLine("      --cancel-flag-path=<path> : --plugins-fileと併用時のみ有効。指定パスにファイルが存在する");
    Console.WriteLine("              状態を1プラグイン処理し終えるたびに確認し、あれば残りのプラグインを処理せず");
    Console.WriteLine("              そこで正常終了する（GUIの「キャンセル」ボタン用。プロセスを強制終了するのでは");
    Console.WriteLine("              なく、処理中のプラグインの完了を待って自発的に止まる）");
    Console.WriteLine("      --no-meaning : 2.意味合成を無効化（既定は有効）");
    Console.WriteLine("      --no-translit : 3.音訳分解を無効化（既定は有効）");
    Console.WriteLine("      --no-namefallback : 4.NameFallbackTranslatorを無効化（既定は有効）");
    Console.WriteLine("              1.完全一致には無効化オプションが無い（正解データそのものであり、無効化すべき状況が無いため）");
    Console.WriteLine("      --discard-user-edits : 既存translations.tsvの翻訳済み行を（手法を問わず）保持せず、");
    Console.WriteLine("              全て①バニラコーパスのみの状態に上書きする（既定は既存の翻訳を保持し、未翻訳の");
    Console.WriteLine("              候補だけ埋める）。人手編集（ModifiedByUser）を含め完全にリセットしたいときに使う");
    Console.WriteLine("  SkyrimJPStringPatcher generatedsdfile [input = Translation/out_temp] [final DSD output dir = out]");
    Console.WriteLine("      input can be a single completed translations.tsv, or a directory containing many");
    Console.WriteLine("      -> out/SKSE/Plugins/DynamicStringDistributor/...");
    Console.WriteLine();
    Console.WriteLine("  【重要】バージョンフォルダを切るとき、Translation/out_temp と PickUpTarget/out_temp は");
    Console.WriteLine("  コピーしない（空の状態で新バージョンを開始する）。中身は毎回の実行で作り直される派生物であり、");
    Console.WriteLine("  古いバージョンのルールで生成された訳が新バージョンに紛れ込む（還流汚染）のを防ぐため。");
    Console.WriteLine("  引き継ぐのは Data/ と Translation/import/ のみ。");
    Console.WriteLine();
    Console.WriteLine("  共通オプション:");
    Console.WriteLine("      --fast : 探索範囲を狭めて実行を速くする（既定は広い探索）");
    Console.WriteLine("               既定でも数秒しか変わらないため、通常は指定不要。");
    Console.WriteLine("               配布物を作るときは必ず既定（広い探索）で実行すること");
    Console.WriteLine();
    Console.WriteLine("  SkyrimJPStringPatcher worddict [corpus.tsv = PickUpTarget/out_temp/corpus.tsv] [output .tsv = Translation/out_temp/word_transliteration_dict.tsv]");
    Console.WriteLine("      -> dumps the word-level EN->JA transliteration dictionary mined from the corpus, for review");
}
