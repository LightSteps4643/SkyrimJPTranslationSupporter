using SkyrimJPStringPatcher.Core;
using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>
/// Requirements for the SkyPatcher translation run (PromptGenerator.RunSkyPatcher), which reuses the
/// plugin side's whole resolution chain (①corpus … ⑤⑥LLM) on SkyPatcher rows:
///  - the unit is the providing MOD, and its work folder is named exactly after the MOD (a name like
///    "Armor v1.2" must not be cut at the dot, or "v1.1" and "v1.2" would share one folder);
///  - translations.tsv carries every row — also the ones that cannot take effect or are not
///    translatable, which are never translated nor sent to an LLM — plus the SkyPatcher columns the
///    output needs;
///  - the xTranslator import, the corpus, and an earlier output's translation all work on these rows;
///  - a translation made earlier is kept only while the original text is still the same;
///  - the plugin side's finished translations are offered to the LLM as same-mod style hints (trusted
///    methods only), without any plugin-side behaviour changing.
/// </summary>
public class SkyPatcherTranslationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sjpts_tests_skypatcher_tr_{Guid.NewGuid():N}");
    private string OutDir => Path.Combine(_root, "SkyPatcherText", "Translation", "out_temp");

    public SkyPatcherTranslationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static Candidate Row(string mod, string path, int line, string english, string recordType = "ARMO FULL",
        string filter = "", string inactive = "", string excluded = "", string coveredJapanese = "", string coveredNotes = "", int occurrence = 1)
    {
        filter = filter.Length > 0 ? filter : $"filterByArmors=X.esp|{line}";
        return new Candidate(mod, $"{path}|{filter}|{occurrence}", recordType, english,
            SourcePath: path, SourceFilter: filter, SourceLine: line, Inactive: inactive, Excluded: excluded,
            DsdCoveredJapanese: coveredJapanese, DsdCoveredNotes: coveredNotes);
    }

    private (string Candidates, string Corpus) Inputs(IEnumerable<Candidate> candidates, params CorpusEntry[] corpus)
    {
        var dir = Path.Combine(_root, "in");
        Directory.CreateDirectory(dir);
        var candidatesPath = Path.Combine(dir, "candidates.tsv");
        var corpusPath = Path.Combine(dir, "corpus.tsv");
        CandidateIo.WriteTsv(candidatesPath, candidates);
        CorpusIo.WriteTsv(corpusPath, corpus);
        return (candidatesPath, corpusPath);
    }

    private void Run((string Candidates, string Corpus) inputs, ITextTranslator? llm = null, IReadOnlyCollection<string>? mods = null,
        bool discard = false, string? importDir = null, string? pluginTranslations = null)
    {
        using var log = RunLog.Open(Path.Combine(_root, "log"), "SkyPatcherTranslation");
        PromptGenerator.RunSkyPatcher(inputs.Candidates, inputs.Corpus, importDir ?? Path.Combine(_root, "no_import"), mods,
            pluginTranslations ?? Path.Combine(_root, "no_plugin_translations"), OutDir, log,
            llmLocal: llm, discardUserEdits: discard);
    }

    /// <summary>translations.tsv of one MOD, read by column name.</summary>
    private List<Dictionary<string, string>> Translations(string mod)
    {
        var lines = File.ReadAllLines(Path.Combine(OutDir, mod, "translations.tsv"));
        var header = lines[0].Split('\t');
        return lines.Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var cells = l.Split('\t');
            return header.Select((h, i) => (h, v: i < cells.Length ? TsvEscaping.Unescape(cells[i]) : "")).ToDictionary(x => x.h, x => x.v);
        }).ToList();
    }

    // ---- the unit and its folder -----------------------------------------------

    [Fact]
    public void EachProvidingMod_GetsAFolderNamedExactlyAfterIt_WithTheModNameRecorded()
    {
        var inputs = Inputs(
        [
            Row("Armor Pack v1.1", "armor/A.ini", 1, "Coat One"),
            Row("Armor Pack v1.2", "armor/A.ini", 1, "Coat Two"),
        ]);

        Run(inputs);

        // Not cut at the dot: "v1.1" and "v1.2" stay two separate folders.
        Assert.Equal("Coat One", Translations("Armor Pack v1.1").Single()["EnglishText"]);
        Assert.Equal("Coat Two", Translations("Armor Pack v1.2").Single()["EnglishText"]);
        Assert.Equal("Armor Pack v1.2", File.ReadAllText(Path.Combine(OutDir, "Armor Pack v1.2", "mod_folder_name.txt")).Trim());
        Assert.False(Directory.Exists(Path.Combine(OutDir, "Armor Pack v1")));
    }

    [Fact]
    public void ModsFilter_RestrictsTheRun()
    {
        var inputs = Inputs([Row("ModA", "armor/A.ini", 1, "Name A"), Row("ModB", "armor/B.ini", 1, "Name B")]);

        Run(inputs, mods: ["ModB"]);

        Assert.False(Directory.Exists(Path.Combine(OutDir, "ModA")));
        Assert.True(File.Exists(Path.Combine(OutDir, "ModB", "translations.tsv")));
    }

    // ---- what translations.tsv holds -----------------------------------------------

    [Fact]
    public void EveryRowIsKept_WithTheSkyPatcherColumns_AndRecordTypeUnchanged()
    {
        var inputs = Inputs(
        [
            Row("Mod", "armor/A.ini", 3, "Gated Coat", inactive: "LineGate"),
            Row("Mod", "armor/A.ini", 4, "$TNG_TCT", excluded: "InterfaceKeyReference"),
            Row("Mod", "armor/A.ini", 5, "Plain Cloak"),
        ]);

        Run(inputs);

        var rows = Translations("Mod");
        Assert.Equal(3, rows.Count);
        var gated = rows.Single(r => r["EnglishText"] == "Gated Coat");
        Assert.Equal("armor/A.ini|filterByArmors=X.esp|3|1", gated["FormId"]);        // the row key
        Assert.Equal("Mod", gated["WinningPlugin"]);
        Assert.Equal("ARMO FULL", gated["RecordType"]);                                // the DSD type, not a SkyPatcher-specific one
        Assert.Equal("armor/A.ini", gated["SourcePath"]);
        Assert.Equal("filterByArmors=X.esp|3", gated["SourceFilter"]);
        Assert.Equal("3", gated["SourceLine"]);
        Assert.Equal("LineGate", gated["Inactive"]);
        Assert.Equal("InterfaceKeyReference", rows.Single(r => r["EnglishText"] == "$TNG_TCT")["Excluded"]);
    }

    [Fact]
    public void RowsThatCannotTakeEffectOrAreNotTranslatable_AreNeverTranslated_NorSentToAnLlm()
    {
        var inputs = Inputs(
        [
            Row("Mod", "armor/A.ini", 1, "Gated Coat", inactive: "LineGate"),
            Row("Mod", "armor/A.ini", 2, "TheKeyRef", excluded: "InterfaceKeyReference"),
            Row("Mod", "armor/A.ini", 3, "Plain Cloak"),
        ]);
        var llm = FakeTextTranslator.Succeeding(("Plain Cloak", "ただのマント"), ("Gated Coat", "ゲートのコート"), ("TheKeyRef", "キー"));

        Run(inputs, llm);

        var rows = Translations("Mod");
        Assert.Equal("ただのマント", rows.Single(r => r["EnglishText"] == "Plain Cloak")["Japanese"]);
        Assert.Equal("", rows.Single(r => r["EnglishText"] == "Gated Coat")["Japanese"]);
        Assert.Equal("", rows.Single(r => r["EnglishText"] == "TheKeyRef")["Japanese"]);
        Assert.DoesNotContain("Gated Coat", llm.LastPromptReceived);
        Assert.DoesNotContain("TheKeyRef", llm.LastPromptReceived);
    }

    // ---- resolution on SkyPatcher rows ---------------------------------------------

    [Fact]
    public void TheCorpusResolvesARow_AsItDoesForAPluginRecord()
    {
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Steel Cuirass")],
            new CorpusEntry("Steel Cuirass", "鋼鉄の鎧", "Skyrim.esm", "vanilla", "ARMO FULL"));

        Run(inputs);

        var row = Translations("Mod").Single();
        Assert.Equal("鋼鉄の鎧", row["Japanese"]);
        Assert.StartsWith("SJPTS_AutoCorpus", row["Notes"]);
    }

    [Fact]
    public void AnLlmResolvesWhatTheEarlierStepsCouldNot()
    {
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Cloak")]);

        Run(inputs, FakeTextTranslator.Succeeding(("Zorgblat Cloak", "ゾルグブラートのマント")));

        var row = Translations("Mod").Single();
        Assert.Equal("ゾルグブラートのマント", row["Japanese"]);
        Assert.Equal("SJPTS_TranslationLocalLlm", row["Notes"]);
    }

    [Fact]
    public void AnEarlierOutputsTranslation_IsAppliedWithItsMethod()
    {
        // PickUpTarget carried it forward (the scan read our earlier output): the row arrives already covered.
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Fur Coat", coveredJapanese: "毛皮のコート", coveredNotes: "SJPTS_ModifiedByUser")]);

        Run(inputs);

        var row = Translations("Mod").Single();
        Assert.Equal("毛皮のコート", row["Japanese"]);
        Assert.Equal("SJPTS_ModifiedByUser", row["Notes"]);
    }

    [Fact]
    public void TheXTranslatorImport_AppliesToSkyPatcherRowsToo()
    {
        var importDir = Path.Combine(_root, "import");
        Directory.CreateDirectory(importDir);
        File.WriteAllText(Path.Combine(importDir, "Pack.xml"),
            "<SSTXMLRessources><Params><Addon>Pack.esp</Addon></Params><Content>" +
            "<String><REC>ARMO:FULL</REC><Source>Fur Coat</Source><Dest>毛皮のコート</Dest></String></Content></SSTXMLRessources>");
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Fur Coat")]);

        Run(inputs, importDir: importDir);

        var row = Translations("Mod").Single();
        Assert.Equal("毛皮のコート", row["Japanese"]);
        Assert.Equal("SJPTS_AutoCorpusImported", row["Notes"]);
    }

    // ---- carrying results between runs ---------------------------------------------

    [Fact]
    public void ATranslationMadeEarlier_IsKeptOnARerun_WhileTheOriginalIsUnchanged()
    {
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Cloak")]);
        Run(inputs, FakeTextTranslator.Succeeding(("Zorgblat Cloak", "ゾルグブラートのマント")));

        var second = FakeTextTranslator.Succeeding(("Zorgblat Cloak", "別の訳"));
        Run(inputs, second);

        Assert.Equal("ゾルグブラートのマント", Translations("Mod").Single()["Japanese"]);
        Assert.Equal(0, second.CallCount);                       // nothing was asked again
    }

    [Fact]
    public void ATranslationMadeEarlier_IsNotKept_WhenTheOriginalChangedUnderTheSameKey()
    {
        Run(Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Cloak", filter: "filterByArmors=X.esp|1")]),
            FakeTextTranslator.Succeeding(("Zorgblat Cloak", "ゾルグブラートのマント")));

        // The mod updated its name for the same line/filter: the old translation no longer fits.
        var rerun = FakeTextTranslator.Succeeding(("Zorgblat Mantle", "ゾルグブラートの外套"));
        Run(Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Mantle", filter: "filterByArmors=X.esp|1")]), rerun);

        var row = Translations("Mod").Single();
        Assert.Equal("Zorgblat Mantle", row["EnglishText"]);
        Assert.Equal("ゾルグブラートの外套", row["Japanese"]);
    }

    [Fact]
    public void DiscardingEarlierResults_StartsAgainFromTheFirstStep()
    {
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Cloak")]);
        Run(inputs, FakeTextTranslator.Succeeding(("Zorgblat Cloak", "ゾルグブラートのマント")));

        Run(inputs, discard: true);

        Assert.Equal("", Translations("Mod").Single()["Japanese"]);
    }

    // ---- MOD-specific phrase detection counts the whole load order ------------------------

    /// <summary>The SkyPatcher run gets its own candidates.tsv, and the corpus.tsv of the plugin side — which sits
    /// in the plugin side's PickUpTarget folder, next to that side's own candidates.tsv.</summary>
    private (string Candidates, string Corpus) InputsWithPluginSide(IEnumerable<Candidate> skyPatcherRows, IEnumerable<string>? pluginTexts)
    {
        var spDir = Path.Combine(_root, "sp_in");
        var pluginDir = Path.Combine(_root, "plugin_in");
        Directory.CreateDirectory(spDir);
        Directory.CreateDirectory(pluginDir);
        var candidates = Path.Combine(spDir, "candidates.tsv");
        var corpus = Path.Combine(pluginDir, "corpus.tsv");
        CandidateIo.WriteTsv(candidates, skyPatcherRows);
        CorpusIo.WriteTsv(corpus, []);
        if (pluginTexts != null)
            CandidateIo.WriteTsv(Path.Combine(pluginDir, "candidates.tsv"),
                pluginTexts.Select((t, i) => new Candidate("Plugin.esp", $"{i:X8}:Plugin.esp", "ARMO FULL", t)));
        return (candidates, corpus);
    }

    private static IEnumerable<Candidate> LightGreatswordRows() =>
        new[] { "Daedric Light Greatsword", "Dwarven Light Greatsword", "Ebony Light Greatsword" }
            .Select((t, i) => Row("Mod", "weapon/W.ini", i + 1, t, recordType: "WEAP FULL", filter: $"filterByWeapons=X.esp|{i + 1}"));

    private string ModPhraseFile => Path.Combine(OutDir, "Mod", "mod_glossary.tsv");

    [Fact]
    public void ARecurringPhraseUniqueToTheMod_IsOfferedForTheGlossary_WhenNothingElseUsesIt()
    {
        Run(InputsWithPluginSide(LightGreatswordRows(), pluginTexts: null));

        Assert.Contains("Light Greatsword", File.ReadAllText(ModPhraseFile));
    }

    [Fact]
    public void APhraseThePluginsUseEverywhere_RanksBelowOneOnlyTheModUses()
    {
        // The detection RANKS this mod's recurring phrases by how distinctive they are against the whole load
        // order (a phrase that is equally common everywhere is penalised; it is not cut off by a threshold).
        // The plugin side's candidates are the rest of the load order: "Light Greatsword" is all through them,
        // "Zorg Blade" appears nowhere else — so, both recurring twice here, "Zorg Blade" must come first.
        var rows = new[] { "Daedric Light Greatsword", "Dwarven Light Greatsword", "Zorg Blade Alpha", "Zorg Blade Beta" }
            .Select((t, i) => Row("Mod", "weapon/W.ini", i + 1, t, recordType: "WEAP FULL", filter: $"filterByWeapons=X.esp|{i + 1}"));
        var pluginTexts = Enumerable.Range(0, 60).Select(i => $"Light Greatsword Variant{i}");

        Run(InputsWithPluginSide(rows, pluginTexts));

        var glossary = File.ReadAllText(ModPhraseFile);
        Assert.Contains("Zorg Blade", glossary);
        Assert.True(glossary.IndexOf("Zorg Blade", StringComparison.Ordinal) < glossary.IndexOf("Light Greatsword", StringComparison.Ordinal),
            "the phrase the whole load order uses must rank below the one only this mod uses");
    }

    [Fact]
    public void WithoutThePluginSidesCandidates_TheTwoPhrasesTieAndFallBackToAlphabeticalOrder()
    {
        // The control for the test above: with nothing to compare against, both recurring phrases look equally
        // distinctive — so the order the previous test sees is really caused by the plugin side's candidates.
        var rows = new[] { "Daedric Light Greatsword", "Dwarven Light Greatsword", "Zorg Blade Alpha", "Zorg Blade Beta" }
            .Select((t, i) => Row("Mod", "weapon/W.ini", i + 1, t, recordType: "WEAP FULL", filter: $"filterByWeapons=X.esp|{i + 1}"));

        Run(InputsWithPluginSide(rows, pluginTexts: null));

        var glossary = File.ReadAllText(ModPhraseFile);
        Assert.True(glossary.IndexOf("Light Greatsword", StringComparison.Ordinal) < glossary.IndexOf("Zorg Blade", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePluginSidesCandidates_AreOnlyCounted_NeverTranslated()
    {
        Run(InputsWithPluginSide(LightGreatswordRows(), ["Plugin Only Name", "Another Plugin Name"]));

        Assert.True(Directory.Exists(Path.Combine(OutDir, "Mod")));
        Assert.Single(Directory.GetDirectories(OutDir).Where(d => File.Exists(Path.Combine(d, "translations.tsv"))));
        Assert.DoesNotContain("Plugin Only Name", File.ReadAllText(Path.Combine(OutDir, "Mod", "translations.tsv")));
    }

    // ---- the plugin side's translations as style hints ---------------------------------

    private void WritePluginTranslation(string plugin, params (string English, string Japanese, string Notes)[] rows)
    {
        var dir = Path.Combine(_root, "plugin_translations", plugin);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "translations.tsv"),
            new[] { "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId\tTranslationCheck" }
                .Concat(rows.Select((r, i) => $"0000{i:X4}:{plugin}\t{plugin}\tARMO FULL\t{r.English}\t{r.Japanese}\t{r.Notes}\t0\t\t")));
    }

    [Fact]
    public void AFinishedPluginTranslationOfATrustedMethod_IsOfferedToTheLlmAsAHint()
    {
        WritePluginTranslation("Elsewhere.esp", ("Zorgblat Helm", "ゾルグブラートの兜", "SJPTS_AutoCorpusMeaning"));
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Cloak")]);
        var llm = FakeTextTranslator.Succeeding(("Zorgblat Cloak", "ゾルグブラートのマント"));

        Run(inputs, llm, pluginTranslations: Path.Combine(_root, "plugin_translations"));

        Assert.Contains("ゾルグブラートの兜", llm.LastPromptReceived);
    }

    [Fact]
    public void APluginTranslationOfAnUntrustedMethod_IsNotOffered()
    {
        // SJPTS_AutoCorpus rows are already reachable through the corpus itself; offering them here would only waste budget.
        WritePluginTranslation("Elsewhere.esp", ("Zorgblat Helm", "ゾルグブラートの兜", "SJPTS_AutoCorpus"));
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Cloak")]);
        var llm = FakeTextTranslator.Succeeding(("Zorgblat Cloak", "ゾルグブラートのマント"));

        Run(inputs, llm, pluginTranslations: Path.Combine(_root, "plugin_translations"));

        Assert.DoesNotContain("ゾルグブラートの兜", llm.LastPromptReceived);
    }

    [Fact]
    public void WithNoPluginTranslationsAtAll_TheRunStillWorks()
    {
        var inputs = Inputs([Row("Mod", "armor/A.ini", 1, "Zorgblat Cloak")]);

        Run(inputs, FakeTextTranslator.Succeeding(("Zorgblat Cloak", "ゾルグブラートのマント")), pluginTranslations: Path.Combine(_root, "does_not_exist"));

        Assert.Equal("ゾルグブラートのマント", Translations("Mod").Single()["Japanese"]);
    }
}
