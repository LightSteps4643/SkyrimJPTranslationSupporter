using System.Net;
using System.Text;
using System.Text.Json;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// LocalLlmTranslator's own request-shaping logic (as opposed to PromptGenerator's
/// use of the ITextTranslator abstraction, which FakeTextTranslator covers) is
/// only observable on the actual HTTP request — verified here via an in-memory
/// fake HttpMessageHandler (LocalLlmTranslator's test-only constructor overload)
/// rather than a real HttpListener/socket, which proved to crash the xUnit test
/// host in this environment. See LocalLlmOptions.ReasoningEffort's remarks for
/// why the field exists: some "thinking"-capable models (confirmed: Ollama's
/// gemma4) can exhaust their whole completion token budget on an internal
/// reasoning trace before ever writing the actual answer; reasoning_effort=
/// "none" was confirmed (against a real Ollama/gemma4 instance, not in this
/// test) to eliminate that. These tests only verify LocalLlmTranslator sends (or
/// omits) the field correctly — they don't re-verify Ollama's own behavior.
/// </summary>
public class LocalLlmTranslatorTests
{
    /// <summary>Captures every request body it was asked to send and how many
    /// times it was called — no real network I/O. What it answers with is
    /// driven by <paramref name="responder"/> (1-based call count in, response
    /// out), so tests can simulate hard failures (HTTP error status), soft/
    /// response-processing failures (empty or non-Japanese content), and
    /// recovery-after-N-attempts, not just the fixed-success case the simple
    /// constructor gives.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }
        public string? CapturedRequestBody { get; private set; }

        public FakeHandler(string answerText = "こんにちは") : this(_ => OkResponse(answerText)) { }

        public FakeHandler(Func<int, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            CapturedRequestBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(CallCount);
        }

