using SkyrimJPStringPatcher.SkyPatcher;

namespace SkyrimJPStringPatcher.Tests.SkyPatcher;

/// <summary>
/// Which SkyPatcher name strings are NOT offered for translation. SkyPatcher renames exist to be
/// displayed, so the plugin side's "looks like internal data" exclusions (internal identifiers,
/// AudioTemplate…, "Do Not Delete…", " fx", "temp") do not apply — they would leave visible names
/// in English. Only strings that cannot be translated, or would break if translated, are left out:
/// already Japanese, no letters at all, markup only, a "$Key" interface-translation reference,
/// an asset path, a placeholder token, an all-consonant acronym. Each exclusion carries a reason
/// code so the excluded list can say why.
/// </summary>
public class SkyPatcherExclusionTests
{
    [Theory]
    [InlineData("Fur Coat")]
    [InlineData("* Horn of Jurgen Windcaller")]
    [InlineData("Ingot - Iron")]
    [InlineData("Berserker's Fur Armor")]
    // Plugin-side exclusions that must NOT apply here (they are visible names):
    [InlineData("AudioTemplateFoo")]
    [InlineData("Do Not Delete This")]
    [InlineData("MyInternalName")]
    [InlineData("Glow fx")]
    [InlineData("Temp Ring")]
    public void NamesThatWillBeDisplayed_AreOffered(string text)
    {
        Assert.Null(SkyPatcherExclusion.Reason(text));
    }

    [Theory]
    [InlineData("毛皮のコート", SkyPatcherExclusion.AlreadyJapanese)]
    [InlineData("Fur コート", SkyPatcherExclusion.AlreadyJapanese)]
    [InlineData("1234", SkyPatcherExclusion.NoLetters)]
    [InlineData("***", SkyPatcherExclusion.NoLetters)]
    [InlineData("", SkyPatcherExclusion.NoLetters)]
    [InlineData("<font color='#ff0000'></font>", SkyPatcherExclusion.MarkupOnly)]
    [InlineData("$TNG_TCT", SkyPatcherExclusion.InterfaceKeyReference)]
    [InlineData("Textures\\Armor\\Foo.dds", SkyPatcherExclusion.AssetPath)]
    [InlineData("xxx", SkyPatcherExclusion.PlaceholderToken)]
    [InlineData("TBD", SkyPatcherExclusion.PlaceholderToken)]
    [InlineData("YMMP", SkyPatcherExclusion.NonWordAcronym)]
    public void StringsThatCannotBeTranslatedOrWouldBreak_AreLeftOut_WithTheirReason(string text, string reason)
    {
        Assert.Equal(reason, SkyPatcherExclusion.Reason(text));
    }
}
