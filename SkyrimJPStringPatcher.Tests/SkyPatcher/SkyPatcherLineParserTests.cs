using SkyrimJPStringPatcher.SkyPatcher;

namespace SkyrimJPStringPatcher.Tests.SkyPatcher;

/// <summary>
/// Requirements for reading one SkyPatcher .ini line (SkyPatcher grammar, §2/§3/§5/§6):
///  - a line is a patch string of ':'-separated segments; a segment is a filter
///    (filterBy…, restrictTo…, hasPlugins/hasPluginsOr, …Excluded) or an operation;
///  - a comment starts with ';';
///  - a rename is the operation "fullName=~New Name~" (NPCs also "shortName=~Short~"): the
///    name is wrapped in ~…~;
///  - what we keep of a line for our own output is its filter part (so the same records are
///    targeted) and its name operations — every other operation is dropped, since writing
///    them again would re-apply them after the mods that own them.
/// </summary>
public class SkyPatcherLineParserTests
{
    [Fact]
    public void RenameLine_YieldsItsFilterAndItsName()
    {
        var line = SkyPatcherLineParser.Parse("filterByArmors=Skyrim.esm|15516:fullName=~* Emperor's Robes~", 47);

        Assert.NotNull(line);
        Assert.Equal(47, line!.LineNumber);
        Assert.Equal(["filterByArmors=Skyrim.esm|15516"], line.FilterSegments);
        Assert.Equal("filterByArmors=Skyrim.esm|15516", line.FilterPart);
        var op = Assert.Single(line.NameOps);
        Assert.Equal(SkyPatcherNameKind.FullName, op.Kind);
        Assert.Equal("* Emperor's Robes", op.Text);
    }

    [Fact]
    public void SpacesAroundTheSegmentSeparator_AreTolerated()
    {
        // Grammar §3 writes patch strings as "filter1=val1 : filter2=val2 : op1=val1".
        var line = SkyPatcherLineParser.Parse("filterByArmors=ArmorBanditCuirass : fullName=~Fur Coat~", 1);

        Assert.NotNull(line);
        Assert.Equal(["filterByArmors=ArmorBanditCuirass"], line!.FilterSegments);
        Assert.Equal("Fur Coat", Assert.Single(line.NameOps).Text);
    }

    [Fact]
    public void ANameThatContainsTheSegmentSeparatorOrAComma_IsOneValue()
    {
        // The name is wrapped in ~…~ (grammar §7), so ':' and ',' inside it are part of the name.
        var line = SkyPatcherLineParser.Parse("filterByMiscs=Dragonborn.esm|1CAC0:fullName=~Amethyst Claw: Left, Half~", 1);

        Assert.Equal("Amethyst Claw: Left, Half", Assert.Single(line!.NameOps).Text);
        Assert.Equal(["filterByMiscs=Dragonborn.esm|1CAC0"], line.FilterSegments);
    }

    [Fact]
    public void OtherOperations_AreDropped_OtherFilterFamiliesAreKept()
    {
        var line = SkyPatcherLineParser.Parse(
            "filterByArmors=A.esp|1,A.esp|2:hasPlugins=Foo.esp:restrictToKeywords=ArmorHeavy:weight=1.5:fullName=~X~:keywordsToAdd=a.esp|123", 1);

        Assert.Equal(
            ["filterByArmors=A.esp|1,A.esp|2", "hasPlugins=Foo.esp", "restrictToKeywords=ArmorHeavy"],
            line!.FilterSegments);
        Assert.Equal("X", Assert.Single(line.NameOps).Text);
    }

    [Theory]
    [InlineData("filterByMgefs=Skyrim.esm|397E", "modNamesLastOverriddenExcluded=Foo.esp")]   // Magic Effect: skip when last overridden by a named mod
    [InlineData("filterByMgefs=Skyrim.esm|397E", "effectShadersExcluded=Skyrim.esm|1")]      // Magic Effect
    [InlineData("filterByNpcs=Skyrim.esm|7", "hasPluginsOr=A.esp,B.esp")]                      // line gate, OR form
    public void EveryDocumentedFilterShape_IsKeptInTheFilterPart(string primary, string extraFilter)
    {
        var line = SkyPatcherLineParser.Parse($"{primary}:{extraFilter}:fullName=~X~", 1);

        Assert.Equal([primary, extraFilter], line!.FilterSegments);
    }

    [Fact]
    public void NpcLineWithBothNames_YieldsBothOps_InOrder()
    {
        var line = SkyPatcherLineParser.Parse("filterByNpcs=Skyrim.esm|7:fullName=~Alpha~:shortName=~Beta~", 1);

        Assert.Equal(
            [(SkyPatcherNameKind.FullName, "Alpha"), (SkyPatcherNameKind.ShortName, "Beta")],
            line!.NameOps.Select(o => (o.Kind, o.Text)).ToArray());
    }

    [Fact]
    public void ACompoundOperationUsingTildesBeforeTheName_DoesNotConfuseTheNameValue()
    {
        // "~" also separates sub-arguments of other operations (grammar §7); only the value right
        // after fullName= is a ~…~ wrapped name.
        var line = SkyPatcherLineParser.Parse(
            "filterByArmors=A.esp|1:alternateTexturesToAdd=TextureSet~Name3D~2:fullName=~X:Y~", 1);

        Assert.Equal("X:Y", Assert.Single(line!.NameOps).Text);
        Assert.Equal(["filterByArmors=A.esp|1"], line.FilterSegments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("; fullName=~Commented Out~")]
    [InlineData("  ; indented comment")]
    [InlineData("filterByArmors=A.esp|1:weight=2")]          // no name operation: nothing for us to translate
    [InlineData("filterByArmors=A.esp|1:fullName=~Unterminated")]   // the closing ~ is missing: not a rename we can read
    public void LinesWithoutAReadableRename_YieldNothing(string text)
    {
        Assert.Null(SkyPatcherLineParser.Parse(text, 1));
    }

    [Fact]
    public void ALineWithoutAnyFilter_HasAnEmptyFilterPart()
    {
        // Grammar §5: no filter set -> every record of the type is patched. Kept as written.
        var line = SkyPatcherLineParser.Parse("fullName=~Everything~", 1);

        Assert.Empty(line!.FilterSegments);
        Assert.Equal("", line.FilterPart);
    }
}
