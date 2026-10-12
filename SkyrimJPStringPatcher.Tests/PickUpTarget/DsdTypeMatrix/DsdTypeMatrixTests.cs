using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget.DsdTypeMatrix;

/// <summary>
/// Requirement: PickUpTarget must pick up EVERY string DSD can replace.
///
/// "Every string DSD can replace" = the 67 type strings in DSD's
/// getTranslationType() (<see cref="DsdTypeSpec"/>). For each one, a record of
/// that kind whose corresponding Mutagen field holds an English sentence must
/// come out of PickUpTargetRunner as a candidate carrying that exact DSD type,
/// FormKey and text (and, where DSD's own docs define the index, that index).
/// Conversely nothing may come out for fields DSD cannot apply, for fields
/// that hold no text, or under a type DSD does not know.
///
/// Background (2026-10-11/12): Key/Class/Flora names (KEYM/CLAS/FLOR FULL) and
/// map-marker names (REFR FULL) were silently never picked up although DSD
/// supports them. The earlier "audit" compared two lists of type NAMES, never
/// whether a real record of each type actually yields a candidate — these
/// tests close exactly that gap, from the specifications, not from this
/// tool's own output.
/// </summary>
public class DsdTypeMatrixTests : IClassFixture<DsdTypeMatrixFixture>
{
    private readonly DsdTypeMatrixFixture _fixture;

    public DsdTypeMatrixTests(DsdTypeMatrixFixture fixture) => _fixture = fixture;

    // ---- the specification itself is consistent ---------------------------

    [Fact]
    public void Spec_ListsDsds17TranslationTypesAnd67DistinctTypeStrings()
    {
        // Counts as of DSD's Manager.cpp (2026-10-12): 17 TranslationTypes, 67 type strings.
        Assert.Equal(17, DsdTypeSpec.TypesByTranslationType.Count);
        var all = DsdTypeSpec.AllTypes.ToList();
        Assert.Equal(67, all.Count);
        Assert.Equal(67, all.Distinct().Count());
        Assert.Equal(38, DsdTypeSpec.TypesByTranslationType["kFullName"].Length);
    }

    [Fact]
    public void Spec_EveryDsdTypeStringHasAMutagenFieldAndEveryMappedTypeIsADsdType()
    {
        var mapped = DsdTypeSpec.MutagenTranslatedFields.Values.Where(v => v != null).Select(v => v!).ToHashSet();
        var allDsd = DsdTypeSpec.AllTypes.ToHashSet();

        Assert.Empty(mapped.Except(allDsd));      // no mapping to a type DSD does not have
        Assert.Empty(allDsd.Except(mapped));      // every DSD type has a Mutagen field behind it
    }

    // ---- Mutagen side: no translated field is overlooked -------------------

    [Fact]
    public void Mutagen_EveryTranslatedStringPropertyIsAccountedForInTheSpec()
    {
        // Requirement: every ITranslatedStringGetter property anywhere in Mutagen's Skyrim records is either
        // mapped to a DSD type or explicitly listed as "DSD has no type for it". A new field appearing after
        // a Mutagen upgrade fails here instead of being silently ignored.
        var found = EnumerateTranslatedStringPaths();
        Assert.NotEmpty(found);
        var unaccounted = found.Where(p => !DsdTypeSpec.MutagenTranslatedFields.ContainsKey(p)).OrderBy(p => p).ToList();
        Assert.True(unaccounted.Count == 0,
            "Translated string properties in Mutagen that DsdTypeSpec does not account for: " + string.Join(", ", unaccounted));
    }

    private static HashSet<string> EnumerateTranslatedStringPaths()
    {
        const string skyrimNamespace = "Mutagen.Bethesda.Skyrim";
        var assembly = typeof(IKeyGetter).Assembly;
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        static bool IsMajorRecordGetter(Type t) =>
            t.IsInterface && t.Name.StartsWith('I') && t.Name.EndsWith("Getter", StringComparison.Ordinal)
            && t.GetInterfaces().Any(i => i == typeof(IMajorRecordGetter));

        var result = new HashSet<string>();

        void Walk(string record, string path, Type type, HashSet<Type> seen, int depth)
        {
            if (depth > 5) return;
            var properties = type.GetProperties()
                .Concat(type.GetInterfaces().Where(i => i.Namespace == skyrimNamespace).SelectMany(i => i.GetProperties()))
                .GroupBy(p => p.Name).Select(g => g.First());
            foreach (var property in properties)
            {
                var propertyPath = path.Length == 0 ? property.Name : path + "." + property.Name;
                var propertyType = property.PropertyType;
                if (propertyType == typeof(ITranslatedStringGetter))
                {
                    result.Add(record + "." + propertyPath);
                    continue;
                }
                var candidates = propertyType.IsGenericType ? propertyType.GetGenericArguments() : new[] { propertyType };
                foreach (var candidate in candidates)
                {
                    if (candidate.IsInterface && candidate.Namespace == skyrimNamespace
                        && candidate.Name.EndsWith("Getter", StringComparison.Ordinal)
                        && !IsMajorRecordGetter(candidate) && !seen.Contains(candidate))
                    {
                        Walk(record, propertyPath, candidate, new HashSet<Type>(seen) { candidate }, depth + 1);
                    }
                }
            }
        }

        foreach (var recordType in types.Where(t => t.Namespace == skyrimNamespace && IsMajorRecordGetter(t)))
        {
            var recordName = recordType.Name[1..^"Getter".Length];
            Walk(recordName, "", recordType, new HashSet<Type> { recordType }, 0);
        }
        return result;
    }

