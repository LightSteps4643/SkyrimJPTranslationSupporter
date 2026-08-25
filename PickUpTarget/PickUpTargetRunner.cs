using System;
using DynamicData;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using SkyrimJPStringPatcher.Core;

// Keyed by (FormKey, DSD "type" string, index) — a single record can carry
// MULTIPLE independently-translatable fields (e.g. a WEAP has both "WEAP
// FULL" and "WEAP DESC"), and indexed types (quest objectives, dialogue
// responses, message buttons, perk effects) can carry MANY candidates under
// the same FormKey+type. "index" is 0 for every FormID-only type; see
// Candidate.cs and NestedTranslatableFields.cs for what it means per type.
using ChainKey = (Mutagen.Bethesda.Plugins.FormKey FormKey, string DsdType, int Index);
using ChainValue = System.Collections.Generic.List<(Mutagen.Bethesda.Plugins.ModKey Source, string Text, string EditorId, string Context)>;

namespace SkyrimJPStringPatcher.PickUpTarget;

public sealed record PickUpTargetResult(
    string ProfileName,
    List<Candidate> Candidates,
    List<CorpusEntry> Corpus,
    List<string> StaleReviewLog,
    Dictionary<string, (int Count, long Chars)> CoveredByPlugin,
    IReadOnlyList<string> ActivePlugins);

