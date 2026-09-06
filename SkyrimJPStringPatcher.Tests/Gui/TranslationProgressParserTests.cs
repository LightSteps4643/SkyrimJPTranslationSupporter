using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

public class TranslationProgressParserTests
{
    [Fact]
    public void TryParsePluginCompleted_NormalTargetLine_ExtractsPluginName()
    {
        var ok = TranslationProgressParser.TryParsePluginCompleted(
            "Target: Sentinel.esp (12 candidates, 8 resolved (①〜⑥))", out var plugin);

        Assert.True(ok);
        Assert.Equal("Sentinel.esp", plugin);
    }

    [Fact]
    public void TryParsePluginCompleted_PluginNameContainingParentheses_ExtractsFullName()
    {
        var ok = TranslationProgressParser.TryParsePluginCompleted(
            "Target: [Caenarvon] Cosplay Pack Gala.esp (413 candidates, 0 resolved (①〜⑥))", out var plugin);

        Assert.True(ok);
        Assert.Equal("[Caenarvon] Cosplay Pack Gala.esp", plugin);
    }

    [Theory]
    [InlineData("Done. Resolved (①〜⑥): 100 / 200")]
    [InlineData("No candidates found for 'Missing.esp' in candidates.tsv.")]
    [InlineData("Generating Translation prompt packages for 3 plugin(s)...")]
    [InlineData("")]
    public void TryParsePluginCompleted_UnrelatedLine_ReturnsFalse(string line)
    {
        var ok = TranslationProgressParser.TryParsePluginCompleted(line, out var plugin);

        Assert.False(ok);
        Assert.Equal("", plugin);
    }
}
