using System.Net.Http.Json;
using System.Text.Json;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>Endpoint/model configuration for <see cref="LocalLlmTranslator"/>. Null
/// wherever this is threaded through the pipeline means "step 5 is off" — the
/// default, since a full unresolved-candidate pass costs 1-2 hours and fails
/// outright if nothing is listening on the endpoint.</summary>
/// <param name="ApiKey">v0.52.1a: sent as an `Authorization: Bearer` header when
/// non-empty — required to reach an authenticated cloud OpenAI-compatible API
/// (OpenAI/OpenRouter/DeepSeek/Groq/etc.), as opposed to an unauthenticated local
/// server (Ollama/LM Studio/...), which this stays compatible with by simply
/// omitting the header when empty (the pre-v0.52.1a behavior).</param>
public sealed record LocalLlmOptions(string Endpoint, string Model, string ApiKey = "");

/// <summary>
/// Step 5 (v0.49.0): whatever 1.〜4. leave unresolved, send to a locally-running
/// LLM instead of leaving it entirely to the AI-chat/human pass. Deliberately the
/// LAST automated step, not a replacement for any earlier one — see
/// DESIGN_NOTES.md's local-LLM investigation for why (1.〜4. already cover the
/// name-type fields this would otherwise compete with, and do so more safely for
/// that shape; this step's own strength is the long-form/sentence-shaped fields
/// 1.〜4. structurally never touch — descriptions, dialogue, quest journals, lore
/// text).
///
/// Talks OpenAI-compatible Chat Completions (<c>POST /v1/chat/completions</c>),
/// not Ollama's own <c>/api/generate</c> — Ollama, LM Studio, text-generation-webui,
/// vLLM, koboldcpp, and llama.cpp's own server all implement this same shape, so a
/// user can point this at whichever local runtime they already have without this
/// tool needing to special-case any one of them. Verified against Ollama
/// (v0.32.13, gemma3:12b): 20 real candidates sent through both endpoints came
/// back essentially identical (8/20 byte-for-byte, the rest wording-level
/// variance indistinguishable from ordinary sampling noise).
///
/// A failure here (server unreachable, timeout, malformed response, non-Japanese
/// response) is NOT fatal — the candidate simply stays unresolved and falls
/// through to prompt.txt exactly as if this step didn't exist. Low quality is
/// accepted by design (see DESIGN_NOTES.md): a wrong or awkward answer from this
/// step is expected to be corrected via xTranslator community translation or a
/// glossary entry, not treated as a tool defect.
/// </summary>
public sealed class LocalLlmTranslator : ITextTranslator
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;

    public LocalLlmTranslator(LocalLlmOptions options)
    {
        _endpoint = options.Endpoint;
        _model = options.Model;
        // 120s: generous relative to the ~2-3s/candidate observed against Ollama —
        // this is a ceiling against a hung server, not a tuned expected latency.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        if (options.ApiKey.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    /// <summary>
    /// Sends <paramref name="promptText"/> as a single user-role message and
    /// returns the assistant's raw response text (trimmed), or null if every
    /// attempt failed — the caller treats null exactly like "this candidate
    /// remains unresolved".
    ///
    /// v0.52.1a: retries up to <paramref name="maxAttempts"/> times (default 3)
    /// on a failed CALL (network hiccup, malformed JSON, empty response, no
    /// Japanese at all), re-sending the SAME prompt unchanged. Previously (up
    /// through v0.49.2a) this also retried whenever the response contained ANY
    /// stray English text, with a corrective follow-up prompt — that made sense
    /// when a single candidate's answer was expected to be pure Japanese with
    /// nothing else. It stopped making sense once <see cref="PromptGenerator.ApplyLlmStep"/>
    /// started batching a whole plugin's candidates into one call (v0.52.1a):
    /// the expected response format is now "English source&lt;TAB&gt;Japanese
    /// translation" TSV lines, so the English source text is SUPPOSED to be
    /// there on every line — the old stray-English check fired on exactly that
    /// and burned 3 wasted retries on every batch, discarding otherwise-correct
    /// responses (confirmed against real data: a batch response the model
    /// answered correctly on the first try was retried twice more anyway, purely
    /// because its own required "English source" column looked like leakage).
    /// Line-level correctness (which candidates actually got a usable-looking
    /// answer) is <see cref="PromptGenerator.ApplyLlmStep"/>'s job now, not this
    /// method's.
    /// </summary>
    public string? TryTranslate(string promptText, out string error, int maxAttempts = 3)
    {
        var lastError = "";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = CallOnce(promptText, out var callError);
            if (response != null)
            {
                error = attempt == 1 ? "" : $"succeeded on attempt {attempt}/{maxAttempts} after retrying a failed call";
                return response;
            }
            lastError = callError;
        }

        error = lastError;
        return null;
    }

    /// <summary>ITextTranslator's 2-arg shape doesn't include maxAttempts — an
    /// interface member can't be satisfied by a method with an extra parameter
    /// even when it has a default, so this just forwards with the default.</summary>
    string? ITextTranslator.TryTranslate(string promptText, out string error) => TryTranslate(promptText, out error);

    /// <summary>One HTTP round-trip. Returns null on any failure (unreachable
    /// server, timeout, malformed response, empty response, or a response with
    /// no Japanese at all) — <see cref="TryTranslate"/> is the retry loop around
    /// this.</summary>
    private string? CallOnce(string promptText, out string error)
    {
        error = "";
        try
        {
            var requestBody = new
            {
                model = _model,
                messages = new[] { new { role = "user", content = promptText } },
            };

            using var response = _http.PostAsJsonAsync(_endpoint, requestBody).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return null;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "empty response";
                return null;
            }

            var trimmed = text.Trim();
            if (!LanguageDetector.ContainsJapanese(trimmed))
            {
                error = $"response doesn't look like Japanese: \"{trimmed}\"";
                return null;
            }

            return trimmed;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }
}
