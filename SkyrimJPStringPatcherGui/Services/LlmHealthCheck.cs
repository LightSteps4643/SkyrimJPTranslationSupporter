using System.Net.Http.Json;
using System.Text.Json;

namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// A pre-flight check the GUI runs when the user enables the "ローカルLLM翻訳"
/// checkbox — sends one minimal request to the SAME OpenAI-compatible chat
/// completions endpoint/model the CLI's `--llm` pass will actually use (see
/// Translation/LocalLlmTranslator.cs on the CLI side), so a dead server or a
/// mistyped/unpulled model name is caught immediately instead of after minutes
/// of `translation --all` grinding through candidates. This duplicates no
/// translation logic — it's a connectivity probe, not a second copy of
/// LocalLlmTranslator.
/// </summary>
public static class LlmHealthCheck
{
    public sealed record CheckResult(bool Ok, string Error);

    public static async Task<CheckResult> CheckAsync(string endpoint, string model)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new CheckResult(false, "ローカルLLMエンドポイントが設定されていません。");
        if (string.IsNullOrWhiteSpace(model))
            return new CheckResult(false, "ローカルLLMモデル名が設定されていません。");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var requestBody = new
            {
                model,
                messages = new[] { new { role = "user", content = "Hi" } },
            };
            using var response = await http.PostAsJsonAsync(endpoint, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                var snippet = body.Length > 200 ? body[..200] + "..." : body;
                return new CheckResult(false,
                    $"サーバーがエラーを返しました（HTTP {(int)response.StatusCode} {response.ReasonPhrase}）。\n" +
                    $"モデル名『{model}』が正しいか、サーバー側に導入済みか確認してください。\n応答: {snippet}");
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("choices", out _))
                return new CheckResult(false, "サーバーから予期しない形式の応答が返りました（OpenAI互換 /v1/chat/completions ではない可能性があります）。");

            return new CheckResult(true, "");
        }
        catch (TaskCanceledException)
        {
            return new CheckResult(false, $"サーバーへの接続がタイムアウトしました（15秒）。\nエンドポイント: {endpoint}\nサーバーが起動しているか確認してください。");
        }
        catch (HttpRequestException ex)
        {
            return new CheckResult(false, $"サーバーに接続できません。\nエンドポイント: {endpoint}\n詳細: {ex.Message}\nサーバー（Ollama等）が起動しているか確認してください。");
        }
        catch (JsonException)
        {
            return new CheckResult(false, "サーバーからの応答をJSONとして解釈できませんでした。OpenAI互換 /v1/chat/completions エンドポイントか確認してください。");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"確認中に予期しないエラーが発生しました: {ex.Message}");
        }
    }
}
