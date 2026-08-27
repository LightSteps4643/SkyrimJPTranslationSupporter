using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

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
            result[parts[3]] = (parts[4], parts[5]); // keyed by EnglishText
        }
        return result;
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
            Assert.DoesNotContain("Target: \"Sjpts Test Sword\"", prompt);
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
            Assert.DoesNotContain("Target: \"Sjpts Llm Candidate\"", prompt);
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
}
