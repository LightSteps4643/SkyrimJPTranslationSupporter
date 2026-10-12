using System.Data;
using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcherGui;

/// <summary>
/// The "SkyPatcher(β)" sub-tab's list: one row per providing MOD, read from
/// SkyPatcherText/Translation/out_temp (see <see cref="SkyPatcherListScanner"/>). It mirrors the plugin
/// list's columns and per-row buttons. Only what belongs to THIS list lives here — its "すべて選択/すべて解除/
/// 選択MODを一括初期化" buttons and its rows' buttons act on this list alone. The common buttons
/// (再スキャン / MO2再読込＆初期化 / 翻訳実行 / 出力ファイル生成) stay in MainForm, which asks this panel for its
/// selected MODs and runs the SkyPatcher step after the plugin step.
/// </summary>
public sealed class SkyPatcherListPanel : UserControl
{
    private const string DetailColumnName = "詳細";
    private const string ModGlossaryColumnName = "MOD固有語句";
    private const string ResetColumnName = "初期化";
    private const string ModColumnName = "MOD";

    private readonly Func<string> _productRoot;
    private readonly Func<string> _pluginImportDir;
    private readonly Func<IReadOnlyList<string>, Task<bool>> _runCli;
    private readonly Action<bool> _setBusy;
    private readonly Form _owner;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        AutoGenerateColumns = false,
    };
    private readonly DataTable _table = new();
    private readonly Label _lblSummary = new() { AutoSize = true };
    private readonly Button _btnSelectAll = new() { Text = "すべて選択", AutoSize = true };
    private readonly Button _btnSelectNone = new() { Text = "すべて解除", AutoSize = true };
    private readonly Button _btnResetSelected = new() { Text = "選択MODを一括初期化", AutoSize = true };

    /// <summary>MODs the user has unchecked — remembered across reloads (which rebuild the table), so an
    /// intentional exclusion is not lost. A MOD seen for the first time is checked: SkyPatcher rows start selected.</summary>
    private readonly HashSet<string> _deselected = new(StringComparer.OrdinalIgnoreCase);
    private List<SkyPatcherListRow> _rows = new();
    private long _maxUntranslatedChars;

    public SkyPatcherListPanel(Form owner, Func<string> productRoot, Func<string> pluginImportDir,
        Func<IReadOnlyList<string>, Task<bool>> runCli, Action<bool> setBusy)
    {
        _owner = owner;
        _productRoot = productRoot;
        _pluginImportDir = pluginImportDir;
        _runCli = runCli;
        _setBusy = setBusy;
        Dock = DockStyle.Fill;

        BuildColumns();
        InitTableColumns();
        BuildLayout();
    }

    private string TranslationOutTemp => Path.Combine(_productRoot(), "SkyPatcherText", "Translation", "out_temp");

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        top.Controls.Add(_lblSummary);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 0) };
        _btnSelectAll.Click += (_, _) => SetAllSelected(true);
        _btnSelectNone.Click += (_, _) => SetAllSelected(false);
        _btnResetSelected.Click += async (_, _) => await ResetSelectedAsync();
        buttons.Controls.Add(_btnSelectAll);
        buttons.Controls.Add(_btnSelectNone);
        buttons.Controls.Add(_btnResetSelected);
        ButtonLayout.UnifyWidths(new[] { _btnSelectAll, _btnSelectNone });
        top.Controls.Add(buttons);

        root.Controls.Add(top, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        Controls.Add(root);

        _grid.CellFormatting += Grid_CellFormatting;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.CurrentCell is DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, _) => UpdateSummary();
        _grid.CellContentClick += Grid_CellContentClick;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "選択", HeaderText = "選択", DataPropertyName = "選択", Width = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ModColumnName, HeaderText = "MOD", DataPropertyName = ModColumnName, ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "全体件数", HeaderText = "全体件数", DataPropertyName = "全体件数", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "未翻訳件数", HeaderText = "未翻訳件数", DataPropertyName = "未翻訳件数", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "翻訳率(件数)", HeaderText = "翻訳率(件数)", DataPropertyName = "翻訳率(件数)", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "未翻訳文字数", HeaderText = "未翻訳文字数", DataPropertyName = "未翻訳文字数", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "翻訳率(文字数)", HeaderText = "翻訳率(文字数)", DataPropertyName = "翻訳率(文字数)", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = DetailColumnName, HeaderText = "", Text = "詳細を確認", UseColumnTextForButtonValue = true, Width = 100 });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = ModGlossaryColumnName, HeaderText = "", Text = "MOD固有文字列の注釈", UseColumnTextForButtonValue = true, Width = 140 });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = ResetColumnName, HeaderText = "", Text = "翻訳状況を初期化", UseColumnTextForButtonValue = true, Width = 130 });
    }

    // Columns are built once (see MainForm.InitTableColumns for why: rebuilding them while the table is bound
    // to the grid breaks a sorted DataGridView).
    private void InitTableColumns()
    {
        _table.Columns.Add("選択", typeof(bool));
        _table.Columns.Add(ModColumnName, typeof(string));
        _table.Columns.Add("全体件数", typeof(int));
        _table.Columns.Add("未翻訳件数", typeof(int));
        _table.Columns.Add("翻訳率(件数)", typeof(string));
        _table.Columns.Add("未翻訳文字数", typeof(long));
        _table.Columns.Add("翻訳率(文字数)", typeof(string));
    }

    /// <summary>Reads SkyPatcherText/Translation/out_temp and rebuilds the list (no CLI call). Selection is kept.</summary>
    public void Reload()
    {
        _rows = SkyPatcherListScanner.Scan(TranslationOutTemp);

        _table.Rows.Clear();
        foreach (var r in _rows)
            _table.Rows.Add(!_deselected.Contains(r.Mod), r.Mod, r.Total, r.Untranslated, $"{r.Ratio:F1}%", r.UntranslatedChars, $"{r.CharsRatio:F1}%");

        _grid.DataSource = _table;
        _maxUntranslatedChars = _rows.Count > 0 ? _rows.Max(r => r.UntranslatedChars) : 0;
        UpdateSummary();
    }

    /// <summary>Remember the current unchecked MODs (called before a reload that wipes and rebuilds the folders).</summary>
    public void RememberDeselected()
    {
        _deselected.Clear();
        foreach (DataRow row in _table.Rows)
            if (row["選択"] is false)
                _deselected.Add((string)row[ModColumnName]);
    }

    public List<string> SelectedMods() =>
        _table.Rows.Cast<DataRow>().Where(r => r["選択"] is true).Select(r => (string)r[ModColumnName]).ToList();

    /// <summary>Untranslated characters per MOD, for the log window's progress bar.</summary>
    public Dictionary<string, long> UntranslatedCharsByMod(IEnumerable<string> mods) =>
        mods.ToDictionary(m => m, m => _rows.FirstOrDefault(r => r.Mod.Equals(m, StringComparison.OrdinalIgnoreCase))?.UntranslatedChars ?? 0L,
            StringComparer.OrdinalIgnoreCase);

    public int UntranslatedCount(IEnumerable<string> mods) =>
        mods.Sum(m => _rows.FirstOrDefault(r => r.Mod.Equals(m, StringComparison.OrdinalIgnoreCase))?.Untranslated ?? 0);

    public void SetBusy(bool busy)
    {
        _btnSelectAll.Enabled = !busy;
        _btnSelectNone.Enabled = !busy;
        _btnResetSelected.Enabled = !busy;
        _grid.Enabled = !busy;
    }

    private void SetAllSelected(bool selected)
    {
        foreach (DataRow row in _table.Rows) row["選択"] = selected;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_rows.Count == 0)
        {
            _lblSummary.Text = "SkyPatcherの翻訳データがありません。「MO2再読込＆初期化」を押すと、MODのSkyPatcher用.iniを読み込みます。\n" +
                "（SkyPatcherの.iniが無い、またはfullName/shortNameを含む行が無い場合も、この一覧は空です）";
            return;
        }

        var selected = _table.Rows.Cast<DataRow>().Count(r => r["選択"] is true);
        _lblSummary.Text = $"{_rows.Count}MOD中 {selected}件選択中 ／ 全体の未翻訳 {_rows.Sum(r => r.Untranslated)}件・{_rows.Sum(r => r.UntranslatedChars):N0}字" +
            "　（背景色が濃いほど未翻訳文字数が多いMOD）\n" +
            "※SkyPatcherの.iniでアイテム名等を書き換えているMODを表示（翻訳しない行は、件数に含めません。詳細ウィンドウでグレーで確認できます）";
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_grid.Columns[e.ColumnIndex].Name != "未翻訳文字数" || _maxUntranslatedChars <= 0) return;
        if (e.Value is not long chars) return;

        // Continuous intensity, not a threshold — same tint as the plugin list.
        var t = Math.Clamp((double)chars / _maxUntranslatedChars, 0, 1);
        e.CellStyle!.BackColor = Color.FromArgb((int)(255 - t * 30), (int)(255 - t * 160), (int)(255 - t * 170));
    }

    private async void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var columnName = _grid.Columns[e.ColumnIndex].Name;
        if (columnName != DetailColumnName && columnName != ModGlossaryColumnName && columnName != ResetColumnName) return;

        var mod = (string)_grid.Rows[e.RowIndex].Cells[ModColumnName].Value!;
        var modDir = Path.Combine(TranslationOutTemp, mod);

        if (columnName == ResetColumnName)
        {
            await ResetAsync([mod], $"「{mod}」");
            return;
        }

        if (columnName == ModGlossaryColumnName)
        {
            var glossaryPath = Path.Combine(modDir, "mod_glossary.tsv");
            if (!File.Exists(glossaryPath))
            {
                MessageBox.Show(_owner, $"まだこのMODのMOD固有文字列の候補がありません:\n{glossaryPath}\n先に「MO2再読込＆初期化」または「翻訳実行」を行ってください。\n（候補が1件も検出されなかった場合、このファイル自体が作られないこともあります）",
                    "ファイルが見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            PseudoModal.Show(new ModGlossaryDetailForm(mod, glossaryPath), _owner);
            return;
        }

        var path = Path.Combine(modDir, "translations.tsv");
        if (!File.Exists(path))
        {
            MessageBox.Show(_owner, $"まだこのMODの翻訳結果がありません:\n{path}\n先に「MO2再読込＆初期化」または「翻訳実行」を行ってください。",
                "ファイルが見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        PseudoModal.Show(new TranslationDetailForm(mod, path, onSaved: Reload, skyPatcher: true), _owner);
    }

    private async Task ResetSelectedAsync()
    {
        var mods = SelectedMods();
        if (mods.Count == 0)
        {
            MessageBox.Show(_owner, "初期化するMODを少なくとも1つ選択してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await ResetAsync(mods, $"選択中の{mods.Count}MOD");
    }

    /// <summary>Back to the first-step-only baseline for the named MODs, discarding their translations
    /// (including manual edits) — backed up first, like the plugin list's reset.</summary>
    private async Task ResetAsync(IReadOnlyList<string> mods, string what)
    {
        var confirm = MessageBox.Show(_owner,
            $"{what}の翻訳状況を初期化します。\n" +
            "対象の翻訳結果（手動での編集を含む）をすべて消去し、初期状態に戻します。\n" +
            "元に戻せません。よろしいですか？",
            "SkyPatcherの翻訳状況を初期化", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        TranslationBackup.Backup(TranslationOutTemp, mods, "translations.tsv");

        _setBusy(true);
        var modsFile = Path.Combine(Path.GetTempPath(), $"sjpts_sp_reset_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(modsFile, mods);
            if (!await _runCli(SkyPatcherCliArgs.Reset(modsFile, _pluginImportDir()))) return;
            Reload();
        }
        finally
        {
            _setBusy(false);
            try { File.Delete(modsFile); } catch { /* best-effort cleanup */ }
        }
    }
}
