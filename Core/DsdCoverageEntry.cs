using System.Text.Json.Serialization;

namespace SkyrimJPStringPatcher.Core;

/// <summary>One row as read back from an existing (community-authored) DSD json.</summary>
public sealed class DsdSourceEntry
{
    [JsonPropertyName("editor_id")] public string? EditorId { get; init; }
    [JsonPropertyName("form_id")] public string FormId { get; init; } = "";
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("original")] public string? Original { get; init; }
    [JsonPropertyName("string")] public string String { get; init; } = "";
    [JsonPropertyName("status")] public string? Status { get; init; }
}

public enum DsdCoverageVerdict
{
    /// <summary>Winning DSD entry exists, its "string" is Japanese — treat the FormID/field as already handled.</summary>
    VerifiedJapanese,
    /// <summary>An entry exists but its "string" contains no Japanese — probably not a translation row, don't trust it.</summary>
    PresentButNotJapanese,
    /// <summary>An entry exists and is Japanese, but its recorded "original" no longer matches the record's current
    /// text — the source mod likely updated and this translation may be stale / may not apply at runtime.</summary>
    StaleOriginalMismatch,
}

public sealed record DsdCoverageResult(DsdCoverageVerdict Verdict, string SourceFile, string TranslatedString);
