using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>Which <c>claude</c> executable to run and (optionally) which model
/// to ask for. No API key here — authentication is whatever the Claude Code CLI
/// itself is already configured with on this machine (<c>claude login</c> or its
/// own environment variable), not this tool's concern.</summary>
public sealed record ClaudeCodeOptions(string ExePath, string Model);

/// <summary>Cumulative token/cost usage across every <see cref="ClaudeCodeTranslator.TryTranslate"/>
/// call made through one instance — see that class's remarks for why this
/// exists (visibility into what a translation run actually costs).</summary>
public sealed record ClaudeCodeUsage(int CallCount, long InputTokens, long OutputTokens,
    long CacheCreationInputTokens, long CacheReadInputTokens, double TotalCostUsd);

/// <summary>
/// v0.52.1a: an alternative backend for step 6 (see <see cref="ITextTranslator"/>) —
/// instead of an HTTP call to an OpenAI-compatible endpoint
/// (<see cref="LocalLlmTranslator"/>), shells out to the Claude Code CLI's
/// non-interactive "print mode" (<c>claude -p</c>: read one prompt from stdin,
/// print the response, exit) exactly the way <c>SkyrimJPStringPatcherGui</c>
/// shells out to THIS tool's own console exe — same
/// spawn-a-process-and-read-its-output shape, just a different downstream tool.
///
/// v0.52.1a: requests <c>--output-format json</c> rather than plain text. Plain
/// text mode gives no way to know what a run actually cost — and unlike
/// LocalLlmTranslator (a local, free model), every call here is billed against
/// the user's Claude subscription, so "how many tokens did this just use" is a
/// real question, not idle curiosity. The JSON payload's <c>result</c> field
/// carries the same answer text plain mode would have printed to stdout; its
/// <c>usage</c>/<c>total_cost_usd</c> fields are accumulated into
/// <see cref="Usage"/> so a caller can log a per-run total once all candidates
/// are done (see Program.cs).
///
/// v0.52.1a: failures are split into two kinds, only one of which counts toward
/// <see cref="CircuitOpen"/> — see <see cref="CallOnce"/>'s remarks for the
/// reasoning (short version: a systemic problem like token-limit exhaustion
/// should trip the breaker; an individual candidate's response happening to be
/// unparsable/empty/non-Japanese should not, or a run with a few naturally
/// awkward candidates would abort itself for no real reason).
/// </summary>
public sealed class ClaudeCodeTranslator : ITextTranslator
{
    /// <summary>連続で何回「異常系」失敗したらサーキットブレーカーを開くか。
    /// 使用上限到達のような系統的な失敗は毎回同じ理由で失敗し続けるため、数回
    /// 連続で失敗した時点でそれ以降も全滅する可能性が高いと判断してよい——
    /// 単発の失敗（その候補だけがたまたま解決できなかった等）で誤って開いて
    /// しまわないよう、成功するたびにカウントはリセットする。
    ///
    /// v0.53.0: この「3回連続」は、それぞれその場で
    /// <see cref="HardFailureRetryAttempts"/>回リトライしてもなお異常系だった
    /// 場合だけを1回とカウントする——単発のリトライで回復しない、本物の持続的な
    /// 異常だけを数える設計に変更した（下記`CallOnceWithHardRetry`参照）。</summary>
    private const int ConsecutiveFailureThreshold = 3;

    /// <summary>異常系の失敗1件につき、その場で最大何回まで試すか（初回込み）。
    /// v0.53.0以前はここが1回きりで、プラグインをまたいだ単発の異常系失敗
    /// （セッション制限とは無関係な、その呼び出しだけのブレ）が無条件に
    /// サーキットブレーカーのカウントへ積み上がってしまっていた——本当に
    /// セッション制限等の持続的な異常であれば、即座にリトライしても同じ結果に
    /// なるはずなので、その場のリトライで確認してから数える方が正確。</summary>
    private const int HardFailureRetryAttempts = 3;

    private readonly string _exePath;
    private readonly string _model;

    private int _callCount;
    private long _inputTokens;
    private long _outputTokens;
    private long _cacheCreationInputTokens;
    private long _cacheReadInputTokens;
    private double _totalCostUsd;

    private int _consecutiveFailures;

