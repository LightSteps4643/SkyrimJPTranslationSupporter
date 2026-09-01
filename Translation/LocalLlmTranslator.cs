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
/// <param name="ReasoningEffort">v0.58.1: forwarded verbatim as the request body's
/// `reasoning_effort` field when non-null (e.g. "none"/"low"/"medium"/"high") —
/// an OpenAI-style knob some "thinking"-capable models (confirmed: Ollama's
/// gemma4) honor to control how much of the completion token budget goes to an
/// internal reasoning trace before the actual answer. Left unset (the default)
/// sends no such field at all, preserving prior behavior for every model/server
/// that doesn't support it. Confirmed harmless when set against non-thinking
/// models too (gemma3:12b, qwen2.5:14b-instruct via Ollama both answer normally
/// either way), so this is safe to set unconditionally rather than gating it on
/// the configured model name. See <see cref="LocalLlmTranslator.CallOnce"/>'s
/// remarks for why this exists.</param>
public sealed record LocalLlmOptions(string Endpoint, string Model, string ApiKey = "", string? ReasoningEffort = null);

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
///
/// v0.58.4: brought in line with <see cref="ClaudeCodeTranslator"/>'s failure
/// handling — see <see cref="CallOnce"/>'s remarks for the hard-failure-vs-
/// response-processing-failure split, and <see cref="CircuitOpen"/> for the
/// consecutive-hard-failure breaker. Previously this class retried EVERY kind of
/// failure blindly up to 3 times (including an empty/non-Japanese response,
/// which is highly reproducible for the same prompt and just wastes up to 3x the
/// per-call wait for no benefit) and had no circuit breaker at all (a genuinely
/// dead/crashed local server would be retried candidate-by-candidate for the
/// entire remaining run instead of being detected and skipped).
/// </summary>
public sealed class LocalLlmTranslator : ITextTranslator
{
    /// <summary>連続で何回「異常系」失敗したらサーキットブレーカーを開くか。
    /// ClaudeCodeTranslatorと同じ閾値・同じ考え方（下記CallOnceWithHardRetry参照）
    /// ——ローカルサーバーが異常停止した場合等、系統的な失敗は毎回同じ理由で
    /// 失敗し続けるため、数回連続で失敗した時点でそれ以降も全滅する可能性が
    /// 高いと判断してよい。</summary>
    private const int ConsecutiveFailureThreshold = 3;

    /// <summary>異常系の失敗1件につき、その場で最大何回まで試すか（初回込み）。
    /// ClaudeCodeTranslatorと同じ——単発のリトライで回復しない、本物の持続的な
    /// 異常だけをサーキットブレーカーのカウントへ積み上げる。</summary>
    private const int HardFailureRetryAttempts = 3;

    /// <summary>v0.58.4: 実機検証（gemma4:26b、文字数上限6000・思考ON運用）で
    /// 1回の呼び出しが約120秒かかるケースを確認したことを基準に、送信する
    /// プロンプト文字数に比例させる。接続確認（Services/LlmHealthCheck.cs、
    /// モデルロード済み前提で固定30秒）とは別に、翻訳実行本体は候補（バッチ）が
    /// 大きい・思考ONで遅いケースほど長く待てるようにする一方、小さい候補が
    /// ハングした場合はより早く見切れるようにする——固定120秒だと、小さい候補
    /// がハングしても120秒待たされる一方、思考ONの大きいバッチではまだ足りない
    /// 可能性もあった。</summary>
    private const double TimeoutSecondsPerChar = 120.0 / 6000.0;

    private const double MinimumTimeoutSeconds = 30.0;

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _reasoningEffort;

    private int _consecutiveFailures;

    /// <summary>連続失敗が閾値に達し、以降の呼び出しをすべて即座に打ち切って
    /// いるかどうか。呼び出し元（PromptGenerator.ApplyLlmStep）はこれを見て、
    /// 残りの候補をまとめてスキップする。</summary>
    public bool CircuitOpen { get; private set; }