    // ---- PickUpTarget side: every DSD type string is picked up -------------

    /// <summary>DSD types whose records cannot be produced through a written-and-re-read plugin (a Mutagen
    /// limitation, see the in-memory tests below); each is verified by its own dedicated test.</summary>
    private static readonly string[] VerifiedInMemoryInstead = { "PERK EPF2" };

    public static IEnumerable<object[]> TypesVerifiedThroughThePluginRunner() =>
        DsdTypeSpec.AllTypes.Where(t => !VerifiedInMemoryInstead.Contains(t)).Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(TypesVerifiedThroughThePluginRunner))]
    public void EveryDsdTypeString_AnEnglishFieldOfThatKindProducesTheCandidate(string dsdType)
    {
        var expectations = _fixture.Expected.Where(e => e.DsdType == dsdType).ToList();
        Assert.True(expectations.Count > 0, $"test fixture bug: no record was built for DSD type '{dsdType}'");

        foreach (var expectation in expectations)
        {
            var matches = _fixture.Result.Candidates
                .Where(c => c.RecordType == dsdType && c.FormId == expectation.FormId && c.CurrentText == expectation.Text)
                .ToList();
            Assert.True(matches.Count == 1,
                $"Expected exactly one '{dsdType}' candidate for record {expectation.FormId} with text \"{expectation.Text}\", found {matches.Count}.");

            var candidate = matches[0];
            if (expectation.Index.HasValue)
                Assert.Equal(expectation.Index.Value, candidate.Index);
            if (expectation.EditorId != null)
                Assert.Equal(expectation.EditorId, candidate.EditorId);
        }
    }

    [Fact]
    public void PerkEntryPointButtonLabel_ProducesAPerkEpf2Candidate()
    {
        // DSD: "PERK EPF2" = the perk entry point's button label (Mutagen Perk.xml: Effects[].ButtonLabel -> EPF2).
        // The label cannot be written into a plugin and read back through Mutagen (a Mutagen limitation), so the
        // extraction is exercised on the in-memory record directly.
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("SjptsPerkInMemory.esp"), SkyrimRelease.SkyrimSE);
        var perk = mod.Perks.AddNew();
        var label = DsdTypeMatrixFixture.SampleText("PERK EPF2");
        perk.Effects.Add(new PerkEntryPointSetText { ButtonLabel = label });

        var fields = NestedTranslatableFields.For(perk).ToList();

        // The requirement is that the label is offered as a PERK EPF2 candidate. (It is not "exactly one": the tool
        // also offers the same text under PERK EPFD because Mutagen's definition does not say which of DSD's two
        // types holds which field — documented in NestedTranslatableFields.)
        Assert.Contains(fields, f => f.DsdType == "PERK EPF2" && f.Field?.String == label);
    }

    [Fact]
    public void EveryDsdTypeVerifiedInMemoryInstead_IsARealDsdType()
    {
        Assert.All(VerifiedInMemoryInstead, t => Assert.Contains(t, DsdTypeSpec.AllTypes));
    }

    // ---- nothing is produced where it must not be --------------------------

    [Fact]
    public void FieldsDsdCannotApply_ProduceNoCandidate()
    {
        // DSD has no type for these translated fields; a candidate would be translated for nothing
        // (the output could never reach the game).
        Assert.NotEmpty(_fixture.UnsupportedTexts);
        var leaked = _fixture.Result.Candidates
            .Where(c => _fixture.UnsupportedTexts.Contains(c.CurrentText))
            .Select(c => $"{c.RecordType}: \"{c.CurrentText}\"")
            .ToList();
        Assert.True(leaked.Count == 0, "Candidates were produced for fields DSD cannot apply: " + string.Join("; ", leaked));
    }

    [Fact]
    public void NoCandidateCarriesATypeDsdDoesNotKnow()
    {
        var dsdTypes = DsdTypeSpec.AllTypes.ToHashSet();
        var unknown = _fixture.Result.Candidates.Select(c => c.RecordType).Where(t => !dsdTypes.Contains(t)).Distinct().ToList();
        Assert.True(unknown.Count == 0, "Candidates with types DSD does not support: " + string.Join(", ", unknown));
    }

    [Fact]
    public void RecordsWithoutText_ProduceNoCandidate()
    {
        Assert.NotEmpty(_fixture.NoTextRecords);
        foreach (var (why, formId) in _fixture.NoTextRecords)
        {
            var produced = _fixture.Result.Candidates.Where(c => c.FormId == formId).Select(c => c.RecordType).ToList();
            Assert.True(produced.Count == 0, $"A candidate was produced for a {why}: {string.Join(", ", produced)}");
        }
    }

    // ---- candidates that share a record stay distinct ----------------------

    [Fact]
    public void QuestLogEntriesOfOneQuest_AreDistinctCandidates()
    {
        // DSD matches QUST CNAM by the original text at runtime (its one exception), so no particular index is
        // required — but two log entries of one quest must not collapse into one candidate.
        var entries = _fixture.Result.Candidates.Where(c => c.RecordType == "QUST CNAM").ToList();
        Assert.Equal(2, entries.Count);
        Assert.NotEqual(entries[0].Index, entries[1].Index);
    }
}
