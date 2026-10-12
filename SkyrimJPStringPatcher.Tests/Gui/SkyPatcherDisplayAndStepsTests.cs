using SkyrimJPStringPatcher.SkyPatcher;
using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcher.Tests.Gui;

/// <summary>
/// What the SkyPatcher detail window shows (Japanese reasons for a row that cannot take effect / is not
/// translatable, a readable target) and how the common buttons chain the plugin and SkyPatcher runs (the
/// plugin run first; when a step fails, the later ones do not run — the SkyPatcher run uses the plugin
/// side's finished translations as hints, so it must not go on after a failed plugin run).
/// </summary>
public class SkyPatcherDisplayAndStepsTests
{
    [Fact]
    public void EveryReasonTheScannerCanGive_HasAJapaneseLabel()
    {
        var inactive = new[]
        {
            SkyPatcherInactive.TypeDisabled, SkyPatcherInactive.FileGate, SkyPatcherInactive.LineGate,
            SkyPatcherInactive.TargetPluginMissing, SkyPatcherInactive.OverriddenInSameFile,
        };
        var excluded = new[]
        {
            SkyPatcherExclusion.AlreadyJapanese, SkyPatcherExclusion.NoLetters, SkyPatcherExclusion.MarkupOnly,
            SkyPatcherExclusion.InterfaceKeyReference, SkyPatcherExclusion.AssetPath, SkyPatcherExclusion.PlaceholderToken,
            SkyPatcherExclusion.NonWordAcronym,
        };

        // A label that is just the code itself would show an English identifier to the user.
        foreach (var code in inactive) Assert.NotEqual(code, SkyPatcherDisplay.ReasonLabel(code, ""));
        foreach (var code in excluded) Assert.NotEqual(code, SkyPatcherDisplay.ReasonLabel("", code));
    }

    [Fact]
    public void ARowThatCannotTakeEffect_IsExplainedByThat_EvenWhenItIsAlsoNotTranslatable()
    {
        var label = SkyPatcherDisplay.ReasonLabel(SkyPatcherInactive.LineGate, SkyPatcherExclusion.AlreadyJapanese);

        Assert.Equal(SkyPatcherDisplay.ReasonLabel(SkyPatcherInactive.LineGate, ""), label);
        Assert.Equal("", SkyPatcherDisplay.ReasonLabel("", ""));
    }

    [Fact]
    public void AnUnknownCode_IsShownAsIs_NotHidden()
    {
        Assert.Equal("SomethingNew", SkyPatcherDisplay.ReasonLabel("SomethingNew", ""));
    }

    [Theory]
    [InlineData("filterByArmors=Skyrim.esm|15516", "filterByArmors=Skyrim.esm|15516")]
    [InlineData("filterByArmors=Skyrim.esm|1,Skyrim.esm|2", "filterByArmors=Skyrim.esm|1,Skyrim.esm|2（2件）")]
    [InlineData("filterByArmors=A.esp|1,A.esp|2,A.esp|3:hasPlugins=A.esp", "filterByArmors=A.esp|1,A.esp|2,A.esp|3:hasPlugins=A.esp（3件）")]
    [InlineData("filterByNpcs=Skyrim.esm|7:hasPluginsOr=A.esp,B.esp", "filterByNpcs=Skyrim.esm|7:hasPluginsOr=A.esp,B.esp")]   // the count is of the first filter's targets only
    [InlineData("", "（フィルターなし）")]
    public void TheTarget_IsTheFilterAsWritten_WithTheNumberOfRecordsWhenSeveral(string filter, string expected)
    {
        Assert.Equal(expected, SkyPatcherDisplay.TargetLabel(filter));
    }

    // ---- the window title: how many rows are the work, and why the others are not ------------

    [Fact]
    public void Title_NamesTheRowsToTranslate_AndWhyTheOthersAreNot_MostFirst()
    {
        var notTranslated = new[]
        {
            ("TargetPluginMissing", ""), ("TargetPluginMissing", ""), ("TargetPluginMissing", ""),
            ("OverriddenInSameFile", ""),
            ("", "InterfaceKeyReference"), ("", "AlreadyJapanese"),
        };

        Assert.Equal("翻訳対象 9件（対象のプラグインなし 3件、翻訳できない文字列 2件、後の行に上書きされる 1件）",
            SkyPatcherDisplay.Title(9, notTranslated));
    }

    [Fact]
    public void Title_WithNothingLeftOut_IsJustTheCount()
    {
        Assert.Equal("翻訳対象 4件", SkyPatcherDisplay.Title(4, []));
    }

    [Fact]
    public void Title_ARowThatCannotTakeEffect_IsCountedByThat_NotAlsoAsUntranslatable()
    {
        // The same precedence as the row's own reason: not taking effect comes first.
        Assert.Equal("翻訳対象 0件（行ゲート不成立 1件）", SkyPatcherDisplay.Title(0, [("LineGate", "AlreadyJapanese")]));
    }

    [Fact]
    public void Title_AnUnknownReason_IsShownAsIs()
    {
        Assert.Equal("翻訳対象 1件（SomethingNew 1件）", SkyPatcherDisplay.Title(1, [("SomethingNew", "")]));
    }

    [Fact]
    public void EveryReasonLabel_UsesThePlainWordingNotTheOldValidInvalidOne()
    {
        // The window no longer talks about 有効/無効/対象外 — it says what is translated and what is not.
        foreach (var code in new[] { "TypeDisabled", "FileGate", "LineGate", "TargetPluginMissing", "OverriddenInSameFile" })
            Assert.DoesNotContain("無効の", SkyPatcherDisplay.ReasonLabel(code, ""));
        foreach (var code in new[] { "AlreadyJapanese", "NoLetters", "MarkupOnly", "InterfaceKeyReference", "AssetPath", "PlaceholderToken", "NonWordAcronym" })
            Assert.DoesNotContain("対象外", SkyPatcherDisplay.ReasonLabel("", code));
    }

    [Fact]
    public async Task Steps_RunInOrder_AndAllSucceedWhenEachDoes()
    {
        var order = new List<string>();

        var ok = await StepRunner.RunUntilFailureAsync(
        [
            () => { order.Add("plugin"); return Task.FromResult(true); },
            () => { order.Add("skypatcher"); return Task.FromResult(true); },
        ]);

        Assert.True(ok);
        Assert.Equal(["plugin", "skypatcher"], order);
    }

    [Fact]
    public async Task AFailedStep_StopsTheLaterOnes()
    {
        var order = new List<string>();

        var ok = await StepRunner.RunUntilFailureAsync(
        [
            () => { order.Add("plugin"); return Task.FromResult(false); },
            () => { order.Add("skypatcher"); return Task.FromResult(true); },
        ]);

        Assert.False(ok);
        Assert.Equal(["plugin"], order);
    }

    [Fact]
    public async Task NoSteps_IsSuccess()
    {
        Assert.True(await StepRunner.RunUntilFailureAsync([]));
    }
}