    public LocalLlmTranslator(LocalLlmOptions options) : this(options, new HttpClientHandler()) { }

    /// <summary>Test-only seam: lets tests substitute an in-memory
    /// <see cref="HttpMessageHandler"/> for the real network stack, so
    /// LocalLlmTranslator's own request-shaping logic (e.g. whether
    /// reasoning_effort is included) can be verified without a real socket/
    /// HttpListener — real sockets proved unreliable to spin up reliably inside
    /// the xUnit test host in this project's environment (observed test-host
    /// process crashes), and an injected handler sidesteps that class of
    /// flakiness entirely, not just for these tests specifically.</summary>
    public LocalLlmTranslator(LocalLlmOptions options, HttpMessageHandler handler)
    {
        _endpoint = options.Endpoint;
        _model = options.Model;
        _reasoningEffort = options.ReasoningEffort;
        // v0.58.4: this Timeout is now just a final backstop against a
        // completely wedged connection — the timeout that actually matters is
        // computed per request (see ComputeTimeout) and passed as a
        // CancellationToken to PostAsJsonAsync, since a single HttpClient can't
        // have a different Timeout per call.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        if (options.ApiKey.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    private static TimeSpan ComputeTimeout(int promptLength) =>
        TimeSpan.FromSeconds(Math.Max(MinimumTimeoutSeconds, promptLength * TimeoutSecondsPerChar));

    /// <summary>
    /// Sends <paramref name="promptText"/> as a single user-role message and
    /// returns the assistant's raw response text (trimmed), or null if this
    /// candidate/batch couldn't be resolved — the caller treats null exactly
    /// like "this candidate remains unresolved".
    ///
    /// v0.58.4: failures are split into two kinds, only one of which counts
    /// toward <see cref="CircuitOpen"/> — see <see cref="CallOnce"/>'s remarks
    /// for the reasoning (same split as <see cref="ClaudeCodeTranslator"/>: a
    /// systemic problem like a dead/crashed server should trip the breaker; an
    /// individual response happening to be unparsable/empty/non-Japanese should
    /// not — retrying that blindly just re-asks the same question and gets the
    /// same reproducible non-answer, wasting up to 3x the wait for nothing).
    /// </summary>
    public string? TryTranslate(string promptText, out string error)
    {
        if (CircuitOpen)
        {
            error = $"circuit breaker open ({ConsecutiveFailureThreshold}回連続の異常系失敗のため以降のローカルLLM呼び出しを打ち切り中)";
            return null;
        }

        var (response, hardFailure, callError) = CallOnceWithHardRetry(promptText);

        if (hardFailure)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= ConsecutiveFailureThreshold)
                CircuitOpen = true;
            error = callError;
            return null;
        }

        if (response != null)
        {
            _consecutiveFailures = 0;
            error = "";
            return response;
        }

