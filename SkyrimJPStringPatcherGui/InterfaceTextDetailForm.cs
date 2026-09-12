namespace SkyrimJPStringPatcherGui;

/// <summary>Viewer/editor for one mod's interface_translations.tsv — copied from (and
/// trimmed down against) TranslationDetailForm.cs's DataGridView review
/// pattern, against the simpler InterfaceText row shape (Key/English/
/// Japanese/Resolved/Notes — no FormId/RecordType/EditorId, no escape/unescape
/// since these values can never contain a literal tab or newline: the file
/// format itself is one value per line).
///
/// v0.51.0's "GUI has zero project references to Core/Translation/etc." holds
/// here too — <see cref="InterfaceTranslationRow"/>/tsv read-write below is a small
/// deliberate duplication of SJPTS_InterfaceText/InterfaceTranslationsTsv.cs's own
/// (tiny, ~20-line) logic, the same call TranslationDetailForm.cs already
/// made for Core/TsvEscaping.cs's Escape/Unescape — not worth a project
/// reference that would drag Mutagen in transitively through
/// SkyrimJPStringPatcher.csproj.</summary>
public sealed record InterfaceTranslationRow(string Key, string English, string Japanese, bool Resolved, string Notes = "");

public sealed class InterfaceTextDetailForm : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
    };
    private readonly TextBox _txtFilter = new() { Dock = DockStyle.Fill };
    private readonly Button _btnOk = new() { Text = "OK（保存）", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "保存せず閉じる", AutoSize = true };
    private readonly Label _lblEditCount = new() { AutoSize = true };

    private List<InterfaceTranslationRow> _rows = new();
    private string _path = "";
    private readonly Dictionary<string, string> _edits = new(); // Key -> edited Japanese
    private readonly Action? _onSaved;

    public InterfaceTextDetailForm(string modName, string tsvPath, Action? onSaved = null)
    {
        _onSaved = onSaved;
        Text = $"UIテキスト翻訳の詳細 — {modName}";
        Width = 1000;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;

        BuildColumns();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(new Label { Text = "絞り込み（Key・原文・訳文）:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 10, 6, 3) }, 0, 0);
        top.Controls.Add(_txtFilter, 1, 0);
        Controls.Add(_grid);
        Controls.Add(top);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        _btnOk.Click += BtnOk_Click;
        _btnCancel.Click += BtnCancel_Click;
        bottom.Controls.Add(_btnOk);
        bottom.Controls.Add(_btnCancel);
        bottom.Controls.Add(_lblEditCount);
        _lblEditCount.Margin = new Padding(3, 10, 14, 3);
        Controls.Add(bottom);

        LoadData(tsvPath);
        _txtFilter.TextChanged += (_, _) => ApplyFilter();
        _grid.CellValueChanged += Grid_CellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Key", Width = 260, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "English", HeaderText = "原文", Width = 300, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Alignment = DataGridViewContentAlignment.TopLeft },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Japanese", HeaderText = "訳文（編集可）", Width = 300, ReadOnly = false,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Alignment = DataGridViewContentAlignment.TopLeft },
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Resolved", HeaderText = "対応済み", Width = 70, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Notes", HeaderText = "Notes", Width = 220, ReadOnly = true });
    }

    private void LoadData(string path)
    {
        _path = path;
        _rows = ReadTsv(path);
        Bind(_rows);
        Text = $"UIテキスト翻訳の詳細 — {Path.GetFileName(Path.GetDirectoryName(path))} — {_rows.Count}件";
    }

    private void Bind(List<InterfaceTranslationRow> rows)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var row in rows)
        {
            var edited = _edits.TryGetValue(row.Key, out var editedJapanese);
            var japanese = edited ? editedJapanese! : row.Japanese;
            var resolved = edited || row.Resolved;
            var notes = edited ? "ModifiedByUser" : row.Notes;

            var idx = _grid.Rows.Add(row.Key, row.English, japanese, resolved, notes);
            var gridRow = _grid.Rows[idx];
            gridRow.Tag = row.Key;
            if (!resolved)
                gridRow.DefaultCellStyle.BackColor = Color.FromArgb(255, 245, 235); // 未対応行を薄く強調
            else if (edited)
                gridRow.DefaultCellStyle.BackColor = Color.FromArgb(235, 245, 255); // 編集済み行を薄く強調
        }
        _grid.ResumeLayout();
        UpdateEditCountLabel();
    }

    private void Grid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Japanese") return;

        var gridRow = _grid.Rows[e.RowIndex];
        var key = (string)gridRow.Tag!;
        var original = _rows.FirstOrDefault(r => r.Key == key);
        var originalJapanese = original?.Japanese ?? "";
        var newValue = (string)(gridRow.Cells["Japanese"].Value ?? "");

        if (newValue == originalJapanese)
        {
            _edits.Remove(key);
            gridRow.Cells["Resolved"].Value = original?.Resolved ?? false;
            gridRow.Cells["Notes"].Value = original?.Notes ?? "";
            gridRow.DefaultCellStyle.BackColor = (original?.Resolved ?? false) ? Color.White : Color.FromArgb(255, 245, 235);
        }
        else
        {
            _edits[key] = newValue;
            gridRow.Cells["Resolved"].Value = true;
            gridRow.Cells["Notes"].Value = "ModifiedByUser";
            gridRow.DefaultCellStyle.BackColor = Color.FromArgb(235, 245, 255);
        }
        UpdateEditCountLabel();
    }

    private void UpdateEditCountLabel() =>
        _lblEditCount.Text = _edits.Count > 0 ? $"{_edits.Count}件編集済み（未保存）" : "";

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);

        if (_edits.Count > 0)
        {
            try
            {
                SaveChanges();
                MessageBox.Show(this, $"{_edits.Count}件の変更を保存しました。\n{_path}", "保存完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _onSaved?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"保存に失敗しました:\n{ex.Message}", "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        Close();
    }

    private void BtnCancel_Click(object? sender, EventArgs e)
    {
        if (_edits.Count > 0)
        {
            var result = MessageBox.Show(this, $"{_edits.Count}件の変更を保存せずに閉じます。よろしいですか？",
                "確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK) return;
        }
        Close();
    }

    private void SaveChanges()
    {
        var updated = _rows.Select(r =>
            _edits.TryGetValue(r.Key, out var jp) ? r with { Japanese = jp, Resolved = true, Notes = "ModifiedByUser" } : r
        ).ToList();
        WriteTsv(_path, updated);
        _rows = updated;
        _edits.Clear();
    }

    /// <summary>Mirrors SJPTS_InterfaceText/InterfaceTranslationsTsv.cs's Read exactly
    /// (same 5-column tab-separated format, header skipped) — see this class's
    /// remarks for why it's duplicated here rather than referenced.</summary>
    private static List<InterfaceTranslationRow> ReadTsv(string path)
    {
        var rows = new List<InterfaceTranslationRow>();
        if (!File.Exists(path)) return rows;
        foreach (var line in File.ReadAllLines(path, System.Text.Encoding.UTF8).Skip(1))
        {
            if (line.Length == 0) continue;
            var cols = line.Split('\t');
            if (cols.Length < 4) continue;
            var notes = cols.Length >= 5 ? cols[4] : "";
            rows.Add(new InterfaceTranslationRow(cols[0], cols[1], cols[2], cols[3] == "1", notes));
        }
        return rows;
    }

    /// <summary>Mirrors SJPTS_InterfaceText/InterfaceTranslationsTsv.cs's Write exactly.</summary>
    private static void WriteTsv(string path, IReadOnlyList<InterfaceTranslationRow> rows)
    {
        using var writer = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
        writer.NewLine = "\n";
        writer.WriteLine("Key\tEnglish\tJapanese\tResolved\tNotes");
        foreach (var row in rows)
            writer.WriteLine($"{row.Key}\t{row.English}\t{row.Japanese}\t{(row.Resolved ? "1" : "0")}\t{row.Notes}");
    }

    private void ApplyFilter()
    {
        var text = _txtFilter.Text.Trim();
        Bind(text.Length == 0
            ? _rows
            : _rows.Where(r =>
                r.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.English.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.Japanese.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (_edits.TryGetValue(r.Key, out var e) && e.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList());
    }
}
