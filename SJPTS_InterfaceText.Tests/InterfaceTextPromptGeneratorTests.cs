using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;
using SJPTS_InterfaceText;

namespace SJPTS_InterfaceText.Tests;

/// <summary>A scriptable stand-in for ClaudeCodeTranslator/LocalLlmTranslator —
/// ApplyLlmStep only ever talks to ITextTranslator, so a fake is enough to
/// exercise its dedup/batching/circuit-breaker/parsing logic without a real
/// LLM call.</summary>
public sealed class FakeTranslator : ITextTranslator
{
    private readonly Queue<(string? Response, string Error)> _responses = new();
    public bool CircuitOpen { get; set; }
    public List<string> PromptsReceived { get; } = new();

    public void Enqueue(string? response, string error = "") => _responses.Enqueue((response, error));

    public string? TryTranslate(string promptText, out string error)
    {
        PromptsReceived.Add(promptText);
        if (_responses.Count == 0)
        {
            error = "";
            return null;
        }
        var (response, err) = _responses.Dequeue();
        error = err;
        return response;
    }
}

public class InterfaceTextPromptGeneratorTests
{
    private static RunLog OpenTempLog(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_log_{Guid.NewGuid():N}");
        return RunLog.Open(dir, "Test");
    }

    [Fact]
    public void ApplyLlmStep_SingleBatch_ResolvesAllKeys()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello"), ("$Bar", "World") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tこんにちは\n<SJPTS_TARGET>World</SJPTS_TARGET>\t世界\n");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal("こんにちは", result["$Foo"].Japanese);
            Assert.Equal("世界", result["$Bar"].Japanese);
            Assert.Single(fake.PromptsReceived); // fits in one batch
        }
        finally { }
    }

    /// <summary>2026-09-12: debugging aid distinct from the ESP CLI's own
    /// prompt.txt (a human AI-chat handoff for what's still unresolved) —
    /// this records what WAS actually sent, win or lose. Content must match
    /// PromptsReceived exactly (byte-for-byte what went to TryTranslate).</summary>
    [Fact]
    public void ApplyLlmStep_WritesPromptBatchFile_MatchingWhatWasSent()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tこんにちは\n");

        using var log = OpenTempLog(out var dir);
        try
        {
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            var promptBatchPath = Path.Combine(dir, "prompt_localLLM_batch1_of_1.txt");
            Assert.True(File.Exists(promptBatchPath));
            Assert.Equal(fake.PromptsReceived[0], File.ReadAllText(promptBatchPath));
        }
        finally { }
    }

    /// <summary>A translate run that does ローカルLLM then 生成AI（クラウド）
    /// makes two separate ApplyLlmStep calls against the SAME mod folder —
    /// the second (cloudLLM) call must not delete the first's (localLLM)
    /// files, since only same-prefix stale files are cleared.</summary>
    [Fact]
    public void ApplyLlmStep_CloudRunAfterLocalRun_DoesNotDeleteLocalRunsPromptFiles()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };

        using var log = OpenTempLog(out var dir);
        try
        {
            var localFake = new FakeTranslator();
            localFake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tこんにちは\n");
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, localFake, "TestMod", log, null, 12_000, dir, "localLLM");

            var cloudFake = new FakeTranslator();
            cloudFake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tこんにちは\n");
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, cloudFake, "TestMod", log, null, 12_000, dir, "cloudLLM");

            Assert.True(File.Exists(Path.Combine(dir, "prompt_localLLM_batch1_of_1.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "prompt_cloudLLM_batch1_of_1.txt")));
        }
        finally { }
    }

    [Fact]
    public void ApplyLlmStep_DuplicateEnglishText_AsksOnceAppliesToAllKeys()
    {
        // Two different keys share the exact same English text — should be
        // asked about once (dedup by text) and the answer applied to both.
        var pending = new List<(string Key, string English)>
        {
            ("$A_Enable", "Enable"), ("$B_Enable", "Enable"),
        };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Enable</SJPTS_TARGET>\t有効化\n");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal("有効化", result["$A_Enable"].Japanese);
            Assert.Equal("有効化", result["$B_Enable"].Japanese);
            Assert.Single(fake.PromptsReceived);
            // The real entry's tagged text should appear exactly once (deduplicated) —
            // counting "- Target:" itself would also match the instruction header's
            // own worked example ("like this: - Target: <tag>example text</tag>").
            var occurrences = fake.PromptsReceived[0].Split("<SJPTS_TARGET>Enable</SJPTS_TARGET>").Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally { }
    }

    [Fact]
    public void ApplyLlmStep_OverCharLimit_SplitsIntoMultipleBatchCalls()
    {
        // Distinct text per entry — identical text would get deduplicated into
        // one group (see the dedup test above) and never exercise batch splitting.
        var pending = Enumerable.Range(0, 10)
            .Select(i => ($"$K{i}", new string((char)('a' + i), 100)))
            .ToList();
        var fake = new FakeTranslator();
        for (var i = 0; i < 10; i++) fake.Enqueue(""); // each batch call gets an (empty/unusable) response

        using var log = OpenTempLog(out var dir);
        try
        {
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, batchCharLimit: 250, modWorkDir: dir, providerLabel: "localLLM");

            Assert.True(fake.PromptsReceived.Count > 1);
        }
        finally { }
    }

    [Fact]
    public void ApplyLlmStep_CircuitOpen_SkipsWithoutCallingTranslator()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator { CircuitOpen = true };

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Empty(result);
            Assert.Empty(fake.PromptsReceived); // never even called
        }
        finally { }
    }

    [Fact]
    public void ApplyLlmStep_BatchFails_ContinuesWithoutRetrying()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator();
        fake.Enqueue(null, "simulated failure");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Empty(result);
            Assert.Single(fake.PromptsReceived); // exactly one attempt, no retry
        }
        finally { }
    }

    [Fact]
    public void ApplyLlmStep_ResponseHasNoJapanese_AcceptedWithNoJapaneseNotesTag()
    {
        // 2026-09-12: reversed from the earlier "treated as unresolved"
        // behavior — real-data finding (e.g. "Ok"->"OK") showed the model is
        // often CORRECT that a string doesn't need translation, matching the
        // ESP CLI's own precedent (DsdJsonGenerator.cs). Now accepted as
        // Resolved=true with a dedicated Notes tag, not silently dropped.
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tHello\n"); // model echoed English back

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal("Hello", result["$Foo"].Japanese);
            Assert.Equal("TranslationLocalLlmNoJapanese", result["$Foo"].Notes);
        }
        finally { }
    }

    [Fact]
    public void ApplyLlmStep_ModelWrapsAnswerInQuotesOrTags_StillParses()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\t<SJPTS_TARGET>\"こんにちは\"</SJPTS_TARGET>\n");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal("こんにちは", result["$Foo"].Japanese);
        }
        finally { }
    }

    [Fact]
    public void ApplyLlmStep_UnknownAnswerLine_KeyStaysUnresolved()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator();
        fake.Enqueue("this response has no tab-separated recognizable lines at all\n");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Empty(result);
        }
        finally { }
    }

    // ==== 2026-09-12 TDD: tag-based response matching (real-data bug,
    // HeelsFix "$HEELSFIX_TARGET_ACTOR" = "Target:") — written BEFORE the fix,
    // per the agreed TDD approach. Mirrors the equivalent new tests added to
    // SkyrimJPStringPatcher.Tests/Translation/PromptGeneratorTests.cs (ESP
    // side) — see this session's discussion / design/interface_translations.md
    // for the full analysis. Some are expected to FAIL against the current
    // implementation; others are regression safety nets for the refactor. ====

    /// <summary>Regression-safety net for the refactor — a tag-wrapped
    /// response for a candidate whose own text is literally "Target:" already
    /// resolves correctly even under the OLD implementation (the old
    /// "Target:" strip never fires because the text starts with the tag
    /// character "&lt;", not literally "Target:"); the actual real-data bug is
    /// specific to an UNTAGGED response, covered by the sibling tests below.</summary>
    [Fact]
    public void ApplyLlmStep_CandidateTextIsLiterallyTargetColon_TaggedResponseResolves()
    {
        var pending = new List<(string Key, string English)> { ("$HEELSFIX_TARGET_ACTOR", "Target:") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Target:</SJPTS_TARGET>\t対象");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "heelsfix", log, null, 12_000, dir, "localLLM");

            Assert.Equal("対象", result["$HEELSFIX_TARGET_ACTOR"].Japanese);
        }
        finally { }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. An
    /// untagged response currently resolves just fine (tags are optional
    /// today) — this is the actual real-data bug's exact shape: the current
    /// prompt tells the model to omit the tags, and the old "Target:" strip
    /// then destroys real content that happens to start with that word.
    /// Under the new design (prompt requires the tags; parsing requires them
    /// too), an untagged response must instead be rejected as unparseable.</summary>
    [Fact]
    public void ApplyLlmStep_ResponseWithoutTags_IsRejectedAsUnparseable()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Sjpts Format Edge Case Candidate") };
        var fake = new FakeTranslator();
        fake.Enqueue("Sjpts Format Edge Case Candidate\t訳文");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Empty(result);
        }
        finally { }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. Text
    /// before the tag (e.g. the model echoing the prompt's own "Target: "
    /// label) is currently silently stripped and still matches — the new
    /// design must instead reject anything other than whitespace outside the
    /// tag pair.</summary>
    [Fact]
    public void ApplyLlmStep_ResponseWithNoiseBeforeTag_IsRejectedAsUnparseable()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Sjpts Format Edge Case Candidate") };
        var fake = new FakeTranslator();
        fake.Enqueue("Target: <SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Empty(result);
        }
        finally { }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. A missing
    /// opening tag (only the closing tag present) currently still resolves,
    /// because the old logic strips a trailing close tag independently of
    /// whether a matching opening tag was ever seen. The new design requires
    /// both tags to be present as a matched pair.</summary>
    [Fact]
    public void ApplyLlmStep_ResponseMissingOpeningTag_IsRejectedAsUnparseable()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Sjpts Format Edge Case Candidate") };
        var fake = new FakeTranslator();
        fake.Enqueue("Sjpts Format Edge Case Candidate</SJPTS_TARGET>\t訳文");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Empty(result);
        }
        finally { }
    }

    /// <summary>EXPECTED TO FAIL against the current implementation. Symmetric
    /// to the missing-opening-tag case above — a missing closing tag (only
    /// the opening tag present) currently still resolves for the same reason
    /// (independent prefix/suffix strips instead of a matched pair).</summary>
    [Fact]
    public void ApplyLlmStep_ResponseMissingClosingTag_IsRejectedAsUnparseable()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Sjpts Format Edge Case Candidate") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Sjpts Format Edge Case Candidate\t訳文");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Empty(result);
        }
        finally { }
    }

    /// <summary>Regression-safety net for the refactor — whitespace
    /// immediately around an otherwise well-formed tag pair must still be
    /// tolerated (Trim()-level only, not arbitrary text around it). Already
    /// passes today; kept to make sure the simplified extraction logic
    /// doesn't accidentally start requiring an exact match with zero
    /// tolerance.</summary>
    [Fact]
    public void ApplyLlmStep_WhitespaceAroundOtherwiseWellFormedTag_StillResolves()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Sjpts Format Edge Case Candidate") };
        var fake = new FakeTranslator();
        fake.Enqueue("  <SJPTS_TARGET>Sjpts Format Edge Case Candidate</SJPTS_TARGET>  \t訳文");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal("訳文", result["$Foo"].Japanese);
        }
        finally { }
    }
}
