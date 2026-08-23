using System.Runtime.CompilerServices;
using System.Text;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// The counterpart to <see cref="RunLog"/> — v0.45.0. RunLog is a curated,
/// human-facing REPORT: what got excluded, what needs review, counts. It
/// deliberately does not trace ordinary successful processing (see its own
/// remarks). TraceLog is the other thing: an ordinary program-execution log —
/// timestamped, tagged with the source file/method that wrote each line, at a
/// severity a person can filter by. It exists for the question RunLog was never
/// meant to answer: "what did the CODE actually do, in what order, and what
/// blew up" — the kind of thing you'd want if a run crashed on someone else's
/// machine and you only have the log file, not a debugger attached.
///
/// Written with <c>AutoFlush = true</c> and one line per call (no buffering
/// until <see cref="Dispose"/>, unlike RunLog) specifically so a crash mid-run
/// still leaves everything logged up to that point on disk.
///
/// Level defaults to Info and is overridable via the <c>SKYRIMJPSP_LOG_LEVEL</c>
/// environment variable (e.g. <c>SKYRIMJPSP_LOG_LEVEL=Debug</c>) — same idea as
/// any conventional logging framework's minimum-level knob, just without
/// pulling in a logging framework for a 3-command CLI tool. Trace is verbose
/// enough to include per-candidate resolution detail; Info covers stage/major-
/// step boundaries and timings; Warning/Error cover recoverable oddities and
/// exceptions respectively.
/// </summary>
public enum TraceLevel { Trace, Debug, Info, Warning, Error }

public sealed class TraceLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly TraceLevel _minLevel;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    private TraceLog(StreamWriter writer, TraceLevel minLevel)
    {
        _writer = writer;
        _minLevel = minLevel;
    }

    /// <summary>Opens the trace log for a stage, alongside (not instead of) that
    /// stage's <see cref="RunLog"/>. File lives next to the stage's own
    /// <c>&lt;stage&gt;.log</c>, named <c>&lt;stage&gt;.trace.log</c>.</summary>
    public static TraceLog Open(string stageFolder, string stageName, TraceLevel? minLevel = null)
    {
        Directory.CreateDirectory(stageFolder);
        var path = Path.Combine(stageFolder, $"{stageName.ToLowerInvariant()}.trace.log");
        var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var log = new TraceLog(writer, minLevel ?? ResolveMinLevel());
        log.Info($"=== {stageName} start ({BuildVersion.Current}, level={log._minLevel}) ===");
        return log;
    }

    private static TraceLevel ResolveMinLevel()
    {
        var env = Environment.GetEnvironmentVariable("SKYRIMJPSP_LOG_LEVEL");
        return env != null && Enum.TryParse<TraceLevel>(env, ignoreCase: true, out var parsed) ? parsed : TraceLevel.Info;
    }

    public void Trace(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "") =>
        Write(TraceLevel.Trace, message, member, file);

    public void Debug(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "") =>
        Write(TraceLevel.Debug, message, member, file);

    public void Info(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "") =>
        Write(TraceLevel.Info, message, member, file);

    public void Warning(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "") =>
        Write(TraceLevel.Warning, message, member, file);

    /// <param name="exception">When given, its full <c>ToString()</c> (type,
    /// message, stack trace, inner exceptions) is appended on the lines that
    /// follow — this is the detail a bug report from someone else's machine
    /// needs that a bare console message loses the moment the window closes.</param>
    public void Error(string message, Exception? exception = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
    {
        Write(TraceLevel.Error, message, member, file);
        if (exception != null) _writer.WriteLine(exception.ToString());
    }

    private void Write(TraceLevel level, string message, string member, string file)
    {
        if (level < _minLevel) return;
        var fileName = Path.GetFileNameWithoutExtension(file);
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level,-7}] {_stopwatch.Elapsed.TotalSeconds,7:F1}s {fileName}.{member}: {message}");
    }

    public void Dispose()
    {
        Info($"=== end (elapsed {_stopwatch.Elapsed.TotalSeconds:F1}s) ===");
        _writer.Dispose();
    }
}
