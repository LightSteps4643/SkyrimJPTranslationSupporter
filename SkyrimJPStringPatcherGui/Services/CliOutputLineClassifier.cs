namespace SkyrimJPStringPatcherGui.Services;

/// <summary>2026-09-12: pure classification of one line of a CLI subprocess's
/// stdout — extracted out of CliExecutionRunner.RunAsync's inline output
/// handler (which used to be duplicated verbatim across MainForm.cs and
/// InterfaceTextPanel.cs) so the marker-line detection itself, as opposed to
/// the process-launching/MessageBox side of things, is unit-testable without
/// spinning up a Form or a real subprocess — the same way
/// TranslationProgressParser already is.</summary>
public enum CliOutputLineKind
{
    Plain,
    IssuesLine,
    IssuesPluginsLine,
    ErrorLine,
}

public readonly record struct ClassifiedCliOutputLine(CliOutputLineKind Kind, string Payload);

public static class CliOutputLineClassifier
{
    // v0.54.2 (既知の課題21.): pickuptargetが不正なプラグイン/レコードをスキップ
    // した場合、機械可読な専用プレフィックス("##SJPTS_ISSUES##")の1行をstdoutへ
    // 出す。LogWindowの大量の情報に埋もれさせないよう、この行を検知したら実行
    // 成功時でも明示的なMessageBoxで知らせる（レアケースのため）。
    private const string IssuesMarkerPrefix = "##SJPTS_ISSUES##";
    private const string IssuesPluginsMarkerPrefix = "##SJPTS_ISSUES_PLUGINS##";

    // v0.57.1: pickuptarget prints "[error] ..." (readable, not a stack
    // trace) for a recoverable MO2 configuration problem (see
    // Mo2InstanceConfigurationException) — captured here so the failure
    // dialog can show the ACTUAL cause instead of just a bare exit code.
    private const string ErrorMarkerPrefix = "[error] ";

    /// <param name="line">One line of the CLI's stdout, as-is.</param>
    /// <returns>Kind + payload. IssuesLine's payload is the FULL original
    /// line (unstripped, matching the pre-2026-09-12 behavior — the prefix
    /// itself is harmless noise to FormatIssuesSummary's own "token=count"
    /// parsing). IssuesPluginsLine/ErrorLine payloads have their prefix
    /// stripped and (for IssuesPluginsLine) trimmed.</returns>
    public static ClassifiedCliOutputLine Classify(string line)
    {
        // より長い方のプレフィックスを先にチェックする——
        // "##SJPTS_ISSUES_PLUGINS##"は"##SJPTS_ISSUES##"では始まらないため
        // 実際は衝突しないが、念のため意図を明確にする順序にしてある。
        if (line.StartsWith(IssuesPluginsMarkerPrefix, StringComparison.Ordinal))
            return new ClassifiedCliOutputLine(CliOutputLineKind.IssuesPluginsLine, line[IssuesPluginsMarkerPrefix.Length..].Trim());
        if (line.StartsWith(IssuesMarkerPrefix, StringComparison.Ordinal))
            return new ClassifiedCliOutputLine(CliOutputLineKind.IssuesLine, line);
        if (line.StartsWith(ErrorMarkerPrefix, StringComparison.Ordinal))
            return new ClassifiedCliOutputLine(CliOutputLineKind.ErrorLine, line[ErrorMarkerPrefix.Length..]);
        return new ClassifiedCliOutputLine(CliOutputLineKind.Plain, line);
    }
}
