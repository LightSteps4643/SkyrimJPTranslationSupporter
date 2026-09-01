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

            Assert.Equal(("テスト用の剣", "AutoCorpus"), translations["Sjpts Test Sword"]);

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

            Assert.Equal(1, fakeLlm.CallCount);
            var pluginDir = Path.Combine(outputDir, "SjptsTestMod");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("LLMによる訳", "TranslationLocalLlm"), translations["Sjpts Llm Candidate"]);

            var prompt = File.ReadAllText(Path.Combine(pluginDir, "prompt.txt"));
            Assert.DoesNotContain("Target: <SJPTS_TARGET>Sjpts Llm Candidate</SJPTS_TARGET>", prompt);
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
    /// (here "TranslationLocalLlmNoJapanese") instead of either discarding it
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

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            Assert.Equal(1, fakeLlm.CallCount);
            var pluginDir = Path.Combine(outputDir, "SjptsMatchingEdgeCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            // Resolved (not left unresolved -- no point retrying, it'll just
            // reproduce the same answer), but tagged distinctly from a normal
            // TranslationLocalLlm success so it surfaces for human review.
            Assert.Equal(("Sjpts Scrambled Gibberish Candidate", "TranslationLocalLlmNoJapanese"),
                translations["Sjpts Scrambled Gibberish Candidate"]);
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
                "000803:SjptsTestMod.esp\tSjptsTestMod.esp\tWEAP FULL\tSjpts Preserved Candidate\t前回のAI訳\tTranslationCloudLlm\t0\t\n");

            using var log = OpenTestLog(root);
            // If RunOne incorrectly re-ran the LLM for the preserved candidate,
            // this fake would answer with a DIFFERENT translation, exposing the
            // bug immediately via a mismatched assertion below.
            var fakeLlm = FakeTextTranslator.Succeeding(("Sjpts Preserved Candidate", "再実行で上書きされた誤答"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), TargetPlugin, outputDir, log, llmLocal: fakeLlm);

            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));
            Assert.Equal(("前回のAI訳", "TranslationCloudLlm"), translations["Sjpts Preserved Candidate"]);
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
                "000803:SjptsTestMod.esp\tSjptsTestMod.esp\tWEAP FULL\tSjpts Preserved Candidate\t前回の訳\tModifiedByUser\t0\t\n");

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

            Assert.Equal(("きらめきの指輪", "AutoCorpusMeaning"), translations["Glimmeroot Ring"]);
            Assert.Equal(("ネムラスコル", "AutoCorpusTransliterate"), translations["NemraSkol"]);

            var (vrennJapanese, vrennMethod) = translations["Vrenn Ring"];
            Assert.Equal("TranslationNameFallback", vrennMethod);
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
            Assert.Equal("TranslationLocalLlm", method);
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
            Assert.Equal("TranslationLocalLlm", method);
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
            Assert.Equal("TranslationLocalLlm", method);
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
            Assert.Equal("TranslationLocalLlm", method);
            Assert.Equal("タグ付き訳", japanese);
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
            Assert.Equal(("バッチ候補一", "TranslationLocalLlm"), translations["Sjpts Batch Candidate One"]);
            Assert.Equal(("バッチ候補二", "TranslationLocalLlm"), translations["Sjpts Batch Candidate Two"]);

            var logText = File.ReadAllText(Path.Combine(root, "Translation", "translation.log"));
            Assert.Contains("3回のバッチ呼び出しに分割", logText);
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
            Assert.Equal(("末尾空白の訳", "TranslationLocalLlm"), translations["Sjpts Trailing Space Candidate "]);
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

            Assert.Equal(("「両側引用の訳」", "TranslationLocalLlm"), translations["\"Sjpts Quoted Both Sides\""]);
            Assert.Equal(("「先頭のみ引用の訳", "TranslationLocalLlm"), translations["\"Sjpts Leading Quote Only"]);
            Assert.Equal(("末尾のみ引用の訳」", "TranslationLocalLlm"), translations["Sjpts Trailing Quote Only\""]);
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

            Assert.Equal(("引用符無し候補の訳", "TranslationLocalLlm"), translations["Sjpts Plain No Quote Candidate"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.58.5: defensive coverage for NormalizeBatchResponseSource's
    /// &lt;SJPTS_TARGET&gt;/&lt;/SJPTS_TARGET&gt; stripping — not observed in
    /// real gemma4 testing (the model always omitted the wrapper tags as
    /// instructed), but the prompt can't force this from every possible
    /// model/server, so a model that echoes the tags back anyway must still
    /// match correctly rather than silently failing forever.</summary>
    [Fact]
    public void RunOne_LlmBatch_ModelEchoesWrapperTagsBackDespiteInstructions_StillMatches()
    {
        const string plugin = "SjptsMatchingEdgeCases.esp";
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_promptgen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputDir = Path.Combine(root, "out_temp");
            using var log = OpenTestLog(root);
            var fakeLlm = FakeTextTranslator.Succeeding(
                ("<SJPTS_TARGET>Sjpts Plain No Quote Candidate</SJPTS_TARGET>", "タグ付きで返された訳"));

            PromptGenerator.RunOne(CandidatesTsvPath, CorpusTsvPath, NonexistentImportDir(root), plugin, outputDir, log, llmLocal: fakeLlm);

            var pluginDir = Path.Combine(outputDir, "SjptsMatchingEdgeCases");
            var translations = ReadTranslationsTemplate(Path.Combine(pluginDir, "translations.tsv"));

            Assert.Equal(("タグ付きで返された訳", "TranslationLocalLlm"), translations["Sjpts Plain No Quote Candidate"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
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
}
