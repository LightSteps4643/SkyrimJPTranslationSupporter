namespace SkyrimJPStringPatcher.SkyPatcher;

/// <summary>The shape of what we write into SkyPatcher .ini files, shared by the writer (generate) and the
/// reader that carries an earlier output forward (scan). Our files live in a subfolder of the type folder
/// named so that it sorts last in SkyPatcher's 0-&gt;z file order; before each translated name we write a
/// comment (';' lines are ignored by SkyPatcher) recording the original and how it was translated:
/// "; SJPTS &lt;fullName|shortName&gt; | &lt;method&gt; | &lt;original&gt;". The original may itself contain " | ", so only
/// the first two separators split the fields.</summary>
public static class SkyPatcherOutputFormat
{
    public const string FolderName = "zzz_SJPTS";

    private const string Prefix = "; SJPTS ";
    private const string Separator = " | ";

    public static string FormatComment(SkyPatcherNameKind kind, string notes, string original) =>
        $"{Prefix}{KindName(kind)}{Separator}{notes}{Separator}{original}";

    public static bool TryParseComment(string line, out SkyPatcherNameKind kind, out string notes, out string original)
    {
        kind = default;
        notes = original = "";
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var rest = line[Prefix.Length..];
        var first = rest.IndexOf(Separator, StringComparison.Ordinal);
        if (first < 0) return false;
        if (!TryParseKind(rest[..first], out kind)) return false;

        var afterKind = rest[(first + Separator.Length)..];
        var second = afterKind.IndexOf(Separator, StringComparison.Ordinal);
        if (second < 0) return false;

        notes = afterKind[..second];
        original = afterKind[(second + Separator.Length)..];
        return true;
    }

    private static string KindName(SkyPatcherNameKind kind) => kind == SkyPatcherNameKind.FullName ? "fullName" : "shortName";

    private static bool TryParseKind(string text, out SkyPatcherNameKind kind)
    {
        switch (text)
        {
            case "fullName": kind = SkyPatcherNameKind.FullName; return true;
            case "shortName": kind = SkyPatcherNameKind.ShortName; return true;
            default: kind = default; return false;
        }
    }
}
