using System.Text;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Per-stage run log, written to "&lt;Stage&gt;/&lt;stage&gt;.log".
///
/// Scope, per the requirement that drove it: this is NOT a trace of everything
/// processed — the candidate/corpus/translation TSVs already ARE the record of
/// normal results, and duplicating them here would bury the interesting part.
/// What the log exists for is the two things those files cannot show after the
/// fact: **what got excluded, and where something special happened.** Sections
/// are therefore weighted that way — counts for the routine, full detail for
/// exclusions and special handling.
///
/// The log lives in the STAGE folder rather than in the stage's out_temp: the
/// out_temp folders get wiped between runs, and (for GenerateDsdFile) the output
/// folder is the actual mod directory installed into MO2, where a stray .log
/// would ship to users.
/// </summary>
/// <summary>v0.46.0: which language <see cref="RunLog"/> writes its report in —
/// see <see cref="RunLog.ResolveLang"/>. Only these two exist for now (per the
/// user's own scoping decision when this was added): a hypothetical third
/// language would need every call site's (ja, en) pair widened to a triple,
/// which is not worth doing speculatively.</summary>
public enum RunLogLang { Ja, En }

public sealed class RunLog : IDisposable
{
    private readonly string _path;
    private readonly RunLogLang _lang;
    private readonly StringBuilder _body = new();
    private readonly List<(string Category, string Item)> _details = new();
    private readonly DateTime _startedAt = DateTime.Now;

    private RunLog(string path, string stageName, string version, RunLogLang lang)
    {
        _path = path;
        _lang = lang;
        _body.AppendLine("================================================================");
        _body.AppendLine(lang == RunLogLang.Ja
            ? $" SkyrimJPStringPatcher — {stageName} 実行ログ"
            : $" SkyrimJPStringPatcher — {stageName} run log");
        _body.AppendLine(lang == RunLogLang.Ja ? $" 日時: {_startedAt:yyyy-MM-dd HH:mm:ss}" : $" Date: {_startedAt:yyyy-MM-dd HH:mm:ss}");
        _body.AppendLine(lang == RunLogLang.Ja ? $" バージョン: {version}" : $" Version: {version}");
        _body.AppendLine("================================================================");
    }

    /// <summary>Opens the log for a stage. <paramref name="stageFolder"/> is the
    /// stage's own source folder ("PickUpTarget"), created if missing so a run
    /// from an unusual working directory still records something. Language
    /// (Japanese/English) is fixed for the whole report and taken from the
    /// <c>SKYRIMJPSP_LOG_LANG</c> environment variable (<c>en</c> for English,
    /// anything else — including unset — defaults to Japanese, this tool's
    /// primary audience). v0.45.0's TraceLog is deliberately English-only and
    /// NOT affected by this variable — see its own remarks for why.</summary>
    public static RunLog Open(string stageFolder, string stageName)
    {
        Directory.CreateDirectory(stageFolder);
        var path = Path.Combine(stageFolder, $"{stageName.ToLowerInvariant()}.log");
        return new RunLog(path, stageName, BuildVersion.Current, ResolveLang());
    }

    private static RunLogLang ResolveLang()
    {
        var env = Environment.GetEnvironmentVariable("SKYRIMJPSP_LOG_LANG");
        return string.Equals(env, "en", StringComparison.OrdinalIgnoreCase) ? RunLogLang.En : RunLogLang.Ja;
    }

    /// <param name="ja">Japanese section title.</param>
    /// <param name="en">English section title.</param>
    public void Section(string ja, string en)
    {
        _body.AppendLine();
        _body.AppendLine($"[{(_lang == RunLogLang.Ja ? ja : en)}]");
    }

    /// <summary>A plain line inside the current section.</summary>
    /// <param name="ja">Japanese text.</param>
    /// <param name="en">English text.</param>
    public void Line(string ja, string en) => _body.AppendLine($"  {(_lang == RunLogLang.Ja ? ja : en)}");

    /// <summary>Echo to the console AND the log — for the handful of lines a
    /// person watching the run needs to see anyway. Console output stays
    /// English-only regardless of <see cref="RunLogLang"/> — the console is
    /// already English throughout (Console.WriteLine calls elsewhere), so
    /// splitting just this one echo path would make the console itself
    /// inconsistently bilingual.</summary>
    public void Report(string text)
    {
        Console.WriteLine(text);
        _body.AppendLine($"  {text}");
    }

