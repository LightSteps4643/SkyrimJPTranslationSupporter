using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// TraceLog is RunLog's counterpart — an ordinary, timestamped, level-filtered
/// program-execution log ("what did the code actually do") rather than
/// RunLog's curated human-facing report. Every production call site passes it
/// as an OPTIONAL parameter (`TraceLog? trace = null`), so no existing test
/// happened to construct one at all — 0% coverage despite being used
/// pervasively across all 3 stages in real runs.
/// </summary>
public class TraceLogTests
{
    private static string TracePath(string stageFolder, string stageName) =>
        Path.Combine(stageFolder, $"{stageName.ToLowerInvariant()}.trace.log");

    [Fact]
    public void Open_CreatesTheExpectedFile_WithAStartLine()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        try
        {
            using (var trace = TraceLog.Open(root, "PickUpTarget"))
            {
                // just opening should already have written the start line
            }

            var path = TracePath(root, "PickUpTarget");
            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.Contains("PickUpTarget start"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Write_BelowTheMinLevel_IsSuppressed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        try
        {
            using (var trace = TraceLog.Open(root, "Translation", TraceLevel.Warning))
            {
                trace.Trace("this trace line must not appear");
                trace.Debug("this debug line must not appear");
                trace.Info("this info line must not appear");
            }

            var content = File.ReadAllText(TracePath(root, "Translation"));
            Assert.DoesNotContain("this trace line must not appear", content);
            Assert.DoesNotContain("this debug line must not appear", content);
            Assert.DoesNotContain("this info line must not appear", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Write_AtOrAboveTheMinLevel_IsWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        try
        {
            using (var trace = TraceLog.Open(root, "Translation", TraceLevel.Warning))
            {
                trace.Warning("this warning line must appear");
                trace.Error("this error line must appear");
            }

            var content = File.ReadAllText(TracePath(root, "Translation"));
            Assert.Contains("this warning line must appear", content);
            Assert.Contains("this error line must appear", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The detail a bug report from someone else's machine actually
    /// needs — a bare console message loses this the moment the window
    /// closes.</summary>
    [Fact]
    public void Error_WithException_AppendsTheExceptionsFullDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        try
        {
            Exception caught;
            try { throw new InvalidOperationException("sjpts test failure marker"); }
            catch (Exception ex) { caught = ex; }

            using (var trace = TraceLog.Open(root, "GenerateDsdFile"))
            {
                trace.Error("something failed", caught);
            }

            var content = File.ReadAllText(TracePath(root, "GenerateDsdFile"));
            Assert.Contains("something failed", content);
            Assert.Contains("sjpts test failure marker", content);
            Assert.Contains("InvalidOperationException", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Dispose_WritesAnEndLine()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        try
        {
            var trace = TraceLog.Open(root, "PickUpTarget");
            trace.Dispose();

            var content = File.ReadAllText(TracePath(root, "PickUpTarget"));
            Assert.Contains("end (elapsed", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Each line is tagged with the calling method/file
    /// ([CallerMemberName]/[CallerFilePath]) — that's what lets someone find
    /// where in the code a given trace line came from.</summary>
    [Fact]
    public void Write_TagsEachLineWithTheCallingMethodAndFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        try
        {
            using (var trace = TraceLog.Open(root, "PickUpTarget"))
            {
                trace.Info("marker line");
            }

            var content = File.ReadAllText(TracePath(root, "PickUpTarget"));
            Assert.Contains($"{nameof(TraceLogTests)}.{nameof(Write_TagsEachLineWithTheCallingMethodAndFileName)}: marker line", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The SKYRIMJPSP_LOG_LEVEL environment variable overrides the
    /// default (Info) when no explicit minLevel is passed to Open() — the
    /// same override PickUpTargetRunner.exe's real CLI entry point relies on
    /// for on-demand verbose logging.</summary>
    [Fact]
    public void Open_WithoutExplicitMinLevel_HonorsTheEnvironmentVariableOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("SKYRIMJPSP_LOG_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LEVEL", "Debug");

            using (var trace = TraceLog.Open(root, "PickUpTarget"))
            {
                trace.Debug("debug line should appear because the env var raised the floor to Debug");
            }

            var content = File.ReadAllText(TracePath(root, "PickUpTarget"));
            Assert.Contains("debug line should appear because the env var raised the floor to Debug", content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LEVEL", previous);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Open_WithAnUnrecognizedEnvironmentVariableValue_FallsBackToInfo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_tracelog_{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("SKYRIMJPSP_LOG_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LEVEL", "NotARealLevel");

            using (var trace = TraceLog.Open(root, "PickUpTarget"))
            {
                trace.Debug("must not appear (Debug is below the Info fallback)");
                trace.Info("must appear");
            }

            var content = File.ReadAllText(TracePath(root, "PickUpTarget"));
            Assert.DoesNotContain("must not appear", content);
            Assert.Contains("must appear", content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SKYRIMJPSP_LOG_LEVEL", previous);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
