using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcherGui;

/// <summary>
/// 設定ウィンドウ — v0.52.1a: メインウィンドウ（旧ベース画面）から切り出した。
///
/// 背景: メインウィンドウは元々「MO2フォルダ・各種パス・LLM設定」＋「①MO2ロード
/// ②プラグイン一覧③スキャン」という構成だったが、②は削除（MO2自体で見れば足りる）、
/// ③は「翻訳前の状況」ウィンドウの「再スキャン」ボタンと完全に重複していた——結果、
/// ベース画面の実質的な役割はほぼ「設定」だけになっていた。そこで「翻訳前の状況」
/// ウィンドウをそのままメインウィンドウに昇格させ、旧ベース画面の内容（設定＋①MO2
/// ロード）を丸ごとこの専用ウィンドウへ移した。
///
/// APIキー（生成AI連携設定側）はDPAPI暗号化してAppSettingsへ保存するため、設定ファイル
/// を直接手編集する方式には出来ない（平文で書いても復号できない）——これがGUIでの
/// 設定編集を残した理由。それ以外の設定（MO2フォルダ・パス類）は単純な文字列だが、
/// 一貫性のため同じウィンドウにまとめてある。
///
/// CloudAiSettingsFormと同じ「編集して保存」パターン——ただし常時表示のメイン
/// ウィンドウと違い、ダイアログとしてPseudoModalで開く（Services/PseudoModal.cs）。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly MainForm _owner;

    private readonly TextBox _txtMo2Dir = new();
    private readonly TextBox _txtLlmEndpoint = new();
    private readonly TextBox _txtLlmModel = new();
    private readonly TextBox _txtImportDir = new() { ReadOnly = true };
    private readonly TextBox _txtOutputDir = new() { ReadOnly = true };
    private readonly Button _btnCloudAiSettings = new() { Text = "生成AI（クラウド）連携設定", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
    private readonly Button _btnLoadMo2 = new() { Text = "MO2フォルダをロード", AutoSize = true };
    private readonly Button _btnSaveSettings = new() { Text = "設定を保存", AutoSize = true };
    private readonly Label _lblMo2Status = new() { AutoSize = true, Margin = new Padding(6, 8, 3, 3) };

    public SettingsForm(MainForm owner)
    {
        _owner = owner;
        Text = "設定";
        Width = 720;
        StartPosition = FormStartPosition.CenterParent;

        BuildLayout();
        LoadFromSettings();

        // v0.52.1a: 旧ベース画面は明示的な「設定を保存」以外に、①ロード・
        // ウィンドウを閉じるタイミングでも暗黙に保存していた——押し忘れで
        // 編集内容が失われる驚きを避けるため、閉じるときも同様に保存する。
        FormClosing += (_, _) => SaveToSettings();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Controls.Add(grid, 0, 0);

        var settingsButtons = new List<Button>();
        void AddRow(string label, TextBox box, string? buttonText, Action? buttonAction)
        {
            var btn = AddSettingRow(grid, label, box, buttonText, buttonAction);
            if (btn != null) settingsButtons.Add(btn);
        }

        AddRow("MO2インスタンスフォルダ", _txtMo2Dir, "参照...", BrowseMo2Folder);

        // MO2ロードは設定行と同じグリッドの1行として、ボタン列にだけ配置——
        // テキストボックスを2行分占有させたくないため専用行にする。
        var loadRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _btnLoadMo2.Click += BtnLoadMo2_Click;
        var loadPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        loadPanel.Controls.Add(_btnLoadMo2);
        loadPanel.Controls.Add(_lblMo2Status);
        grid.SetColumnSpan(loadPanel, 2);
        grid.Controls.Add(loadPanel, 1, loadRow);
        settingsButtons.Add(_btnLoadMo2);

        AddRow("ローカルLLM エンドポイント", _txtLlmEndpoint, null, null);
        AddRow("ローカルLLM モデル名", _txtLlmModel, null, null);
        AddCloudAiSettingsRow(grid);
        settingsButtons.Add(_btnCloudAiSettings);
        // v0.54.0: 既知の課題——CLI実行ファイル(.exe)は以前ここで手動指定できたが、
        // GUI・CLIは常に同じ製品フォルダの兄弟として配置される前提であり、
        // CliLocator.TryAutoDetect()が確実に見つけられるため、ユーザーが変更する
        // 必要は無いと判断し設定項目ごと削除した（見つからない場合はRunCliAsyncが
        // エラーダイアログで再ビルド/再インストールを促す）。
        // 表示は製品ルートからの相対パス（見やすさ・移動耐性のため）に
        // 統一し、実際にエクスプローラーで開く際だけ絶対パスへ解決する。
        AddRow("xTranslator用翻訳ファイルインポートフォルダ", _txtImportDir, "開く", () => OpenFolder(Path.Combine(_owner.ProductRoot, "Translation", "import")));
        AddRow("DSDファイル出力先フォルダ", _txtOutputDir, "開く", () => OpenFolder(Path.Combine(_owner.ProductRoot, "out")));

        var saveRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0) };
        _btnSaveSettings.Click += (_, _) =>
        {
            SaveToSettings();
            MessageBox.Show(this, "設定を保存しました。", "設定", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        saveRow.Controls.Add(_btnSaveSettings);
        settingsButtons.Add(_btnSaveSettings);
        root.Controls.Add(saveRow, 0, 1);

        ButtonLayout.UnifyWidths(settingsButtons);

        ClientSize = new Size(720, root.PreferredSize.Height + 20);
    }

    private Button? AddSettingRow(TableLayoutPanel grid, string label, TextBox box, string? buttonText, Action? buttonAction)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, row);
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(3, 5, 3, 3);
        grid.Controls.Add(box, 1, row);
        if (buttonAction == null) return null;

        var btn = new Button { Text = buttonText, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
        btn.Click += (_, _) => buttonAction();
        grid.Controls.Add(btn, 2, row);
        return btn;
    }

    private void AddCloudAiSettingsRow(TableLayoutPanel grid)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "生成AI（クラウド）連携", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, row);
        _btnCloudAiSettings.Click += (_, _) => OpenCloudAiSettings();
        grid.Controls.Add(_btnCloudAiSettings, 2, row);
    }

    private void OpenCloudAiSettings()
    {
        var form = new CloudAiSettingsForm(_owner.Settings);
        form.FormClosed += (_, _) => { if (form.DialogResult == DialogResult.OK) _owner.Settings.Save(); };
        PseudoModal.Show(form, this);
    }

    private void OpenFolder(string path) => FolderOpener.OpenOrWarn(this, path);

    // v0.54.2: remembered at load time so SaveToSettings can tell whether the
    // local LLM endpoint/model actually changed — see the remark there.
    private string _initialLlmEndpoint = "";
    private string _initialLlmModel = "";

    private void LoadFromSettings()
    {
        var settings = _owner.Settings;
        _txtMo2Dir.Text = settings.Mo2InstanceDir;
        _txtLlmEndpoint.Text = settings.LlmEndpoint;
        _txtLlmModel.Text = settings.LlmModel;
        _initialLlmEndpoint = settings.LlmEndpoint;
        _initialLlmModel = settings.LlmModel;
        _txtImportDir.Text = Path.Combine("Translation", "import");
        _txtOutputDir.Text = "out";
    }

    private void SaveToSettings()
    {
        var settings = _owner.Settings;
        settings.Mo2InstanceDir = _txtMo2Dir.Text.Trim();
        settings.LlmEndpoint = _txtLlmEndpoint.Text.Trim();
        settings.LlmModel = _txtLlmModel.Text.Trim();
        settings.Save();

        // v0.54.2 (既知の課題22.): ローカルLLM設定を実際に変更した場合のみ、
        // ベース画面の「Beta機能: ローカルLLM翻訳」チェックを外す——古い接続先の
        // ままチェックが入りっぱなしになる事故を防ぐ。単にウィンドウを開いて
        // 何も変えずに閉じただけでは発火しない。
        if (settings.LlmEndpoint != _initialLlmEndpoint || settings.LlmModel != _initialLlmModel)
        {
            _owner.ResetLocalLlmCheckbox();
            _initialLlmEndpoint = settings.LlmEndpoint;
            _initialLlmModel = settings.LlmModel;
        }
    }

    private void BrowseMo2Folder()
    {
        using var dlg = new FolderBrowserDialog { Description = "MO2インスタンスフォルダを選択" };
        if (!string.IsNullOrWhiteSpace(_txtMo2Dir.Text) && Directory.Exists(_txtMo2Dir.Text))
            dlg.SelectedPath = _txtMo2Dir.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtMo2Dir.Text = dlg.SelectedPath;
    }

    private async void BtnLoadMo2_Click(object? sender, EventArgs e)
    {
        var mo2Dir = _txtMo2Dir.Text.Trim();
        if (string.IsNullOrWhiteSpace(mo2Dir) || !Directory.Exists(mo2Dir))
        {
            MessageBox.Show(this, "MO2インスタンスフォルダを正しく指定してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SaveToSettings();

        _btnLoadMo2.Enabled = false;
        _lblMo2Status.Text = "ロード中...";
        try
        {
            var ok = await _owner.RunCliAsync(new[] { "pickuptarget", mo2Dir });
            _lblMo2Status.Text = ok ? "ロード完了" : "";
        }
        finally
        {
            _btnLoadMo2.Enabled = true;
        }
    }
}