    /// <summary>Records one excluded / specially-handled item. Identical items are
    /// collapsed with an occurrence count when the log is written, which is what
    /// makes a category like "icon glyphs" readable: 619 exclusions turn out to be
    /// a couple of dozen distinct strings.</summary>
    /// <param name="categoryJa">Japanese category label.</param>
    /// <param name="categoryEn">English category label.</param>
    /// <param name="item">The item text itself — NOT translated (it's data: a
    /// FormId, a file path, an English candidate string), so there is no (ja, en)
    /// pair for it.</param>
    public void Detail(string categoryJa, string categoryEn, string item) =>
        _details.Add((_lang == RunLogLang.Ja ? categoryJa : categoryEn, item));

    /// <summary>Like <see cref="Detail"/>, but also echoes an English one-line
    /// summary to the console immediately (<paramref name="consoleText"/>) —
    /// narrowly scoped to the handful of ⑤⑥（LLM）call sites in
    /// <c>PromptGenerator.ApplyLlmStep</c> (circuit breaker, batch failure,
    /// not-found-in-response, batch call count) where a person watching a long
    /// run needs to see the reason in real time, not just after the run finishes
    /// and <see cref="Dispose"/> flushes the file. Deliberately NOT applied to
    /// the bulk ①-④ per-candidate <see cref="Detail"/> calls, which would flood
    /// console output on a full <c>--all</c> run (DESIGN_NOTES.md 既知の課題12.).</summary>
    public void DetailAndReport(string categoryJa, string categoryEn, string item, string consoleText)
    {
        Detail(categoryJa, categoryEn, item);
        Console.WriteLine(consoleText);
    }

    public int DetailCount(string categoryJa, string categoryEn) =>
        _details.Count(d => d.Category == (_lang == RunLogLang.Ja ? categoryJa : categoryEn));

    /// <summary>Exposed for the rare call site that needs to build a bilingual
    /// item string itself (e.g. one with structural labels baked into the body,
    /// not just a category) rather than going through <see cref="Line"/>/
    /// <see cref="Section"/>/<see cref="Detail"/>'s own (ja, en) parameters.</summary>
    public RunLogLang Lang => _lang;

    public void Dispose()
    {
        // v0.44.3: sections used to appear in whichever order their category was
        // FIRST seen while scanning candidates plugin-by-plugin — essentially
        // arbitrary, and confusing when it happened to print "4.NameFallback
        // Translator" ahead of "2.意味合成"/"3.音訳分解" even though every single
        // candidate is still tried ①→②→③→④ in that order underneath. Category
        // labels already start with their step number by convention (see
        // PromptGenerator's "1."/"2."/"3."/"4." prefixes) — sorting by the label
        // itself makes the log's section order match that reading order too.
        foreach (var group in _details.GroupBy(d => d.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var items = group.GroupBy(d => d.Item)
                .Select(g => (Item: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Item, StringComparer.Ordinal)
                .ToList();

            _body.AppendLine();
            _body.AppendLine(_lang == RunLogLang.Ja
                ? $"[{group.Key}] {group.Count()}件（うち異なる内容 {items.Count}種類）"
                : $"[{group.Key}] {group.Count()} item(s) ({items.Count} distinct)");
            foreach (var (item, count) in items)
                _body.AppendLine(count > 1 ? $"  ×{count}\t{item}" : $"      \t{item}");
        }

        _body.AppendLine();
        _body.AppendLine("================================================================");
        _body.AppendLine(_lang == RunLogLang.Ja
            ? $" 完了: {DateTime.Now:yyyy-MM-dd HH:mm:ss}（所要 {(DateTime.Now - _startedAt).TotalSeconds:F1}秒）"
            : $" Done: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (elapsed {(DateTime.Now - _startedAt).TotalSeconds:F1}s)");
        _body.AppendLine("================================================================");

        File.WriteAllText(_path, _body.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"Wrote log: {_path}");
    }
}

/// <summary>Single place the version string lives, so every log says which build
/// produced it (the folder-per-version workflow makes that the key fact when
/// comparing two runs' logs).</summary>
public static class BuildVersion
{
    public const string Current = "v0.57.1";
}
