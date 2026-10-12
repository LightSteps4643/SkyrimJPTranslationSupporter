using System.Text.RegularExpressions;

namespace SkyrimJPStringPatcher.SkyPatcher;

public enum SkyPatcherNameKind { FullName, ShortName }

/// <summary>One "fullName=~…~" / "shortName=~…~" operation: the name, without its ~ wrapping.</summary>
public sealed record SkyPatcherNameOp(SkyPatcherNameKind Kind, string Text);

/// <summary>A line of a SkyPatcher .ini that sets a display name. <see cref="FilterSegments"/> are the
/// line's filter segments exactly as written (trimmed), in order — what we copy so our output targets
/// the same records. Every other operation on the line is deliberately not kept.</summary>
public sealed record SkyPatcherLine(int LineNumber, IReadOnlyList<string> FilterSegments, IReadOnlyList<SkyPatcherNameOp> NameOps)
{
    public string FilterPart => string.Join(":", FilterSegments);
}

/// <summary>Reads one line of a SkyPatcher .ini (grammar §2/§3/§5/§6). A line is a patch string of
/// ':'-separated segments. The one place ':' does not separate is inside the ~…~ of a name operation:
/// the name is wrapped (grammar §7), so the value ends at the closing ~. Only the value right after
/// "fullName=" / "shortName=" is read that way — "~" also separates the sub-arguments of other
/// operations (e.g. alternateTexturesToAdd=TextureSet~Name3D~2) and must not be mistaken for a wrapper.</summary>
public static class SkyPatcherLineParser
{
    private static readonly Regex NameOpStart =
        new(@"\G(?<key>fullName|shortName)\s*=\s*~", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <returns>null for a blank line, a ';' comment, or a line with no readable name operation.</returns>
    public static SkyPatcherLine? Parse(string text, int lineNumber)
    {
        var s = text.Trim();
        if (s.Length == 0 || s[0] == ';') return null;

        var filters = new List<string>();
        var ops = new List<SkyPatcherNameOp>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;

            var m = NameOpStart.Match(s, i);
            if (m.Success)
            {
                var valueStart = m.Index + m.Length;
                var close = s.IndexOf('~', valueStart);
                if (close >= 0)
                {
                    var kind = m.Groups["key"].Value.Equals("fullName", StringComparison.OrdinalIgnoreCase)
                        ? SkyPatcherNameKind.FullName : SkyPatcherNameKind.ShortName;
                    ops.Add(new SkyPatcherNameOp(kind, s[valueStart..close]));
                    var afterClose = s.IndexOf(':', close + 1);
                    if (afterClose < 0) break;
                    i = afterClose + 1;
                    continue;
                }
            }

            var next = s.IndexOf(':', i);
            var segment = (next < 0 ? s[i..] : s[i..next]).Trim();
            if (segment.Length > 0 && IsFilterKey(KeyOf(segment))) filters.Add(segment);
            if (next < 0) break;
            i = next + 1;
        }

        return ops.Count == 0 ? null : new SkyPatcherLine(lineNumber, filters, ops);
    }

    /// <summary>The text before the first '=' of a segment.</summary>
    public static string KeyOf(string segment)
    {
        var eq = segment.IndexOf('=');
        return (eq < 0 ? segment : segment[..eq]).Trim();
    }

    /// <summary>Every filter the grammar documents for the name-bearing types is one of: filterBy…
    /// (incl. …Or/…Excluded), restrictTo…, hasPlugins/hasPluginsOr, skipRecord…, or a name ending in
    /// "Excluded" (modNamesLastOverriddenExcluded, effectShadersExcluded). Anything else is an operation.</summary>
    public static bool IsFilterKey(string key) =>
        key.StartsWith("filterBy", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("restrictTo", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("hasPlugins", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("skipRecord", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("Excluded", StringComparison.OrdinalIgnoreCase);
}
