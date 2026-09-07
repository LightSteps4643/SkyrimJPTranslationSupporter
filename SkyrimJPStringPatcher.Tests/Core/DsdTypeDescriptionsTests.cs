using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

/// <summary>
/// 2026-09-07: DsdTypeDescriptions had no test coverage at all. Adding a
/// focused case for AVIF DESC alongside its fix (ExtraTranslatableFields.cs
/// was missing the AVIF case entirely; Signatures here was separately missing
/// an "AVIF" entry, which made Describe("AVIF DESC") silently return null --
/// no prompt annotation -- even once the scan-side fix landed).
/// </summary>
public class DsdTypeDescriptionsTests
{
    [Fact]
    public void Describe_AvifDesc_ComposesFromSignatureAndSubrecord()
    {
        var description = DsdTypeDescriptions.Describe("AVIF DESC");

        Assert.Equal("the description of a game skill (e.g. Smithing, Destruction, Speech)", description);
    }
}
