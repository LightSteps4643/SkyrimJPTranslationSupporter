using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcherGui;

/// <summary>Viewer/editor for one mod's interface_translations.tsv — copied from (and
/// trimmed down against) TranslationDetailForm.cs's DataGridView review
/// pattern, against the simpler InterfaceText row shape (Key/English/
/// Japanese/Resolved/Notes — no FormId/RecordType/EditorId).
///
/// v0.51.0's "GUI has zero project references to Core/Translation/etc." holds
/// here too — <see cref="InterfaceTranslationRow"/>/tsv read-write below is a small
/// deliberate duplication of SJPTS_InterfaceText/InterfaceTranslationsTsv.cs's own
/// (tiny, ~20-line) logic, not worth a project reference that would drag
/// Mutagen in transitively through SkyrimJPStringPatcher.csproj.
///
/// 2026-09-12: values here CAN contain a literal tab/newline (an LLM response
/// or a user's own multi-line paste into the grid) despite the file format
/// being nominally one value per line — an unescaped tab/newline silently
/// corrupts or drops the row on the next read (see Core/TsvEscaping.cs's own
/// remarks for the general problem). Escape/Unescape are shared via a
/// file-level link to Core/TsvEscaping.cs (see SkyrimJPStringPatcherGui.csproj)
/// rather than a further private duplicate — the link adds no project
/// reference (Core itself is still not referenced), just this one pure
/// string-utility file compiled directly into this assembly.</summary>
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

    // 2026-09-12: TranslationDetailForm.cs（ESP側）と同じ複数行編集の仕組みを
    // 移植——原文・訳文セルをクリックすると行を拡張し複数行スクロール表示に
    // する。それぞれの役割はTranslationDetailForm.csの同名フィールド・
    // メソッドのコメント参照。
    private int _defaultRowHeight;
    private int _expandedRowIndex = -1;

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

        // 2026-09-12: TranslationDetailForm.cs（ESP側）と同じ配線——原文・訳文
        // セルをシングルクリックで複数行表示に入れられるようにする。
        _grid.EditingControlShowing += Grid_EditingControlShowing;
        _grid.CellBeginEdit += Grid_CellBeginEdit;
        _grid.CellEndEdit += Grid_CellEndEdit;
        _grid.CellClick += Grid_CellClick;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Key", Width = 260, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            // 2026-09-12: 列レベルのReadOnlyは外した——TranslationDetailForm.cs
            // と同じ理由（クリックで訳文と同じ複数行スクロール表示に入るには
            // BeginEditが必要）。実際の書き換えは起こらないよう、編集用
            // テキストボックス自体をReadOnlyにし、離脱時にも値を強制的に戻す
            // （Grid_EditingControlShowing/Grid_CellEndEdit参照）。
            Name = "English", HeaderText = "原文", Width = 300,
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

    // 2026-09-12: 以下4メソッドはTranslationDetailForm.cs（ESP側）の同名
    // メソッドの移植——複数行編集を「クリック操作が完全に終わったあとにしか
    // 発火しないCellClickでだけ」行毎の拡張/折りたたみを行う理由等、詳細な
    // 経緯はそちらのコメント参照。

    private bool IsMultilineColumn(int columnIndex)
    {
        var name = _grid.Columns[columnIndex].Name;
        return name is "Japanese" or "English";
    }

    private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;

        if (_expandedRowIndex != -1 && _expandedRowIndex != e.RowIndex)
        {
            if (_expandedRowIndex < _grid.Rows.Count) _grid.Rows[_expandedRowIndex].Height = _defaultRowHeight;
            _expandedRowIndex = -1;
        }

        if (!IsMultilineColumn(e.ColumnIndex)) return;
        _expandedRowIndex = e.RowIndex;
        _grid.BeginEdit(false); // selectAll:false — 全選択状態で編集開始すると誤って上書きしやすいため
    }

    private void Grid_CellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (!IsMultilineColumn(e.ColumnIndex)) return;
        if (_defaultRowHeight == 0) _defaultRowHeight = _grid.Rows[e.RowIndex].Height;
        _grid.Rows[e.RowIndex].Height = Math.Min(200, Math.Max(_defaultRowHeight, _grid.ClientSize.Height / 3));
        _grid.UpdateRowHeightInfo(e.RowIndex, true);
    }

    /// <summary>原文セルは表示専用 — SaveChanges自体は常に元のEnglishを書き戻すので
    /// 実害はないが、編集モードで万一キー入力があった場合にグリッド上の見た目まで
    /// 書き変わって見えるのは紛らわしいため、離脱時に必ず元の値へ戻す。</summary>
    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_grid.Columns[e.ColumnIndex].Name != "English") return;
        var key = (string)_grid.Rows[e.RowIndex].Tag!;
        var original = _rows.FirstOrDefault(r => r.Key == key);
        _grid.Rows[e.RowIndex].Cells["English"].Value = original?.English ?? "";
    }

    private void Grid_EditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (_grid.CurrentCell?.OwningColumn == null || !IsMultilineColumn(_grid.CurrentCell.ColumnIndex)) return;
        if (e.Control is not TextBox tb) return;
        tb.Multiline = true;
        tb.WordWrap = true;
        tb.AcceptsReturn = true; // 改行はShift+Enter（プレーンEnterはDataGridViewが行移動として先取りする）
        tb.ScrollBars = ScrollBars.Vertical;
        tb.ReadOnly = _grid.Columns[_grid.CurrentCell.ColumnIndex].Name == "English";

        tb.SelectionStart = tb.Text.Length;
        tb.SelectionLength = 0;
        var cellBounds = _grid.GetCellDisplayRectangle(_grid.CurrentCell!.ColumnIndex, _grid.CurrentCell.RowIndex, false);
        if (!cellBounds.IsEmpty) tb.Bounds = cellBounds;

        tb.ScrollBars = ScrollBars.None;
        tb.ScrollBars = ScrollBars.Vertical;
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);

        if (_edits.Count > 0)
        {
            // 2026-09-12: SaveChanges()自身が最後に_edits.Clear()するため、
            // 呼び出し後に_edits.Countを参照すると必ず0になる（実機報告の
            // 「0件保存されました」の原因）。ESP側TranslationDetailForm.cs
            // には無い問題——SaveChanges自体は_editsをクリアしない設計。
            // 呼ぶ前に件数を退避しておく。
            var editedCount = _edits.Count;
            try
            {
                SaveChanges();
                MessageBox.Show(this, $"{editedCount}件の変更を保存しました。\n{_path}", "保存完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            rows.Add(new InterfaceTranslationRow(
                TsvEscaping.Unescape(cols[0]), TsvEscaping.Unescape(cols[1]), TsvEscaping.Unescape(cols[2]),
                cols[3] == "1", TsvEscaping.Unescape(notes)));
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
            writer.WriteLine($"{TsvEscaping.Escape(row.Key)}\t{TsvEscaping.Escape(row.English)}\t{TsvEscaping.Escape(row.Japanese)}\t{(row.Resolved ? "1" : "0")}\t{TsvEscaping.Escape(row.Notes)}");
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
