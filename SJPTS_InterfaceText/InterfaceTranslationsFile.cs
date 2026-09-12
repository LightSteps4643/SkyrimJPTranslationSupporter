using System.Text;

namespace SJPTS_InterfaceText;

public sealed record TranslationEntry(string Key, string English, string? Japanese);

/// <summary>Reads/writes Bethesda's `$Key&lt;TAB&gt;Text` Interface\Translations
/// format — normally UTF-16LE with BOM, CRLF lines, blank lines allowed and
/// preserved as-is on read (skipped) but not required to round-trip exactly
/// for this prototype (output is a fresh merge, not an edit-in-place).</summary>
public static class InterfaceTranslationsFile
{
    private static readonly Encoding Utf16LeWithBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

    /// <summary>Ordered key->value pairs (insertion order preserved, matches
    /// the source file's line order). A key repeated in the source keeps only
    /// the LAST occurrence's value, matching how the game's own loader would
    /// behave reading the same file top-to-bottom.</summary>
    /// <remarks>2026-09-12: real-data finding (a mod shipping a plain-ASCII
    /// "aaa_english.txt", no BOM at all) — File.ReadAllText's own BOM sniffing
    /// (UTF-8/UTF-16/UTF-32) only kicks in when a BOM IS present; with none,
    /// it silently falls back to whatever encoding is PASSED IN, which used to
    /// be hardcoded UTF-16LE here. That misreads a genuinely BOM-less UTF-8/
    /// ASCII file as UTF-16LE (each byte pair reinterpreted as one wide char),
    /// producing garbage with no recognizable tab-separated lines — 0 entries,
    /// silently, no error. UTF-8 is the correct BOM-less fallback (ASCII is a
    /// strict subset, so a plain-ASCII file like this one still round-trips
    /// correctly); a real Bethesda-convention UTF-16LE file still decodes
    /// correctly regardless, since its own BOM is auto-detected first.</remarks>
    public static List<(string Key, string Value)> Parse(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false)).TrimStart('﻿');
        var order = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue; // no key, or malformed line — skip rather than guess
            var key = line[..tab];
            var value = line[(tab + 1)..];
            if (!map.ContainsKey(key)) order.Add(key);
            map[key] = value;
        }

        return order.Select(k => (k, map[k])).ToList();
    }

    public static void Write(string path, IReadOnlyList<(string Key, string Value)> entries)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path, append: false, Utf16LeWithBom);
        writer.NewLine = "\r\n";
        foreach (var (key, value) in entries)
            writer.WriteLine($"{key}\t{value}");
    }
}
