namespace SkyrimJPStringPatcher.Core;

/// <summary>Shared single-line, length-capped preview text for report TSVs
/// (CoverageReportWriter, AutoResolveReportWriter) — enough to recognize what
/// kind of string it is without opening the underlying candidates.tsv.</summary>
public static class TextPreview
{
    public static string Truncate(string s, int maxLength)
    {
        var oneLine = s.Replace("\r", "").Replace("\n", " ");
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
    }
}
