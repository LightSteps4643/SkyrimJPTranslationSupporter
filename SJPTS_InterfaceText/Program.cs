using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;
using SJPTS_InterfaceText;

// 既存CLI（Program.cs）と同じ設定 — GUI側のCliRunner.csはサブプロセスの標準出力を
// UTF-8として読む前提（StandardOutputEncoding = Encoding.UTF8）なので、こちらも
// 明示的にUTF-8で出力しないと、既定のコンソールコードページ（日本語Windowsでは
// 通常Shift-JIS系）とズレて文字化けする。
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// Three verbs mirror the existing SJPTS operational flow (①一覧表示 ②翻訳実行
// ③出力), each its own button in the GUI (design/interface_translations.md):
//   detect    — VFS-scan for untranslated keys. With --mod=X, scans just that
//               mod (unchanged prototype behavior). With neither --mod= nor
//               --mods-file=, scans the WHOLE MO2 load order for every
//               *_english.txt target — same pattern as the ESP pipeline's own
//               `pickuptarget` — and writes mod_summary.tsv (mirrors
//               coverage_by_plugin.tsv) alongside each mod's own
//               interface_translations.tsv. (re)writes each mod's tsv fresh (discards
//               whatever was there — same as the existing tool's "MO2再読込＆初期化").
//   translate — per --mod=X (or every mod listed in --mods-file=, mirroring
//               the ESP pipeline's --plugins-file=): reads interface_translations.tsv,
//               calls the LLM for unresolved rows, updates the tsv. Re-running
//               does NOT re-send already-resolved rows.
//   output    — per --mod=X (or --mods-file=): reads interface_translations.tsv
//               (post-review/manual-edit), writes the final merged *_japanese.txt.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: SJPTS_InterfaceText <detect|translate|output> [--mod=<name> | --mods-file=<path>] [options]");
    return 1;
}

var verb = args[0];
var rest = args.Skip(1).ToArray();

