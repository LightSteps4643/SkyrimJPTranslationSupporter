namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Builds the lines of a plugin's translations.tsv for the detail window's save.
/// Header-driven: every column the file already has is written back, in its
/// original position — so columns this window does not know about (the SkyPatcher
/// ones, or any a later version adds) survive a save. The nine base columns the
/// CLI has always written are guaranteed present: any a file written by an older
/// build lacks are appended. A row the user edited gets the new Japanese, Notes =
/// SJPTS_ModifiedByUser and an emptied TranslationCheck (a human has reviewed it);
/// every other cell of every row is written exactly as read.
/// </summary>
public static class TranslationsTsvWriter
{
    private static readonly string[] BaseColumns =
        ["FormId", "WinningPlugin", "RecordType", "EnglishText", "Japanese", "Notes", "Index", "EditorId", "TranslationCheck"];

    /// <param name="rows">Rows as <see cref="TsvReader.Read"/> returned them — cell values still TSV-escaped.</param>
    /// <param name="editedJapanese">For a row: the user's edited Japanese (unescaped, as shown in the grid), or null if the row was not edited.</param>
    public static List<string> BuildLines(
        IReadOnlyList<string> header,
        IReadOnlyList<Dictionary<string, string>> rows,
        Func<Dictionary<string, string>, string?> editedJapanese)
    {
        var columns = header.Concat(BaseColumns.Where(c => !header.Contains(c, StringComparer.Ordinal))).ToList();

        var lines = new List<string> { string.Join('\t', columns) };
        foreach (var row in rows)
        {
            var edit = editedJapanese(row);
            var cells = columns.Select(column => edit switch
            {
                not null when column == "Japanese" => TsvEscaping.Escape(edit),
                not null when column == "Notes" => "SJPTS_ModifiedByUser",
                not null when column == "TranslationCheck" => "",
                _ => row.GetValueOrDefault(column, ""),
            });
            lines.Add(string.Join('\t', cells));
        }
        return lines;
    }
}
