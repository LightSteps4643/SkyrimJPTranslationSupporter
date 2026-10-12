using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// FullNameFields.For: the name ("FULL") of a record, offered under its DSD
/// type. Requirements (from DSD's getTranslationType() and Mutagen's record
/// definitions — see DsdTypeMatrix/DsdTypeSpec for the sources):
///  - every record kind DSD routes to kFullName offers its Name as "&lt;SIG&gt; FULL",
///    including Key, Class and Flora, whose Name is a translated string in
///    Mutagen although those record types lack the generic
///    ITranslatedNamedGetter aspect (they were silently skipped before);
///  - a record without a name offers nothing;
///  - a record kind DSD has no FULL type for offers nothing, even when it has a name.
///
/// FullNameFields.IsSupportedButUnhandled: the guard that makes a future
/// omission loud — a record of a DSD-supported signature that this tool has no
/// way to read the name of must be reported rather than silently dropped.
/// </summary>
public class FullNameFieldsTests
{
    private static SkyrimMod NewMod() =>
        new(ModKey.FromNameAndExtension("SjptsFullNameFields.esp"), SkyrimRelease.SkyrimSE);

    [Fact]
    public void Key_OffersItsNameAsKeymFull()
    {
        var key = NewMod().Keys.AddNew();
        key.Name = "Key to Shoal's Rest Farm";

        var field = Assert.Single(FullNameFields.For(key));

        Assert.Equal("KEYM FULL", field.DsdType);
        Assert.Equal("Key to Shoal's Rest Farm", field.Field.String);
    }

    [Fact]
    public void Class_OffersItsNameAsClasFull()
    {
        var clas = NewMod().Classes.AddNew();
        clas.Name = "Skeleton Archer Class";

        var field = Assert.Single(FullNameFields.For(clas));

        Assert.Equal("CLAS FULL", field.DsdType);
        Assert.Equal("Skeleton Archer Class", field.Field.String);
    }

    [Fact]
    public void Flora_OffersItsNameAsFlorFull()
    {
        var flora = NewMod().Florae.AddNew();
        flora.Name = "Chickens' Nest";

        var field = Assert.Single(FullNameFields.For(flora));

        Assert.Equal("FLOR FULL", field.DsdType);
        Assert.Equal("Chickens' Nest", field.Field.String);
    }

    [Fact]
    public void OrdinaryRecord_StillOffersItsNameThroughTheGenericPath()
    {
        var armor = NewMod().Armors.AddNew();
        armor.Name = "Steel Cuirass";

        var field = Assert.Single(FullNameFields.For(armor));

        Assert.Equal("ARMO FULL", field.DsdType);
        Assert.Equal("Steel Cuirass", field.Field.String);
    }

    [Fact]
    public void RecordsWithoutAName_OfferNoText()
    {
        // A record built in memory carries an EMPTY translated string where a plugin read from disk carries none
        // at all; either way there is no text to offer. (That such a record never becomes a candidate is checked
        // end to end by DsdTypeMatrixTests.RecordsWithoutText_ProduceNoCandidate.)
        var mod = NewMod();
        foreach (var record in new IMajorRecordGetter[] { mod.Keys.AddNew(), mod.Classes.AddNew(), mod.Florae.AddNew(), mod.Armors.AddNew() })
            Assert.All(FullNameFields.For(record), f => Assert.True(string.IsNullOrEmpty(f.Field.String)));
    }

    [Fact]
    public void RecordKindDsdHasNoFullTypeFor_OffersNothingEvenWithAName()
    {
        // DSD has no FULL type for factions (not in getTranslationType()): the name could never reach the game.
        var faction = NewMod().Factions.AddNew();
        faction.Name = "Sample Faction Name";

        Assert.Empty(FullNameFields.For(faction));
    }

    // ---- the guard against silently dropped record kinds -------------------

    [Fact]
    public void IsSupportedButUnhandled_ASupportedSignatureWithNoWayToReadItsName_IsReported()
    {
        // MaterialType has no name at all, so there is no way to read one — pretend its signature were DSD-supported.
        var materialType = NewMod().MaterialTypes.AddNew();

        var unhandled = FullNameFields.IsSupportedButUnhandled(materialType, new HashSet<string> { "MATT" }, out var signature);

        Assert.True(unhandled);
        Assert.Equal("MATT", signature);
    }

    [Fact]
    public void IsSupportedButUnhandled_ASignatureDsdDoesNotSupport_IsNotReported()
    {
        var materialType = NewMod().MaterialTypes.AddNew();

        Assert.False(FullNameFields.IsSupportedButUnhandled(materialType, new HashSet<string> { "ACTI" }, out _));
    }

    [Fact]
    public void IsSupportedButUnhandled_RecordsWhoseNameCanBeRead_AreNotReported()
    {
        var mod = NewMod();
        Assert.False(FullNameFields.IsSupportedButUnhandled(mod.Keys.AddNew(), out _));
        Assert.False(FullNameFields.IsSupportedButUnhandled(mod.Classes.AddNew(), out _));
        Assert.False(FullNameFields.IsSupportedButUnhandled(mod.Florae.AddNew(), out _));
        Assert.False(FullNameFields.IsSupportedButUnhandled(mod.Armors.AddNew(), out _));
    }
}
