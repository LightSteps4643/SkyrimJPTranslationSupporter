namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Reads a header-having TSV file the CLI already wrote, as plain rows keyed by
/// column name. Deliberately dumb (no type coercion, no domain knowledge of what
/// a column means) — each form parses the columns it needs itself, so this stays
/// a data-file reader and not a second copy of any CLI-side model.
/// </summary>
public static class TsvReader
{
    public static List<Dictionary<string, string>> Read(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return rows;

        using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
        var headerLine = reader.ReadLine();
        if (headerLine == null) return rows;
        var headers = headerLine.Split('\t');

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var cells = line.Split('\t');
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < headers.Length; i++)
                row[headers[i]] = i < cells.Length ? cells[i] : "";
            rows.Add(row);
        }
        return rows;
    }
}
