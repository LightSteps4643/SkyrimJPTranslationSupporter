namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// 2026-09-12: shared CLI-subprocess execution/orchestration for both GUI tabs
/// (MainForm's「プラグイン翻訳」and InterfaceTextPanel's「Interface翻訳」) —
/// extracted from two near-line-for-line-identical private RunCliAsync methods.
/// Only two things genuinely differed between the two tabs: which CLI locator
/// (CliLocator vs InterfaceTextCliLocator) and which progress-completion
/// parser (TranslationProgressParser.TryParsePluginCompleted vs
/// TryParseModCompleted) to use — both are supplied as delegates. Everything
/// else (process launch, marker-line handling via
/// <see cref="CliOutputLineClassifier"/>, progress-bar math,
/// cancellation/exception handling, the trailing blank-log-line convention)
/// was already 100% tab-agnostic. See design/gui_architecture.md.
///
/// Owns its own CancellationTokenSource (mirrors each tab's former private
/// _currentRunCts field) so the owning Form's FormClosing can still
/// force-kill an in-flight subprocess via <see cref="CancelForShutdown"/> —
/// this is a DIFFERENT, non-cooperative cancellation path from each tab's own
/// cooperative "キャンセル" button flow (_activeCancelFlagPath), which stays
/// local to each Form since it also drives its own confirmation dialog /
/// completion-message wording.
/// </summary>
public sealed class CliExecutionRunner
{
    public delegate bool ValidateDelegate(string path, out string error);
    public delegate bool TryParseCompletedDelegate(string line, out string name);

    private readonly LogWindow _logWindow;
    private readonly Func<string?> _tryAutoDetect;
    private readonly Func<string, string, string> _resolveAbsolute;
    private readonly ValidateDelegate _validate;
    private readonly TryParseCompletedDelegate _tryParseCompleted;
    private CancellationTokenSource? _currentRunCts;

    public CliExecutionRunner(
        LogWindow logWindow,
        Func<string?> tryAutoDetect,
        Func<string, string, string> resolveAbsolute,
        ValidateDelegate validate,
        TryParseCompletedDelegate tryParseCompleted)
    {
        _logWindow = logWindow;
        _tryAutoDetect = tryAutoDetect;
        _resolveAbsolute = resolveAbsolute;
        _validate = validate;
        _tryParseCompleted = tryParseCompleted;
    }

    /// <summary>A CLI subprocess launched via <see cref="RunAsync"/> doesn't
    /// stop just because the GUI window closes — without this, closing
    /// mid-run leaves the CLI exe running invisibly in the background.
    /// Cancelling makes CliRunner.RunAsync kill the process (and its tree)
    /// before the exception propagates back up — RunAsync's own
    /// OperationCanceledException handling stays silent (no error dialog)
    /// since this is an intentional user shutdown, not a failure.</summary>
    public void CancelForShutdown() => _currentRunCts?.Cancel();