// 2026-09-12: no hardcoded fallback — mirrors the ESP CLI's own pickuptarget
// (Program.cs), which takes the MO2 instance dir as a required argument with
// no default at all. The old hardcoded "D:\Modding\MO2" was a leftover dev-
// machine path: harmless via the GUI (always passes --mo2-instance=
// explicitly, from AppSettings) but a real problem for direct CLI use — a
// forgotten --mo2-instance= would silently scan a personal, possibly-
// nonexistent path instead of failing loudly.
string? mo2Instance = null;
string? modName = null;
string? modsFile = null;
// Folder layout mirrors the ESP CLI's own two meaningfully-separate stages
// (design discussion, 2026-09-12): "Translation" (candidate resolution — both
// the non-LLM existing/import japanese matching in DetectOneMod, and the LLM
// step in RunTranslateOne, share this folder, exactly like the ESP CLI keeps
// translations.tsv/prompt.txt/*.log together under Translation/out_temp/
// <plugin>/) vs "GenerateTranslationFile" (final merged output, ESP's own
// GenerateDsdFile/out/ counterpart). No PickUpTarget-equivalent folder: unlike
// the ESP CLI's Mutagen scan, this tool's *_english.txt VFS scan is cheap and
// mod-scoped, with no standalone artifact worth persisting separately.
string workDir = Path.Combine(AppContext.BaseDirectory, "Translation", "out_temp");
string outDir = Path.Combine(AppContext.BaseDirectory, "GenerateTranslationFile", "out");
// User-supplied *_japanese.txt files (same $Key<TAB>Text format this tool
// reads/writes itself — NOT xTranslator's XML, unlike the ESP CLI's own
// Translation/import) — checked by `detect` in DetectOneMod, ahead of
// (i.e. overriding) whatever the mod's own load order already provides,
// since a file placed here is a deliberate, curated override.
string importDir = Path.Combine(AppContext.BaseDirectory, "Translation", "import");
string claudeExe = "claude";
string claudeModel = "";
string? localLlmEndpoint = null;
string localLlmModel = "";
// Mirrors the ESP CLI's own convention (Program.cs): the local LLM API key
// travels via an environment variable, never as a plain command-line argument
// (a plain arg is visible in the process list/command-line logs) — set by
// CliRunner.cs on the subprocess when the GUI has one configured.
string localLlmApiKey = Environment.GetEnvironmentVariable("SKYRIMJPSP_LLM_API_KEY") ?? "";
// Mirrors the ESP CLI's own --llm-local-reasoning-effort=/--llm-cloud-reasoning-
// effort= — forwarded verbatim to LocalLlmOptions.ReasoningEffort (see that
// record's remarks for why this exists: a "thinking"-capable model like
// Ollama's gemma4 can burn its whole completion token budget on an internal
// reasoning trace and return empty content otherwise).
string? localLlmReasoningEffort = null;
string? cloudLlmReasoningEffort = null;
// Mirrors the ESP CLI's own --llm-cloud-provider=<claudecode|http> (Program.cs):
// "claudecode" (default) launches the Claude Code CLI as a subprocess; "http"
// talks to an OpenAI-compatible endpoint directly, reusing LocalLlmTranslator
// (a generic OpenAI-compatible HTTP client despite its name — same class the
// ESP CLI reuses for its own cloud "http" provider).
string cloudLlmProvider = "claudecode";
string? cloudLlmEndpoint = null;
string cloudLlmModel = "";
int? charLimitOverride = null;
// Mirrors the ESP CLI's own --cancel-flag-path= (Program.cs / PromptGenerator.
// RunMany): checked between mods in ForEachTargetMod below, same plugin-level
// (here: mod-level) granularity as the ESP pipeline — not mid-batch.
string? cancelFlagPath = null;
// Mirrors the ESP CLI's own --mods-dir=/--profile-dir=/--overwrite-dir=
// (Program.cs) — for a non-standard MO2 layout (portable instance, custom
// folder placement). Forwarded straight to Mo2InstanceReader.Read, which
// already supports these (shared Core code) — this CLI just wasn't passing
// them through yet.
string? mo2ModsDirOverride = null;
string? mo2ProfileDirOverride = null;
string? mo2OverwriteDirOverride = null;

foreach (var arg in rest)
{
    if (arg.StartsWith("--mo2-instance=")) mo2Instance = arg["--mo2-instance=".Length..];
    else if (arg.StartsWith("--mod=")) modName = arg["--mod=".Length..];
    else if (arg.StartsWith("--mods-file=")) modsFile = arg["--mods-file=".Length..];
    else if (arg.StartsWith("--work=")) workDir = arg["--work=".Length..];
    else if (arg.StartsWith("--out=")) outDir = arg["--out=".Length..];
    else if (arg.StartsWith("--import=")) importDir = arg["--import=".Length..];
    else if (arg.StartsWith("--claude-code-exe=")) claudeExe = arg["--claude-code-exe=".Length..];
    else if (arg.StartsWith("--claude-code-model=")) claudeModel = arg["--claude-code-model=".Length..];
    else if (arg.StartsWith("--local-llm-endpoint=")) localLlmEndpoint = arg["--local-llm-endpoint=".Length..];
    else if (arg.StartsWith("--local-llm-model=")) localLlmModel = arg["--local-llm-model=".Length..];
    else if (arg.StartsWith("--local-llm-reasoning-effort=")) localLlmReasoningEffort = arg["--local-llm-reasoning-effort=".Length..];
    else if (arg.StartsWith("--llm-cloud-provider=")) cloudLlmProvider = arg["--llm-cloud-provider=".Length..];
    else if (arg.StartsWith("--llm-cloud-endpoint=")) cloudLlmEndpoint = arg["--llm-cloud-endpoint=".Length..];
    else if (arg.StartsWith("--llm-cloud-model=")) cloudLlmModel = arg["--llm-cloud-model=".Length..];
    else if (arg.StartsWith("--llm-cloud-reasoning-effort=")) cloudLlmReasoningEffort = arg["--llm-cloud-reasoning-effort=".Length..];
    else if (arg.StartsWith("--char-limit=")) charLimitOverride = ParseCharLimit(arg["--char-limit=".Length..]);
    else if (arg.StartsWith("--cancel-flag-path=")) cancelFlagPath = arg["--cancel-flag-path=".Length..];
    else if (arg.StartsWith("--mods-dir=")) mo2ModsDirOverride = arg["--mods-dir=".Length..];
    else if (arg.StartsWith("--profile-dir=")) mo2ProfileDirOverride = arg["--profile-dir=".Length..];
    else if (arg.StartsWith("--overwrite-dir=")) mo2OverwriteDirOverride = arg["--overwrite-dir=".Length..];
}

