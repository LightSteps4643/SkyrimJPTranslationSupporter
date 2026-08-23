using System.Text.Json.Serialization;

namespace SkyrimJPStringPatcher.Core;

/// <summary>One row of a Dynamic String Distributor JSON file.</summary>
public sealed class DsdEntry
{
    [JsonPropertyName("editor_id")] public string EditorId { get; init; } = "";
    [JsonPropertyName("form_id")] public string FormId { get; init; } = "";
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("original")] public string Original { get; init; } = "";
    [JsonPropertyName("string")] public string String { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
}
