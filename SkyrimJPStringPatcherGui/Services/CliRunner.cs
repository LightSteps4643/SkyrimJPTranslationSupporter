using System.Diagnostics;

namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// The GUI's only touch point with the actual translation pipeline: spawns the
/// console app's built exe as a subprocess and relays its stdout/stderr line by
/// line. No pipeline logic lives here or anywhere else in the GUI project — this
/// class knows how to run a process and nothing about what PickUpTarget/
/// Translation/GenerateDsdFile actually do.
/// </summary>
public static class CliRunner
{
    public sealed record Result(int ExitCode, bool Succeeded);

    /// <param name="arguments">Each element is one argument, passed via
    /// ProcessStartInfo.ArgumentList so .NET handles quoting/escaping — plugin
    /// names routinely contain spaces, brackets, apostrophes, and ampersands
    /// (e.g. "RoM - Saints &amp; Seducers Patch.esp"), which a single
    /// concatenated command-line string would get wrong.</param>
    /// <param name="onOutputLine">Called for every stdout/stderr line, on a
    /// background thread — callers marshal to the UI thread themselves.</param>
    /// <param name="llmLocalApiKey">v0.52.1a: step 5（ローカルLLM）用のAPIキー、
    /// 設定されていれば — 子プロセスへSKYRIMJPSP_LLM_API_KEY環境変数として渡す
    /// （コマンドライン引数には一切乗せない）。argvは、このプロセスが実行されて
    /// いる間、他のあらゆるプロセスから見える（タスクマネージャーのコマンドライン列・
    /// WMI等）ため、環境変数のほうが（完全に安全ではないにせよ）まだ良く、他の
    /// CLIツールも同じ理由で同じ慣習に従っている。--llm-localを使わない起動でも
    /// 渡して問題ない——CLI側は--llm-localが同時に指定されたときだけこれを読む
    /// （Program.cs参照）。</param>
    /// <param name="llmCloudApiKey">同様に、step 6（生成AI翻訳・クラウド）が
    /// OpenAI互換API方式のとき用のAPIキー。SKYRIMJPSP_CLOUD_LLM_API_KEY環境変数
    /// として渡す。ローカルとクラウドを同時に有効化しても互いのキーを上書きしない
    /// よう、あえて別の環境変数にしている。</param>
    public static async Task<Result> RunAsync(string exePath, IReadOnlyList<string> arguments, string workingDirectory,
        Action<string> onOutputLine, CancellationToken cancellationToken = default, string? llmLocalApiKey = null, string? llmCloudApiKey = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(llmLocalApiKey)) psi.EnvironmentVariables["SKYRIMJPSP_LLM_API_KEY"] = llmLocalApiKey;
        if (!string.IsNullOrEmpty(llmCloudApiKey)) psi.EnvironmentVariables["SKYRIMJPSP_CLOUD_LLM_API_KEY"] = llmCloudApiKey;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutputLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutputLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        return new Result(process.ExitCode, process.ExitCode == 0);
    }
}
