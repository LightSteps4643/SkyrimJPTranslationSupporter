using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcherGui;

/// <summary>One row of mod_glossary.tsv (Translation/ModPhraseGlossary.cs's
/// output) — English/Japanese/Count/Score, in that file's own column order.
/// Count/Score are kept as plain strings (display-only here; this form never
/// recomputes them, only the CLI does on regeneration).</summary>
public sealed record ModGlossaryRow(string English, string Japanese, string Count, string Score);

/// <summary>
/// 2026-09-18: Viewer/editor for one plugin's Translation/out_temp/&lt;plugin&gt;/
/// mod_glossary.tsv (Translation/ModPhraseGlossary.cs's output) — this MOD's
/// own detected recurring phrases, with a person's optional Japanese hint fed
/// into issue #4's "c" (same-mod hint) pool for ⑤ローカルLLM/⑥生成AI翻訳
/// (ModPhraseGlossary.LoadFilled). Deliberately mirrors TranslationDetailForm.cs's
/// shape (same button layout, same edit-tracking pattern) but simpler — no
/// multiline editing (phrases and their translations are always short, unlike
/// a book's DESC text), and only English/Japanese/Count/Score columns.
///
/// Only the Japanese column is editable; Count/Score are read-only here (this
/// form never recomputes them — only the CLI's own detection run does, on the
/// next `translation` execution). Saving rewrites the file with the SAME fixed
/// comment header ModPhraseGlossary.WriteTemplate itself writes (this form has
/// no project reference to Translation/ModPhraseGlossary.cs — GUI has zero
/// project references to Core/Translation, design/gui_architecture.md — so
/// ReadTsv/WriteTsv are a small, independent duplicate here, same pattern as
/// TranslationDetailForm.cs/InterfaceTextDetailForm.cs's own TSV round-trip
/// helpers). English/Japanese are NOT TsvEscaping-escaped, matching
/// ModPhraseGlossary.cs's own (unescaped) read/write contract exactly.
/// </summary>
public sealed class ModGlossaryDetailForm : Form
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

    private List<ModGlossaryRow> _rows = new();
    private string _path = "";
    private readonly string _plugin;

    // key -> 編集後の日本語訳
    private readonly Dictionary<string, string> _edits = new();

    private readonly Action? _onSaved;

    public ModGlossaryDetailForm(string plugin, string modGlossaryTsvPath, Action? onSaved = null)
    {
        _plugin = plugin;
        _onSaved = onSaved;
        Text = $"MOD固有文字列の注釈 — {plugin}";
        Width = 900;
        Height = 640;
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

        LoadData(modGlossaryTsvPath);
        _txtFilter.TextChanged += (_, _) => ApplyFilter();
        _grid.CellValueChanged += Grid_CellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "English", HeaderText = "原文", Width = 260, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Japanese", HeaderText = "訳・ヒント（編集可）", Width = 260, ReadOnly = false, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "出現回数", Width = 80, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Score", HeaderText = "固有度スコア", Width = 90, ReadOnly = true });
    }

    private void LoadData(string path)
    {
        _path = path;
        _rows = ReadTsv(path);
        Bind(_rows);
        Text = $"MOD固有文字列の注釈 — {_plugin} — {_rows.Count}件";
    }

    private void Bind(List<ModGlossaryRow> rows)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var row in rows)
        {
            var edited = _edits.TryGetValue(row.English, out var editedJapanese);
            var japanese = edited ? editedJapanese! : row.Japanese;

            var idx = _grid.Rows.Add(row.English, japanese, row.Count, row.Score);
            var gridRow = _grid.Rows[idx];
            gridRow.Tag = row.English;
            if (edited)
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
        var original = _rows.FirstOrDefault(r => r.English == key);
        var originalJapanese = original?.Japanese ?? "";
        var newValue = (string)(gridRow.Cells["Japanese"].Value ?? "");

        if (newValue == originalJapanese)
        {
            _edits.Remove(key);
            gridRow.DefaultCellStyle.BackColor = Color.White;
        }
        else
        {
            _edits[key] = newValue;
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
            // 2026-09-18: SaveChanges()自身が最後に_edits.Clear()するため、
            // 呼び出し後に_edits.Countを参照すると必ず0になる（実機報告の
            // 「0件の変更を保存しました」の原因——InterfaceTextDetailForm.cs
            // で2026-09-12に一度見つかった同じ不具合の再発）。呼ぶ前に件数を
            // 退避しておく。
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
            _edits.TryGetValue(r.English, out var jp) ? r with { Japanese = jp } : r
        ).ToList();
        WriteTsv(_path, _plugin, updated);
        _rows = updated;
        _edits.Clear();
    }

    /// <summary>Mirrors Translation/ModPhraseGlossary.cs's own (unescaped)
    /// comment-skipping read exactly — see this class's remarks for why it's
    /// duplicated here rather than referenced.</summary>
    private static List<ModGlossaryRow> ReadTsv(string path)
    {
        var rows = new List<ModGlossaryRow>();
        if (!File.Exists(path)) return rows;
        foreach (var line in File.ReadAllLines(path, System.Text.Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var cols = line.Split('\t');
            if (cols.Length < 1 || cols[0].Trim().Length == 0) continue;
            var english = cols[0].Trim();
            var japanese = cols.Length > 1 ? cols[1].Trim() : "";
            var count = cols.Length > 2 ? cols[2].Trim() : "";
            var score = cols.Length > 3 ? cols[3].Trim() : "";
            rows.Add(new ModGlossaryRow(english, japanese, count, score));
        }
        return rows;
    }

    /// <summary>Mirrors Translation/ModPhraseGlossary.cs's WriteTemplate's own
    /// fixed comment header exactly — must stay byte-for-byte identical so a
    /// person opening the file directly (not through this GUI) still sees the
    /// same explanation the CLI itself would have written.</summary>
    private static void WriteTsv(string path, string plugin, IReadOnlyList<ModGlossaryRow> rows)
    {
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine($"# {plugin} — このMODだけに効く語彙集（⑤ローカルLLM・⑥生成AI翻訳へのヒントとして使われます）");
        writer.WriteLine("#");
        writer.WriteLine("# 以下は、このMOD内で繰り返し使われているが、まだ確立した訳が無い語句の候補です。");
        writer.WriteLine("# Count＝このMOD内での出現回数、Score＝TF-IDFによる固有度（高いほどMOD特有）です。");
        writer.WriteLine("# 日本語列に書き込んでおくと、このMODの他の未翻訳候補を訳す際のヒントとして使われます");
        writer.WriteLine("# （このMODの候補にしか影響しません。他のMODには一切影響しません）。");
        writer.WriteLine("# 空欄のままでも問題ありません（このファイルが無いのと同じ扱いになるだけです）。");
        writer.WriteLine("# 記入済みの日本語列は再生成時も保持されます（消えません。Count/Scoreは毎回最新値に更新されます）。");
        writer.WriteLine("#");
        writer.WriteLine("# English\tJapanese\tCount\tScore");
        foreach (var row in rows)
            writer.WriteLine($"{row.English}\t{row.Japanese}\t{row.Count}\t{row.Score}");
    }

    private void ApplyFilter()
    {
        var text = _txtFilter.Text.Trim();
        Bind(text.Length == 0
            ? _rows
            : _rows.Where(r =>
                r.English.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.Japanese.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (_edits.TryGetValue(r.English, out var e) && e.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList());
    }
}
