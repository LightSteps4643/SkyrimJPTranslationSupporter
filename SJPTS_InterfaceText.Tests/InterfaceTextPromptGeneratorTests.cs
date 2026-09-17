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
    private readonly Queue<(string? Response, string Error, bool? Truncated)> _responses = new();
    public bool CircuitOpen { get; set; }

    /// <summary>Fallback used only while nothing was queued with its OWN
    /// per-call truncated flag via <see cref="Enqueue"/>'s truncated
    /// parameter — kept so every existing call site that sets this property
    /// directly (single fixed-truncation-state tests) keeps working
    /// unchanged.</summary>
    public bool LastResponseTruncated { get; set; }
    public List<string> PromptsReceived { get; } = new();

    /// <summary>2026-09-16: if set, <see cref="CircuitOpen"/> flips to true
    /// right after this many <see cref="TryTranslate"/> calls have been
    /// made — simulates a real backend's connection dying PARTWAY THROUGH a
    /// pass (e.g. Ollama crashing mid-run), as opposed to one that's already
    /// broken before the first call (set <see cref="CircuitOpen"/> directly
    /// for that case instead).</summary>
    public int? TripCircuitOpenAfterCall { get; set; }

    public void Enqueue(string? response, string error = "", bool? truncated = null) => _responses.Enqueue((response, error, truncated));

    public string? TryTranslate(string promptText, out string error)
    {
        PromptsReceived.Add(promptText);
        if (TripCircuitOpenAfterCall.HasValue && PromptsReceived.Count >= TripCircuitOpenAfterCall.Value)
            CircuitOpen = true;
        if (_responses.Count == 0)
        {
            error = "";
            return null;
        }
        var (response, err, truncated) = _responses.Dequeue();
        if (truncated.HasValue) LastResponseTruncated = truncated.Value;
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

    /// <summary>2026-09-18: mirrors the ESP side's
    /// RunOne_FilledModGlossaryEntry_AppearsAsSameModHintInPrompt — once a
    /// person fills in mod_glossary.tsv's Japanese column (issue #4's "c"),
    /// that hint must reach the LLM prompt for this mod's OTHER unresolved
    /// candidates. Confirms the ExternalSameModBaseline wiring just added to
    /// ApplyLlmStep actually works (previously always empty for Interface).</summary>
    [Fact]
    public void ApplyLlmStep_FilledModGlossaryEntry_AppearsAsSameModHintInPrompt()
    {
        var pending = new List<(string Key, string English)>
        {
            ("$1", "Sjpts Daedric Windrune Blade"), ("$2", "Sjpts Dwarven Windrune Blade"),
        };
        var fake = new FakeTranslator();
        fake.Enqueue(
            "<SJPTS_TARGET>Sjpts Daedric Windrune Blade</SJPTS_TARGET>\tデイドラの風紋の刃\n" +
            "<SJPTS_TARGET>Sjpts Dwarven Windrune Blade</SJPTS_TARGET>\tドワーフの風紋の刃\n");

        using var log = OpenTempLog(out var dir);
        try
        {
            // A person has already filled this in by hand before this run.
            SkyrimJPStringPatcher.Translation.ModPhraseGlossary.WriteTemplate(dir, "TestMod",
                [new SkyrimJPStringPatcher.Translation.ModPhraseGlossary.DetectedPhrase("Windrune Blade", 3, 10.0)]);
            var glossaryPath = SkyrimJPStringPatcher.Translation.ModPhraseGlossary.PathFor(dir);
            var content = File.ReadAllText(glossaryPath).Replace("Windrune Blade\t\t3\t10", "Windrune Blade\t風紋の刃\t3\t10");
            File.WriteAllText(glossaryPath, content);

            InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Contains(fake.PromptsReceived, p => p.Contains("Windrune Blade") && p.Contains("風紋の刃"));
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

            var promptBatchPath = Path.Combine(dir, "prompt_localLLM_call1.txt");
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

            Assert.True(File.Exists(Path.Combine(dir, "prompt_localLLM_call1.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "prompt_cloudLLM_call1.txt")));
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

    /// <summary>2026-09-16: the round/batch -&gt; pass redesign moved the
    /// circuit-breaker check inside the pass's inner (per-call) loop, needing
    /// a "circuitOpened" flag to break BOTH that loop and the outer pass
    /// loop — mirrors Translation/PromptGenerator.cs's own ApplyLlmStep and
    /// its own test of the same name. The only pre-existing circuit-breaker
    /// coverage (ApplyLlmStep_CircuitOpen_SkipsWithoutCallingTranslator)
    /// tests a translator that's ALREADY open before the very first call;
    /// this exercises the breaker tripping PARTWAY THROUGH a pass (a real
    /// backend's connection dying mid-run, e.g. Ollama crashing).</summary>
    [Fact]
    public void ApplyLlmStep_CircuitBreakerOpensMidPass_StopsWithoutSendingRemainingCandidates()
    {
        var pending = new List<(string Key, string English)>
        {
            ("$A", "Alpha"), ("$B", "Bravo"), ("$C", "Charlie"),
        };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Alpha</SJPTS_TARGET>\tアルファ");
        fake.Enqueue("<SJPTS_TARGET>Bravo</SJPTS_TARGET>\tブラボー");
        fake.Enqueue("<SJPTS_TARGET>Charlie</SJPTS_TARGET>\tチャーリー"); // never reached
        fake.TripCircuitOpenAfterCall = 2;

        using var log = OpenTempLog(out var dir);
        try
        {
            // Distinct text per entry, so dedup never merges them into one
            // call; a tiny char limit forces exactly one candidate per call.
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, batchCharLimit: 60, modWorkDir: dir, providerLabel: "localLLM");

            // Circuit opens right after the 2nd call — a 3rd call must never
            // be made, even though "$C"/"Charlie" is still unresolved and
            // waiting in this same pass.
            Assert.Equal(2, fake.PromptsReceived.Count);
            Assert.Equal("アルファ", result["$A"].Japanese);
            Assert.Equal("ブラボー", result["$B"].Japanese);
            Assert.False(result.ContainsKey("$C"));

            // Without the "circuitOpened" flag breaking both loops, this
            // would still stop at 2 calls (the following, otherwise-empty
            // pass would see zero progress and stop on its own) but would
            // log the circuit-breaker message a second time for no reason.
            // 2026-09-16: category text now correctly says "ローカルLLM" (a
            // bug found by independent code review — this was previously
            // hardcoded to "生成AI翻訳"/cloud regardless of providerLabel,
            // fixed by the LlmBatchTranslationEngine extraction).
            Assert.Equal(1, log.DetailCount(
                "ローカルLLMのサーキットブレーカー作動（残りをまとめてスキップ）",
                "local LLM circuit breaker open (remaining candidates skipped)"));
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
    public void ApplyLlmStep_TranslatorReportsResponseTruncated_LogsSpecificReason()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello"), ("$Bar", "World") };
        var fake = new FakeTranslator { LastResponseTruncated = true };
        fake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tこんにちは"); // "$Bar" never answered — cut off

        using var log = OpenTempLog(out var dir);
        try
        {
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            // 2026-09-16: LlmBatchTranslationEngineへの統合に伴い、この
            // カテゴリ文字列にも（ESP側と同じく）どのプロバイダの実行かを
            // 示す接頭辞が一貫して付くようになった（以前のInterface側は
            // この特定のメッセージにだけ接頭辞が付いていなかった）。
            Assert.Equal(1, log.DetailCount(
                "ローカルLLM: モデルの応答が出力トークン数の上限で打ち切られた可能性があります",
                "local LLM: the model's response may have been cut off by an output token limit"));
        }
        finally { }
    }

    /// <summary>
    /// 2026-09-13 (issue #2): mirrors PromptGeneratorTests'
    /// RunOne_LlmBatch_RoundTruncatedWithProgress_AutomaticallyRetriesRemainderNextRound
    /// — a batch cut off by an output-token limit must not force a manual
    /// re-run just for the leftover candidates. ApplyLlmStep now automatically
    /// re-batches and re-requests ONLY the still-unresolved candidates for
    /// another round, as long as the previous round both truncated AND made
    /// progress (no fixed round cap, per explicit user rejection of an
    /// arbitrary "3 rounds"). Round 1 here resolves "$Foo" but is silent on
    /// "$Bar" while truncated; round 2 (the automatic retry) resolves "$Bar"
    /// from a clean, non-truncated response.
    /// </summary>
    [Fact]
    public void ApplyLlmStep_RoundTruncatedWithProgress_AutomaticallyRetriesRemainderNextRound()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello"), ("$Bar", "World") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tこんにちは", truncated: true); // round 1: "$Bar" never answered
        fake.Enqueue("<SJPTS_TARGET>World</SJPTS_TARGET>\t世界", truncated: false); // round 2: the automatic retry

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal(2, fake.PromptsReceived.Count);
            Assert.Equal(("こんにちは", "SJPTS_TranslationLocalLlm", "AllJapanese"), result["$Foo"]);
            Assert.Equal(("世界", "SJPTS_TranslationLocalLlm", "AllJapanese"), result["$Bar"]);
        }
        finally { }
    }

    /// <summary>
    /// issue #4 (c: same-mod hints), 2026-09-16 — mirrors
    /// PromptGeneratorTests' own ESP-side retry-round test. Round 1 resolves
    /// "$Foo" ("Frostwind Blade") and truncates before "$Bar" ("Frostwind
    /// Shield", sharing "Frostwind"); round 2's automatic retry prompt must
    /// carry a "Same-mod translations so far" block naming round 1's own
    /// result, and round 1's own prompt must not have one (nothing was
    /// resolved yet this session when it was built).</summary>
    [Fact]
    public void ApplyLlmStep_RetryRound_IncludesSameModHintFromEarlierRoundsOwnResult()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Frostwind Blade"), ("$Bar", "Frostwind Shield") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Frostwind Blade</SJPTS_TARGET>\t氷風の刃", truncated: true); // round 1: "$Bar" never answered
        fake.Enqueue("<SJPTS_TARGET>Frostwind Shield</SJPTS_TARGET>\t氷風の盾", truncated: false); // round 2: the automatic retry

        using var log = OpenTempLog(out var dir);
        try
        {
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal(2, fake.PromptsReceived.Count);
            Assert.DoesNotContain("Same-mod translations so far", fake.PromptsReceived[0]);
            Assert.Contains("Same-mod translations so far", fake.PromptsReceived[1]);
            // 2026-09-16: legend+numeric code format (validated design) —
            // step's local LLM resolution tags with "SJPTS_TranslationLocalLlm",
            // SameModTrustTier priority 7.
            Assert.Contains("7=translated by a local LLM", fake.PromptsReceived[1]);
            Assert.Contains("\"Frostwind Blade\"\t\"氷風の刃\"\t7", fake.PromptsReceived[1]);
        }
        finally { }
    }

    /// <summary>2026-09-13: real bug — `batchLabel` ("バッチ1/2"/"バッチ") is
    /// Japanese, but was embedded verbatim into English-only strings (the
    /// DetailAndReport consoleText param and trace.Warning, both meant to be
    /// English regardless of RunLog's own ja/en setting per RunLog.cs's own
    /// doc comment). Forces English-language RunLog output for a truncated
    /// batch and asserts the log file contains no Japanese characters.</summary>
    [Fact]
    public void ApplyLlmStep_EnglishLogLanguage_TruncatedBatch_LogTextHasNoJapanese()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator { LastResponseTruncated = true };
        fake.Enqueue("<SJPTS_TARGET>Something Else</SJPTS_TARGET>\t訳文"); // "$Foo" never answered

        var originalLangEnv = Environment.GetEnvironmentVariable("SKYRIMJPSP_LOG_LANG");
        var dir = Path.Combine(Path.GetTempPath(), $"sjpts_uitext_log_{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LANG", "en");
            using (var log = RunLog.Open(dir, "Test"))
                InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            var logText = File.ReadAllText(Path.Combine(dir, "test.log"));
            Assert.DoesNotContain("バッチ", logText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LANG", originalLangEnv);
        }
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
            Assert.Equal("SJPTS_TranslationLocalLlmNoJapanese", result["$Foo"].Notes);
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

    /// <summary>
    /// 2026-09-13: real-data bug — a candidate whose English text has
    /// meaningful trailing whitespace (real example: FloatingSubtitles'
    /// "$FSUB_DualSubtitlesOffscreen_Text" = "DUAL SUBTITLES " with a
    /// trailing space) was embedded VERBATIM (untrimmed) into the prompt's
    /// tag, per BuildBlock — and the prompt explicitly instructs the model to
    /// copy the tagged text "unchanged". When the model does exactly that
    /// (confirmed against real gemma4:26b output), the matching code's
    /// `group.Key.Trim()` at the lookup site stripped the space back off
    /// before comparing against `byLine`'s untrimmed key — guaranteeing a
    /// mismatch no matter how faithfully the model answered, on every retry.
    /// This is the exact-match side of the fix: the untrimmed candidate must
    /// still match the model's untrimmed, faithful echo.
    /// </summary>
    [Fact]
    public void ApplyLlmStep_CandidateHasTrailingWhitespace_ModelEchoesExactly_StillResolves()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "DUAL SUBTITLES ") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>DUAL SUBTITLES </SJPTS_TARGET>\t二重字幕");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal("二重字幕", result["$Foo"].Japanese);
        }
        finally { }
    }

    /// <summary>The flexible/fallback side of the same fix: a model that
    /// (despite the "copy unchanged" instruction) trims the candidate's own
    /// trailing whitespace when echoing it back must still resolve — matching
    /// should tolerate either an exact echo or a trimmed one, not demand
    /// exactly one of the two.</summary>
    [Fact]
    public void ApplyLlmStep_CandidateHasTrailingWhitespace_ModelTrimsItWhenEchoing_StillResolvesViaFallback()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "DUAL SUBTITLES ") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>DUAL SUBTITLES</SJPTS_TARGET>\t二重字幕");

        using var log = OpenTempLog(out var dir);
        try
        {
            var result = InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            Assert.Equal("二重字幕", result["$Foo"].Japanese);
        }
        finally { }
    }

    // ==== 2026-09-12: ClassifyTaggedSourceIssue — mirrors the equivalent new
    // tests in SkyrimJPStringPatcher.Tests/Translation/PromptGeneratorTests.cs
    // (ESP side). 2026-09-16: moved to the shared LlmBatchTranslationEngine
    // (public) as part of the ESP/Interface ApplyLlmStep duplication
    // refactor — no longer needs reflection. ====

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

    // ==== 2026-09-12: MOD name preservation — real-data bug (Floating
    // Subtitles' "$FSUB_Title_Text" = "Floating Subtitles", HeelsFix's
    // "$HEELSFIX_MOD_NAME" = "Heels Fix" both got transliterated into
    // katakana instead of staying as the mod's own brand name). Confirmed
    // against real gemma4:26b output (manual test, see this session's
    // discussion) that telling the model the mod's display name and asking
    // it to preserve an exact/near-exact match works, including NOT
    // over-excluding ordinary vocabulary that merely shares a word with the
    // mod name (e.g. "High Heels" for a mod named "Heels Fix" still
    // translates normally). These tests only check the PROMPT TEXT sent
    // (the model's actual judgment isn't something a fake can exercise). ====

    [Fact]
    public void ApplyLlmStep_ModDisplayNameGiven_PromptNamesTheModAndAsksToPreserveIt()
    {
        var pending = new List<(string Key, string English)> { ("$HEELSFIX_MOD_NAME", "Heels Fix") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Heels Fix</SJPTS_TARGET>\tHeels Fix");

        using var log = OpenTempLog(out var dir);
        try
        {
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "heelsfix", log, null, 12_000, dir, "localLLM", modDisplayName: "Heels Fix");

            var prompt = Assert.Single(fake.PromptsReceived);
            Assert.Contains("a Skyrim SE mod named \"Heels Fix\"", prompt);
            Assert.Contains("If a string IS this mod's own name/title \"Heels Fix\"", prompt);
        }
        finally { }
    }

    /// <summary>When the caller doesn't supply modDisplayName (every
    /// pre-existing test/call site above), the prompt must still be
    /// well-formed — falling back to modName (the file-based target) rather
    /// than throwing or leaving a blank mod name in the instruction text.</summary>
    [Fact]
    public void ApplyLlmStep_ModDisplayNameOmitted_PromptFallsBackToModName()
    {
        var pending = new List<(string Key, string English)> { ("$Foo", "Hello") };
        var fake = new FakeTranslator();
        fake.Enqueue("<SJPTS_TARGET>Hello</SJPTS_TARGET>\tこんにちは");

        using var log = OpenTempLog(out var dir);
        try
        {
            InterfaceTextPromptGenerator.ApplyLlmStep(pending, fake, "TestMod", log, null, 12_000, dir, "localLLM");

            var prompt = Assert.Single(fake.PromptsReceived);
            Assert.Contains("a Skyrim SE mod named \"TestMod\"", prompt);
        }
        finally { }
    }
}
