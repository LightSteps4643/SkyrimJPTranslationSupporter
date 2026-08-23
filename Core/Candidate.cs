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
/// updated rather than re-translated from nothing.</summary>
public sealed record Candidate(
    string WinningPlugin,
    string FormId,
    string RecordType,
    string CurrentText,
    int Index = 0,
    string EditorId = "",
    string Context = "",
    string StaleOriginal = "",
    string StaleTranslation = "");