        // レスポンス処理の失敗——サーバー自体は正常に応答しているので系統的な
        // 異常とは判断せず、サーキットブレーカーにはカウントしない。同一
        // プロンプトでの自動リトライは行わず、そのまま未解決として返す
        // （同じプロンプトを再送しても高い確率で同じ結果になるだけのため）。
        error = callError;
        return null;
    }

    /// <summary>v0.58.4: <see cref="ClaudeCodeTranslator.CallOnce"/>と同じ
    /// パターン——「異常系」の結果が返ってきた場合だけ、その場で最大
    /// <see cref="HardFailureRetryAttempts"/>回まで同じプロンプトを再送する。
    /// 成功、またはレスポンス処理の失敗に変わった時点で即座に打ち切り、それを
    /// そのまま返す——このメソッドを抜けた時点で「まだ異常系のまま」だった
    /// 場合だけが、呼び出し元のサーキットブレーカーのカウントに入る。</summary>
    private (string? Text, bool HardFailure, string Error) CallOnceWithHardRetry(string promptText)
    {
        var result = CallOnce(promptText);
        for (var attempt = 2; attempt <= HardFailureRetryAttempts && result.HardFailure; attempt++)
            result = CallOnce(promptText);
        return result;
    }

    /// <summary>
    /// One HTTP round-trip. 戻り値タプルの<c>HardFailure</c>は、
    /// この結果が<see cref="CircuitOpen"/>のカウントに入れるべき「異常系」かどうか
    /// を呼び出し元へ伝える: サーバー未起動・タイムアウト・HTTPエラーステータス
    /// （プロセス起動失敗・非ゼロ終了コードに相当）——これらは接続先そのものが
    /// おかしい可能性を示すため、系統的な異常として扱う。対して「レスポンス処理の
    /// 失敗」（JSONとしてパースできない・choices/message/content の形が予期しない・
    /// 空・日本語に見えない）は、サーバー自体は正常応答しているので、その候補
    /// だけがたまたま解決しづらかっただけと判断し、系統的な異常には含めない
    /// （ClaudeCodeTranslator.CallOnceと同じ区別）。
    /// </summary>
    private (string? Text, bool HardFailure, string Error) CallOnce(string promptText)
    {
        var timeout = ComputeTimeout(promptText.Length);
        try
        {
            // v0.58.1: reasoning_effort is only included when explicitly configured
            // (--llm-local-reasoning-effort=/--llm-cloud-reasoning-effort=), not
            // hardcoded — see LocalLlmOptions.ReasoningEffort's remarks for why this
            // knob exists (a "thinking"-capable model can burn its whole completion
            // token budget on an internal reasoning trace before ever writing the
            // actual TSV answer, confirmed against Ollama's gemma4 on real batches:
            // finish_reason "length" with content empty). A JsonObject (rather than an
            // anonymous type) is used here specifically so the field can be omitted
            // entirely rather than sent as an explicit null, preserving prior request
            // shape/behavior for models and servers this isn't configured for.
            var requestBody = new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = _model,
                ["messages"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = promptText }),
            };
            if (_reasoningEffort != null)
                requestBody["reasoning_effort"] = _reasoningEffort;

            using var cts = new CancellationTokenSource(timeout);
            using var response = _http.PostAsJsonAsync(_endpoint, requestBody, cts.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return (null, true, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string? text;
            try
            {
                using var doc = JsonDocument.Parse(body);
                text = doc.RootElement.TryGetProperty("choices", out var choicesEl)
                    && choicesEl.ValueKind == JsonValueKind.Array && choicesEl.GetArrayLength() > 0
                    && choicesEl[0].TryGetProperty("message", out var messageEl)
                    && messageEl.TryGetProperty("content", out var contentEl)
                    ? contentEl.GetString()
                    : null;
            }
            catch (JsonException ex)
            {
                return (null, false, $"failed to parse response JSON: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(text))
                return (null, false, "empty response");

            // v0.58.5: 既知の課題26.関連——以前はここでバッチ応答全体（複数候補
            // まとめて）に日本語が1文字も無ければバッチごと失敗にしていたが、
            // これは粗すぎた。バニラSkyrim自身が意図的に翻訳していない文字列
            // （$MageScriptFont等、マスター魔法書の秘術ページ。公式日本語版でも
            // 未翻訳のまま確認済み）に対して、モデルが指示通り原文をそのまま
            // 返してくること自体は、モデルとしては正しい振る舞いであり失敗
            // ではない。「訳文に日本語が含まれるか」の判定は、個々の候補ごとに
            // 意味を持つ話なので、バッチ全体レベルのここではなく、候補単位の
            // マッチングを行うPromptGenerator.ApplyLlmStep側に移した。
            return (text.Trim(), false, "");
        }
        catch (OperationCanceledException)
        {
            return (null, true, $"request timed out ({timeout.TotalSeconds:F0}s)");
        }
        catch (Exception ex)
        {
            return (null, true, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
