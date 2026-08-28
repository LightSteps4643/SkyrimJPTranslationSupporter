using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcherGui;

/// <summary>Viewer/editor for one plugin's Translation/out_temp/&lt;plugin&gt;/
/// translations.tsv — the CLI's own per-candidate output. The Japanese column is
/// editable; "OK" writes the whole file back (with any edits) so it round-trips
/// cleanly into "DSDファイル生成" — exactly the CLI's own file format and nothing
/// else, so this stays a plain file editor and not a second copy of pipeline
/// logic. An edited row's Notes becomes "ModifiedByUser", overriding whatever
/// auto-resolution method (or lack of one) produced it before.</summary>
public sealed class TranslationDetailForm : Form
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

    private List<Dictionary<string, string>> _rows = new();
    private string _path = "";

    // FormId単体では重複する（同一FormKeyに複数RecordType/Indexの候補があるため、
    // 実データで確認済み）ので、行の識別には FormId+RecordType+Index の複合キーを使う。
    private const char KeySep = '\u0001';
    private static string RowKey(Dictionary<string, string> row) =>
        $"{row.GetValueOrDefault("FormId")}{KeySep}{row.GetValueOrDefault("RecordType")}{KeySep}{row.GetValueOrDefault("Index")}";

    // key -> 編集後の日本語訳（unescape済み、グリッド表示と同じ形）
    private readonly Dictionary<string, string> _edits = new();

    private readonly Action? _onSaved;

    /// <param name="onSaved">Called right after a successful save (before this
    /// window closes) — lets the opener (MainForm's grid) refresh its own
    /// 未翻訳件数/文字数 columns from the file this window just rewrote, instead
    /// of showing stale numbers until the next scan.</param>
    public TranslationDetailForm(string pluginName, string translationsTsvPath, Action? onSaved = null)
    {
        _onSaved = onSaved;
        Text = $"翻訳状況の詳細 — {pluginName}";
        Width = 1100;
        Height = 760;
        StartPosition = FormStartPosition.CenterParent;

        BuildColumns();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(new Label { Text = "絞り込み（原文・訳文）:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 10, 6, 3) }, 0, 0);
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

        LoadData(translationsTsvPath);
        _txtFilter.TextChanged += (_, _) => ApplyFilter();
        _grid.CellValueChanged += Grid_CellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        // 訳文セルを編集中だけ、複数行入力できるようにする（長文・改行入りの訳を
        // 1行の横スクロールに押し込めず、ウィンドウ内に収まる形で編集できるように）。
        // v0.52.1a: 原文セルも同じ仕組みで複数行表示・スクロールできるようにした——
        // ただし原文は表示専用なので、CellEndEditで必ず元の値へ戻す（下記参照）。
        _grid.EditingControlShowing += Grid_EditingControlShowing;
        _grid.CellBeginEdit += Grid_CellBeginEdit;
        _grid.CellEndEdit += Grid_CellEndEdit;

        // 訳文列・原文列は選択+F2やダブルクリックでなく、シングルクリックですぐ
        // 複数行表示に入れるようにする。
        _grid.CellClick += Grid_CellClick;
    }

    private int _defaultRowHeight;

    // v0.52.1a: 複数行表示中の行番号（無ければ-1）。行の折りたたみをCellEndEditでは
    // 一切行わず、必ずここ（CellClick、クリック操作が完全に終わったあとにしか
    // 発火しないイベント）でだけ行うのがポイント——下記参照。
    private int _expandedRowIndex = -1;

    /// <summary>
    /// v0.52.1a: 以前はCellEndEditで折りたたみ（行の高さを戻す処理）をしていたが、
    /// これは「別のセルをクリックする」というマウス操作の途中（MouseDown直後、
    /// 選択確定前）に同期的に発火するため、その場で行の高さを変えると、クリック
    /// 開始時点（行が高いまま）と終了時点（行が縮んだ後）でヒットテスト結果が
    /// ズレてしまう。上に移動するクリックは影響を受けない（縮む行より上は位置が
    /// 動かないため）が、下に移動するクリックは、縮んだ分だけ全てのセルが
    /// 上にずれるため、クリック開始位置と終了位置が別セルとして扱われ、
    /// DataGridViewにドラッグ選択と誤認されたり、選択自体が成立しなかったりする
    /// 不具合が起きていた（実機で確認済み: 上移動は正常、下移動のみ不良）。
    ///
    /// CellClickは、マウスの押下・解放を含む一連のクリック操作が完全に終わった
    /// あとにしか発火しないため、ここで折りたたみ・展開の両方を行えば、
    /// 選択が確定したあとにしかレイアウトが変わらず、上記のズレが起こらない。
    /// </summary>
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

    private bool IsMultilineColumn(int columnIndex)
    {
        var name = _grid.Columns[columnIndex].Name;
        return name is "Japanese" or "EnglishText";
    }

    private void Grid_CellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (!IsMultilineColumn(e.ColumnIndex)) return;
        if (_defaultRowHeight == 0) _defaultRowHeight = _grid.Rows[e.RowIndex].Height;
        // ウィンドウの高さの範囲内で、複数行がある程度見える高さに広げる。
        _grid.Rows[e.RowIndex].Height = Math.Min(200, Math.Max(_defaultRowHeight, _grid.ClientSize.Height / 3));
        // 行高さの変更を即座に反映させないと、この直後に作られる編集用テキストボックスが
        // 古い（変更前の）行の大きさを基準に配置され、スクロールバーの位置がずれる。
        _grid.UpdateRowHeightInfo(e.RowIndex, true);
    }

    /// <summary>原文セルは表示専用 — SaveChanges自体は常に元のEnglishTextを書き戻す
    /// ので実害はないが、編集モードで万一キー入力があった場合にグリッド上の見た目
    /// まで書き変わって見えるのは紛らわしいため、離脱時に必ず元の値へ戻す。行の高さを
    /// 戻す処理はここでは行わない — Grid_CellClickの説明コメント参照（クリック操作の
    /// 途中で高さを変えると選択がズレる不具合があったため、折りたたみは必ずクリックが
    /// 完全に終わったあとのCellClickでのみ行う）。</summary>
    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_grid.Columns[e.ColumnIndex].Name != "EnglishText") return;
        var key = (string)_grid.Rows[e.RowIndex].Tag!;
        var original = _rows.FirstOrDefault(r => RowKey(r) == key);
        _grid.Rows[e.RowIndex].Cells["EnglishText"].Value = original != null ? Unescape(original.GetValueOrDefault("EnglishText", "")) : "";
    }

    private void Grid_EditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (_grid.CurrentCell?.OwningColumn == null || !IsMultilineColumn(_grid.CurrentCell.ColumnIndex)) return;
        if (e.Control is not TextBox tb) return;
        tb.Multiline = true;
        tb.WordWrap = true;
        tb.AcceptsReturn = true; // 改行はShift+Enter（プレーンEnterはDataGridViewが行移動として先取りする）
        tb.ScrollBars = ScrollBars.Vertical;
        // 原文は表示専用 — ReadOnlyにしておけば選択・コピーとスクロールはできるが、
        // 誤入力そのものが起こらない（CellEndEditでの値の巻き戻しは、それでも保険として残す）。
        tb.ReadOnly = _grid.Columns[_grid.CurrentCell.ColumnIndex].Name == "EnglishText";

        // 編集開始時に全文選択された状態だと、そのまま入力して誤って全文を
        // 上書きしてしまうリスクがあるため、カーソルを末尾に置いて選択なしにする。
        tb.SelectionStart = tb.Text.Length;
        tb.SelectionLength = 0;
        // 上のUpdateRowHeightInfoだけでは編集コントロール自身の実位置・サイズが追従しない
        // ことがあるため、現在のセルの実際の位置・大きさに明示的に合わせる（Sizeだけを
        // 変更するとLocationが古いセルのままになり、テキストボックスとスクロールバーが
        // セルの枠からずれて表示される不具合の原因だった）。
        var cellBounds = _grid.GetCellDisplayRectangle(_grid.CurrentCell!.ColumnIndex, _grid.CurrentCell.RowIndex, false);
        if (!cellBounds.IsEmpty) tb.Bounds = cellBounds;

        // Boundsを変更しても、Windows側が生成済みのスクロールバーの可動範囲を
        // 新しい高さで再計算しないことがある（作成時のサイズのまま固定されて見える）。
        // 一度非表示にしてから戻すことで、Windowsに現在のサイズを基準として
        // スクロールバーを作り直させる。
        tb.ScrollBars = ScrollBars.None;
        tb.ScrollBars = ScrollBars.Vertical;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FormId", HeaderText = "FormId", Width = 130, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RecordType", HeaderText = "種別", Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "EnglishText",
            HeaderText = "原文",
            Width = 320,
            // v0.52.1a: 列レベルのReadOnlyは外した — クリックで訳文と同じ複数行
            // スクロール表示に入れるにはBeginEditが必要なため。実際の書き換えは
            // 起こらないよう、編集用テキストボックス自体をReadOnlyにし、離脱時にも
            // 値を強制的に戻している（Grid_EditingControlShowing/Grid_CellEndEdit参照）。
            // 訳文セル編集時に行の高さが広がった際、原文側も折り返して全体を
            // 表示する（訳文と同様、上揃えで余白のズレを起こさないようにする）。
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Alignment = DataGridViewContentAlignment.TopLeft },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Japanese",
            HeaderText = "訳文（編集可）",
            Width = 320,
            ReadOnly = false,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            // 既定の上下中央揃え(MiddleLeft)のままだと、複数行編集時にテキストボックスが
            // 内容量に応じた上余白を付けてしまい、スクロールバーの位置とずれて見える
            // （改行を増やしてスクロールが必要になった瞬間だけ正常に見える、という
            // 現象の原因だった）。上揃えにして余白計算そのものを起こさないようにする。
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.TopLeft },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Notes", HeaderText = "解決方法（Notes）", Width = 180, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "EditorId", HeaderText = "EditorId", Width = 130, ReadOnly = true });
    }

    private void LoadData(string path)
    {
        _path = path;
        _rows = TsvReader.Read(path);
        Bind(_rows);
        Text = $"翻訳状況の詳細 — {Path.GetFileName(Path.GetDirectoryName(path))} — {_rows.Count}件";
    }

    private void Bind(List<Dictionary<string, string>> rows)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        // 絞り込み等での再構築後は行番号の対応が失われるため、複数行表示の状態も一旦忘れる。
        _expandedRowIndex = -1;
        foreach (var row in rows)
        {
            var key = RowKey(row);
            var edited = _edits.TryGetValue(key, out var editedJapanese);
            var japanese = edited ? editedJapanese! : Unescape(row.GetValueOrDefault("Japanese", ""));
            var notes = edited ? "ModifiedByUser" : row.GetValueOrDefault("Notes", "");

            var idx = _grid.Rows.Add(
                row.GetValueOrDefault("FormId", ""),
                row.GetValueOrDefault("RecordType", ""),
                Unescape(row.GetValueOrDefault("EnglishText", "")),
                japanese,
                notes,
                row.GetValueOrDefault("EditorId", ""));

            var gridRow = _grid.Rows[idx];
            gridRow.Tag = key;
            if (string.IsNullOrEmpty(japanese))
                gridRow.DefaultCellStyle.BackColor = Color.FromArgb(255, 245, 235); // 未翻訳のまま残っている行を薄く強調
            else if (edited)
                gridRow.DefaultCellStyle.BackColor = Color.FromArgb(235, 245, 255); // 編集済みの行を薄く強調
        }
        _grid.ResumeLayout();
        UpdateEditCountLabel();
    }

    private void Grid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Japanese") return;

        var gridRow = _grid.Rows[e.RowIndex];
        var key = (string)gridRow.Tag!;
        var original = _rows.FirstOrDefault(r => RowKey(r) == key);
        var originalJapanese = original != null ? Unescape(original.GetValueOrDefault("Japanese", "")) : "";
        var newValue = (string)(gridRow.Cells["Japanese"].Value ?? "");

        if (newValue == originalJapanese)
        {
            _edits.Remove(key);
            gridRow.Cells["Notes"].Value = original?.GetValueOrDefault("Notes", "") ?? "";
            gridRow.DefaultCellStyle.BackColor = string.IsNullOrEmpty(newValue) ? Color.FromArgb(255, 245, 235) : Color.White;
        }
        else
        {
            _edits[key] = newValue;
            gridRow.Cells["Notes"].Value = "ModifiedByUser";
            gridRow.DefaultCellStyle.BackColor = Color.FromArgb(235, 245, 255);
        }
        UpdateEditCountLabel();
    }

    private void UpdateEditCountLabel()
    {
        _lblEditCount.Text = _edits.Count > 0 ? $"{_edits.Count}件編集済み（未保存）" : "";
    }

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

    /// <summary>Rewrites translations.tsv in place. Untouched fields are written
    /// back byte-for-byte as originally read (TsvReader never unescapes, so
    /// _rows' raw values already round-trip correctly) — only an edited row's
    /// Japanese/Notes get replaced, keeping the risk surface to exactly the
    /// fields the user actually touched.</summary>
    private void SaveChanges()
    {
        var lines = new List<string> { "FormId\tWinningPlugin\tRecordType\tEnglishText\tJapanese\tNotes\tIndex\tEditorId" };
        foreach (var row in _rows)
        {
            var key = RowKey(row);
            var japanese = _edits.TryGetValue(key, out var edited) ? Escape(edited) : row.GetValueOrDefault("Japanese", "");
            var notes = _edits.ContainsKey(key) ? "ModifiedByUser" : row.GetValueOrDefault("Notes", "");
            lines.Add(string.Join('\t',
                row.GetValueOrDefault("FormId", ""),
                row.GetValueOrDefault("WinningPlugin", ""),
                row.GetValueOrDefault("RecordType", ""),
                row.GetValueOrDefault("EnglishText", ""),
                japanese,
                notes,
                row.GetValueOrDefault("Index", ""),
                row.GetValueOrDefault("EditorId", "")));
        }
        File.WriteAllLines(_path, lines, new System.Text.UTF8Encoding(true)); // BOM付きUTF-8 — CLI側の出力と同じ形式
    }

    // Mirrors Core/TsvEscaping.cs's Escape/Unescape exactly — GUI has no reference
    // to Core, so this is a small deliberate duplication of two pure string
    // functions, not a copy of pipeline logic. v0.55.4: Unescape rewritten to a
    // single left-to-right scan — see Core/TsvEscaping.cs's remarks for why the
    // old sequential-Replace version corrupted a literal backslash immediately
    // followed by a literal 'n'/'t' (e.g. a Windows path).
    private static string Unescape(string s)
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
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "");

    private void ApplyFilter()
    {
        var text = _txtFilter.Text.Trim();
        Bind(text.Length == 0
            ? _rows
            : _rows.Where(r =>
                r.GetValueOrDefault("EnglishText", "").Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.GetValueOrDefault("Japanese", "").Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (_edits.TryGetValue(RowKey(r), out var e) && e.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList());
    }
}
