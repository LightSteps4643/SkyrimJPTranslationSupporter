using System.Text.Json;
using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.GenerateDsdFile;
using SkyrimJPStringPatcher.Tests.PickUpTarget.DsdTypeMatrix;

namespace SkyrimJPStringPatcher.Tests.GenerateDsdFile;

/// <summary>
/// Requirement (output side): a translated row of ANY of DSD's 67 type strings
/// must be written to the DSD json as an entry of the SAME type, for the same
/// record (form_id = "&lt;8 hex digits&gt;|&lt;plugin&gt;"), carrying the original and the
/// translation unchanged — and, for the one type DSD identifies by EditorID
/// ("GMST DATA"), the editor_id too. The generator must not depend on a list of
/// known types: a type it has never been taught still has to come out intact,
/// otherwise a newly picked-up kind of string (key / class / flora names, map
/// markers) would be translated and then silently lost at the last step.
///
/// The expectations are the INPUT rows themselves (pass-through), plus DSD's
/// documented entry shape — not a snapshot of what the generator printed.
/// </summary>
public class DsdTypeOutputTests : IClassFixture<DsdTypeOutputTests.Fixture>
{
    private const string Plugin = "TestMod.esp";

    public sealed class Fixture : IDisposable
    {
        public sealed record Row(string DsdType, string FormId, string English, string Japanese, string EditorId);

        public List<Row> Rows { get; } = new();
        public List<JsonElement> Entries { get; } = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_dsdtypeout_{Guid.NewGuid():N}");

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var number = 0x00800000;
            foreach (var type in DsdTypeSpec.AllTypes)
            {
                number++;
                var formId = $"{number:X8}:{Plugin}";
                var editorId = type == "GMST DATA" ? "sSjptsOutputSample" : "";
                Rows.Add(new Row(type, formId, $"English text for {type}", $"{type} の日本語訳テスト", editorId));
            }

            var tsv = new System.Text.StringBuilder("FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\r\n");
            foreach (var r in Rows)
                tsv.Append($"{r.FormId}\t{Plugin}\t{r.DsdType}\t{r.English}\t{r.Japanese}\tSJPTS_TranslationLocalLlm\t0\t{r.EditorId}\r\n");
            var input = Path.Combine(_root, "translations.tsv");
            File.WriteAllText(input, tsv.ToString());

            var outDir = Path.Combine(_root, "out");
            using var log = RunLog.Open(Path.Combine(_root, "GenerateDsdFile"), "GenerateDsdFile");
            DsdJsonGenerator.Run(input, outDir, log, outputTimestamp: new DateTime(2026, 1, 1));

            var pluginDir = Path.Combine(outDir, "SKSE", "Plugins", "DynamicStringDistributor", Plugin);
            foreach (var file in Directory.GetFiles(pluginDir, "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var element in doc.RootElement.EnumerateArray())
                    Entries.Add(element.Clone());
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private readonly Fixture _fixture;

    public DsdTypeOutputTests(Fixture fixture) => _fixture = fixture;

    [Theory]
    [MemberData(nameof(DsdTypeSpec.AllTypeRows), MemberType = typeof(DsdTypeSpec))]
    public void EveryDsdTypeString_ATranslatedRowIsWrittenAsADsdEntryOfTheSameType(string dsdType)
    {
        var row = Assert.Single(_fixture.Rows, r => r.DsdType == dsdType);
        // DSD's documented entry: "form_id": "<FormID as 8 hex digits>|<plugin>" (see the samples in DSD's docs).
        var expectedFormId = row.FormId.Replace(':', '|');

        var matches = _fixture.Entries
            .Where(e => e.GetProperty("type").GetString() == dsdType && e.GetProperty("form_id").GetString() == expectedFormId)
            .ToList();

        Assert.True(matches.Count == 1, $"Expected exactly one DSD entry of type '{dsdType}' for {expectedFormId}, found {matches.Count}.");
        var entry = matches[0];
        Assert.Equal(row.English, entry.GetProperty("original").GetString());
        Assert.Equal(row.Japanese, entry.GetProperty("string").GetString());
        if (row.EditorId.Length > 0)
            Assert.Equal(row.EditorId, entry.GetProperty("editor_id").GetString());
    }

    [Fact]
    public void EveryTranslatedRow_ProducesAnEntry_AndNothingExtra()
    {
        Assert.Equal(DsdTypeSpec.AllTypes.Count(), _fixture.Rows.Count);
        Assert.Equal(_fixture.Rows.Count, _fixture.Entries.Count);
    }
}