// ESP CLI's own --llm-local-batch-char-limit=/--llm-cloud-batch-char-limit=
// validate the same way (Program.cs) rather than letting int.Parse throw an
// unhandled FormatException straight through to the user.
int ParseCharLimit(string raw)
{
    if (!int.TryParse(raw, out var value) || value <= 0)
    {
        Console.Error.WriteLine($"--char-limit=<positive integer> required (got: '{raw}').");
        Environment.Exit(1);
    }
    return value;
}

return verb switch
{
    "detect" => RunDetect(),
    "translate" => RunTranslate(),
    "output" => ForEachTargetMod(RunOutputOne),
    _ => Fail($"unknown verb '{verb}' — expected detect|translate|output"),
};

// Mirrors the ESP CLI's own Program.cs: one RunLog/TraceLog opened ONCE per
// invocation, at the "Translation" stage root — NOT one per mod. Every mod
// processed in this run (--mod=/--mods-file=) writes into the SAME
// interfacetext.log/interfacetext.trace.log, so a person debugging a failed
// run has one file to check, not one scattered per mod folder (2026-09-12
// design discussion — the old per-mod RunLog.Open inside RunTranslateOne made
// failures easy to miss since nothing consolidated them).
int RunTranslate()
{
    var logDir = Directory.GetParent(workDir)?.FullName ?? workDir;
    using var log = RunLog.Open(logDir, "InterfaceText");
    using var trace = TraceLog.Open(logDir, "InterfaceText");
    return ForEachTargetMod(target => RunTranslateOne(target, log, trace));
}

