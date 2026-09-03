using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTarget/ExtraTranslatableFields.cs's normal-case ("面"/breadth)
/// coverage: a hand-written type switch mapping 15 record types (16 counting
/// Book's two fields) to their DSD type string. Each case shares the same
/// trivial shape (yield the DsdType + the field), so the real risk isn't the
/// mechanism (already covered by known issue 21.'s SafeForEach/PickUpTargetRunner
/// tests) but a wrong DsdType string or wrong field mapped for one specific
/// record type — exactly what this test catches.
///
/// Fixtures/PickUpTarget/ExtraFieldsTest.esp bundles one record of each type
/// into a single plugin (amortizing ESP-construction cost across all of them,
/// rather than one fixture per type) — built the same way as known issue 21.'s
/// fixture (Mutagen itself, via a throwaway generator script not kept in the
/// repo). Text values are realistic display sentences with spaces — an
/// EditorID-style value with no spaces (the first attempt used e.g.
/// "ArmoDescText") trips PickUpTargetRunner's own
/// NonTranslatableText.LooksLikeInternalIdentifier heuristic and gets
/// excluded, which is itself a useful thing this test setup surfaced.
/// </summary>
public class ExtraTranslatableFieldsTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "ExtraFieldsTest.esp"),
            Path.Combine(modDir, "ExtraFieldsTest.esp"));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*ExtraFieldsTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_ExtraFieldsFixture_ProducesEveryRecordTypesCandidateWithTheRightDsdType()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_extrafields_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var expected = new (string DsdType, string Text)[]
            {
                ("ACTI RNAM", "Open the Chest"),
                ("FLOR RNAM", "Pick the Flower"),
                ("LSCR DESC", "A weathered old altar stands in the ruins."),
                ("MGEF DNAM", "Deals frost damage over time."),
                ("WOOP TNAM", "Fire Breath"),
                ("BOOK DESC", "Once upon a time, in the land of Skyrim..."),
                ("BOOK CNAM", "A dusty old tome."),
                ("AMMO DESC", "A finely crafted arrow."),
                ("ARMO DESC", "Sturdy armor forged from iron."),
                ("WEAP DESC", "A simple but reliable blade."),
                ("SPEL DESC", "Summons a ward of protective magic."),
                ("SCRL DESC", "A scroll bearing a powerful enchantment."),
                ("SHOU DESC", "Calls forth the power of the ancient dragons."),
                ("RACE DESC", "A proud and ancient people of the north."),
                ("MESG DESC", "Are you sure you want to proceed?"),
                ("PERK DESC", "Grants greater skill with one-handed weapons."),
                ("NPC_ SHRT", "The Wanderer"),
            };

            Assert.Equal(expected.Length, result.Candidates.Count);
            foreach (var (dsdType, text) in expected)
            {
                var candidate = Assert.Single(result.Candidates, c => c.RecordType == dsdType);
                Assert.Equal(text, candidate.CurrentText);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// v0.59.x: GitHub issue #2 (kinchanramen) — BOOK's two DSD-mapped fields
    /// were swapped from day one. Cross-checked against three independent
    /// sources: (1) Mutagen's own record definition
    /// (Mutagen.Bethesda.Skyrim/Records/Major Records/Book.xml on GitHub) —
    /// &lt;String name="BookText" recordType="DESC" .../&gt; and
    /// &lt;String name="Description" recordType="CNAM" .../&gt;; (2) DSD's own
    /// documentation (docs/modules/ROOT/pages/index.adoc on
    /// SkyHorizon3/SSE-Dynamic-String-Distributor) — lists "BOOK CNAM" among
    /// its flat/short "no index required" fields, and its own worked example
    /// for "type": "BOOK DESC" is a multi-paragraph in-character letter (long
    /// body text); (3) the issue reporter's own real-data test. All three
    /// agree: DESC = the book's actual body (Mutagen's BookText), CNAM = the
    /// separate short description (Mutagen's Description) — the OPPOSITE of
    /// what ExtraTranslatableFields.cs currently yields.
    ///
    /// This is a pure behavior/black-box test — it goes through
    /// PickUpTargetRunner.Run exactly like the test above, asserting only
    /// input (the fixture ESP's field values) vs. output (which DSD type
    /// string each value ends up labeled as), with no reference to
    /// ExtraTranslatableFields.cs's internals. It reuses the SAME fixture
    /// (Fixtures/PickUpTarget/ExtraFieldsTest.esp) as the test above — no new
    /// binary fixture needed, since that ESP's Book record already has
    /// semantically-differentiated values in each field (a short
    /// description-shaped sentence in Description, a long narrative-shaped
    /// one in BookText — reverse-engineered from the test above's expected
    /// values BEFORE this fix, back when they still encoded the swapped/buggy
    /// mapping). Deliberately confirmed red against today's (pre-fix) code —
    /// this test's own failure is the point: it demonstrates the wrong
    /// behavior objectively, ahead of the fix, so the fix can be verified
    /// against a test that was written and confirmed red BEFORE the change,
    /// not authored to already fit it. Overlaps with the test above (now that
    /// it's been corrected too) — kept as its own focused test since it
    /// carries this issue's full evidence trail as a standalone doc comment.
    /// </summary>
    [Fact]
    public void Run_ExtraFieldsFixture_BookDescIsTheBodyText_BookCnamIsTheShortDescription()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_extrafields_book_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var bookDesc = Assert.Single(result.Candidates, c => c.RecordType == "BOOK DESC");
            var bookCnam = Assert.Single(result.Candidates, c => c.RecordType == "BOOK CNAM");

            Assert.Equal("Once upon a time, in the land of Skyrim...", bookDesc.CurrentText);
            Assert.Equal("A dusty old tome.", bookCnam.CurrentText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
