namespace SkyrimJPStringPatcher.Core;

/// <summary>One untranslated (winning) record field — the unit PickUpTarget
/// produces and Translation/GenerateDsdFile consume. Serialized as TSV between
/// stages (see CandidateIo).
///
/// <paramref name="Index"/> disambiguates multiple candidates on the SAME
/// FormId+RecordType (e.g. two dialogue responses, or two quest objectives) —
/// it's meaningful for DSD's indexed types (kQuestObjective, kRuntime2,
/// kRuntimeIndex, kButtonText1/2) and always 0 for FormID-only types. For "QUST
/// CNAM" specifically it is NOT a DSD-meaningful index at all (that type matches
/// by original TEXT content at runtime, DSD's one exception — see DESIGN_NOTES),
/// it is only used internally to keep multiple log entries on the same quest
/// from colliding as candidates.
///
/// <paramref name="EditorId"/> is populated only for EditorID-matched types
/// (currently just "GMST DATA" — GMST FormIDs are documented as unstable
/// across game versions, so DSD matches game settings by EditorID instead).
///
/// <paramref name="StaleOriginal"/>/<paramref name="StaleTranslation"/> are set
/// only under PickUpTarget's opt-in <c>--include-stale</c> (v0.8.0): the record
/// IS already covered by a shipped DSD translation, but that translation was
/// authored against a DIFFERENT original text than the record now carries.
/// Carrying both forward lets the prompt show the translator what the previous
/// translation said and what it was translating, so an out-of-date entry can be
/// updated rather than re-translated from nothing.
///
/// <paramref name="Warning"/> (v0.54.2, DESIGN_NOTES.md known issue 21): set
/// when PickUpTarget's ① exclusion check for this record's FormKey failed to
/// read and the record was fail-open included anyway. Translation writes this
/// into translations.tsv's Notes column, but ONLY while the row stays
/// unresolved (see PromptGenerator.WriteTranslationTemplate) — once any method
/// resolves it, the resolution tag takes over and this warning is dropped, on
/// the reasoning that a successfully-translated string no longer needs review.
///
/// <paramref name="CrossModPrecedentJapanese"/>/<paramref name="CrossModPrecedentNeedsReview"/>
/// (v0.56.0): set when this exact (FormKey, RecordType, Index) chain has a
/// NON-winning contributor whose text was Japanese — some other mod (or an
/// earlier revision of the same one) already translated this very record,
/// before the current winner's override carried English text back in. Takes
/// priority over every other resolution method in Translation (even ①コーパス
/// 完全一致) since it is keyed on record identity, not text matching, so it
/// survives incidental reformatting a text-only corpus match would miss.
/// <paramref name="CrossModPrecedentNeedsReview"/> is true whenever the tool
/// cannot positively confirm the precedent still applies (either the
/// immediately-preceding chain entry's English text differs from the current
/// winner's — a genuine known issue 21-style stale precedent — or no English
/// reference exists in the chain at all to compare against, e.g. a mod
/// translated directly in place with no separate English-only file ever
/// installed). This tool does not adjudicate whether a translation is still
/// objectively correct for the current text (mirrors the existing DSD
/// stale-coverage handling): the precedent is still applied either way, this
/// flag only controls whether a review warning is logged.</summary>
public sealed record Candidate(
    string WinningPlugin,
    string FormId,
    string RecordType,
    string CurrentText,
    int Index = 0,
    string EditorId = "",
    string Context = "",
    string StaleOriginal = "",
    string StaleTranslation = "",
    string Warning = "",
    string CrossModPrecedentJapanese = "",
    bool CrossModPrecedentNeedsReview = false);