        public static HttpResponseMessage OkResponse(string answerText)
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = answerText } } },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void TryTranslate_ReasoningEffortNotConfigured_RequestBodyOmitsTheField()
    {
        var handler = new FakeHandler();
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "some-model"), handler);

        var result = translator.TryTranslate("translate this", out var error);

        Assert.Equal("こんにちは", result);
        Assert.Equal("", error);

        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _),
            "reasoning_effort must not be present at all when LocalLlmOptions.ReasoningEffort is left unset — a model/server " +
            "that doesn't recognize the field should never see it, not even as an explicit null.");
    }

    [Fact]
    public void TryTranslate_ReasoningEffortConfigured_RequestBodyIncludesTheExactValue()
    {
        var handler = new FakeHandler();
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "gemma4:26b", ReasoningEffort: "none"), handler);

        var result = translator.TryTranslate("translate this", out var error);

        Assert.Equal("こんにちは", result);
        Assert.Equal("", error);

        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        Assert.True(doc.RootElement.TryGetProperty("reasoning_effort", out var value));
        Assert.Equal("none", value.GetString());
    }

    [Fact]
    public void TryTranslate_RequestBodyStillCarriesModelAndMessage_RegardlessOfReasoningEffort()
    {
        var handler = new FakeHandler();
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "gemma4:26b", ReasoningEffort: "none"), handler);

        translator.TryTranslate("please translate: Iron Sword", out _);

        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        Assert.Equal("gemma4:26b", doc.RootElement.GetProperty("model").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("please translate: Iron Sword", messages[0].GetProperty("content").GetString());
    }

    /// <summary>v0.58.4: brought LocalLlmTranslator's failure handling in line
    /// with ClaudeCodeTranslator's — a "hard" (systemic/infra-level) failure,
    /// like a non-2xx HTTP status (server unreachable/erroring), is retried up
    /// to 3 times internally with the SAME prompt before giving up.</summary>
    [Fact]
    public void TryTranslate_HttpErrorStatus_IsHardFailure_RetriesUpToThreeTimes()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "some-model"), handler);

        var result = translator.TryTranslate("translate this", out var error);

        Assert.Null(result);
        Assert.Equal(3, handler.CallCount);
        Assert.Contains("HTTP 500", error);
    }

    /// <summary>v0.58.4: a "soft"/response-processing failure — the server
    /// answered successfully (HTTP 200) but with empty content — must NOT be
    /// retried. Retrying the same prompt would almost certainly reproduce the
    /// exact same empty answer, so the old blind-retry-everything behavior just
    /// wasted up to 3x the per-call wait for nothing.</summary>
    [Fact]
    public void TryTranslate_EmptyResponse_IsSoftFailure_DoesNotRetry()
    {
        var handler = new FakeHandler(_ => FakeHandler.OkResponse(""));
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "some-model"), handler);

        var result = translator.TryTranslate("translate this", out var error);

        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("empty response", error);
    }

    /// <summary>v0.58.5: CallOnce no longer rejects a non-Japanese response at
    /// all — that check moved to PromptGenerator.ApplyLlmStep, which judges it
    /// per CANDIDATE, not per whole batch response (see its own remarks: a
    /// model that responds in well-formed TSV but with no Japanese, e.g.
    /// echoing back vanilla Skyrim's own untranslated "arcane script" spell-
    /// tome text, answered correctly in FORMAT — that's not a translator-level
    /// failure). TryTranslate must therefore treat this as an ordinary success
    /// and hand the raw text back unchanged.</summary>
    [Fact]
    public void TryTranslate_NonJapaneseResponse_IsNotRejected_ReturnedAsIs()
    {
        var handler = new FakeHandler(answerText: "hello");
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "some-model"), handler);

        var result = translator.TryTranslate("translate this", out var error);

        Assert.Equal("hello", result);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("", error);
    }

    /// <summary>v0.58.4: a hard failure that recovers on retry (e.g. a
    /// transient blip) succeeds without exhausting all 3 attempts, and resets
    /// the consecutive-failure count — mirrors ClaudeCodeTranslator's own
    /// "single retry recovers -> not a persistent problem" reasoning.</summary>
    [Fact]
    public void TryTranslate_HardFailureRecoversOnSecondAttempt_SucceedsWithoutExhaustingRetries()
    {
        var handler = new FakeHandler(count => count == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FakeHandler.OkResponse("回復した訳"));
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "some-model"), handler);

        var result = translator.TryTranslate("translate this", out var error);

        Assert.Equal("回復した訳", result);
        Assert.Equal(2, handler.CallCount); // succeeded on the 2nd attempt, no 3rd call
        Assert.False(translator.CircuitOpen);
    }

    /// <summary>v0.58.4: LocalLlmTranslator now has a circuit breaker, matching
    /// ClaudeCodeTranslator — a local server that's genuinely down/crashed
    /// (every call is a hard failure) trips it after
    /// ConsecutiveFailureThreshold (3) TryTranslate calls each exhausting their
    /// own 3 internal retries, and every call after that returns immediately
    /// WITHOUT touching the handler again — this is what lets
    /// PromptGenerator.ApplyLlmStep skip the rest of a run instead of grinding
    /// through every remaining candidate at 3x the per-call timeout each.</summary>
    [Fact]
    public void TryTranslate_ConsecutiveHardFailures_OpensCircuitBreaker_AndStopsCallingHandler()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "some-model"), handler);

        Assert.Null(translator.TryTranslate("candidate 1", out _));
        Assert.False(translator.CircuitOpen);
        Assert.Null(translator.TryTranslate("candidate 2", out _));
        Assert.False(translator.CircuitOpen);
        Assert.Null(translator.TryTranslate("candidate 3", out _));
        Assert.True(translator.CircuitOpen); // 3rd consecutive TryTranslate-level hard failure trips it

        var callCountAtOpen = handler.CallCount; // 3 TryTranslate calls x 3 internal retries each = 9
        Assert.Equal(9, callCountAtOpen);

        var result = translator.TryTranslate("candidate 4", out var error);

        Assert.Null(result);
        Assert.Equal(callCountAtOpen, handler.CallCount); // never reached the handler at all
        Assert.Contains("circuit breaker open", error);
    }

    /// <summary>v0.58.4: soft/response-processing failures must NEVER count
    /// toward the circuit breaker — a run with several naturally awkward
    /// candidates (empty/non-Japanese answers) is not evidence the server
    /// itself is broken, and aborting the rest of the run over it would be
    /// wrong (same reasoning ClaudeCodeTranslator's own remarks give).</summary>
    [Fact]
    public void TryTranslate_RepeatedSoftFailures_NeverOpenCircuitBreaker()
    {
        var handler = new FakeHandler(_ => FakeHandler.OkResponse(""));
        var translator = new LocalLlmTranslator(new LocalLlmOptions("http://fake/v1/chat/completions", "some-model"), handler);

        for (var i = 0; i < 5; i++)
            Assert.Null(translator.TryTranslate($"candidate {i}", out _));

        Assert.False(translator.CircuitOpen);
        Assert.Equal(5, handler.CallCount); // one call per TryTranslate -- soft failures are never retried
    }
}
