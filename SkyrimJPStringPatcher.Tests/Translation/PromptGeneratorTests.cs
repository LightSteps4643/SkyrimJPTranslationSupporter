using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Tests.PickUpTarget;
using SkyrimJPStringPatcher.Translation;
using static SkyrimJPStringPatcher.Core.TsvEscaping;
// CurrentDirectoryScope lives in the SkyrimJPStringPatcher.Tests namespace directly.

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// PromptGenerator.RunOne — the actual orchestration ①〜⑥ auto-resolution
/// funnels through (BuildContext, existing-translation carry-forward,
/// ApplyLlmStep's batching/response-parsing contract), as opposed to
/// AutoTranslatorTests' narrower focus on ①'s own exact-match logic in
/// isolation.
///
/// Fixtures/Translation/PromptGenerator/{candidates.tsv,corpus.tsv} are real
/// PickUpTarget-shaped interchange files, read via CandidateIo/CorpusIo
/// exactly like the real pipeline. The plugin name ("SjptsTestMod.esp") and
/// every candidate string are deliberately fictional/unique — BuildContext
/// unconditionally merges in the REAL Data/skyrim_taiyaku_reference.tsv and
/// Data/name_glossary.tsv, so a realistic-but-real vanilla string here could
/// silently resolve via ① for a reason this test isn't asserting about.
///
/// ⑤⑥ (LLM) tests use FakeTextTranslator (no network/subprocess) — the seam
/// ITextTranslator already exists for exactly this. ModGlossary.WriteTemplate
/// (triggered when ④ leaves blocked words) writes under
/// Directory.GetCurrentDirectory()/Data/mod_glossary — confirmed this
/// resolves to the TEST PROJECT's own build output copy of Data/ during
/// `dotnet test`, not the repository's real tracked Data/ folder, so it
/// cannot pollute real curated data; not otherwise guarded against here.
/// </summary>
public class PromptGeneratorTests
{
    private const string TargetPlugin = "SjptsTestMod.esp";
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Translation", "PromptGenerator");

    private static string CandidatesTsvPath => Path.Combine(FixturesDir, "candidates.tsv");
    private static string CorpusTsvPath => Path.Combine(FixturesDir, "corpus.tsv");

    private static RunLog OpenTestLog(string root) => RunLog.Open(Path.Combine(root, "Translation"), "Translation");

    /// <summary>A directory that does not exist — XTranslatorImporter.Load
    /// tolerates this (logs "no import" and returns 0 entries), so tests that
    /// don't care about xTranslator import can use it instead of standing up
    /// an empty real folder.</summary>
    private static string NonexistentImportDir(string root) => Path.Combine(root, "no_such_import_dir");