/// <summary>
/// PickUpTarget: 処理対象の選定。MO2のロードオーダーを解決し、各レコードの勝者を
/// 特定して、日本語化されていない候補（と、副産物として英日対応コーパス）を
/// 洗い出す。Translation/GenerateDsdFileはこの結果（TSVファイル）だけを見ればよく、
/// Mutagen/MO2の知識は一切不要 — MutagenへのアクセスはこのPickUpTargetにのみ閉じ込める。
///
/// v0.4.1でリファクタリング: v0.1.0〜v0.4.0の機能追加で`Run()`が1メソッドに
/// 肥大化していたため、「プラグインを開く」「翻訳対象フィールドを洗い出す」
/// 「コーパスに反映する」「候補を確定する」の4段階に分割した。挙動は変えていない
/// （PATCHバージョンアップ）。
/// </summary>
public static class PickUpTargetRunner
{
    /// <param name="includeStale">Treat records whose shipped DSD translation was
    /// written against a now-changed original as translatable again, instead of
    /// only reporting them. Off by default — see the note at the decision site.</param>
    public static PickUpTargetResult Run(string mo2InstanceDir, RunLog log, bool includeStale = false, TraceLog? trace = null)
    {
        Console.WriteLine($"Reading MO2 instance: {mo2InstanceDir}");
        trace?.Info($"Mo2InstanceReader.Read start: {mo2InstanceDir}");
        var instance = Mo2InstanceReader.Read(mo2InstanceDir);
        trace?.Info($"Mo2InstanceReader.Read done: profile={instance.ProfileName}, {instance.LoadOrder.Count} plugins");
        Console.WriteLine($"Profile: {instance.ProfileName}, active plugins (incl. implicit masters): {instance.LoadOrder.Count}");

        log.Section("入力", "Input");
        log.Line($"MO2インスタンス: {mo2InstanceDir}", $"MO2 instance: {mo2InstanceDir}");
        log.Line($"プロファイル: {instance.ProfileName}", $"Profile: {instance.ProfileName}");
        log.Line($"アクティブプラグイン: {instance.LoadOrder.Count}（暗黙のマスタを含む）", $"Active plugins: {instance.LoadOrder.Count} (incl. implicit masters)");
        log.Line(
            $"--include-stale: {(includeStale ? "あり（原文が変化した既存DSD訳を再翻訳対象に含める）" : "なし（既定。報告のみ）")}",
            $"--include-stale: {(includeStale ? "yes (re-includes existing DSD translations whose original text changed)" : "no (default; report only)")}");

        Console.WriteLine("Staging VFS-winning Strings/* files for Japanese resolution...");
        trace?.Info("StringsStaging.Build start (staging VFS-winning Strings/*)");
        var stringsStagingDir = StringsStaging.Build(instance);
        trace?.Info($"StringsStaging.Build done: {stringsStagingDir}");
        var readParams = new BinaryReadParameters
        {
            StringsParam = new StringsReadParameters
            {
                TargetLanguage = Language.Japanese,
                StringsFolderOverride = stringsStagingDir,
            },
        };

        try
        {
            var mods = OpenMods(instance, readParams, log, trace);

            trace?.Info($"ScanTranslatableFields start: scanning {mods.Count} plugins");
            var scan = ScanTranslatableFields(mods, trace);
            trace?.Info($"ScanTranslatableFields done: {scan.Chains.Count} translatable fields, {scan.Corpus.Count} corpus source pairs, {scan.NotPlayerFacing.Count} not-player-facing");
            Console.WriteLine($"Collected {scan.Chains.Count} translatable (record, field, index) candidates across the load order");

            Console.WriteLine("Scanning existing DSD coverage in the load order...");
            trace?.Info("DsdCoverageScanner.Scan start (reading existing DSD json files)");
            var coverage = DsdCoverageScanner.Scan(instance);
            trace?.Info($"DsdCoverageScanner.Scan done: {coverage.Count} existing DSD entries");
            Console.WriteLine($"Found {coverage.Count} existing DSD entries (any type, any language) across active plugin folders");

            var corpusBeforeCoverage = scan.Corpus.Count;
            AddCoverageToCorpus(coverage, scan.Corpus);
            trace?.Debug($"AddCoverageToCorpus done: corpus {corpusBeforeCoverage} -> {scan.Corpus.Count} entries");
            Console.WriteLine($"Corpus (English->Japanese precedent pairs) size: {scan.Corpus.Count}");

            trace?.Info($"BuildCandidates start: {scan.Chains.Count} chains");
            var (candidates, alreadyCoveredByDsd, markupOnly, notPlayerFacing, staleIncluded, staleReviewLog, coveredByPlugin) =
                BuildCandidates(scan.Chains, coverage, log, includeStale, scan.NotPlayerFacing, trace);
            trace?.Info($"BuildCandidates done: {candidates.Count} candidates (excluded: already-DSD {alreadyCoveredByDsd}, markup {markupOnly}, not-player-facing {notPlayerFacing})");
            Console.WriteLine($"Already covered by existing DSD Japanese translations: {alreadyCoveredByDsd} (of which {staleReviewLog.Count} flagged for review)");
            Console.WriteLine($"Excluded as markup/icon-glyph, not translatable text: {markupOnly}");
            Console.WriteLine($"Excluded as not player-facing (HideInUI / asset path / internal identifier): {notPlayerFacing}");
            if (includeStale)
                Console.WriteLine($"Re-included for retranslation (--include-stale): {staleIncluded}");
            Console.WriteLine($"Translation candidates: {candidates.Count}");

            log.Section("処理サマリ", "Processing summary");
            log.Line($"走査した翻訳可能フィールド: {scan.Chains.Count}（レコード×フィールド×index）", $"Scanned translatable fields: {scan.Chains.Count} (record x field x index)");
            log.Line($"既存DSDエントリ: {coverage.Count}（種別・言語を問わず）", $"Existing DSD entries: {coverage.Count} (any type, any language)");
            log.Line($"コーパス（英日対訳ペア）: {scan.Corpus.Count}", $"Corpus (English/Japanese precedent pairs): {scan.Corpus.Count}");
            log.Line($"既存DSDで翻訳済みのため除外: {alreadyCoveredByDsd}", $"Excluded, already covered by existing DSD: {alreadyCoveredByDsd}");
            log.Line($"マークアップ/アイコングリフとして除外: {markupOnly}", $"Excluded as markup/icon-glyph: {markupOnly}");
            log.Line($"ユーザーの目に触れないものとして除外: {notPlayerFacing}（HideInUI / アセットパス / 内部識別子。内訳は下記）", $"Excluded as not player-facing: {notPlayerFacing} (HideInUI / asset path / internal identifier — breakdown below)");
            log.Line($"翻訳候補: {candidates.Count}", $"Translation candidates: {candidates.Count}");
            log.Line("※候補の内訳・全件は candidates.tsv / candidates.txt を、対訳は corpus.tsv を参照",
                "* See candidates.tsv / candidates.txt for the full candidate breakdown, corpus.tsv for the precedent pairs");

            log.Section("特殊な照合処理の記録（DSDの仕様による）", "Special matching notes (per DSD's own spec)");
            log.Line("PERK EPF2 / EPFD : 同一テキストを両方のtype文字列で出力している。DSDは(FormID, TranslationType)",
                "PERK EPF2 / EPFD : emits the same text under both type strings. DSD matches on (FormID, TranslationType),");
            log.Line("                   で照合するため両者は独立で衝突せず、外れた側は無視されるだけで害がない",
                "                   so the two never collide; the unused one is simply ignored, harmlessly");
            log.Line("GMST DATA        : FormIDではなくEditorIDで照合（DSDのkGameSetting）",
                "GMST DATA        : matched by EditorID, not FormID (DSD's kGameSetting)");
            log.Line("QUST CNAM        : 原文テキストの内容そのものをキーに照合（DSDのkRuntimeLegacy、唯一の例外）",
                "QUST CNAM        : matched by the original text itself (DSD's kRuntimeLegacy, the one exception)");
            log.Line("REFR FULL        : スキーマ上は存在するがロードオーダー内に実データ0件のため未対応",
                "REFR FULL        : exists in the schema but unsupported — 0 real records in this load order");

            var activePlugins = instance.LoadOrder.Select(p => p.FileName).ToList();
            return new PickUpTargetResult(instance.ProfileName, candidates, scan.Corpus, staleReviewLog, coveredByPlugin, activePlugins);
        }
        finally
        {
            try { Directory.Delete(stringsStagingDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static List<string> OpenMods(Mo2Instance instance, BinaryReadParameters readParams, RunLog log, TraceLog? trace = null)
    {
        trace?.Info($"OpenMods start: opening {instance.LoadOrder.Count} plugins");
        var mods = new List<string>();
        foreach (var plugin in instance.LoadOrder)
        {
            try
            {
                if (plugin.AbsolutePath.EndsWith(".esp") || plugin.AbsolutePath.EndsWith(".esm") || plugin.AbsolutePath.EndsWith(".esl"))
                {
                    trace?.Trace($"Opening: {plugin.FileName} ({plugin.AbsolutePath})");
                    mods.Add(plugin.AbsolutePath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] failed to open '{plugin.FileName}': {ex.Message}");
                //log.Detail("除外: プラグインを開けなかった", "Excluded: failed to open plugin", $"{plugin.FileName} — {ex.Message}");
                trace?.Warning($"Failed to open plugin: {plugin.FileName} — {ex.Message}");
            }
        }
        Console.WriteLine($"Successfully opened {mods.Count}/{instance.LoadOrder.Count} plugins");
        trace?.Info($"OpenMods done: opened {mods.Count}/{instance.LoadOrder.Count} plugins");
        return mods;
    }

    private sealed record ScanResult(
        Dictionary<ChainKey, ChainValue> Chains,
        List<CorpusEntry> Corpus,
        HashSet<FormKey> NotPlayerFacing);


    /// <summary>Walks every record in every mod (in load order) and records, per
    /// (FormKey, DSD type, index), each mod's own contribution — the LAST entry
    /// in each chain is therefore the load-order WINNER, same VFS-override
    /// resolution FULL always used. Also opportunistically builds the vanilla
    /// half of the corpus (English/Japanese pairs found on the same field).</summary>
    private static ScanResult ScanTranslatableFields(List<string> mods, TraceLog? trace = null)
    {
        var chains = new Dictionary<ChainKey, ChainValue>();
        var corpus = new List<CorpusEntry>();

        // Records the player never reads, identified from the record itself rather
        // than from its text. Collected here, applied in BuildCandidates.
        //var notPlayerFacing = new HashSet<FormKey>();

        // MGEF's "never show this effect in the UI" flag. Resolved in a typed
        // pre-pass because SPEL's rule below needs to consult it for effects that
        // may not have been enumerated yet.
        //var hiddenEffects = CollectHiddenMagicEffects(mods);
        //notPlayerFacing.UnionWith(hiddenEffects);
        //trace?.Debug($"CollectHiddenMagicEffects done: {hiddenEffects.Count} entries (MGEF HideInUI)");

        // v0.29.9: ARMO/WEAP's "Non-Playable" flag — the engine's own declaration
        // that a record can never be equipped or shown to the player, used by
        // some mods (body-physics addons in particular) for internal dummy
        // records that exist only as on/off switches for a script or NiOverride
        // config, never as real gear. Confirmed against real data via houseCARL:
        // 3BBB's "SMP ON Object" toggle records all carry NonPlayable, while a
        // real cosmetic item sharing similar branding ("KSSMP Angels") does not
        // — this flag cleanly separates the two without guessing from the name
        // text, and (unlike the v0.29.8 name-pattern check it replaces) it
        // generalizes to ANY mod's internal dummy armor/weapon, not just this
        // one plugin's specific naming convention.
        //var nonPlayableGear = CollectNonPlayableGear(mods);
        //notPlayerFacing.UnionWith(nonPlayableGear);
        //trace?.Debug($"CollectNonPlayableGear done: {nonPlayableGear.Count} entries (ARMO/WEAP NonPlayable)");

        HashSet<FormKey> notPlayerFacing = new HashSet<FormKey>();//You do not need to fill in this field; simply leave it blank. Since certain elements are controlled by scripts, relying on this alone does not guarantee effectiveness.


        void Consider(FormKey formKey, string dsdType, int index, ModKey source, ITranslatedStringGetter? field, string editorId = "", string context = "")
        {
            if (field == null) return;

            // A non-localized (third-party mod) plugin has exactly ONE embedded
            // string, and TryLookup(Japanese) on it simply fails outright rather
            // than falling back — unlike INamedGetter.Name's plain-string
            // convenience accessor (used for FULL), which DOES fall back to
            // whatever's embedded when the requested language isn't present.
            // Replicate that fallback by hand: prefer Japanese if present (a
            // localized/vanilla record, or an already-DSD-translated one),
            // otherwise fall back to English (or whatever the sole embedded
            // string resolves as) as the "winner" text to language-check.
            var hasJapanese = field.TryLookup(Language.Japanese, out var jpnText) && !string.IsNullOrWhiteSpace(jpnText);
            var hasEnglish = field.TryLookup(Language.English, out var engText) && !string.IsNullOrWhiteSpace(engText);

            // v0.50.1a: normalize CRLF here, at the earliest point raw game text
            // enters the pipeline — Bethesda string data routinely embeds literal
            // "\r\n" line breaks. TsvEscaping.Escape strips "\r" when writing any
            // TSV, so by the time Translation reads a candidate back it's already
            // lost the CR; PickUpTarget's own in-memory character counts (used by
            // coverage_by_plugin.tsv / plugin_summary.txt) never went through that
            // round-trip and so counted the CR too — a 1-char-per-line-break
            // phantom "untranslated" discrepancy for any candidate spanning
            // multiple lines (confirmed on DynDOLOD.esm's 3 CRLF-containing loading
            // screen jokes, all otherwise fully translated). Stripping "\r" this
            // early instead means every downstream consumer (corpus, candidates,
            // char counts, prompt.txt, DSD output) sees the exact same text.
            if (jpnText != null) jpnText = jpnText.Replace("\r", "");
            if (engText != null) engText = engText.Replace("\r", "");

            var winnerText = hasJapanese ? jpnText! : hasEnglish ? engText! : null;
            if (string.IsNullOrWhiteSpace(winnerText)) return;

            if (hasEnglish && hasJapanese && LanguageDetector.ContainsJapanese(jpnText!) && engText != jpnText)
                corpus.Add(new CorpusEntry(engText!, jpnText!, source.FileName, "vanilla", dsdType));

            var key = (formKey, dsdType, index);
            if (!chains.TryGetValue(key, out var list))
            {
                list = new ChainValue();
                chains[key] = list;
            }
            list.Add((source, winnerText!, editorId, context));
        }

        //CollectHiddenMagicEffects();

        foreach (var modPath in mods)
        {
            
            //foreach (var record in mod.EnumerateMajorRecords())
            //{
            //    var context = RecordContextExtractor.For(record, raceNames);

            //    // A perk flagged Hidden, or one that isn't Playable, never appears in
            //    // the skill tree — so neither its name nor its description is read.
            //    if (record is IPerkGetter perk && (perk.Hidden || !perk.Playable))
            //        notPlayerFacing.Add(record.FormKey);

            //    // An Ability is a spell the player cannot cast and which the magic
            //    // menu does not list; it surfaces only as its effects in the Active
            //    // Effects list. So when EVERY one of those effects is itself flagged
            //    // HideInUI, nothing about the spell is ever displayed. Deliberately
            //    // limited to Ability — a castable Spell's name IS shown in the magic
            //    // menu regardless of what its effects do, and Powers/LesserPowers
            //    // appear under Powers. Confirmed against real records: the effects of
            //    // Flourish / Quick Draw / Agility / Eagle Eye 25 are all HideInUI.
            //    if (record is ISpellGetter spell
            //        && spell.Type == SpellType.Ability
            //        && spell.Effects.Count > 0
            //        && spell.Effects.All(e => !e.BaseEffect.IsNull && hiddenEffects.Contains(e.BaseEffect.FormKey)))
            //    {
            //        notPlayerFacing.Add(record.FormKey);
            //    }

               
            //}

            EspReader NEspReader = new EspReader();
            NEspReader.ReadMod(modPath);

            var Records = SkyrimDataLoader.LoadAll(NEspReader);

            foreach (var record in Records)
            {
                ModKey modKey = null;

                if (NEspReader != null && NEspReader.CurrentMod != null && NEspReader.CurrentMod.ModKey != null)
                {
                    modKey = NEspReader.CurrentMod.ModKey;
                }

                Consider(record.FormKey, record.Sig, 0, modKey, record.String, record.EditID, record?.String?.ToString());
            }
        }
       

        //var raceNames = CollectRaceNames(mods);
        //trace?.Debug($"CollectRaceNames done: {raceNames.Count} entries");

        //foreach (var mod in mods)
        //{
        //    foreach (var record in mod.EnumerateMajorRecords())
        //    {
        //        var context = RecordContextExtractor.For(record, raceNames);

        //        // A perk flagged Hidden, or one that isn't Playable, never appears in
        //        // the skill tree — so neither its name nor its description is read.
        //        if (record is IPerkGetter perk && (perk.Hidden || !perk.Playable))
        //            notPlayerFacing.Add(record.FormKey);

        //        // An Ability is a spell the player cannot cast and which the magic
        //        // menu does not list; it surfaces only as its effects in the Active
        //        // Effects list. So when EVERY one of those effects is itself flagged
        //        // HideInUI, nothing about the spell is ever displayed. Deliberately
        //        // limited to Ability — a castable Spell's name IS shown in the magic
        //        // menu regardless of what its effects do, and Powers/LesserPowers
        //        // appear under Powers. Confirmed against real records: the effects of
        //        // Flourish / Quick Draw / Agility / Eagle Eye 25 are all HideInUI.
        //        if (record is ISpellGetter spell
        //            && spell.Type == SpellType.Ability
        //            && spell.Effects.Count > 0
        //            && spell.Effects.All(e => !e.BaseEffect.IsNull && hiddenEffects.Contains(e.BaseEffect.FormKey)))
        //        {
        //            notPlayerFacing.Add(record.FormKey);
        //        }

        //        // FULL (generic across every INamedGetter/ITranslatedNamedGetter record type).
        //        if (record is INamedGetter named)
        //        {
        //            var name = named.Name;
        //            if (!string.IsNullOrWhiteSpace(name) && record is ITranslatedNamedGetter translatedNamed && translatedNamed.Name != null)
        //            {
        //                var signature = RecordSignatureMap.Resolve(record.GetType().Name);
        //                if (RecordSignatureMap.DsdFullNameSupported.Contains(signature))
        //                    Consider(record.FormKey, $"{signature} FULL", 0, mod.ModKey, translatedNamed.Name, context: context);
        //            }
        //        }

        //        // Flat, FormID-only-matched DSD-supported fields beyond FULL.
        //        foreach (var (dsdType, field) in ExtraTranslatableFields.For(record))
        //            Consider(record.FormKey, dsdType, 0, mod.ModKey, field, context: context);

        //        // Nested list structures and the EditorID-matched GMST exception.
        //        foreach (var fieldRef in NestedTranslatableFields.For(record))
        //            Consider(record.FormKey, fieldRef.DsdType, fieldRef.Index, mod.ModKey, fieldRef.Field, fieldRef.EditorId, context);
        //    }
        //}

        return new ScanResult(chains, corpus, notPlayerFacing);
    }

    /// <summary>
    /// FormKeys of magic effects whose winning version carries HideInUI. A typed
    /// pre-pass (only the MGEF group is walked, as with CollectRaceNames) for two
    /// reasons: SPEL's "all effects hidden" test must be able to consult effects
    /// the main enumeration hasn't reached yet, and the flag must be read from the
    /// LOAD-ORDER WINNER — a later mod can set or clear it, and only the winner's
    /// value describes what the player actually sees.
    /// </summary>
    private static HashSet<FormKey> CollectHiddenMagicEffects(List<ISkyrimModGetter> mods)
    {
        var latest = new Dictionary<FormKey, bool>();
        foreach (var mod in mods)
            foreach (var effect in mod.EnumerateMajorRecords<IMagicEffectGetter>())
                latest[effect.FormKey] = effect.Flags.HasFlag(MagicEffect.Flag.HideInUI); // later mods win
        return latest.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>
    /// FormKeys of ARMO/WEAP records whose winning version carries the engine's
    /// own "Non-Playable" flag — same load-order-winner resolution as
    /// <see cref="CollectHiddenMagicEffects"/>, for the same reason (a later
    /// mod's override can set or clear the flag, and only the winner's value
    /// describes what the player can actually equip or see). v0.29.9.
    /// </summary>
    private static HashSet<FormKey> CollectNonPlayableGear(List<ISkyrimModGetter> mods)
    {
        var latest = new Dictionary<FormKey, bool>();
        foreach (var mod in mods)
        {
            foreach (var armor in mod.EnumerateMajorRecords<IArmorGetter>())
                latest[armor.FormKey] = armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable);
            foreach (var weapon in mod.EnumerateMajorRecords<IWeaponGetter>())
                latest[weapon.FormKey] = weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable);
        }
        return latest.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>
    /// RACE names, resolved once up front so NPC context can say "種族: ノルド"
    /// rather than a raw FormKey. A typed enumeration only walks the RACE group
    /// (a few hundred records), so this costs far less than the full
    /// EnumerateMajorRecords() pass — and it must be a separate pass regardless,
    /// since an NPC can be reached before its own race record within a single
    /// mod's enumeration order. Prefers the resolved Name (already Japanese for
    /// vanilla races, because the mods were opened with Japanese as the target
    /// language) and falls back to the EditorID.
    /// </summary>
    private static Dictionary<FormKey, string> CollectRaceNames(List<ISkyrimModGetter> mods)
    {
        var names = new Dictionary<FormKey, string>();
        foreach (var mod in mods)
        {
            foreach (var race in mod.EnumerateMajorRecords<IRaceGetter>())
            {
                var name = race.Name?.String;
                if (string.IsNullOrWhiteSpace(name)) name = race.EditorID;
                if (!string.IsNullOrWhiteSpace(name)) names[race.FormKey] = name!; // later mods win
            }
        }
        return names;
    }

    /// <summary>Existing DSD coverage is itself a source of precedent — its
    /// original/string pairs feed the corpus alongside the vanilla dual-language
    /// pairs ScanTranslatableFields already found.</summary>
    private static void AddCoverageToCorpus(DsdCoverageIndex coverage, List<CorpusEntry> corpus)
    {
        foreach (var ((_, dsdType, _), cov) in coverage.ByFormTypeIndex)
        {
            if (cov.OriginalRecorded != null && !string.IsNullOrWhiteSpace(cov.OriginalRecorded)
                && LanguageDetector.ContainsJapanese(cov.TranslatedString) && cov.OriginalRecorded != cov.TranslatedString)
            {
                corpus.Add(new CorpusEntry(cov.OriginalRecorded, cov.TranslatedString, Path.GetFileName(cov.SourceFile), "dsd", dsdType));
            }
        }
    }

    /// <summary>For each scanned (FormKey, type, index), take the load-order
    /// winner, skip it if it's already Japanese or already DSD-covered (using
    /// each type's own <see cref="DsdTypeMatching"/> strategy), and keep
    /// whatever's left as a real translation candidate.</summary>
    private static (List<Candidate> Candidates, int AlreadyCoveredByDsd, int MarkupOnly, int NotPlayerFacing, int StaleIncluded, List<string> StaleReviewLog, Dictionary<string, (int Count, long Chars)> CoveredByPlugin) BuildCandidates(
        Dictionary<ChainKey, ChainValue> chains, DsdCoverageIndex coverage, RunLog log, bool includeStale, HashSet<FormKey> hiddenRecords, TraceLog? trace = null)
    {
        var candidates = new List<Candidate>();
        var alreadyCoveredByDsd = 0;
        var markupOnly = 0;
        var notPlayerFacing = 0;
        var staleIncluded = 0;
        var staleReviewLog = new List<string>();

        // v0.18.0: per-plugin "already translated" tally, keyed by the SAME
        // winning-plugin name used for candidates (winner.Source.FileName) —
        // this is what lets a coverage report compare covered vs. uncovered
        // for the same plugin. Char count uses winner.Text (the English/current
        // text length) since that is the unit "how much work is left" is
        // measured in, same as the candidate side.
        var coveredByPlugin = new Dictionary<string, (int Count, long Chars)>(StringComparer.OrdinalIgnoreCase);

        foreach (var ((formKey, dsdType, index), chain) in chains)
        {
            var winner = chain[^1];
            if (LanguageDetector.ContainsJapanese(winner.Text))
            {
                trace?.Trace($"Skip [{dsdType}] {formKey}: already Japanese (\"{winner.Text}\")");
                continue;
            }

            DsdCoverageEntry? cov = DsdTypeMatching.GetStrategy(dsdType) switch
            {
                DsdMatchStrategy.ByEditorId => coverage.ByEditorId.TryGetValue($"{dsdType}|{winner.EditorId}", out var byEdid) ? byEdid : null,
                DsdMatchStrategy.ByOriginalText => coverage.ByFormType.TryGetValue((formKey, dsdType), out var entries)
                    ? entries.FirstOrDefault(e => e.OriginalRecorded == winner.Text)
                    : null,
                _ => coverage.ByFormTypeIndex.TryGetValue((formKey, dsdType, index), out var byIdx) ? byIdx : null,
            };

            if (cov != null && LanguageDetector.ContainsJapanese(cov.TranslatedString))
            {
                alreadyCoveredByDsd++;
                trace?.Trace($"Skip [{dsdType}] {formKey}: already covered by DSD (\"{winner.Text}\" -> \"{cov.TranslatedString}\" in {Path.GetFileName(cov.SourceFile)})");
                var pluginTally = coveredByPlugin.GetValueOrDefault(winner.Source.FileName);
                coveredByPlugin[winner.Source.FileName] = (pluginTally.Count + 1, pluginTally.Chars + winner.Text.Length);
                // Compare TRIMMED (v0.7.2). This is a REPORTING judgment — "is the
                // shipped translation likely out of date?" — and a difference of one
                // trailing space is not evidence of that. 151 of 615 flagged entries
                // were exactly this: "…Good day. " vs "…Good day.". They drowned out
                // the 464 real ones, which include genuine content changes a later
                // mod introduced ("Sun Bane" → "Sun Fire", whole description
                // rewrites) where the old Japanese really does still get applied.
                //
                // Deliberately NOT applied to the coverage-matching comparison in the
                // ByOriginalText branch above: that one must mirror what DSD itself
                // does at runtime (kRuntimeLegacy keys on the original text), so
                // being more lenient there would mark something "already covered"
                // that DSD will in fact fail to match.
                if (cov.OriginalRecorded != null && cov.OriginalRecorded.Trim() != winner.Text.Trim())
                {
                    staleReviewLog.Add($"{formKey} ({dsdType}, index={index}) — DSD translation in {Path.GetFileName(cov.SourceFile)} may be stale (original text changed) but is still applied");
                    log.Detail(
                        includeStale
                            ? "再翻訳対象に含めた: 既存DSD翻訳の原文が変化している（--include-stale 指定）"
                            : "要レビュー: 既存DSD翻訳は適用され続けるが、原文が変化している（訳が古い可能性）",
                        includeStale
                            ? "Re-included for retranslation: the original text behind an existing DSD translation has changed (--include-stale)"
                            : "Needs review: an existing DSD translation keeps being applied, but its original text has changed (may be stale)",
                        log.Lang == RunLogLang.Ja
                            ? $"{formKey} [{dsdType}] in {Path.GetFileName(cov.SourceFile)}\n" +
                              $"        DSDが記録した原文: {cov.OriginalRecorded}\n" +
                              $"        現在の原文        : {winner.Text}"
                            : $"{formKey} [{dsdType}] in {Path.GetFileName(cov.SourceFile)}\n" +
                              $"        original text recorded by DSD: {cov.OriginalRecorded}\n" +
                              $"        current original text        : {winner.Text}");

                    // Opt-in (v0.8.0). DSD matches on FormID alone, so the shipped
                    // Japanese keeps being applied to text it was never written for
                    // — "Sun Bane"'s translation now labels a record reading "Sun
                    // Fire". Treating those as translatable again is the only way to
                    // correct them, but it is deliberately NOT the default: it means
                    // emitting our own entry for a (FormID, type) another DSD file
                    // already claims, and which of the two wins is decided by DSD's
                    // file/folder processing order, not by us.
                    if (includeStale)
                    {
                        staleIncluded++;
                        trace?.Trace($"Candidate [{dsdType}] {formKey}: re-included as stale (--include-stale) (\"{winner.Text}\", DSD recorded original \"{cov.OriginalRecorded}\")");
                        candidates.Add(new Candidate(
                            winner.Source.FileName, formKey.ToString(), dsdType, winner.Text, index, winner.EditorId, winner.Context,
                            StaleOriginal: cov.OriginalRecorded, StaleTranslation: cov.TranslatedString));
                        continue;
                    }
                }
                continue;
            }

            if (!LanguageDetector.IsTranslatableEnglish(winner.Text))
            {
                trace?.Trace($"Skip [{dsdType}] {formKey}: not translatable English (\"{winner.Text}\")");
                continue;
            }

            // v0.5.0: markup whose visible result is a picture, not words — e.g. the
            // icon-font glyphs that make up 90% of this load order's ACTI/FLOR RNAM.
            if (NonTranslatableText.IsMarkupOnly(winner.Text))
            {
                markupOnly++;
                log.Detail("除外: マークアップ/アイコングリフ（翻訳すると表示が壊れる）", "Excluded: markup/icon-glyph (translating it would break the display)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: markup/icon-glyph (\"{winner.Text}\")");
                continue;
            }

            // v0.9.0, three "the player never reads this" exclusions. Each is logged
            // in full rather than merely counted: they are judgment calls about
            // visibility, so they must stay reviewable after the fact.
            if (hiddenRecords.Contains(formKey))
            {
                notPlayerFacing++;
                log.Detail("除外: レコード自体がUIに表示されない（MGEFのHideInUI / 非表示・非習得のPERK / 全効果が非表示のAbility / ARMO・WEAPのNonPlayable）",
                    "Excluded: the record itself is never shown in the UI (MGEF HideInUI / hidden-or-unlearnable PERK / an Ability whose effects are all hidden / ARMO or WEAP NonPlayable)",
                    $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: not player-facing (record-level flag) (\"{winner.Text}\")");
                continue;
            }

            if (NonTranslatableText.IsAssetPath(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: 文字列全体がアセットパス（訳すとパスが壊れる）", "Excluded: entire string is an asset path (translating would break the path)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: asset path (\"{winner.Text}\")");
                continue;
            }

            if (NonTranslatableText.LooksLikeInternalIdentifier(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: EditorID風の内部識別子（表示名として書かれていない）", "Excluded: looks like an internal EditorID-style identifier (not written as a display name)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: looks like an internal identifier (\"{winner.Text}\")");
                continue;
            }

            // v0.48.1: "AudioTemplate ..." (voice-template-only NPC, sometimes
            // written with a space and so missed by LooksLikeInternalIdentifier's
            // no-whitespace requirement).
            if (NonTranslatableText.LooksLikeAudioTemplateName(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: 音声テンプレート専用の内部NPC名（ゲーム中で表示されない）", "Excluded: an audio-template-only internal NPC name (never shown in the game)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: audio template name (\"{winner.Text}\")");
                continue;
            }

            // v0.48.1: "Do Not Delete ..." (Creation Kit FormID-ordering
            // placeholder record, never a real display name).
            if (NonTranslatableText.LooksLikeDoNotDeleteNote(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: 削除禁止の内部プレースホルダーレコード（ゲーム中で表示されない）", "Excluded: a \"do not delete\" internal placeholder record (never shown in the game)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: \"do not delete\" placeholder (\"{winner.Text}\")");
                continue;
            }

            // v0.27.0, scoped to QUST FULL only: internal version-tracking quest
            // names ("Retroactive fixes for 4.2.1") never shown in the journal.
            if (dsdType == "QUST FULL" && NonTranslatableText.LooksLikeVersionTrackingQuestName(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: バージョン管理用の内部クエスト名（QUST FULL、ジャーナルに表示されない）", "Excluded: internal version-tracking quest name (QUST FULL, never shown in the journal)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: version-tracking quest name (\"{winner.Text}\")");
                continue;
            }

            // v0.27.0: internal effect/placeholder names ("AtronachFrost fx").
            if (NonTranslatableText.LooksLikeInternalFxName(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: 内部エフェクト名（fxサフィックス）", "Excluded: internal effect name (fx suffix)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: internal fx name (\"{winner.Text}\")");
                continue;
            }

            // v0.30.0, scoped to name types: a developer "temp" marker anywhere in
            // the name ("TEMP - LIGHTS OUT", "Colovian Brandy TEMP"). Sits ahead of
            // IsPlaceholderToken, which only catches the bare-"TEMP" subset.
            if (dsdType.EndsWith(" FULL", StringComparison.Ordinal)
                && NonTranslatableText.LooksLikeDevTempPlaceholder(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: 開発用tempマーカーを含む名称（未完成の仮レコード）", "Excluded: contains a developer temp marker (unfinished placeholder record)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: dev temp placeholder name (\"{winner.Text}\")");
                continue;
            }

            // v0.27.0: literal placeholder tokens ("xxx", "TODO", ...).
            if (NonTranslatableText.IsPlaceholderToken(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: プレースホルダー文字列", "Excluded: placeholder string", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: placeholder token (\"{winner.Text}\")");
                continue;
            }

            // v0.27.0: all-uppercase, no-vowel acronym/tag ("YMMP").
            if (NonTranslatableText.LooksLikeNonWordAcronym(winner.Text))
            {
                notPlayerFacing++;
                log.Detail("除外: 母音を含まない全大文字語（辞書に存在しない語・略称の可能性）", "Excluded: all-uppercase word with no vowels (possibly not a dictionary word / an acronym)", $"[{dsdType}] {winner.Text}");
                trace?.Trace($"Exclude [{dsdType}] {formKey}: all-uppercase no-vowel acronym (\"{winner.Text}\")");
                continue;
            }

            trace?.Trace($"Candidate [{dsdType}] {formKey}: \"{winner.Text}\" (winning plugin: {winner.Source.FileName})");
            candidates.Add(new Candidate(winner.Source.FileName, formKey.ToString(), dsdType, winner.Text, index, winner.EditorId, winner.Context));
        }

        return (candidates, alreadyCoveredByDsd, markupOnly, notPlayerFacing, staleIncluded, staleReviewLog, coveredByPlugin);
    }
}
