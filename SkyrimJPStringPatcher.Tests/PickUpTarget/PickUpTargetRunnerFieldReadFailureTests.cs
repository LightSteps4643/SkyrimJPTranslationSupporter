using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.PickUpTarget;

namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// Two of PickUpTargetRunner's per-field try/catch blocks (② the Name/FULL
/// read at ~PickUpTargetRunner.cs:400, ③ ExtraTranslatableFields' read at
/// ~PickUpTargetRunner.cs:413) had no test at all — coverage showed 0%.
/// Neither can be triggered by a structurally-corrupted single field the way
/// Fixtures/MalformedPerkTest.esp does (that trick corrupts a PERK entry
/// point's own binary layout, caught by ④ NestedTranslatableFields instead).
/// Both ② and ③ instead go through Mutagen's *TranslatedString* machinery,
/// which reads a genuinely-localized plugin's separate loose Strings/*.STRINGS
/// / *.DLSTRINGS files — truncating one of those files corrupts its whole
/// lookup table (confirmed via a scratchpad spike: any TryLookup/String
/// access against a truncated table throws System.ArgumentException("Strings
/// file had duplicate entries.")), which is a much cheaper and more reliable
/// way to make ONE specific field's read throw than binary-corrupting the
/// .esp itself.
///
/// Fixtures/PickUpTarget/NameFullCorruptTest/: a localized ARMO record whose
/// Japanese .STRINGS table (backing Name/FULL) is truncated to 8 bytes — this
/// is the ONLY notable field on the record, so ②'s catch is unambiguous.
///
/// Fixtures/PickUpTarget/ExtraFieldCorruptTest/: a localized ARMO record with
/// BOTH languages present on Name (so FULL resolves normally and the same-mod
/// corpus harvest — SameModCorpusHarvestTests' pattern — still fires,
/// confirming the record itself IS being processed) but whose Japanese
/// .DLSTRINGS table (backing Description) is truncated — isolating the
/// failure to ③ alone.
/// </summary>
public class PickUpTargetRunnerFieldReadFailureTests
{
    private static string BuildFakeMo2Instance(string root, string fixtureName, string espFileName)
    {
        var mo2Dir = Path.Combine(root, "mo2");
        var modDir = Path.Combine(mo2Dir, "mods", "TestMod");
        var stringsDir = Path.Combine(modDir, "Strings");
        var profileDir = Path.Combine(mo2Dir, "profiles", "Default");
        Directory.CreateDirectory(stringsDir);
        Directory.CreateDirectory(profileDir);

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PickUpTarget", fixtureName);
        File.Copy(Path.Combine(fixtureDir, espFileName), Path.Combine(modDir, espFileName));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fixtureDir, "Strings")))
            File.Copy(file, Path.Combine(stringsDir, Path.GetFileName(file)));

        File.WriteAllText(Path.Combine(mo2Dir, "ModOrganizer.ini"),
            "[General]\r\n" +
            $"gamePath=@ByteArray({Path.Combine(root, "nonexistent_game")})\r\n" +
            "selected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "+TestMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{espFileName}\r\n");

        return mo2Dir;
    }

    [Fact]
    public void Run_NameFullTableCorrupted_ExcludesTheRecordEntirely_ReportsSkippedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_namefullcorrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root, "NameFullCorruptTest", "NameFullCorruptTest.esp");
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var (result, stdout) = ConsoleCapture.Run(() => PickUpTargetRunner.Run(mo2Dir, log));

            Assert.DoesNotContain(result.Candidates, c => c.RecordType == "ARMO FULL");

            Assert.Contains("##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0", stdout);
            Assert.Contains("##SJPTS_ISSUES_PLUGINS## NameFullCorruptTest.esp", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>v0.55.3で修正済み: SafeEnumeration.SafeForEachは、以前はENUMERATOR
    /// （MoveNext/Current）だけをtry/catchで保護し、onItemコールバックは無保護の
    /// まま呼んでいた（Core/SafeEnumeration.cs）。ExtraTranslatableFieldsの
    /// SafeForEach呼び出しは`fieldRef => Consider(...)`をonItemとして渡しており、
    /// Consider()自身がTranslatedString.TryLookup経由で例外を投げうる（Strings
    /// テーブルが壊れている場合）ため、修正前はSafeForEach自身のonErrorをすり抜け、
    /// ScanTranslatableFieldsもすり抜け、PickUpTargetRunner.Run()から未捕捉のまま
    /// 抜けていた（Run()はtry/finallyのみでcatchが無い）。既知の課題21が防ごうと
    /// していたプロセス全体クラッシュと同じクラスの問題が、別の経路（enumeratorで
    /// はなくonItem）から起こり得ていた。onItemもtry/catchで保護し、onErrorを
    /// 呼んだ上で列挙を継続するよう修正済み。</summary>
    [Fact]
    public void Run_ExtraFieldTableCorrupted_ExcludesOnlyThatField_KeepsTheRecordOtherwiseIntact_ReportsSkippedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_extrafieldcorrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mo2Dir = BuildFakeMo2Instance(root, "ExtraFieldCorruptTest", "ExtraFieldCorruptTest.esp");
            using var log = RunLog.Open(Path.Combine(root, "PickUpTarget"), "PickUpTarget");

            var (result, stdout) = ConsoleCapture.Run(() => PickUpTargetRunner.Run(mo2Dir, log));

            // The record itself was fully processed -- Name/FULL had both
            // languages, so it correctly reached the corpus (proving ② and
            // context extraction were unaffected by the Description corruption).
            var corpusEntry = Assert.Single(result.Corpus, e => e.English == "Good Name Test");
            Assert.Equal("良い名前テスト", corpusEntry.Japanese);

            // Only the Description field's own read failed.
            Assert.DoesNotContain(result.Candidates, c => c.RecordType == "ARMO DESC");

            Assert.Contains("##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0", stdout);
            Assert.Contains("##SJPTS_ISSUES_PLUGINS## ExtraFieldCorruptTest.esp", stdout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