    /// <summary>Snapshot of everything spent through this instance so far —
    /// read once after a run finishes (RunOne/RunMany/RunAll all share a single
    /// instance across every candidate, so this accumulates the WHOLE run's
    /// usage, not just one call's).</summary>
    public ClaudeCodeUsage Usage => new(_callCount, _inputTokens, _outputTokens,
        _cacheCreationInputTokens, _cacheReadInputTokens, _totalCostUsd);

    /// <summary>連続失敗が閾値に達し、以降の呼び出しをすべて即座に打ち切って
    /// いるかどうか。呼び出し元（PromptGenerator.ApplyLlmStep）はこれを見て、
    /// 残りの候補をまとめてスキップする。</summary>
    public bool CircuitOpen { get; private set; }

    public ClaudeCodeTranslator(ClaudeCodeOptions options)
    {
        _exePath = options.ExePath;
        _model = options.Model;
    }

    /// <summary>
    /// 失敗を「異常系」（プロセス起動失敗・タイムアウト・終了コード異常・
    /// <c>is_error:true</c>）と「レスポンス処理の失敗」（JSONとしてパース
    /// できない・空・日本語に見えない）に分ける。
    ///
    /// 「異常系」は<see cref="HardFailureRetryAttempts"/>回まで
    /// （`CallOnceWithHardRetry`内で完結する独立のリトライ）。異常系を
    /// サーキットブレーカーの連続失敗カウントへ加算するのは、そのリトライも
    /// 尽きて**なお**異常系だった場合だけ——単発のリトライで回復するなら、
    /// それはセッション制限のような持続的な異常ではなく、その呼び出しだけの
    /// ブレだったということ。（v0.52.1a時点は異常系を一切リトライせず即座に
    /// カウントしていたが、これだとプラグインをまたいだ無関係な単発失敗3件が
    /// 偶然重なっただけでもサーキットブレーカーが作動してしまっていた——
    /// 実機で確認された不具合）。
    ///
    /// v0.53.0a: 「レスポンス処理の失敗」は同一プロンプトでの自動リトライを
    /// 行わず、その場で未解決として素通りする（DESIGN_NOTES.md既知の課題14.）。
    /// 応答形式が期待通りでないケースは偶然の当たり外れではなく高い確率で
    /// 再現するため、自動リトライしてもクレジットを消費するだけで成功しない
    /// ことが多い。再試行したい場合は「翻訳実行」をユーザーが再度押す
    /// （既存の翻訳結果は保持されるので、未解決分だけが対象になる）。
    /// </summary>
    public string? TryTranslate(string promptText, out string error)
    {
        if (CircuitOpen)
        {
            error = $"circuit breaker open ({ConsecutiveFailureThreshold}回連続の異常系失敗のため以降の生成AI翻訳呼び出しを打ち切り中)";
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

        // レスポンス処理の失敗——プロセス自体は正常終了しているので系統的な
        // 異常とは判断せず、サーキットブレーカーにはカウントしない。同一
        // プロンプトでの自動リトライは行わず、そのまま未解決として返す。
        error = callError;
        return null;
    }

/// <summary>v0.53.0: wraps <see cref="CallOnce"/> with a small, independent
    /// retry loop that fires ONLY while the result keeps coming back as a hard
    /// (異常系) failure — up to <see cref="HardFailureRetryAttempts"/> attempts
    /// total, same prompt each time. The moment a retry stops being a hard
    /// failure (recovers to a success or degrades to a soft/response-processing
    /// failure), it's returned immediately and this stops retrying — the outer
    /// <see cref="TryTranslate"/> loop takes it from there exactly as if that had
    /// been the first attempt. Only a result that is STILL a hard failure after
    /// every retry here reaches <see cref="TryTranslate"/>'s circuit-breaker
    /// counter, so that counter reflects verified persistent failures, not
    /// single unrelated blips scattered across different plugins/batches.</summary>
    private (string? Text, bool HardFailure, string Error) CallOnceWithHardRetry(string promptText)
    {
        var result = CallOnce(promptText);
        for (var attempt = 2; attempt <= HardFailureRetryAttempts && result.HardFailure; attempt++)
            result = CallOnce(promptText);
        return result;
    }

    /// <summary>
    /// One <c>claude -p</c> subprocess invocation. <paramref name="hardFailure"/>
    /// (via the returned tuple) tells the caller whether this is a "process-level"
    /// failure that should count toward <see cref="CircuitOpen"/>:
    /// process-start failure, timeout, non-zero exit code, and
    /// <c>is_error:true</c> in the parsed JSON (checked regardless of exit code —
    /// a quota/limit response could plausibly come back as exit 0 with an
    /// error-shaped body rather than a non-zero exit; this hasn't actually been
    /// observed yet, but exit-code-only detection would silently miss it if it
    /// ever does happen, since is_error:true responses would otherwise fall
    /// through to the "response processing" branches below and just retry
    /// forever without ever tripping the breaker) — versus a "soft" failure
    /// (JSON parse failure, empty response, no Japanese detected) that's just
    /// this one candidate being hard to translate, not a sign anything is
    /// systemically broken.
    /// </summary>
    private (string? Text, bool HardFailure, string Error) CallOnce(string promptText)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-p"); // print mode: one prompt in, one response out, exit — no interactive session
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json"); // carries usage/cost alongside the answer — see class remarks
            if (_model.Length > 0)
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(_model);
            }

