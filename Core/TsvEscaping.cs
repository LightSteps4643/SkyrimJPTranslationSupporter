namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// The single Escape/Unescape pair every TSV writer/reader in the pipeline uses
/// to keep a field's own tabs/newlines/backslashes from corrupting the TSV
/// structure. v0.46.1: consolidated from 6 independent copies (CandidateIo,
/// CorpusIo, CoverageReportWriter, PromptGenerator, AutoResolveReportWriter,
/// DsdJsonGenerator) — those all had to agree on the exact same scheme for a
/// value written by one file to round-trip correctly when read by another, so
/// keeping them as separate copies was a real correctness hazard (a fix or
/// tweak applied to one copy and missed in another would silently corrupt any
/// field containing an embedded tab/newline — real data has had these, e.g.
/// multi-paragraph BOOK DESC text).
/// </summary>
public static class TsvEscaping
{
    public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "");

    public static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
}
