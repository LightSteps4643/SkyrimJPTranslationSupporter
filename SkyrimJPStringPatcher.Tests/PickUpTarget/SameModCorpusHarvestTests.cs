using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// PickUpTargetRunner.ScanTranslatableFields' "vanilla" corpus-harvesting
/// success path: when a SINGLE mod's SAME field carries BOTH an English and
/// a Japanese string simultaneously (a genuinely localized plugin — the same
/// shape vanilla Skyrim.esm itself uses, as opposed to a third-party mod's
/// single embedded string), that pair is harvested as corpus precedent. This
/// basic success case had no dedicated test before — only the currently-Skip
/// -marked cross-mod scenario (Integration/PickUpTargetTranslationCrossModTests)
/// existed, which is a different, still-unimplemented feature (pairing
/// English/Japanese contributed by TWO DIFFERENT mods to the same field).
///
/// Fixtures/PickUpTarget/SameModCorpusTest/SameModCorpusTest.esp is a
/// genuinely localized plugin (UsingLocalization=true) with its own loose
/// Strings/* files (built via Mutagen, not hand-encoded) defining two WEAP
/// records:
/// - "SjptsLocalizedWeapon": English "Steel Sword" AND Japanese "鋼の剣" on
///   the SAME field. Since a Japanese variant is present, this record's own
///   winning text IS Japanese, so it never becomes a candidate itself — the
///   payoff is entirely in whether the pair reaches the corpus.
/// - "SjptsUnresolvedWeapon": English "Steel Sword" only (no Japanese) — an
///   ordinary unresolved candidate whose text exactly matches the harvested
///   pair's English side, so AutoTranslator's ①完全一致 should resolve it
///   automatically from the same-mod-harvested precedent alone.
///
/// **KNOWN BUG found while writing this class (2026-08-28), fixed in v0.55.5**:
/// "SjptsUnresolvedWeapon" used to never become a candidate at all, because
/// PickUpTargetRunner.cs's ② FULL extraction pre-filtered on `named.Name`
/// (INamedGetter's plain-string convenience accessor). That accessor DOES fall
/// back to whatever's embedded when a NON-localized plugin lacks the target
/// language (confirmed) — but for a genuinely LOCALIZED plugin (this
/// fixture's shape) whose target-language variant is simply missing for one
/// field, `named.Name` used to return "" instead of falling back, so the
/// `!string.IsNullOrWhiteSpace(name)` guard silently dropped the record before
/// `Consider()` was ever called — no log entry, no candidate, no corpus
/// contribution. ③/④'s `Consider()` calls (ExtraTranslatableFields/
/// NestedTranslatableFields) pass the field object directly and were always
/// unaffected; only ②'s `named.Name` pre-filter had this gap. Fixed by
/// dropping the `named.Name` pre-filter entirely and passing the field object
/// straight to `Consider()`, matching ③/④ — `Consider()` already has its own
/// TryLookup-based Japanese-then-English fallback that behaves the same
/// regardless of whether the plugin is localized.
/// </summary>
public class SameModCorpusHarvestTests
{
    private static string BuildFakeMo2Instance(string root)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var stringsDir = Path.Combine(modDir, "Strings");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(stringsDir);
        Directory.CreateDirectory(profileDir);

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", "SameModCorpusTest");
        File.Copy(Path.Combine(fixtureDir, "SameModCorpusTest.esp"), Path.Combine(modDir, "SameModCorpusTest.esp"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fixtureDir, "Strings")))
            File.Copy(file, Path.Combine(stringsDir, Path.GetFileName(file)));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*SameModCorpusTest.esp\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_LocalizedFieldWithBothLanguages_HarvestsThePairAsVanillaCorpusPrecedent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_samemodcorpus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var corpusEntry = Assert.Single(result.Corpus, e => e.English == "Steel Sword");
            Assert.Equal("鋼の剣", corpusEntry.Japanese);
            Assert.Equal("vanilla", corpusEntry.SourceKind);
            Assert.Equal("WEAP FULL", corpusEntry.DsdType);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The already-Japanese record itself never becomes a
    /// candidate — its own text needs no translation. Only the SEPARATE
    /// unresolved record should appear as a candidate.</summary>
    [Fact]
    public void Run_TheLocalizedRecordItself_NeverBecomesACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_samemodcorpus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("Steel Sword", candidate.CurrentText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The actual end-to-end payoff: Translation's own ①完全一致
    /// resolves the unresolved candidate automatically, purely from the
    /// same-mod-harvested precedent — no DSD file, no AI call needed. Mirrors
    /// the (currently Skip-marked) cross-mod integration test's structure,
    /// but for the scenario that already works today.</summary>
    [Fact]
    public void Run_ThenTranslate_ResolvesTheUnresolvedRecordFromTheHarvestedPrecedent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_samemodcorpus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);
            var candidate = Assert.Single(result.Candidates);

            var autoTranslator = new AutoTranslator(result.Corpus);
            var resolved = autoTranslator.TryTranslate(candidate.CurrentText, candidate.RecordType);

            Assert.NotNull(resolved);
            Assert.Equal("鋼の剣", resolved!.Japanese);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
