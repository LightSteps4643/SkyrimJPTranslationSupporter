using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

public class CliOutputLineClassifierTests
{
    [Fact]
    public void Classify_PlainLine_ReturnsPlainWithOriginalText()
    {
        var result = CliOutputLineClassifier.Classify("some ordinary log line");
        Assert.Equal(CliOutputLineKind.Plain, result.Kind);
        Assert.Equal("some ordinary log line", result.Payload);
    }

    [Fact]
    public void Classify_IssuesLine_ReturnsIssuesLineWithFullOriginalLine()
    {
        const string line = "##SJPTS_ISSUES## plugins=1 fields=0 fail_open=0 context_only=0";
        var result = CliOutputLineClassifier.Classify(line);
        Assert.Equal(CliOutputLineKind.IssuesLine, result.Kind);
        // Payload is the FULL line, unstripped — matches the original
        // pre-2026-09-12 RunCliAsync behavior (FormatIssuesSummary's own
        // token=count parsing tolerates the leading marker as harmless noise).
        Assert.Equal(line, result.Payload);
    }

    [Fact]
    public void Classify_IssuesPluginsLine_ReturnsPayloadWithPrefixStrippedAndTrimmed()
    {
        var result = CliOutputLineClassifier.Classify("##SJPTS_ISSUES_PLUGINS## PluginA.esp|PluginB.esp ");
        Assert.Equal(CliOutputLineKind.IssuesPluginsLine, result.Kind);
        Assert.Equal("PluginA.esp|PluginB.esp", result.Payload);
    }

    [Fact]
    public void Classify_ErrorLine_ReturnsPayloadWithPrefixStripped()
    {
        var result = CliOutputLineClassifier.Classify("[error] MO2 instance not found");
        Assert.Equal(CliOutputLineKind.ErrorLine, result.Kind);
        Assert.Equal("MO2 instance not found", result.Payload);
    }

    /// <summary>The two ISSUES prefixes must be checked longest-first — a line
    /// starting with the PLUGINS variant also starts with the plain variant as
    /// a string, so checking the plain one first would misclassify it.</summary>
    [Fact]
    public void Classify_IssuesPluginsLine_IsNotMisclassifiedAsPlainIssuesLine()
    {
        var result = CliOutputLineClassifier.Classify("##SJPTS_ISSUES_PLUGINS## SomePlugin.esp");
        Assert.Equal(CliOutputLineKind.IssuesPluginsLine, result.Kind);
    }
}
