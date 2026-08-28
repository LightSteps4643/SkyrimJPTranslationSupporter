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

    /// <summary>v0.55.4: rewritten from three sequential whole-string Replace()
    /// calls (\n, then \t, then \\) to a single left-to-right scan. The old
    /// approach broke on a literal backslash immediately followed by a literal
    /// 'n' or 't' (e.g. a Windows path like "path\notes.txt"): Escape() doubles
    /// every backslash, and the old Unescape ran its "\n"/"\t" replacements
    /// BEFORE its "\\" replacement, so the second half of a doubled-backslash
    /// pair would accidentally combine with an unrelated following literal 'n'/
    /// 't' into what looked like an escaped newline/tab — corrupting the field.
    /// This scan instead decides what a backslash means by looking at ONLY the
    /// single character immediately following it in the escaped text, then
    /// consumes both characters together as a unit and advances past them — so
    /// a character already used to resolve one escape can never be reused as
    /// half of a different one.</summary>
    public static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                switch (s[i + 1])
                {
                    case 'n': sb.Append('\n'); i++; continue;
                    case 't': sb.Append('\t'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