            using var process = Process.Start(psi);
            if (process == null)
                return (null, true, $"failed to start '{_exePath}'");

            // Piped via stdin, not as a CLI argument — the prompt (with reference
            // examples/context baked in, see PromptGenerator.BuildCandidateBlock)
            // can run to several KB, well past a comfortable argv size, and stdin
            // sidesteps any shell-quoting concern entirely.
            process.StandardInput.Write(promptText);
            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            // 180s: Claude Code's own model calls run noticeably slower than a
            // local Ollama round-trip (~2-3s) — generous ceiling against a hung
            // process, not a tuned expected latency.
            if (!process.WaitForExit(180_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (null, true, "claude CLI timed out (180s)");
            }

            if (process.ExitCode != 0)
            {
                // --output-format json は失敗時も stdout 側に診断情報を書き出す
                // ことが多く（実測: stderr は空のまま）、stderr しか見ていないと
                // 理由が一切わからないログになってしまう。stdout を優先し、
                // それも空ならstderrにフォールバック。ログが単一行の候補一覧に
                // 埋め込まれるため、異常に長い出力は切り詰める。
                var detail = stdout.Trim();
                if (detail.Length == 0) detail = stderr.Trim();
                const int maxDetailLength = 500;
                if (detail.Length > maxDetailLength) detail = detail[..maxDetailLength] + "...(truncated)";
                return (null, true, $"claude CLI exited {process.ExitCode}: {detail}");
            }

            string? text;
            bool isError;
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                text = root.TryGetProperty("result", out var resultEl) ? resultEl.GetString() : null;
                isError = root.TryGetProperty("is_error", out var isErrorEl) && isErrorEl.ValueKind == JsonValueKind.True;

                // Accumulate usage even on a response that turns out unusable
                // below (empty / non-Japanese / is_error) — the call still
                // happened and may still have cost tokens, so the running total
                // should reflect it.
                _callCount++;
                if (root.TryGetProperty("total_cost_usd", out var costEl) && costEl.TryGetDouble(out var cost))
                    _totalCostUsd += cost;
                if (root.TryGetProperty("usage", out var usageEl))
                {
                    if (usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt64(out var inTok)) _inputTokens += inTok;
                    if (usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt64(out var outTok)) _outputTokens += outTok;
                    if (usageEl.TryGetProperty("cache_creation_input_tokens", out var ccEl) && ccEl.TryGetInt64(out var ccTok)) _cacheCreationInputTokens += ccTok;
                    if (usageEl.TryGetProperty("cache_read_input_tokens", out var crEl) && crEl.TryGetInt64(out var crTok)) _cacheReadInputTokens += crTok;
                }
            }
            catch (JsonException ex)
            {
                return (null, false, $"failed to parse --output-format json response: {ex.Message}");
            }

            if (isError)
            {
                var detail = (text ?? "").Trim();
                const int maxDetailLength = 500;
                if (detail.Length > maxDetailLength) detail = detail[..maxDetailLength] + "...(truncated)";
                return (null, true, $"claude CLI reported is_error=true: {detail}");
            }

            if (string.IsNullOrWhiteSpace(text))
                return (null, false, "empty response");

            var trimmed = text.Trim();
            if (!LanguageDetector.ContainsJapanese(trimmed))
                return (null, false, $"response doesn't look like Japanese: \"{trimmed}\"");

            return (trimmed, false, "");
        }
        catch (Exception ex)
        {
            return (null, true, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
