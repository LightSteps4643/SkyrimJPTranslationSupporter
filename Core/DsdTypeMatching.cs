namespace SkyrimJPStringPatcher.Core;

/// <summary>How DSD identifies "the same field" for a given "type" string when
/// deciding whether it's already covered by an existing translation.</summary>
public enum DsdMatchStrategy
{
    /// <summary>The common case: FormID + type + index together identify one field.</summary>
    ByFormIdIndex,

    /// <summary>kGameSetting ("GMST DATA"): matches by EditorID, not FormID — GMST
    /// FormIDs are documented as unstable across game versions.</summary>
    ByEditorId,

    /// <summary>kRuntimeLegacy ("QUST CNAM"): DSD's one exception — matches by the
    /// literal ORIGINAL TEXT content across every log entry on the quest, not by
    /// index at all.</summary>
    ByOriginalText,
}

/// <summary>
/// Single source of truth for "how does DSD match this type string", so this
/// knowledge lives in one place instead of being re-derived (or, worse,
/// silently forgotten) at every call site that needs to know it. Only types
/// that DON'T use the default FormID+index matching need an entry here — see
/// DESIGN_NOTES.md's DSD TranslationType table (read directly from
/// SSE-Dynamic-String-Distributor's Manager.cpp) for the full picture of what
/// each of DSD's 17 types actually is.
/// </summary>
public static class DsdTypeMatching
{
    private static readonly Dictionary<string, DsdMatchStrategy> Overrides = new()
    {
        ["GMST DATA"] = DsdMatchStrategy.ByEditorId,
        ["QUST CNAM"] = DsdMatchStrategy.ByOriginalText,
    };

    public static DsdMatchStrategy GetStrategy(string dsdType) =>
        Overrides.TryGetValue(dsdType, out var strategy) ? strategy : DsdMatchStrategy.ByFormIdIndex;
}