    private static Dictionary<string, (string Japanese, string Notes)> ReadTranslationsTemplate(string path)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 6) continue;
            result[Unescape(parts[3])] = (Unescape(parts[4]), Unescape(parts[5])); // keyed by EnglishText
        }
        return result;
    }

    /// <summary>Pre-seeds (overwriting any prior content) this plugin's
    /// mod-scoped glossary with one filled row, so a test can force a
    /// candidate through step ④ (NameFallbackTranslator) deterministically —
    /// the ONLY source ④ has that ①〜③ (AutoTranslator.TryTranslate) don't.</summary>
    private static void SeedModGlossary(string plugin, string english, string japanese)
    {
        var path = Path.Combine(ModGlossary.DirectoryPath, Path.GetFileNameWithoutExtension(plugin) + ".tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{english}\t{japanese}\n");
    }

    [Fact]
    public void RunOne_ExactMatchCandidate_ResolvesViaCorpus_NotWrittenToPrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log);

            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("テスト用の剣", "SJPTS_AutoCorpus"), translations["Sjpts Test Sword"]);

            // It may still appear as a PRECEDENT EXAMPLE surfaced for some
            // other unresolved candidate's prompt block (that's the intended
            // behavior) — what must NOT happen is it appearing as its own
            // Target: line, i.e. something the AI is being asked to translate.
            var prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt.txt"));
            Assert.DoesNotContain("Target: <SJPTS_TARGET>Sjpts Test Sword</SJPTS_TARGET>", prompt);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-18: translations.tsv gained a new "TranslationCheck"
    /// column (TranslationQualityChecker) recording a machine-classifiable
    /// quality category for the resolved Japanese, independent of the Notes
    /// column (which records resolution METHOD, not quality — see
    /// TranslationQualityChecker's remarks for why the two are kept
    /// separate). Deliberately scoped to ⑤ローカルLLM/⑥生成AI翻訳 only
    /// (user decision, 2026-09-18): ②③④ are structurally guaranteed to
    /// produce complete Japanese by construction, and ①コーパス完全一致
    /// (including vanilla) is already-correct reference data, not something
    /// this tool produced and needs to grade — so a corpus-resolved candidate
    /// must have this column left BLANK, while an LLM-resolved one gets a
    /// real classification.</summary>
    [Fact]
    public void RunOne_CorpusResolvedCandidate_LeavesTranslationCheckBlank()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log);

            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var lines = File.ReadAllLines(Path.Combine(pluginDir, "translations.tsv"));
            var headers = lines[0].Split('\t');
            var translationCheckCol = Array.IndexOf(headers, "TranslationCheck");
            Assert.True(translationCheckCol >= 0, "expected a TranslationCheck column in translations.tsv's header");

            var row = lines.Skip(1).Select(l => l.Split('\t')).Single(p => Unescape(p[3]) == "Sjpts Test Sword");
            Assert.Equal("", row[translationCheckCol]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunOne_LocalLlmResolvedCandidate_WritesTranslationCheckClassification()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Llm Candidate", "完全な日本語の訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var lines = File.ReadAllLines(Path.Combine(pluginDir, "translations.tsv"));
            var headers = lines[0].Split('\t');
            var translationCheckCol = Array.IndexOf(headers, "TranslationCheck");

            var row = lines.Skip(1).Select(l => l.Split('\t')).Single(p => Unescape(p[3]) == "Sjpts Llm Candidate");
            Assert.Equal("AllJapanese", row[translationCheckCol]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Re-running (carrying forward an already-LLM-resolved row
    /// without re-calling the LLM) must not lose the previously-computed
    /// TranslationCheck value — ReadExistingTranslations now carries it
    /// forward the same way it already does for Japanese/Notes.</summary>
    [Fact]
    public void RunOne_RerunCarriesForwardPreviouslyComputedTranslationCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using (var log1 = OpenTestLog(root))
            {
                var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Llm Candidate", "This is 剣"));
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log1, llmLocal: fakeLlm);
            }

            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var firstRunHeaders = File.ReadAllLines(Path.Combine(pluginDir, "translations.tsv"))[0].Split('\t');
            var col = Array.IndexOf(firstRunHeaders, "TranslationCheck");
            var firstRunRow = File.ReadAllLines(Path.Combine(pluginDir, "translations.tsv")).Skip(1)
                .Select(l => l.Split('\t')).Single(p => Unescape(p[3]) == "Sjpts Llm Candidate");
            Assert.Equal("ContainsMostOfAlphaNumeric", firstRunRow[col]);

            // Re-run with NO llmLocal at all -- if the row weren't carried
            // forward it would either stay unresolved or (worse) silently
            // lose its TranslationCheck value while keeping its Japanese/Notes.
            using (var log2 = OpenTestLog(root))
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log2);

            var secondRunRow = File.ReadAllLines(Path.Combine(pluginDir, "translations.tsv")).Skip(1)
                .Select(l => l.Split('\t')).Single(p => Unescape(p[3]) == "Sjpts Llm Candidate");
            Assert.Equal("ContainsMostOfAlphaNumeric", secondRunRow[col]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunOne_CandidateWithNoPrecedentAndNoLlm_EndsUpUnresolvedInPromptTxt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log);

            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("", ""), translations["Sjpts Unresolved Candidate"]);

            var prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt.txt"));
            Assert.Contains("Sjpts Unresolved Candidate", prompt);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunOne_LocalLlmSucceeds_ResolvesWithTranslationLocalLlmTag()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Llm Candidate", "LLMによる訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, llmLocal: fakeLlm);

            // 2026-09-16: このフィクスチャにはSjptsTestMod向けに未解決候補が
            // 4件あり、fakeが解決できるのは"Sjpts Llm Candidate"だけ。1回目の
            // 呼び出しでそれが解決されて進捗ありと判定されるため（進捗ベースの
            // 自己終了条件——finish_reason=lengthの有無は問わない）、残り3件を
            // 対象に2回目の呼び出しが行われる（そこでは解決0件のため終了）。
            Assert.Equal(2, fakeLlm.CallCount);
            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("LLMによる訳", "SJPTS_TranslationLocalLlm"), translations["Sjpts Llm Candidate"]);

            var prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt.txt"));
            Assert.DoesNotContain("Target: <SJPTS_TARGET>Sjpts Llm Candidate</SJPTS_TARGET>", prompt);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-17: item 11（実データでLLM応答への内部タグ残存が複数
    /// 確認された）対応。訳文の末尾に本ツール自身の境界タグ（&lt;/SJPTS_TARGET&gt;）が
    /// 残存した応答は、原文には存在しないタグが訳文に混入しているため、構造的な
    /// タグ検証（&lt;[^&gt;]*&gt;で抽出したタグの多重集合比較）で弾かれ、未解決のまま
    /// 残るべき。</summary>
    [Fact]
    public void RunOne_LocalLlmResponse_LeaksClosingTargetTagInAnswer_TreatedAsUnresolved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw(
                "<SJPTS_TARGET>Sjpts Llm Candidate</SJPTS_TARGET>\tLLMによる訳</SJPTS_TARGET>");

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("", ""), translations["Sjpts Llm Candidate"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-12: debugging aid distinct from prompt.txt (that one is
    /// a human AI-chat handoff for whatever's STILL unresolved after this
    /// step; this file is a record of what WAS actually sent to step 5,
    /// win or lose — added after investigating a real local-LLM failure on
    /// the Interface\Translations side of the tool, then brought here for
    /// parity). Content must match what ApplyLlmStep actually built and
    /// passed to ITextTranslator.TryTranslate — checked directly against
    /// FakeTextTranslator's own captured prompt rather than re-deriving the
    /// expected text, since the point is exact byte-for-byte parity.</summary>
    [Fact]
    public void RunOne_LocalLlmStep_WritesPromptBatchFile_MatchingWhatWasSent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Llm Candidate", "LLMによる訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            // 2026-09-16: このフィクスチャは未解決4件のうちfakeが1件しか解決
            // できないため、進捗ベースの自己終了条件により2回目の呼び出しが
            // 発生する（RunOne_LocalLlmSucceeds_ResolvesWithTranslationLocalLlmTag
            // 参照）。ファイル名は「呼び出し番号のみ」（合計回数は事前に分から
            // ないため「_of_総数」は付かない）——LastPromptReceivedは直近
            // （2回目）の呼び出しを指すので、それに対応する2回目のファイルと
            // 突き合わせる。2回目の送信対象には既に解決済みの
            // "Sjpts Llm Candidate"は含まれない。
            var promptCallPath = Path.Combine(pluginDir, "prompt_localLLM_call2.txt");
            Assert.True(File.Exists(promptCallPath));
            var promptCallContent = File.ReadAllText(promptCallPath);
            Assert.Equal(fakeLlm.LastPromptReceived, promptCallContent);
            Assert.Contains("Target: <SJPTS_TARGET>Sjpts Unresolved Candidate</SJPTS_TARGET>", promptCallContent);
            // "Sjpts Llm Candidate" itself is no longer sent as a translation
            // target (already resolved in call 1) — it may legitimately still
            // appear inside issue #4's same-mod hint block, since the other
            // candidates share vocabulary with it.
            Assert.DoesNotContain("Target: <SJPTS_TARGET>Sjpts Llm Candidate</SJPTS_TARGET>", promptCallContent);

            // Only this run's own step-5 file exists — no leftover step-6
            // (cloudLLM) file from a run that never enabled step 6.
            Assert.Empty(Directory.GetFiles(pluginDir, "prompt_cloudLLM_call*.txt"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.58.5: a real bug found investigating why gemma4 batches
    /// consisting entirely of vanilla Skyrim's own untranslated "arcane
    /// script" spell-tome content (e.g. the $MageScriptFont flavor page —
    /// scrambled, non-linguistic text that even the OFFICIAL Japanese release
    /// leaves untranslated, confirmed by reading Skyrim.esm directly) always
    /// failed outright: LocalLlmTranslator.CallOnce used to reject the WHOLE
    /// batch response if it contained no Japanese anywhere, even though the
    /// model answered in perfectly well-formed "English&lt;TAB&gt;Japanese" TSV —
    /// it just had nothing translatable to put in the Japanese column, so it
    /// echoed the source back (the model's own signal that this is genuinely
    /// untranslatable, not a translation failure). That whole-batch gate is
    /// gone; ApplyLlmStep now judges Japanese-content PER CANDIDATE and tags a
    /// non-Japanese-but-successfully-matched result with methodTag+"NoJapanese"
    /// (here "SJPTS_TranslationLocalLlmNoJapanese") instead of either discarding it
    /// (wasting a retry that would just reproduce the same result) or silently
    /// accepting it under the ordinary tag (risking a genuine failure looking
    /// identical to a real translation) — a human can tell the two apart in
    /// the review UI, the tool itself cannot.</summary>
    [Fact]
    public void RunOne_LlmBatch_ResponseHasNoJapanese_ResolvesWithDedicatedReviewTag()
    {
        const string plugin = "SjptsMatchingEdgeCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            // The model answers in valid TSV shape, but the "translation" is
            // just the source echoed back unchanged -- no Japanese anywhere.
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Scrambled Gibberish Candidate", "Sjpts Scrambled Gibberish Candidate"));

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            try
            {
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            // 2026-09-16: this plugin's 6 fixture candidates used to fit in a
            // single batch under the old 100%-of-batchCharLimit packing
            // budget; now that packing reserves ~25% for issue #4's "c" block
            // (SameModHintBudgetRatio) even when c ends up empty, they split
            // into 2 sub-batches, so the local LLM is called twice.
            Assert.Equal(2, fakeLlm.CallCount);
            var pluginDir = Path.Combine(outputDir, "SjptsMatchingEdgeCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            // Resolved (not left unresolved -- no point retrying, it'll just
            // reproduce the same answer), but tagged distinctly from a normal
            // SJPTS_TranslationLocalLlm success so it surfaces for human review.
            Assert.Equal(("Sjpts Scrambled Gibberish Candidate", "SJPTS_TranslationLocalLlmNoJapanese"),
                translations["Sjpts Scrambled Gibberish Candidate"]);

            // 2026-09-06: this used to be log.Detail-only (translation.log
            // only, invisible in the GUI's real-time log window, which just
            // relays this process's Console output) — a user watching a long
            // run had no way to notice a candidate got flagged for review
            // until after the whole run finished. Now DetailAndReport also
            // echoes it to Console immediately, same as the other ⑤⑥
            // unresolved/needs-review paths.
            // 2026-09-13: console output is English-only regardless of
            // RunLogLang (RunLog.cs's own documented design) — this used to
            // wrongly assert Japanese text here, a real bug fixed alongside
            // the "バッチ" leak into English-only strings.
            Assert.Contains("saved as-is for review", capturedOut.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunOne_LocalLlmFails_StaysUnresolvedAndFallsThroughToPrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Failing("simulated LLM outage");

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, llmLocal: fakeLlm);

            Assert.Equal(1, fakeLlm.CallCount);
            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("", ""), translations["Sjpts Llm Candidate"]);

            var prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt.txt"));
            Assert.Contains("Sjpts Llm Candidate", prompt);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.53.0's carry-forward behavior: a row that already has SOME
    /// translation in an existing translations.tsv (any method, including a
    /// costly ⑤/⑆ AI call) must be preserved verbatim on the next RunOne, and
    /// AutoTranslator/the LLM must never be re-consulted for it — the whole
    /// point of the fix that stopped ⑤/⑥ results from silently vanishing (and
    /// being re-billed) on every `translation` re-run.</summary>
    [Fact]
    public void RunOne_Rerun_PreservesAlreadyResolvedRow_WithoutReinvokingLlm()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            Directory.CreateDirectory(pluginDir);
            // Simulate a PRIOR run's already-resolved translations.tsv, as if a
            // costly ⑥ cloud AI call had already answered this one.
            File.WriteAllText(Path.Combine(pluginDir, "translations.tsv"),
                "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\n" +
                "000803:SjptsTestMod.esp\tSjptsTestMod.esp\tWEAP FULL\tSjpts Preserved Candidate\t前回のAI訳\tSJPTS_TranslationCloudLlm\t0\t\n");

            using var log = OpenTestLog(root);
            // If RunOne incorrectly re-ran the LLM for the preserved candidate,
            // this fake would answer with a DIFFERENT translation, exposing the
            // bug immediately via a mismatched assertion below.
            var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Preserved Candidate", "再実行で上書きされた誤答"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, llmLocal: fakeLlm);

            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("前回のAI訳", "SJPTS_TranslationCloudLlm"), translations["Sjpts Preserved Candidate"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunOne_DiscardUserEdits_ResetsPreviouslyResolvedRow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "translations.tsv"),
                "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\n" +
                "000803:SjptsTestMod.esp\tSjptsTestMod.esp\tWEAP FULL\tSjpts Preserved Candidate\t前回の訳\tSJPTS_ModifiedByUser\t0\t\n");

            using var log = OpenTestLog(root);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, discardUserEdits: true);

            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            // No corpus/LLM support for this candidate, so after being reset it
            // simply goes back to unresolved, exactly like a brand-new candidate.
            Assert.Equal(("", ""), translations["Sjpts Preserved Candidate"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunOne_NoCandidatesForPlugin_LogsAndReturnsWithoutWritingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);

            var (_, stdout) = ConsoleCapture.Run(() =>
            {
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), "SjptsNoSuchPlugin.esp", outputDir, log);
                return 0;
            });

            Assert.Contains("No candidates found for 'SjptsNoSuchPlugin.esp'", stdout);
            Assert.False(Directory.Exists(Path.Combine(outputDir, "SjptsNoSuchPlugin")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>④②③ (meaning composition/transliteration decomposition/
    /// NameFallbackTranslator) reached through the FULL orchestration
    /// (RunOne -> WritePluginFilesWithDir), as opposed to AutoTranslatorTests'
    /// narrower "reached through AutoTranslator itself" checks — coverage
    /// showed these paths at 0% specifically at the PromptGenerator layer
    /// (the Detail-log branches, CountByMethod's tallies, and the
    /// ModGlossary-application log line all live here, not in AutoTranslator).
    ///
    /// "Glimmeroot Ring"/"NemraSkol"/"Vrenn Ring" and the corpus rows that
    /// teach them (Fixtures/Translation/PromptGenerator/corpus.tsv) mirror
    /// CorpusMeaningTranslatorTests'/CorpusTransliteratorTests' own fixture
    /// SHAPE (a modifier/head cross-product; two standalone transliterated
    /// pieces) rather than reusing their exact fixture files, since those
    /// live in a different Fixtures subfolder and PromptGeneratorTests needs
    /// vocabulary guaranteed not to also appear in the REAL
    /// Data/skyrim_taiyaku_reference.tsv this test's own BuildContext call
    /// merges in (see this class's own remarks).
    ///
    /// "Vrenn" has no corpus/meaning/transliteration precedent at all, so ①③
    /// leave "Vrenn Ring" unresolved — only a MOD-glossary entry (which only
    /// step ④ consults) can resolve it, forcing this candidate through ④
    /// specifically and deterministically.</summary>
    [Fact]
    public void RunOne_MeaningTransliterationAndNameFallback_ResolveThroughFullPipeline()
    {
        const string plugin = "SjptsResolutionMethods.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root); // isolates ModGlossary.DirectoryPath (CWD-relative) to this test
            SeedModGlossary(plugin, "Vrenn", "ヴレン");
            var outputDir = Path.Combine(root, "out_temp");
            using (var log = OpenTestLog(root))
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log);

            var pluginDir = Path.Combine(outputDir, "SjptsResolutionMethods");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("きらめきの指輪", "SJPTS_AutoCorpusMeaning"), translations["Glimmeroot Ring"]);
            Assert.Equal(("ネムラスコル", "SJPTS_AutoCorpusTransliterate"), translations["NemraSkol"]);

            var (vrennJapanese, vrennMethod) = translations["Vrenn Ring"];
            Assert.Equal("SJPTS_TranslationNameFallback", vrennMethod);
            Assert.NotEmpty(vrennJapanese);

            var logText = File.ReadAllText(Path.Combine(root, "Translation", "translation.log"));
            // "語を適用" (word(s) applied) is unique to this specific log.Line call --
            // NameFallbackTranslator's OWN "MOD用語集" Detail tag (logged separately,
            // as part of ④'s own resolution note for "Vrenn Ring") would otherwise
            // make a bare "MOD用語集" substring check pass vacuously even if this
            // log.Line itself were removed.
            Assert.Contains("MOD用語集: SjptsResolutionMethods.esp → 1語を適用", logText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunOne_DisabledStages_LogsWhichStepsWereSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            using (var log = OpenTestLog(root))
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, stageOptions: stages);

            var logText = File.ReadAllText(Path.Combine(root, "Translation", "translation.log"));
            Assert.Contains("2.意味合成: 無効化（--no-meaning）", logText);
            Assert.Contains("3.音訳分解: 無効化（--no-translit）", logText);
            Assert.Contains("4.NameFallbackTranslator: 無効化（--no-namefallback）", logText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-06: a candidate the model's batch response simply
    /// never answers (omitted entirely -- FakeTextTranslator.Succeeding()
    /// with no matching pair returns an empty, still-non-null response) must
    /// stay unresolved AND explain why in the console output the GUI's
    /// real-time log window relays -- previously this branch only said "not
    /// found in batch response" without any human-readable reason, which a
    /// user flagged as unhelpful ("just says it was skipped, not why"). The
    /// agreed fix: since every path into this branch boils down to the same
    /// root cause (the tool couldn't match the model's response to this
    /// candidate in an interpretable way), one clear sentence covers it
    /// rather than trying to sub-classify the underlying model behavior.</summary>
    [Fact]
    public void RunOne_LlmBatch_CandidateOmittedFromResponse_StaysUnresolvedAndExplainsWhyOnConsole()
    {
        const string plugin = "SjptsLlmSkipReasons.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            // No answers at all -- the fake still returns a (non-null, empty)
            // response, exactly like a model that ignored this candidate.
            var fakeLlm = FakeTextTranslator.Succeeding();

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            try
            {
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var pluginDir = Path.Combine(outputDir, "SjptsLlmSkipReasons");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            // Left unresolved (no tag, blank Japanese) -- unlike the
            // NoJapanese case above, the tool never got an answer to store at all.
            Assert.Equal(("", ""), translations["Sjpts Omitted By Model Candidate"]);

            // 2026-09-13: console output is English-only regardless of
            // RunLogLang (RunLog.cs's own documented design) — this used to
            // wrongly assert Japanese text here, a real bug fixed alongside
            // the "バッチ" leak into English-only strings.
            Assert.Contains("could not resolve", capturedOut.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.53.0a's line-break workaround (FlattenMultiline/
    /// MultilineBreakMarker), reached only through the LLM batch path — a
    /// candidate whose original text contains a real newline (e.g. a book
    /// cover) must survive an LLM round-trip with the newline restored.</summary>
    [Fact]
    public void RunOne_MultilineCandidate_LlmBatch_RestoresLineBreakAfterRoundTrip()
    {
        const string plugin = "SjptsResolutionMethods.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root);
            SeedModGlossary(plugin, "Vrenn", "ヴレン"); // keeps this plugin's other candidates resolved/quiet
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            // The matched key is the FLATTENED text (marker instead of the real
            // newline) -- this is what ApplyLlmStep actually puts on the
            // "Target:" line and matches the response against.
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Multiline Candidate<SJPTS_BR>Second Line", "マルチライン訳<SJPTS_BR>二行目訳"),
                ("Sjpts Batch Candidate One", "バッチ候補一"),
                ("Sjpts Batch Candidate Two", "バッチ候補二"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsResolutionMethods");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            var (japanese, method) = translations["Sjpts Multiline Candidate\nSecond Line"];
            Assert.Equal("SJPTS_TranslationLocalLlm", method);
            Assert.Equal("マルチライン訳\n二行目訳", japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.58.6: real-machine investigation (gemma4:26b/gemma3:12b/
    /// qwen2.5:14b-instruct, "Heretical Thoughts" in unofficial skyrim special
    /// edition patch.esp) found that a model echoing back a multiline
    /// candidate's source text reliably appends one spurious extra
    /// MultilineBreakMarker right before the tab, even though the
    /// translation itself is otherwise perfect — breaking the exact-text
    /// match against matchKey every time, deterministically, regardless of
    /// model. StripSpuriousBoundaryMarker's fallback dictionary exists to
    /// resolve exactly this case without weakening the primary exact
    /// match.</summary>
    [Fact]
    public void RunOne_LlmBatch_ModelAppendsSpuriousTrailingMarkerToSourceEcho_StillResolvesViaFallback()
    {
        const string plugin = "SjptsMarkerFallback.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root);
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            // The echoed source has one extra "<SJPTS_BR>" tacked on at the
            // very end -- the true matchKey (flattened) has no trailing
            // marker at all. This is the exact shape observed on real gemma
            // responses.
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Spurious Marker Candidate<SJPTS_BR>Second Line<SJPTS_BR>", "スプリアスマーカー訳<SJPTS_BR>二行目訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsMarkerFallback");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            var (japanese, method) = translations["Sjpts Spurious Marker Candidate\nSecond Line"];
            Assert.Equal("SJPTS_TranslationLocalLlm", method);
            Assert.Equal("スプリアスマーカー訳\n二行目訳", japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Companion to the spurious-marker fallback test above: a
    /// candidate whose ORIGINAL text legitimately ends with a real newline
    /// (matchKey legitimately ends with MultilineBreakMarker after
    /// flattening -- confirmed 315 such candidates exist in real load-order
    /// data) must still match via the normal exact-match path when the model
    /// echoes it back correctly, marker included. The fallback dictionary
    /// must never interfere with this case.</summary>
    [Fact]
    public void RunOne_LlmBatch_CandidateLegitimatelyEndsWithMarker_MatchesExactlyRegardlessOfFallback()
    {
        const string plugin = "SjptsMarkerFallback.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root);
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Legit Trailing Newline<SJPTS_BR>", "正当な訳文<SJPTS_BR>"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsMarkerFallback");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            var (japanese, method) = translations["Sjpts Legit Trailing Newline\n"];
            Assert.Equal("SJPTS_TranslationLocalLlm", method);
            Assert.Equal("正当な訳文\n", japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.59.0: real-machine investigation (gemma4:26b, Cloaks.esp)
    /// found that a model sometimes wraps its JAPANESE answer in
    /// &lt;SJPTS_TARGET&gt;...&lt;/SJPTS_TARGET&gt; — the same tag this project
    /// uses to delimit the SOURCE text it sent, presumably over-generalizing
    /// the prompt's own "Target: &lt;SJPTS_TARGET&gt;example text
    /// &lt;/SJPTS_TARGET&gt;" example as "wrap your answer in this format too".
    /// NormalizeBatchResponseSource already stripped this from the echoed
    /// source column, but nothing stripped it from the Japanese answer
    /// column, so the tags ended up saved verbatim in translations.tsv.</summary>
    [Fact]
    public void RunOne_LlmBatch_ModelWrapsJapaneseAnswerInTargetTags_TagsAreStripped()
    {
        const string plugin = "SjptsMarkerFallback.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root);
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Target Tag Wrapped Answer", "<SJPTS_TARGET>タグ付き訳</SJPTS_TARGET>"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsMarkerFallback");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            var (japanese, method) = translations["Sjpts Target Tag Wrapped Answer"];
            Assert.Equal("SJPTS_TranslationLocalLlm", method);
            Assert.Equal("タグ付き訳", japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-16: the 2026-09-16 round/batch -&gt; pass redesign moved
    /// the circuit-breaker check inside the pass's inner (per-call) loop,
    /// needing a "circuitOpened" flag to break BOTH that loop and the outer
    /// pass loop. The only pre-existing circuit-breaker coverage tests a
    /// translator that's ALREADY open before the very first call — this
    /// exercises the breaker tripping PARTWAY THROUGH a pass (mirrors a real
    /// backend's connection dying mid-run, e.g. Ollama crashing), which is
    /// the actual new code path added by this redesign.</summary>
    [Fact]
    public void RunOne_CircuitBreakerOpensMidPass_StopsWithoutSendingRemainingCandidates()
    {
        const string plugin = "SjptsResolutionMethods.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root);
            SeedModGlossary(plugin, "Vrenn", "ヴレン");
            var outputDir = Path.Combine(root, "out_temp");
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Multiline Candidate<SJPTS_BR>Second Line", "マルチライン訳"),
                ("Sjpts Batch Candidate One", "バッチ候補一"),
                ("Sjpts Batch Candidate Two", "バッチ候補二"));
            fakeLlm.TripCircuitOpenAfterCall = 2;

            using (var log = OpenTestLog(root))
            {
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, llmLocalBatchCharLimit: 10);

                // Circuit opens right after the 2nd call — a 3rd call must
                // never be made, even though a 3rd distinct candidate is
                // still unresolved and waiting in this same pass.
                Assert.Equal(2, fakeLlm.CallCount);

                // 2026-09-16: without the "circuitOpened" flag breaking BOTH
                // the inner (per-call) and outer (pass) loops, this pass would
                // still stop at 2 calls (the following pass would see zero
                // progress and stop on its own) — but the circuit-breaker
                // check would fire AGAIN at the top of that next, otherwise-
                // empty pass, logging this message a second time for no
                // reason. Asserting exactly 1 occurrence is what actually
                // distinguishes the flag being present from being removed
                // (confirmed by temporarily deleting it: CallCount stayed 2,
                // but this count became 2 as well).
                Assert.Equal(1, log.DetailCount(
                    "5.ローカルLLMのサーキットブレーカー作動（残りをまとめてスキップ）",
                    "5. local LLM circuit breaker open (remaining candidates skipped)"));
            }

            var pluginDir = Path.Combine(outputDir, "SjptsResolutionMethods");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("バッチ候補一", "SJPTS_TranslationLocalLlm"), translations["Sjpts Batch Candidate One"]);
            // The 3rd candidate (last in file order) is the one skipped —
            // never sent, so it stays unresolved rather than getting the
            // canned answer the fake would otherwise have given it.
            Assert.Equal(("", ""), translations["Sjpts Batch Candidate Two"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>ApplyLlmStep splits a plugin's unresolved set into multiple
    /// sub-batch calls once the combined block text would exceed
    /// llmBatchCharLimit — real batches split by actual char volume on real
    /// data, but any single non-trivial candidate block already exceeds a
    /// tiny limit, so setting the limit far below one candidate's own block
    /// size forces a fresh batch per candidate deterministically.</summary>
    [Fact]
    public void RunOne_LlmBatch_SplitsIntoMultipleBatchesWhenOverCharLimit()
    {
        const string plugin = "SjptsResolutionMethods.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root);
            SeedModGlossary(plugin, "Vrenn", "ヴレン");
            var outputDir = Path.Combine(root, "out_temp");
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Multiline Candidate<SJPTS_BR>Second Line", "マルチライン訳"),
                ("Sjpts Batch Candidate One", "バッチ候補一"),
                ("Sjpts Batch Candidate Two", "バッチ候補二"));

            using (var log = OpenTestLog(root))
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, llmLocalBatchCharLimit: 10);

            // 3 distinct unresolved texts reach step 5 here (multiline + the 2
            // batch candidates) -- with a 10-char limit each forces its own
            // batch, so the fake must have been called 3 times.
            Assert.Equal(3, fakeLlm.CallCount);

            var pluginDir = Path.Combine(outputDir, "SjptsResolutionMethods");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("バッチ候補一", "SJPTS_TranslationLocalLlm"), translations["Sjpts Batch Candidate One"]);
            Assert.Equal(("バッチ候補二", "SJPTS_TranslationLocalLlm"), translations["Sjpts Batch Candidate Two"]);

            // 2026-09-16: ラウンド/バッチの2階層をやめ、LLM呼び出し1回=1
            // イテレーションのフラットなループにしたため、「合計何回のバッチ
            // 呼び出しに分割するか」を事前にまとめて報告するログは無くなり、
            // 呼び出しのたびに「未解決N件のうちM件を送信」と報告するように
            // なった。3回目の呼び出しが実際に行われたことを確認する。
            var logText = File.ReadAllText(Path.Combine(root, "Translation", "translation.log"));
            Assert.Contains("呼び出し3", logText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.50.1a's multi-plugin batch mode — RunMany processes only
    /// the REQUESTED subset of plugins (not the whole load order like RunAll,
    /// not a single plugin like RunOne), reports plugins with 0 candidates,
    /// and supports the GUI's cancel-after-current-plugin flag.</summary>
    [Fact]
    public void RunMany_ProcessesOnlyRequestedPluginSubset_ReportsMissingPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);

            var (_, stdout) = ConsoleCapture.Run(() =>
            {
                PromptGenerator.RunMany(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root),
                    ["SjptsMultiPluginB.esp", "SjptsNoSuchPlugin.esp"], outputDir, log);
                return 0;
            });

            Assert.True(Directory.Exists(Path.Combine(outputDir, "SjptsMultiPluginB")));
            // Not requested -- RunMany must never process a plugin outside the subset.
            Assert.False(Directory.Exists(Path.Combine(outputDir, "SjptsMultiPluginA")));
            Assert.Contains("No candidates found for 'SjptsNoSuchPlugin.esp'", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void RunMany_CancelFlag_StopsAfterCurrentPlugin_LeavesRestUnprocessed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            var cancelFlagPath = Path.Combine(root, "cancel.flag");
            File.WriteAllText(cancelFlagPath, ""); // already present BEFORE the run starts
            using var log = OpenTestLog(root);

            // candidates.tsv lists SjptsMultiPluginA.esp's rows before
            // SjptsMultiPluginB.esp's -- RunMany preserves first-seen order, so A
            // is the first (and, since the flag is already set, ONLY) plugin
            // processed.
            var (_, stdout) = ConsoleCapture.Run(() =>
            {
                PromptGenerator.RunMany(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root),
                    ["SjptsMultiPluginA.esp", "SjptsMultiPluginB.esp"], outputDir, log, cancelFlagPath: cancelFlagPath);
                return 0;
            });

            Assert.True(Directory.Exists(Path.Combine(outputDir, "SjptsMultiPluginA")));
            Assert.False(Directory.Exists(Path.Combine(outputDir, "SjptsMultiPluginB")));
            Assert.Contains("Cancelled by user after [SjptsMultiPluginA.esp]", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>RunAll's own orchestration (as opposed to RunOne's per-plugin
    /// write path, which WritePluginFilesWithDir tests above already cover):
    /// every plugin in the whole candidates.tsv is grouped and processed, in
    /// DESCENDING candidate-count order, and the three load-order-wide
    /// summary files (translation_index.txt/auto_resolve_by_plugin.tsv/
    /// plugin_summary.txt) get written -- files RunOne/RunMany deliberately
    /// never touch (see RunMany's own remarks on why).</summary>
    [Fact]
    public void RunAll_GroupsPluginsByDescendingCandidateCount_WritesLoadOrderWideSummaryFiles()
    {
        const string resolutionPlugin = "SjptsResolutionMethods.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cwd = new CurrentDirectoryScope(root);
            SeedModGlossary(resolutionPlugin, "Vrenn", "ヴレン");
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);

            PromptGenerator.RunAll(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), outputDir, log);

            // Every plugin in the fixture got its own folder.
            foreach (var dir in new[] { "SjptsTestMod", "SjptsResolutionMethods", "SjptsMultiPluginA", "SjptsMultiPluginB" })
                Assert.True(Directory.Exists(Path.Combine(outputDir, dir)), $"expected {dir} to have been processed");

            var indexPath = Path.Combine(outputDir, "translation_index.txt");
            Assert.True(File.Exists(indexPath));
            var indexText = File.ReadAllText(indexPath);
            // SjptsResolutionMethods.esp (6 candidates) must be listed before
            // SjptsTestMod.esp (4), which must be listed before
            // SjptsMultiPluginA.esp (2), before SjptsMultiPluginB.esp (1) --
            // descending candidate count, ties aside (there are none here).
            var resolutionIndex = indexText.IndexOf("SjptsResolutionMethods.esp", StringComparison.Ordinal);
            var testModIndex = indexText.IndexOf("SjptsTestMod.esp", StringComparison.Ordinal);
            var multiAIndex = indexText.IndexOf("SjptsMultiPluginA.esp", StringComparison.Ordinal);
            var multiBIndex = indexText.IndexOf("SjptsMultiPluginB.esp", StringComparison.Ordinal);
            Assert.True(resolutionIndex >= 0 && testModIndex >= 0 && multiAIndex >= 0 && multiBIndex >= 0);
            Assert.True(resolutionIndex < testModIndex);
            Assert.True(testModIndex < multiAIndex);
            Assert.True(multiAIndex < multiBIndex);

            Assert.True(File.Exists(Path.Combine(outputDir, "auto_resolve_by_plugin.tsv")));
            Assert.True(File.Exists(Path.Combine(outputDir, "plugin_summary.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>BuildCandidateBlock's optional prompt.txt lines — all only
    /// rendered for a candidate carrying the specific data that triggers
    /// them, so none of the resolution-focused tests above ever exercised
    /// them. One deliberately busy fixture (000840/000841/000842 in
    /// candidates.tsv) carries every trigger at once: two rows sharing the
    /// same English text (duplicate-occurrence line), one of them with
    /// stale-original/-translation fields (v0.8.0's --include-stale carry),
    /// a Context string (v0.6.0's per-record Mutagen context), a word
    /// ("Windrose") this fixture's corpus already taught as a meaning
    /// modifier (WordGlossary hint), a word ("Rex") that is also this load
    /// order's own NPC_ FULL display name (v0.48.1's name hint), and a word
    /// ("Corvid") that also appears in the plugin's own filename (v0.48.1's
    /// brand hint).</summary>
    /// <summary>v0.58.4: reproduces a real bug found against Cloaks_SMP_Patch.esp
    /// (ARMO DESC flavor text often ships with a trailing space in the source
    /// game data) — ApplyLlmStep's response parser (NormalizeBatchResponseSource)
    /// always Trim()s the model's echoed English column before looking it up,
    /// but the matchKey it looked the answer up BY was the raw, un-Trim()med
    /// candidate text. A model naturally drops meaningless trailing whitespace
    /// when it echoes the source back, so this candidate could NEVER match —
    /// deterministically, on every single run, regardless of model quality —
    /// until matchKey was also Trim()med at comparison time.</summary>
    [Fact]
    public void RunOne_LlmBatch_CandidateWithTrailingWhitespace_MatchesTrimmedEcho()
    {
        const string plugin = "SjptsMatchingEdgeCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            // Simulates a real model's response: it echoes the source WITHOUT the
            // candidate's own trailing space (models don't preserve meaningless
            // trailing whitespace), which is exactly the mismatch that used to
            // leave this candidate unresolved forever.
            var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Trailing Space Candidate", "末尾空白の訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsMatchingEdgeCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            // The candidate's own key (with its trailing space, as it appears in
            // the game data) must resolve -- the trailing space itself is NOT
            // expected to be preserved in the Japanese translation (steps ①-④
            // don't preserve it either; this is pre-existing, unrelated behavior).
            Assert.Equal(("末尾空白の訳", "SJPTS_TranslationLocalLlm"), translations["Sjpts Trailing Space Candidate "]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-17: the corpus fixture has 3 rows all sharing the exact
    /// same (English, Japanese) pair ("Sjpts Duplicate Reference Item" →
    /// "デュープの実例"), differing only by Source/SourceKind — mirrors a real
    /// pattern found in production data (the same vanilla item name/translation
    /// recorded once per DSD/cross-mod/import occurrence). PrecedentRetriever
    /// has no dedup of its own, so all 3 used to show up as 3 separate
    /// "Reference examples" lines, wasting budget that should go to genuinely
    /// different examples instead.</summary>
    [Fact]
    public void RunOne_CorpusHasDuplicateReferenceExamplePairs_ShownOnlyOnceInPrompt()
    {
        const string plugin = "SjptsReferenceBudget.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Duplicate Reference Candidate", "重複参照候補の訳"),
                ("Sjpts Many References Candidate", "多数参照候補の訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsReferenceBudget");
            var promptPath = Path.Combine(pluginDir, "prompt_localLLM_call1.txt");
            Assert.True(File.Exists(promptPath), $"expected {promptPath} to exist");
            var promptText = File.ReadAllText(promptPath);

            var occurrences = promptText.Split("\"Sjpts Duplicate Reference Item\" → \"デュープの実例\"").Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-17: real-data finding (Light Greatswords.esp) — a
    /// candidate can legitimately have many more "relevant" corpus precedents
    /// than any single prompt can afford to show, and PrecedentRetriever itself
    /// applies no count limit (only a per-entry 500-char cap and a 0.4
    /// relative-score cutoff). Local LLM gets the more generous of the two
    /// limits (topN=15 or 15% of batchCharLimit, whichever binds first) since
    /// it's cheap to run and time is not critical; cloud LLM gets the tighter
    /// pair (topN=10 or 10%) since it's already more accurate and each call has
    /// a real dollar cost. topN=15 (not 20, tried first) — a real-machine
    /// verification (gemma4:26b) found topN=20's extra reference-example volume
    /// pushed some prompts' total size (fixed instruction + issue #4's "c" +
    /// candidates, none of which batchCharLimit's own packing accounts for)
    /// past the empirically-established ~9,659-char safe line, so it was
    /// dialed back. The corpus fixture has 25 distinct, equally-similar
    /// precedents for this candidate — enough to make topN the binding
    /// constraint at a generous batchCharLimit override (so the char-ratio
    /// budget alone wouldn't have been the bottleneck).</summary>
    [Fact]
    public void RunOne_ManyEquallyRelevantPrecedents_LocalLlmKeepsAtMostTopNFifteen()
    {
        const string plugin = "SjptsReferenceBudget.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Duplicate Reference Candidate", "重複参照候補の訳"),
                ("Sjpts Many References Candidate", "多数参照候補の訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log,
                llmLocal: fakeLlm, llmLocalBatchCharLimit: 20_000);

            var pluginDir = Path.Combine(outputDir, "SjptsReferenceBudget");
            var promptPath = Path.Combine(pluginDir, "prompt_localLLM_call1.txt");
            Assert.True(File.Exists(promptPath), $"expected {promptPath} to exist");
            var promptText = File.ReadAllText(promptPath);

            var occurrences = System.Text.RegularExpressions.Regex.Matches(promptText, "Sjpts Many References Item \\d\\d").Count;
            Assert.Equal(15, occurrences);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Same setup as the local-LLM topN test above, but routed through
    /// step ⑥ (cloud LLM, llmLocal left null so step ⑤ is skipped entirely) —
    /// cloud's tighter limit (topN=10) must apply instead of local's (20).</summary>
    [Fact]
    public void RunOne_ManyEquallyRelevantPrecedents_CloudLlmKeepsAtMostTopNTen()
    {
        const string plugin = "SjptsReferenceBudget.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Duplicate Reference Candidate", "重複参照候補の訳"),
                ("Sjpts Many References Candidate", "多数参照候補の訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log,
                llmCloud: fakeLlm, llmCloudBatchCharLimit: 20_000);

            var pluginDir = Path.Combine(outputDir, "SjptsReferenceBudget");
            var promptPath = Path.Combine(pluginDir, "prompt_cloudLLM_call1.txt");
            Assert.True(File.Exists(promptPath), $"expected {promptPath} to exist");
            var promptText = File.ReadAllText(promptPath);

            var occurrences = System.Text.RegularExpressions.Regex.Matches(promptText, "Sjpts Many References Item \\d\\d").Count;
            Assert.Equal(10, occurrences);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>2026-09-17: ModPhraseGlossary's mod_glossary.tsv (⑤/⑥-facing,
    /// distinct from ④-only Data/mod_glossary/*.tsv) should be generated
    /// alongside translations.tsv for a MOD whose candidates share a
    /// distinctively recurring phrase ("Windrune Blade", 3 occurrences, not
    /// resolvable via ①②③ — a fictional term unrelated to anything in the
    /// corpus fixture).</summary>
    [Fact]
    public void RunOne_ModHasRecurringUnresolvedPhrase_WritesModGlossaryTemplate()
    {
        const string plugin = "SjptsModPhraseGlossary.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Daedric Windrune Blade", "訳1"), ("Sjpts Dwarven Windrune Blade", "訳2"), ("Sjpts Ebony Windrune Blade", "訳3"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsModPhraseGlossary");
            var glossaryPath = ModPhraseGlossary.PathFor(pluginDir);
            Assert.True(File.Exists(glossaryPath), $"expected {glossaryPath} to exist");
            var lines = File.ReadAllLines(glossaryPath).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.Contains(lines, l => l.StartsWith("Windrune Blade\t"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Once a person fills in mod_glossary.tsv's Japanese column, that
    /// row must reach the LLM prompt as a same-mod hint (issue #4's "c") for
    /// this mod's OTHER unresolved candidates — the whole point of this
    /// mechanism (a person pre-deciding one occurrence's translation so the
    /// rest of the mod's candidates stay consistent with it).</summary>
    [Fact]
    public void RunOne_FilledModGlossaryEntry_AppearsAsSameModHintInPrompt()
    {
        const string plugin = "SjptsModPhraseGlossary.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            var pluginDir = Path.Combine(outputDir, "SjptsModPhraseGlossary");
            Directory.CreateDirectory(pluginDir);
            // A person has already filled this in by hand before this run.
            File.WriteAllLines(ModPhraseGlossary.PathFor(pluginDir), ["# comment", "Windrune Blade\t風紋の刃"]);

            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Daedric Windrune Blade", "デイドラの風紋の刃"), ("Sjpts Dwarven Windrune Blade", "ドワーフの風紋の刃"), ("Sjpts Ebony Windrune Blade", "黒檀の風紋の刃"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var promptPath = Path.Combine(pluginDir, "prompt_localLLM_call1.txt");
            Assert.True(File.Exists(promptPath), $"expected {promptPath} to exist");
            var promptText = File.ReadAllText(promptPath);
            Assert.Contains("Windrune Blade", promptText);
            Assert.Contains("風紋の刃", promptText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.58.5: replaces the old boundary-quote-marker test suite
    /// (v0.58.4's DoubleQuoteMarker/MarkBoundaryQuotes/StripOuterQuoteIndependently,
    /// removed) now that BuildCandidateBlock wraps Target text in
    /// &lt;SJPTS_TARGET&gt;...&lt;/SJPTS_TARGET&gt; tags instead of quotes — a
    /// candidate whose own text starts and/or ends with a literal " (e.g.
    /// dialogue like <c>"Do you take me for a fool?" she snapped.</c>) no
    /// longer needs any special handling at all: its quotes are never
    /// confused with the delimiter, so the model just echoes them back
    /// unchanged as part of the matching key, exactly like any other
    /// character. Confirmed against real gemma4 output (8/8 test cases
    /// including this exact shape) before implementing.</summary>
    [Fact]
    public void RunOne_LlmBatch_CandidateWithBoundaryQuotes_ResolvesDirectly_NoSpecialHandlingNeeded()
    {
        const string plugin = "SjptsMatchingEdgeCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            // The model echoes each candidate's own quotes back completely
            // unchanged -- no marker, no tag, nothing but the literal text.
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("\"Sjpts Quoted Both Sides\"", "「両側引用の訳」"),
                ("\"Sjpts Leading Quote Only", "「先頭のみ引用の訳"),
                ("Sjpts Trailing Quote Only\"", "末尾のみ引用の訳」"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsMatchingEdgeCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("「両側引用の訳」", "SJPTS_TranslationLocalLlm"), translations["\"Sjpts Quoted Both Sides\""]);
            Assert.Equal(("「先頭のみ引用の訳", "SJPTS_TranslationLocalLlm"), translations["\"Sjpts Leading Quote Only"]);
            Assert.Equal(("末尾のみ引用の訳」", "SJPTS_TranslationLocalLlm"), translations["Sjpts Trailing Quote Only\""]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.58.5: StripSurroundingQuotes, restored to its original
    /// simple symmetric form now that the boundary-quote-marker system is
    /// gone, still handles a model that wraps its JAPANESE answer field in
    /// quotes as an unrelated formatting habit (confirmed against real
    /// Claude Code CLI output, independent of this project's own
    /// &lt;SJPTS_TARGET&gt; delimiter choice). Deliberately NOT applied to the
    /// English matching key any more (see NormalizeBatchResponseSource's
    /// remarks) — the source column here is left unquoted by the fake so
    /// this test isolates the Japanese-column behavior specifically.</summary>
    [Fact]
    public void RunOne_LlmBatch_ModelWrapsJapaneseAnswerInQuotes_IsStripped()
    {
        const string plugin = "SjptsMatchingEdgeCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("Sjpts Plain No Quote Candidate", "\"引用符無し候補の訳\""));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsMatchingEdgeCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("引用符無し候補の訳", "SJPTS_TranslationLocalLlm"), translations["Sjpts Plain No Quote Candidate"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // 2026-09-12: RunOne_LlmBatch_ModelEchoesWrapperTagsBackDespiteInstructions_StillMatches
    // (v0.58.5, defensive coverage for a model that echoes the wrapper tags
    // back "despite instructions") was removed here — its premise no longer
    // applies now that the prompt REQUIRES the model to echo the tags (see
    // the new tag-based matching tests above), and its scenario is now just
    // the default/expected case covered by every other Succeeding()-based
    // test, plus more thoroughly by the new noise/malformed-tag tests above.

    // ==== 2026-09-12 TDD: tag-based response matching (real-data bug, HeelsFix
    // "$HEELSFIX_TARGET_ACTOR" = "Target:") — written BEFORE the fix, per the
    // agreed TDD approach. See design/interface_translations.md and this
    // session's discussion for the full analysis: NormalizeBatchResponseSource's
    // unconditional "Target:"/"- " prefix strip corrupts a candidate whose own
    // text collides with that literal prefix when the response isn't
    // tag-wrapped, AND (independently) currently tolerates malformed/partial
    // tag pairs and arbitrary noise around a tag that the new, stricter design
    // must reject. Some of these are expected to FAIL against the current
    // implementation (see each test's own remarks); others are regression
    // safety nets for the refactor and should already pass. ====

    /// <summary>Regression-safety net for the refactor (candidate text is
    /// literally the word the old heuristic tried to strip) — a tag-wrapped
    /// response already resolves this correctly even under the OLD
    /// implementation (the old "Target:" strip never fires because the text
    /// starts with the tag character "&lt;", not literally "Target:"); the
    /// actual bug is specific to an UNTAGGED response, covered by the sibling
    /// tests below. Kept here to make sure the new, simplified extraction
    /// logic doesn't regress this exact real-world case.</summary>
    [Fact]
    public void RunOne_LlmBatch_CandidateTextIsLiterallyTargetColon_TaggedResponseResolves()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(("Target:", "対象"));
            // ①〜④の自動解決（特に④NameFallbackTranslator——"Target:"は一般的な
            // 単語のため、これ単体で辞書的に解決されてしまいLLM経路に到達しない）
            // を無効化し、確実に⑤ローカルLLM経路でこのテストのフェイク応答が
            // 使われるようにする。
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("対象", "SJPTS_TranslationLocalLlm"), translations["Target:"]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. An
    /// untagged response currently resolves just fine (tags are optional
    /// today) — this is the actual real-data bug's exact shape (HeelsFix's
    /// model response for "Target:" had no tags, since the current prompt
    /// tells the model to omit them, and the old "Target:" strip then
    /// destroyed the real content). Under the new design (prompt requires the
    /// tags; parsing requires them too), an untagged response must instead be
    /// rejected as unparseable.</summary>
    [Fact]
    public void RunOne_LlmBatch_ResponseWithoutTags_IsRejectedAsUnparseable()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("Sjpts Format Edge Case Candidate\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("", ""), translations["Sjpts Format Edge Case Candidate"]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. Text
    /// before the tag (e.g. the model echoing the prompt's own "Target: "
    /// label) is currently silently stripped and still matches — the new
    /// design must instead reject anything other than whitespace outside the
    /// tag pair, so a non-compliant response fails cleanly (and gets retried
    /// next run) rather than being guessed at.</summary>
    [Fact]
    public void RunOne_LlmBatch_ResponseWithNoiseBeforeTag_IsRejectedAsUnparseable()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("Target: <SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("", ""), translations["Sjpts Format Edge Case Candidate"]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. A missing
    /// opening tag (only the closing tag present) currently still resolves,
    /// because the old logic strips a trailing close tag independently of
    /// whether a matching opening tag was ever seen. The new design requires
    /// both tags to be present as a matched pair.</summary>
    [Fact]
    public void RunOne_LlmBatch_ResponseMissingOpeningTag_IsRejectedAsUnparseable()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("", ""), translations["Sjpts Format Edge Case Candidate"]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. Symmetric
    /// to the missing-opening-tag case above — a missing closing tag (only the
    /// opening tag present) currently still resolves for the same reason
    /// (independent prefix/suffix strips instead of a matched pair).</summary>
    [Fact]
    public void RunOne_LlmBatch_ResponseMissingClosingTag_IsRejectedAsUnparseable()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("<SJPTS_TARGET>Sjpts Format Edge Case Candidate\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("", ""), translations["Sjpts Format Edge Case Candidate"]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>Regression-safety net for the refactor — whitespace
    /// immediately around an otherwise well-formed tag pair must still be
    /// tolerated (Trim()-level only, not arbitrary text around it). Already
    /// passes today; kept to make sure the simplified extraction logic
    /// doesn't accidentally start requiring an exact match with zero
    /// tolerance.</summary>
    [Fact]
    public void RunOne_LlmBatch_WhitespaceAroundOtherwiseWellFormedTag_StillResolves()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("  <SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>  \t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("訳文", "SJPTS_TranslationLocalLlm"), translations["Sjpts Format Edge Case Candidate"]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>
    /// 2026-09-13 (issue #2): a batch whose response was cut off by an
    /// output-token limit (finish_reason == "length") must not force the user
    /// to manually re-run the whole translation just to pick up the leftover
    /// candidates. ApplyLlmStep now automatically re-batches and re-requests
    /// ONLY the still-unresolved candidates for another round, as long as the
    /// previous round both truncated AND made progress (no fixed round cap —
    /// an explicit user requirement: an arbitrary "3 rounds" was rejected as
    /// unjustified in favor of this self-terminating condition). This test
    /// simulates round 1 resolving 2 of 3 candidates from this plugin's fixed
    /// candidate set (leaving the trailing-whitespace one, deliberately
    /// omitted from round 1's response) while truncated, and round 2 (the
    /// automatic retry) resolving the remainder from a clean, non-truncated
    /// response — end to end, all 3 must end up translated from exactly 2
    /// TryTranslate calls.
    /// </summary>
    [Fact]
    public void RunOne_LlmBatch_RoundTruncatedWithProgress_AutomaticallyRetriesRemainderNextRound()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("placeholder — replaced by the queued responses below");
            // Round 1: resolves "Target:" and "Sjpts Format Edge Case
            // Candidate", but is silent on "Sjpts Trailing Whitespace
            // Candidate " (as if the response were cut off before reaching
            // it) — and reports truncation, matching a real finish_reason ==
            // "length" response.
            fakeLlm.EnqueueRaw(
                "<SJPTS_TARGET>Target:</SJPTS_TARGET>\tターゲット\n" +
                "<SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文",
                truncated: true);
            // Round 2 (the automatic retry, requesting only the remaining
            // unresolved candidate): a clean, complete, non-truncated
            // response.
            fakeLlm.EnqueueRaw(
                "<SJPTS_TARGET>Sjpts Trailing Whitespace Candidate </SJPTS_TARGET>\t末尾空白の訳文",
                truncated: false);
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            Assert.Equal(2, fakeLlm.CallCount);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("ターゲット", "SJPTS_TranslationLocalLlm"), translations["Target:"]);
            Assert.Equal(("訳文", "SJPTS_TranslationLocalLlm"), translations["Sjpts Format Edge Case Candidate"]);
            Assert.Equal(("末尾空白の訳文", "SJPTS_TranslationLocalLlm"), translations["Sjpts Trailing Whitespace Candidate "]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>
    /// 2026-09-13: real-data investigation — a batch response cut off by an
    /// output-token limit (confirmed via real gemma4:26b/Ollama,
    /// finish_reason == "length") ends mid-tag, silently dropping every
    /// candidate after the cutoff. Previously the only trace was eyeballing
    /// the raw response dump in trace.log for a shape that "looks cut off".
    /// When the translator reports LastResponseTruncated, ApplyLlmStep must
    /// log a specific, named reason instead of leaving that inference to a
    /// human reading raw text.
    /// </summary>
    [Fact]
    public void RunOne_LlmBatch_TranslatorReportsResponseTruncated_LogsSpecificReason()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            // "Target:" candidate never answered — as if the response were cut off before it.
            var fakeLlm = FakeTextTranslator.SucceedingRawTruncated("<SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            // 2026-09-13: 打ち切り(finish_reason=length)かつ当該ラウンドで
            // 1件以上進捗があった場合、未解決分だけを対象に自動で再送する
            // ようになった（issue #2）。このフェイクは常に同じ打ち切り応答を
            // 返すため、1件目が解決した1ラウンド目の後、まだ残っている他の
            // 候補を対象にした2ラウンド目が自動発生し、それも打ち切り扱いに
            // なる（が今度は進捗0のためそこで止まる）——よって打ち切りログは
            // 2回出るのが正しい。
            Assert.Equal(2, log.DetailCount(
                "5.ローカルLLM: モデルの応答が出力トークン数の上限で打ち切られた可能性があります",
                "5. local LLM: the model's response may have been cut off by an output token limit"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>
    /// issue #4 (c: same-mod hints), 2026-09-16 — the actual bug this exists
    /// to fix: within a single run, a re-batch after a truncated round must
    /// see what THIS SAME session already translated for THIS SAME mod, so a
    /// shared word doesn't drift to a different rendering between rounds.
    /// Round 1's batch has all 3 of this fixture's candidates; the fake
    /// always resolves only "Sjpts Format Edge Case Candidate" and reports
    /// truncation, so round 2 retries the other two ("Target:" and "Sjpts
    /// Trailing Whitespace Candidate ") — the latter shares "Sjpts" and
    /// "Candidate" with what round 1 just resolved, so round 2's prompt (the
    /// LAST call made, since round 2 makes zero further progress and the
    /// retry loop then stops) must carry a "Same-mod translations so far"
    /// block naming it. Round 1's own prompt must NOT have one — nothing was
    /// resolved yet this session when it was built.</summary>
    [Fact]
    public void RunOne_RetryRound_IncludesSameModHintFromEarlierRoundsOwnResult()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRawTruncated("<SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            // 2026-09-16: ラウンド/バッチの2階層をやめたため、ファイル名は
            // 呼び出し番号のみ（"call1.txt"/"call2.txt"、"_of_総数"や
            // "_round番号"は付かない）。
            var round1Prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt_localLLM_call1.txt"));
            var round2Prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt_localLLM_call2.txt"));

            Assert.DoesNotContain("Same-mod translations so far", round1Prompt);
            Assert.Contains("Same-mod translations so far", round2Prompt);
            // 2026-09-16: legend+numeric code format (validated design) —
            // step 5's local LLM resolution tags with "SJPTS_TranslationLocalLlm",
            // SameModTrustTier priority 7.
            Assert.Contains("7=translated by a local LLM", round2Prompt);
            Assert.Contains("\"Sjpts Format Edge Case Candidate\"\t\"訳文\"\t7", round2Prompt);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>
    /// 2026-09-13: real bug — `batchLabel` ("バッチ1/2"/"バッチ", built once
    /// per batch) is Japanese, but it was being embedded verbatim into
    /// English-only strings (trace.Warning and the DetailAndReport consoleText
    /// param, both meant to be English regardless of RunLog's own ja/en
    /// setting per RunLog.cs's own doc comment on Report/DetailAndReport).
    /// This test forces English-language RunLog output (SKYRIMJPSP_LOG_LANG=en)
    /// for a truncated-batch scenario and asserts the resulting log file
    /// contains no Japanese characters at all.
    /// </summary>
    [Fact]
    public void RunOne_LlmBatch_EnglishLogLanguage_TruncatedBatch_LogTextHasNoJapanese()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var originalLangEnv = Environment.GetEnvironmentVariable("SKYRIMJPSP_LOG_LANG");
        try
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LANG", "en");
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRawTruncated("<SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);
            log.Dispose();

            var logText = File.ReadAllText(Path.Combine(root, "Translation", "translation.log"));
            Assert.DoesNotContain("バッチ", logText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LANG", originalLangEnv);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// 2026-09-13: real bug, the reverse direction — the DetailAndReport
    /// consoleText param (meant to be English-only per RunLog.cs's own doc
    /// comment, printed to console/log-window unconditionally regardless of
    /// RunLogLang) embedded `{stepLabelEn}` ("local LLM") into an otherwise
    /// Japanese sentence, for the "no Japanese in response" and "unparseable
    /// response" cases. Captures Console.Out during a "no Japanese in
    /// response" run and asserts it contains no Japanese characters.
    /// </summary>
    [Fact]
    public void RunOne_LlmBatch_NoJapaneseInResponse_ConsoleOutputIsEnglishOnly()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        try
        {
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Format Edge Case Candidate", "Sjpts Format Edge Case Candidate")); // echoes English back -> no Japanese
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            Console.SetOut(capturedOut);
            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir: Path.Combine(root, "out_temp"), log, llmLocal: fakeLlm, stageOptions: stages);
        }
        finally
        {
            Console.SetOut(originalOut);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }

        var consoleText = capturedOut.ToString();
        Assert.Contains("no Japanese", consoleText);
        Assert.DoesNotContain('応', consoleText); // "応答"/"要レビュー" etc. — none of the surrounding sentence should be Japanese
    }

    /// <summary>
    /// 2026-09-13: real-data bug — a candidate whose English text has
    /// meaningful trailing whitespace ("Sjpts Trailing Whitespace Candidate ",
    /// mirroring FloatingSubtitles' real "$FSUB_DualSubtitlesOffscreen_Text"
    /// = "DUAL SUBTITLES ") is embedded VERBATIM into the prompt's tag, and
    /// the prompt instructs the model to copy it "unchanged". A model that
    /// does exactly that must still resolve — matching on the untrimmed
    /// response must not be broken by trimming only the candidate side.
    /// </summary>
    [Fact]
    public void RunOne_LlmBatch_CandidateHasTrailingWhitespace_ModelEchoesExactly_StillResolves()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("<SJPTS_TARGET>Sjpts Trailing Whitespace Candidate </SJPTS_TARGET>\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("訳文", "SJPTS_TranslationLocalLlm"), translations["Sjpts Trailing Whitespace Candidate "]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// <summary>The flexible/fallback side of the same fix: a model that
    /// (despite the "copy unchanged" instruction) trims the candidate's own
    /// trailing whitespace when echoing it back must still resolve.</summary>
    [Fact]
    public void RunOne_LlmBatch_CandidateHasTrailingWhitespace_ModelTrimsItWhenEchoing_StillResolvesViaFallback()
    {
        const string plugin = "SjptsTargetTagCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.SucceedingRaw("<SJPTS_TARGET>Sjpts Trailing Whitespace Candidate</SJPTS_TARGET>\t訳文");
            var stages = new TranslationStageOptions(EnableMeaning: false, EnableTransliteration: false, EnableNameFallback: false);

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm, stageOptions: stages);

            var pluginDir = Path.Combine(outputDir, "SjptsTargetTagCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("訳文", "SJPTS_TranslationLocalLlm"), translations["Sjpts Trailing Whitespace Candidate "]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public void RunOne_UnresolvedCandidateWithEveryPromptHint_WritesAllOptionalPromptLines()
    {
        const string plugin = "Sjpts Corvid Outfit.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using (var log = OpenTestLog(root))
                PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log);

            var pluginDir = Path.Combine(outputDir, "Sjpts Corvid Outfit");
            var prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt.txt"));

            Assert.Contains("Context: light armor, slot: body", prompt);
            Assert.Contains("(This string appears 2 times in this plugin", prompt);
            Assert.Contains("Existing translation (for the original text before it changed", prompt);
            Assert.Contains("以前の訳", prompt);
            Assert.Contains("Old Windrose Rex Corvid Nemra Cloak", prompt);
            Assert.Contains("Known translations for words in this candidate:", prompt);
            Assert.Contains("Windrose=", prompt); // via the meaning table (CorpusMeaningTranslator-mined modifier)
            Assert.Contains("Nemra=", prompt); // via TryExactWord (a standalone single-word corpus entry)
            Assert.Contains("Known character/creature names in this mod's load order", prompt);
            Assert.Contains("Rex", prompt);
            Assert.Contains("also appears in this mod's own filename (Sjpts Corvid Outfit.esp)", prompt);
            Assert.Contains("Corvid", prompt);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ==== 2026-09-12: ClassifyTaggedSourceIssue — diagnostic classification of
    // why a response line's source column failed the tag-match, added after
    // a real-data investigation (HeelsFix.esp, gemma4:26b) hit a wall trying
    // to figure out WHY a candidate didn't resolve — neither translation.log
    // nor translation.trace.log captured enough to tell apart "no tags at
    // all" from "tags present but something else was wrong". 2026-09-16:
    // moved to the shared LlmBatchTranslationEngine (public) as part of the
    // ESP/Interface ApplyLlmStep duplication refactor — no longer needs
    // reflection.

    private static string InvokeClassifyTaggedSourceIssue(string text) =>
        LlmBatchTranslationEngine.ClassifyTaggedSourceIssue(text).ToString();

    [Theory]
    [InlineData("Sjpts Format Edge Case Candidate", "NoTags")]
    [InlineData("Sjpts Format Edge Case Candidate</SJPTS_TARGET>", "MissingOpeningTag")]
    [InlineData("<SJPTS_TARGET>Sjpts Format Edge Case Candidate", "MissingClosingTag")]
    [InlineData("Target: <SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>", "ExtraTextOutsideTags")]
    public void ClassifyTaggedSourceIssue_ReturnsExpectedCategory(string malformedSourceColumn, string expectedCategory)
    {
        Assert.Equal(expectedCategory, InvokeClassifyTaggedSourceIssue(malformedSourceColumn));
    }

    // ==== 2026-09-17: HasMatchingTagStructure — item 11（実データでLLM応答への
    // 内部タグ残存が複数確認された）対応。原文と訳文から<[^>]*>で抽出した
    // タグを、順序を無視した多重集合として比較する。実データ検証（実際の
    // DSD出力21,495件のうちTranslationLocalLlm/TranslationCloudLlm 3,809件）
    // で、この方式が既知の全てのタグ漏れ（境界タグ残存・表記ゆれ）を正しく
    //検出し、かつ正常な翻訳（日本語と英語の語順差によるタグの並び替えを
    // 含む）を誤検出しないことを確認済み。

    [Fact]
    public void HasMatchingTagStructure_NoTagsEitherSide_ReturnsTrue()
    {
        Assert.True(LlmBatchTranslationEngine.HasMatchingTagStructure("Sjpts Gilded Hammer", "黄金のハンマー"));
    }

    [Fact]
    public void HasMatchingTagStructure_LeakedClosingTargetTag_ReturnsFalse()
    {
        Assert.False(LlmBatchTranslationEngine.HasMatchingTagStructure("Sjpts Gilded Hammer", "黄金のハンマー</SJPTS_TARGET>"));
    }

    /// <summary>実データ（Soul Hunter Armor.esp）で見つかった、本来
    /// &lt;SJPTS_BR&gt;であるべきマーカーが&lt;SJP_BR&gt;（"TS"欠落）という
    /// 表記ゆれで応答に混入したケース——固定文字列との完全一致チェックでは
    /// 検出できないが、構造比較なら「原文に存在しないタグが訳文に出現した」
    /// というだけで検出できる。</summary>
    [Fact]
    public void HasMatchingTagStructure_UnknownTypoedMarker_ReturnsFalse()
    {
        Assert.False(LlmBatchTranslationEngine.HasMatchingTagStructure(
            "Line one.\nLine two.", "一行目。<SJP_BR>二行目。"));
    }

    [Fact]
    public void HasMatchingTagStructure_SameTagsPreservedInOrder_ReturnsTrue()
    {
        Assert.True(LlmBatchTranslationEngine.HasMatchingTagStructure(
            "<font face=\"$FalmerFont\">Text</font>", "<font face=\"$FalmerFont\">テキスト</font>"));
    }

    /// <summary>日本語と英語の語順差により、同じタグの集合が異なる順序で
    /// 出現しても正しく受理される必要がある（実データのOrdinator.esp等、
    /// &lt;mag&gt;/&lt;dur&gt;の順序が入れ替わるケースを確認済み）。</summary>
    [Fact]
    public void HasMatchingTagStructure_SameTagsDifferentOrder_ReturnsTrue()
    {
        Assert.True(LlmBatchTranslationEngine.HasMatchingTagStructure(
            "Increases <mag> for <dur> seconds", "<dur>秒間、<mag>増加させる"));
    }

    [Fact]
    public void HasMatchingTagStructure_MissingTagInTranslation_ReturnsFalse()
    {
        Assert.False(LlmBatchTranslationEngine.HasMatchingTagStructure(
            "<p align=\"center\">\n</p>\n<p align=\"left\">Text", "テキスト"));
    }

    [Fact]
    public void HasMatchingTagStructure_DuplicateTagCountMustMatch_ReturnsFalse()
    {
        // 個数が違えば（1個 vs 2個）順序を無視しても不一致——多重集合比較の
        // 「個数まで一致する必要がある」という仕様の確認。
        Assert.False(LlmBatchTranslationEngine.HasMatchingTagStructure("<25> and <25>", "<25>だけ"));
    }
}