// --mod=X が指定されていればそれ1件、--mods-file=path が指定されていれば
// そのファイルに列挙された全modを対象にする（既存の--plugins-file=と同じ
// 考え方）。どちらも無ければエラー（detectと違い、translate/outputは
// 対象を明示する必要がある — 中間ファイルが無関係なmod分まで巻き込むと危険）。
int ForEachTargetMod(Func<string, int> action)
{
    List<string> targets;
    if (modsFile != null)
    {
        if (!File.Exists(modsFile)) return Fail($"{modsFile} not found.");
        targets = File.ReadAllLines(modsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
    else if (modName != null)
    {
        targets = new List<string> { modName };
    }
    else
    {
        return Fail("specify either --mod=<name> or --mods-file=<path>.");
    }

    var exitCode = 0;
    foreach (var target in targets)
    {
        Console.WriteLine($"=== {target} ===");
        var result = action(target);
        if (result != 0) exitCode = result;
        // Machine-readable "this mod just finished" marker — the GUI's progress
        // bar parses this line (Services.TranslationProgressParser). Printed
        // regardless of per-mod success/failure so the progress bar still
        // advances on a skipped/failed mod, mirroring the ESP CLI's own
        // "Target: {plugin} (...)" line (always printed once RunMany finishes
        // that plugin, success or not).
        Console.WriteLine($"##SJPTS_MOD_DONE## {target}");

        if (cancelFlagPath != null && File.Exists(cancelFlagPath))
        {
            Console.WriteLine("[warn] cancellation requested — stopping before the remaining mod(s).");
            break;
        }
    }
    return exitCode;
}

int RunDetect()
{
    if (mo2Instance == null)
        return Fail("--mo2-instance=<path> is required.");

    Mo2Instance instance;
    try
    {
        instance = Mo2InstanceReader.Read(mo2Instance, mo2ModsDirOverride, mo2ProfileDirOverride, mo2OverwriteDirOverride);
    }
    catch (Mo2InstanceConfigurationException ex)
    {
        return Fail(ex.Message);
    }

    Console.WriteLine($"enabled mod count: {instance.EnabledModPriorityHighFirst.Count}");

    var vfs = Mo2InstanceReader.BuildVfsDirectoryMerge(instance, "interface/translations");
    Console.WriteLine($"interface/translations file count after VFS resolution: {vfs.Count}");

    List<string> targetMods;
    if (modsFile != null)
    {
        if (!File.Exists(modsFile)) return Fail($"{modsFile} not found.");
        targetMods = File.ReadAllLines(modsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
    else if (modName != null)
    {
        targetMods = new List<string> { modName };
    }
    else
    {
        // Same idea as pickuptarget: scan the whole MO2 load order to find
        // targets — the base filename of each *_english.txt (with "_english"
        // stripped) is treated as the mod name (same rule as --mod=).
        targetMods = vfs.Keys
            .Where(k => k.EndsWith("_english.txt", StringComparison.OrdinalIgnoreCase))
            .Select(k => Path.GetFileNameWithoutExtension(k).Replace("_english", "", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine($"scanning whole MO2 load order: {targetMods.Count} mod(s) targeted");
    }

    var summary = new List<(string Target, string ModFolderName, int TargetFileCount, int UntranslatedCount, string Status)>();
    foreach (var target in targetMods)
    {
        var result = DetectOneMod(vfs, target, instance.ModsDir);
        if (result == null)
        {
            Console.WriteLine($"[warn] no *_english.txt found for '{target}' (after VFS resolution) — skipping.");
            continue;
        }
        summary.Add((target, result.Value.ModFolderName, 1, result.Value.UntranslatedCount, result.Value.UntranslatedCount == 0 ? "対応済み" : "未対応・未翻訳あり"));
    }

    if (targetMods.Count > 1)
    {
        var summaryPath = Path.Combine(workDir, "mod_summary.tsv");
        Directory.CreateDirectory(workDir);
        using (var writer = new StreamWriter(summaryPath, append: false, new System.Text.UTF8Encoding(false)))
        {
            writer.WriteLine("Target\tModFolderName\tTargetFileCount\tUntranslatedCount\tStatus");
            foreach (var (target, modFolderName, files, untranslated, status) in summary)
                writer.WriteLine($"{target}\t{modFolderName}\t{files}\t{untranslated}\t{status}");
        }
        Console.WriteLine($"wrote per-mod summary: {summaryPath} ({summary.Count} entries)");
    }

    return 0;
}

(int UntranslatedCount, int TotalCount, string ModFolderName)? DetectOneMod(Dictionary<string, string> vfs, string target, string modsDir)
{
    var englishPath = vfs.Keys
        .Where(k => k.EndsWith("_english.txt", StringComparison.OrdinalIgnoreCase))
        .Where(k => Path.GetFileNameWithoutExtension(k).StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
                    vfs[k].Contains(target, StringComparison.OrdinalIgnoreCase))
        .Select(k => vfs[k])
        .FirstOrDefault();

    if (englishPath == null) return null;

    // 2026-09-12: the display-facing MOD name is the folder that actually
    // WON this file in the VFS merge (design/interface_translations.md's
    // original intent) — NOT `target` itself, which stays tied to the
    // *_english.txt file's own base name (required for the final
    // <target>_japanese.txt output filename the game actually loads by).
    // These two diverge whenever a mod's file isn't named after the mod
    // itself (real example: "aaa_english.txt" shipped by "Oblivion
    // Interaction Icons DSD 1.4.3 patch").
    var modFolderName = Path.GetRelativePath(modsDir, englishPath).Split(Path.DirectorySeparatorChar)[0];

    Console.WriteLine($"English source: {englishPath}");
    var englishEntries = InterfaceTranslationsFile.Parse(englishPath);
    Console.WriteLine($"key count: {englishEntries.Count}");

    var baseName = Path.GetFileNameWithoutExtension(englishPath)
        .Replace("_english", "", StringComparison.OrdinalIgnoreCase);

    var japanesePath = vfs
        .Where(kv => Path.GetFileNameWithoutExtension(kv.Key)
            .Equals($"{baseName}_japanese", StringComparison.OrdinalIgnoreCase))
        .Select(kv => kv.Value)
        .FirstOrDefault();

    var existingJapanese = new Dictionary<string, string>(StringComparer.Ordinal);
    if (japanesePath != null)
    {
        Console.WriteLine($"existing Japanese file: {japanesePath}");
        foreach (var (key, value) in InterfaceTranslationsFile.Parse(japanesePath))
            existingJapanese[key] = value;
    }
    else
    {
        Console.WriteLine("existing Japanese file: none");
    }

    // Import folder takes priority over the load order's own bundled
    // _japanese.txt — a per-key overwrite (not a wholesale replace), so a mod
    // whose import only covers some keys still falls back to the load order's
    // own file for the rest.
    var importPath = FindImportFile(importDir, baseName);
    if (importPath != null)
    {
        Console.WriteLine($"import file (takes priority): {importPath}");
        foreach (var (key, value) in InterfaceTranslationsFile.Parse(importPath))
            existingJapanese[key] = value;
    }
    else
    {
        Console.WriteLine($"import file: none (looked in {importDir})");
    }

    var rows = englishEntries.Select(e =>
    {
        // 2026-09-11: MOD名そのものを表すkey（$を除いた文字列がMOD名と完全一致）は
        // 翻訳せず英語のまま残す — 実データ検証（Precision/TrueHUD）で、モデルが
        // $Precision/$TrueHUDをそれぞれ「プレシジョン」「トゥルーHUD」と音訳して
        // しまうことを確認済み。ほぼ全keyがMOD名をプレフィックスに持つ命名規則
        // なので、判定は「含むか」ではなく「完全一致」でなければならない。
        // プロンプト側の指示に頼らず、ここで確実に除外する。
        if (e.Key.TrimStart('$').Equals(target, StringComparison.OrdinalIgnoreCase))
            return new InterfaceTranslationRow(e.Key, e.Value, e.Value, true);

        var hasGood = existingJapanese.TryGetValue(e.Key, out var jp) && LanguageDetector.ContainsJapanese(jp);
        return new InterfaceTranslationRow(e.Key, e.Value, hasGood ? jp! : "", hasGood);
    }).ToList();

    var modWorkDir = Path.Combine(workDir, target);
    var tsvPath = Path.Combine(modWorkDir, "interface_translations.tsv");
    InterfaceTranslationsTsv.Write(tsvPath, rows);

    // Display-only — read by the GUI (ScanInterfaceTranslations) to show the
    // real providing MOD's folder name instead of the raw file-based target.
    File.WriteAllText(Path.Combine(modWorkDir, "mod_folder_name.txt"), modFolderName, new System.Text.UTF8Encoding(false));

    var untranslated = rows.Count(r => !r.Resolved);
    Console.WriteLine($"resolved: {rows.Count(r => r.Resolved)} / unresolved-untranslated: {untranslated}");
    Console.WriteLine($"wrote intermediate file (regenerated fresh, discarding any existing one): {tsvPath}");
    Console.WriteLine($"providing MOD: {modFolderName}");
    return (untranslated, rows.Count, modFolderName);
}

// Recursive (mirrors the ESP CLI's own XTranslatorImporter.Load: a zip
// extracted straight into the import folder still resolves) — looks for
// "<baseName>_japanese.<ext>" anywhere under importDir.
string? FindImportFile(string dir, string baseName)
{
    if (!Directory.Exists(dir)) return null;
    return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals($"{baseName}_japanese", StringComparison.OrdinalIgnoreCase));
}

int RunTranslateOne(string target, RunLog log, TraceLog trace)
{
    var modWorkDir = Path.Combine(workDir, target);
    var tsvPath = Path.Combine(modWorkDir, "interface_translations.tsv");

    if (!File.Exists(tsvPath))
        return Fail($"{tsvPath} not found. Run detect first.");

    var rows = InterfaceTranslationsTsv.Read(tsvPath);
    var pending = rows.Where(r => !r.Resolved).Select(r => (r.Key, r.English)).ToList();
    Console.WriteLine($"read intermediate file: {rows.Count} row(s) ({pending.Count} unresolved)");

    if (pending.Count == 0)
    {
        Console.WriteLine("no unresolved keys — skipping the translation call.");
        return 0;
    }

    ITextTranslator translator;
    int defaultCharLimit;
    string providerLabel;
    if (localLlmEndpoint != null)
    {
        translator = new LocalLlmTranslator(new LocalLlmOptions(localLlmEndpoint, localLlmModel, localLlmApiKey, localLlmReasoningEffort));
        defaultCharLimit = InterfaceTextPromptGenerator.DefaultLocalLlmBatchCharLimit;
        providerLabel = "localLLM";
    }
    else if (cloudLlmProvider.Equals("http", StringComparison.OrdinalIgnoreCase))
    {
        if (cloudLlmEndpoint == null || cloudLlmModel.Length == 0)
            return Fail("--llm-cloud-provider=http requires --llm-cloud-endpoint=<url> and --llm-cloud-model=<name>.");
        // Mirrors the ESP CLI's own convention: the cloud API key travels via an
        // environment variable (SKYRIMJPSP_CLOUD_LLM_API_KEY), never as a plain
        // command-line argument — CliRunner.cs sets this on the subprocess.
        var cloudLlmApiKey = Environment.GetEnvironmentVariable("SKYRIMJPSP_CLOUD_LLM_API_KEY") ?? "";
        translator = new LocalLlmTranslator(new LocalLlmOptions(cloudLlmEndpoint, cloudLlmModel, cloudLlmApiKey, cloudLlmReasoningEffort));
        defaultCharLimit = InterfaceTextPromptGenerator.DefaultLlmBatchCharLimit;
        providerLabel = "cloudLLM";
    }
    else
    {
        translator = new ClaudeCodeTranslator(new ClaudeCodeOptions(claudeExe, claudeModel));
        defaultCharLimit = InterfaceTextPromptGenerator.DefaultLlmBatchCharLimit;
        providerLabel = "cloudLLM";
    }
    var charLimit = charLimitOverride ?? defaultCharLimit;

    Directory.CreateDirectory(modWorkDir);

    var translated = InterfaceTextPromptGenerator.ApplyLlmStep(pending, translator, target, log, trace, charLimit, modWorkDir, providerLabel);
    Console.WriteLine($"resolved: {translated.Count} / {pending.Count}");

    // Mirrors Program.cs's own LogCloudAiUsage — emit cumulative usage at the
    // end of the run (design/llm_integration.md "token/cost usage log").
    if (translator is ClaudeCodeTranslator claude)
    {
        var usage = claude.Usage;
        var usageLine = $"cloud AI usage: {usage.CallCount} call(s) / {usage.InputTokens} input, {usage.OutputTokens} output token(s) / " +
            $"{usage.CacheCreationInputTokens} cache-creation, {usage.CacheReadInputTokens} cache-read token(s) / est. cost ${usage.TotalCostUsd:F4}";
        log.Report(usageLine);
    }

    var updated = rows.Select(r =>
        translated.TryGetValue(r.Key, out var t) ? r with { Japanese = t.Japanese, Resolved = true, Notes = t.Notes } : r
    ).ToList();

    InterfaceTranslationsTsv.Write(tsvPath, updated);
    Console.WriteLine($"updated intermediate file: {tsvPath}");

    var stillUnresolved = updated.Where(r => !r.Resolved).ToList();
    if (stillUnresolved.Count > 0)
    {
        // DetailAndReport already echoes the English consoleText to the
        // console (RunLog.Report) — no separate Console.WriteLine needed here.
        log.DetailAndReport("最終的に未解決のまま残ったkey", "keys still unresolved at end of run",
            $"[{target}]  {stillUnresolved.Count}件が未解決のまま残りました（再度 translate を実行すると、この分だけ再試行されます）",
            $"[{target}] {stillUnresolved.Count} key(s) still unresolved (re-run translate to retry just these)");
    }

    return 0;
}

int RunOutputOne(string target)
{
    var modWorkDir = Path.Combine(workDir, target);
    var tsvPath = Path.Combine(modWorkDir, "interface_translations.tsv");

    if (!File.Exists(tsvPath))
        return Fail($"{tsvPath} not found. Run detect / translate first.");

    var rows = InterfaceTranslationsTsv.Read(tsvPath);
    var unresolved = rows.Where(r => !r.Resolved).ToList();
    if (unresolved.Count > 0)
    {
        Console.WriteLine($"[warn] {unresolved.Count} key(s) will be output unresolved (kept in English):");
        foreach (var r in unresolved) Console.WriteLine($"  - {r.Key}: {r.English}");
    }

    // Mirrors the ESP CLI's own DsdJsonGenerator.cs: a Resolved row whose
    // Japanese still doesn't contain Japanese characters is not an error to
    // exclude or warn about (ModifiedByUser/*NoJapanese both represent a
    // deliberate acceptance — a human's own edit, or the model's judgment
    // that this string doesn't need translation, e.g. "Ok"->"OK") — just an
    // informational note so it's easy to spot-check later.
    var acceptedNonJapanese = rows.Where(r => r.Resolved
        && (r.Notes == "ModifiedByUser" || r.Notes.EndsWith("NoJapanese", StringComparison.Ordinal))
        && !LanguageDetector.ContainsJapanese(r.Japanese)).ToList();
    if (acceptedNonJapanese.Count > 0)
    {
        Console.WriteLine($"[note] {acceptedNonJapanese.Count} key(s) output as-is with no Japanese (accepted — {string.Join('/', acceptedNonJapanese.Select(r => r.Notes).Distinct())}):");
        foreach (var r in acceptedNonJapanese) Console.WriteLine($"  - {r.Key}: {r.Japanese}");
    }

    var outputEntries = rows.Select(r => (r.Key, string.IsNullOrEmpty(r.Japanese) ? r.English : r.Japanese)).ToList();

    // baseName はtsvに保存していないため、detect時に使ったのと同じ規則で
    // ファイル名を再構成する代わりに、mod名をそのままベースファイル名として使う
    // （Precisionのようにmod名＝ファイル名prefixの場合はそのまま一致する。
    //   一致しないケースの扱いは本実装時に要検討 — 未確定事項に記載）。
    var outputPath = Path.Combine(outDir, "Interface", "Translations", $"{target}_japanese.txt");
    InterfaceTranslationsFile.Write(outputPath, outputEntries);

    Console.WriteLine($"output complete: {outputPath}");
    return 0;
}

int Fail(string message)
{
    Console.Error.WriteLine($"[error] {message}");
    return 1;
}
