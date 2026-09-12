using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// 2026-09-12: real-data bug — some mods store a bare Interface\Translations
/// key reference (e.g. "$TNG_TCT", "$DAK_Trade") directly in an ESP field
/// (an ARMO's own FULL name, a PERK's activation-prompt EPF2/EPFD) instead of
/// English text, relying on the game resolving the "$Key" at runtime via the
/// Interface\Translations file — the same convention SJPTS_InterfaceText.exe
/// targets separately. Confirmed against real data: TheNewGentleman.esp's
/// ARMO FULL ("$TNG_TCT"/"$TNG_TMT"/"$TNG_TRT") and Dynamic Activation Key -
/// Addons Collection.esp's PERK EPF2/EPFD ("$DAK_Trade" etc., mixed case).
///
/// Before this fix, PickUpTargetRunner has no exclusion for this shape, so it
/// reaches the translation pipeline as an ordinary candidate; translating it
/// would corrupt the exact-match text the game's runtime key lookup depends
/// on, permanently breaking the dynamic name/prompt resolution for that
/// record. This is a pure behavior/black-box test — like
/// ExtraTranslatableFieldsTests, it drives PickUpTargetRunner.Run end-to-end
/// on a throwaway Mutagen-built fixture and asserts only on
/// result.Candidates, with no reference to NonTranslatableText's internals.
/// Deliberately confirmed red against today's (pre-fix) code before the fix
/// lands.
/// </summary>
public class InterfaceTranslationKeyReferenceExclusionTests
{
    private static string BuildFakeMo2Instance(string root, SkyrimMod mod, string plugin)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(mo2Dir, "overwrite"));

        mod.WriteToBinary(Path.Combine(modDir, plugin));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({AppContext.BaseDirectory})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{plugin}\r\n");

        return mo2Dir;
    }

    [Theory]
    [InlineData("$TNG_TCT")] // real: TheNewGentleman.esp ARMO FULL, all-uppercase suffix
    [InlineData("$DAK_Trade")] // real: Dynamic Activation Key - Addons Collection.esp PERK EPF2/EPFD, mixed-case suffix
    public void Run_ArmoFullIsAnInterfaceTranslationKeyReference_ProducesNoCandidate(string keyReference)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_keyref_{Guid.NewGuid():N}");
        try
        {
            const string plugin = "SjptsKeyRefTarget.esp";
            var modKey = ModKey.FromNameAndExtension(plugin);
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var armo = mod.Armors.AddNew();
            armo.EditorID = "SjptsKeyRefArmor";
            armo.Name = keyReference;

            var mo2Dir = BuildFakeMo2Instance(root, mod, plugin);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            Assert.DoesNotContain(result.Candidates, c => c.RecordType == "ARMO FULL");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Control case: an ordinary ARMO FULL display name must still
    /// come through as a candidate — guards against the fix above being
    /// written so broadly that it swallows real armor names too.</summary>
    [Fact]
    public void Run_ArmoFullIsOrdinaryDisplayName_StillProducesACandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_keyref_control_{Guid.NewGuid():N}");
        try
        {
            const string plugin = "SjptsKeyRefControlTarget.esp";
            const string englishText = "Steel Sword";
            var modKey = ModKey.FromNameAndExtension(plugin);
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var armo = mod.Armors.AddNew();
            armo.EditorID = "SjptsKeyRefControlArmor";
            armo.Name = englishText;

            var mo2Dir = BuildFakeMo2Instance(root, mod, plugin);
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var result = PickUpTargetRunner.Run(mo2Dir, log);

            var candidate = Assert.Single(result.Candidates, c => c.RecordType == "ARMO FULL");
            Assert.Equal(englishText, candidate.CurrentText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
