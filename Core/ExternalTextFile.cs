namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// Reads a text file another tool wrote — a SkyPatcher .ini, MO2's modlist.txt / plugins.txt / ModOrganizer.ini —
/// line by line with the rule used for a plugin's text (<see cref="Utf8ThenWindows1252Encoding"/>): a line is read
/// as UTF-8, and as Windows-1252 when its bytes are not valid UTF-8. Such files carry no encoding label either,
/// and reading them as UTF-8 alone turns a Windows-1252 "é" into U+FFFD, which cannot be repaired afterwards.
/// Lines split as <see cref="File.ReadAllLines(string)"/> splits them (CRLF, LF or a lone CR; a final line break
/// adds no empty line); a UTF-8 byte order mark is dropped. A line break byte (0x0A, 0x0D) never occurs inside a
/// UTF-8 or Windows-1252 character, so splitting before decoding is safe.
/// </summary>
public static class ExternalTextFile
{
    private static readonly Utf8ThenWindows1252Encoding Rule = new();

    public static string[] ReadAllLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var lines = new List<string>();
        var lineStart = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        for (var i = lineStart; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n' && bytes[i] != (byte)'\r') continue;
            lines.Add(Rule.GetString(bytes.AsSpan(lineStart, i - lineStart)));
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') i++;
            lineStart = i + 1;
        }
        if (lineStart < bytes.Length) lines.Add(Rule.GetString(bytes.AsSpan(lineStart)));
        return [.. lines];
    }
}