    public async Task<bool> RunAsync(
        Form owner, string? productRoot, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, long>? pluginCharsForProgress,
        string? llmApiKey, string? cloudAiApiKey,
        Action<bool> setBusy, Action<string> appendLog, Action<string> setStatus)
    {
        if (productRoot == null)
        {
            MessageBox.Show(owner, "実行フォルダを特定できませんでした。GUIの配置場所を確認してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        // v0.54.0: CLI実行ファイルのパスはユーザー設定にせず、GUI・CLIが常に同じ
        // 製品フォルダの兄弟として配置される前提で毎回自動検出する（既知の課題
        // 参照——手動指定できる設定項目は不要と判断し廃止した）。
        var cliExePath = _resolveAbsolute(productRoot, _tryAutoDetect() ?? "");
        if (!_validate(cliExePath, out var cliError))
        {
            MessageBox.Show(owner, cliError, "CLI実行ファイルが見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        var argsDisplay = string.Join(' ', arguments);
        setBusy(true);
        setStatus($"実行中: {argsDisplay}");
        appendLog($"> {Path.GetFileName(cliExePath)} {argsDisplay}");
        _currentRunCts = new CancellationTokenSource();
        string? issuesLine = null;
        string? issuesPluginsLine = null;
        string? lastErrorLine = null;
        var progressTotalChars = pluginCharsForProgress?.Values.Sum() ?? 0;
        var progressDoneChars = 0L;
        if (progressTotalChars > 0) _logWindow.SetProgress(0);
        void OnOutputLine(string line)
        {
            appendLog(line);
            if (pluginCharsForProgress != null && progressTotalChars > 0
                && _tryParseCompleted(line, out var completedPlugin)
                && pluginCharsForProgress.TryGetValue(completedPlugin, out var chars))
            {
                progressDoneChars += chars;
                _logWindow.SetProgress((double)progressDoneChars / progressTotalChars);
            }
            var classified = CliOutputLineClassifier.Classify(line);
            switch (classified.Kind)
            {
                case CliOutputLineKind.IssuesPluginsLine: issuesPluginsLine = classified.Payload; break;
                case CliOutputLineKind.IssuesLine: issuesLine = classified.Payload; break;
                case CliOutputLineKind.ErrorLine: lastErrorLine = classified.Payload; break;
            }
        }
        try
        {
            var result = await CliRunner.RunAsync(cliExePath, arguments, productRoot, OnOutputLine, _currentRunCts.Token,
                !string.IsNullOrEmpty(llmApiKey) ? llmApiKey : null, !string.IsNullOrEmpty(cloudAiApiKey) ? cloudAiApiKey : null);
            if (!result.Succeeded)
            {
                appendLog($"[終了コード {result.ExitCode}]");
                var message = lastErrorLine != null
                    ? $"処理が失敗しました:\n{lastErrorLine}"
                    : $"処理が失敗しました（終了コード {result.ExitCode}）。ログを確認してください。";
                MessageBox.Show(owner, message, "実行エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else if (issuesLine != null)
            {
                var message = "一部のプラグイン、またはレコードを正常に処理できなかったためスキップしました。\n" +
                    "処理自体は完了していますが、詳細はログを確認してください。\n\n" + FormatIssuesSummary(issuesLine);
                if (!string.IsNullOrWhiteSpace(issuesPluginsLine))
                    message += "\n\n対象プラグイン:\n" + string.Join('\n', issuesPluginsLine.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => $"・{p}"));
                MessageBox.Show(owner, message, "一部のデータをスキップしました", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            // Window is closing (FormClosing cancelled us via CancelForShutdown)
            // — the child process has already been killed by CliRunner; no
            // dialog, the form itself is on its way out.
            return false;
        }
        catch (Exception ex)
        {
            appendLog($"[例外] {ex.Message}");
            MessageBox.Show(owner, $"CLIの起動に失敗しました:\n{ex.Message}", "実行エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _currentRunCts?.Dispose();
            _currentRunCts = null;
            setBusy(false);
            setStatus("準備完了");
            if (progressTotalChars > 0) _logWindow.SetProgress(null);
            // 空行を1つ挟んで区切りにする——実行ログウィンドウは複数のCLI呼び出し
            // （MO2再読込＆初期化はpickuptarget+translationの2回、翻訳実行→続けて
            // DSDファイル生成、等）の出力が続けて流れ込むため、どこからどこまでが
            // 1回の操作の出力かが分かりにくいという指摘への対応。空行自体には
            // タイムスタンプを付けない（LogWindow.AppendLine参照）。
            appendLog("");
        }
    }

    /// <summary>"##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0"
    /// という機械可読な行を、MessageBoxにそのまま出すのではなく、0件の項目を除いた
    /// 日本語の箇条書きに変換する。</summary>
    public static string FormatIssuesSummary(string issuesLine)
    {
        var labels = new Dictionary<string, string>
        {
            ["plugins"] = "スキップされたプラグイン",
            ["fields"] = "スキップされたレコード/フィールド",
            ["fail_open"] = "除外判定に失敗し、安全側に倒して含めた候補",
            ["context_only"] = "文脈情報のみ抽出できなかった候補（翻訳への影響なし）",
        };

        var lines = new List<string>();
        foreach (var token in issuesLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0) continue;
            var key = token[..eq];
            if (!labels.TryGetValue(key, out var label)) continue;
            if (!int.TryParse(token[(eq + 1)..], out var count) || count <= 0) continue;
            lines.Add($"・{label}: {count}件");
        }

        return lines.Count > 0 ? string.Join('\n', lines) : "";
    }
}
