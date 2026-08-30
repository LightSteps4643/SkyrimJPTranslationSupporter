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
/// CloudAiSettingsFormと同じ「編集してOK確定・キャンセルで破棄」パターン
/// （v0.57.0でこのウィンドウも揃えた）——ただし常時表示のメインウィンドウと
/// 違い、ダイアログとしてPseudoModalで開く（Services/PseudoModal.cs）。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly MainForm _owner;

    private readonly TextBox _txtMo2Dir = new();
    private readonly TextBox _txtMo2ModsDirOverride = new();
    private readonly TextBox _txtMo2ProfileDirOverride = new();
    private readonly TextBox _txtMo2OverwriteDirOverride = new();
    private readonly TextBox _txtLlmEndpoint = new();
    private readonly TextBox _txtLlmModel = new();
    private readonly TextBox _txtImportDir = new() { ReadOnly = true };
    private readonly TextBox _txtOutputDir = new() { ReadOnly = true };
    private readonly Button _btnCloudAiSettings = new() { Text = "生成AI（クラウド）連携設定", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
    private readonly Button _btnOk = new() { Text = "OK", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "キャンセル", AutoSize = true };

    public SettingsForm(MainForm owner)
    {
        _owner = owner;
        Text = "設定";
        Width = 720;
        StartPosition = FormStartPosition.CenterParent;

        BuildLayout();
        LoadFromSettings();

        // v0.57.0: 旧「設定を保存」単一ボタンから、一般的な設定ウィンドウの
        // OK（保存して閉じる）／キャンセル（保存せず閉じる）に変更した。
        // ウィンドウを閉じるだけで暗黙に保存していた旧挙動（v0.52.1a由来）は
        // ここで廃止——「編集して×で閉じたら消えて困る」より「キャンセルの
        // つもりで閉じたら保存されていた」の驚きの方を避ける判断（一般的な
        // 設定ウィンドウはキャンセル＝保存しない、が標準の期待のため）。
        CancelButton = _btnCancel;
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

        // v0.57.3: 旧「MO2フォルダをロード」ボタン（pickuptargetのみ実行）を削除した。
        // pickuptargetはPickUpTarget/out_temp（candidates.tsv等）を書くだけで、
        // ベース画面のプラグイン一覧はTranslation/out_temp（translationが書く方）
        // からしか作られないため、押してもリストには何も反映されず、GUI内の他の
        // どこもPickUpTarget/out_tempを直接読んでいなかった（実質孤立した機能——
        // ユーザーとの確認により、混乱を避けるため削除に至った）。MO2の動作確認・
        // スキャンはベース画面の「MO2再読込＆初期化」（pickuptarget+translation
        // 両方を実行し、リストも更新される）に一本化する。

        // v0.57.2: 「～～フォルダの上書き」という項目名だけでは意味が伝わりにくい
        // というフィードバックを受け、3項目をGroupBoxでまとめ、「そもそもいつ
        // 触るべき設定か」を説明文として明示する形に変更した（v0.57.0時点の
        // 個別行＋「（任意）」プレフィックスのみの表現から改善）。
        AddMo2PathOverridesGroup(grid, settingsButtons);

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

        var buttonRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0) };
        _btnOk.Click += (_, _) =>
        {
            SaveToSettings();
            DialogResult = DialogResult.OK;
            Close();
        };
        _btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        // RightToLeftで先に追加したコントロールが右端に来るため、OKを先に
        // 追加して右端＝OK、その左＝キャンセルという一般的な並びにする。
        buttonRow.Controls.Add(_btnOk);
        buttonRow.Controls.Add(_btnCancel);
        settingsButtons.Add(_btnOk);
        settingsButtons.Add(_btnCancel);
        root.Controls.Add(buttonRow, 0, 1);

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

    /// <summary>v0.57.0のmods/profile/overwrite個別上書き設定3項目を、
    /// v0.57.2でGroupBox＋説明文にまとめた——「どのフォルダを上書きするか」
    /// より前に「そもそもいつ使う設定か」（MO2グローバルインスタンス／
    /// 「Paths」タブでの個別カスタマイズ時のみ）を明示するため。
    /// ModOrganizer.ini自体の位置（インスタンスフォルダ）は「インスタンス
    /// フォルダ」の定義そのものなので、これとは別に上書き項目を設けない
    /// （v0.57.0時点からの判断は変わらず）。</summary>
    private void AddMo2PathOverridesGroup(TableLayoutPanel parentGrid, List<Button> settingsButtons)
    {
        var row = parentGrid.RowCount++;
        parentGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var group = new GroupBox
        {
            Text = "MO2パスの個別設定",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 8),
            Margin = new Padding(3, 6, 3, 6),
        };
        parentGrid.SetColumnSpan(group, 3);
        parentGrid.Controls.Add(group, 0, row);

        var inner = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.Controls.Add(inner);

        var explanationRow = inner.RowCount++;
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var explanation = new Label
        {
            // v0.57.3: プロファイルフォルダの上書きは、ModOrganizer.iniの
            // selected_profile（MO2側で現在選択中のプロファイル）とは無関係に
            // 任意のプロファイルを指定できる——ユーザーからの質問を受けて確認・
            // 明文化した挙動。それを踏まえ、空欄／指定時それぞれの挙動を説明文に
            // 追記した。
            Text = "MO2でグローバルインスタンスで導入している場合や、各種パスをカスタマイズしている場合は、以下の個別のパス設定を実施してください。\r\n" +
                   "プロファイルフォルダを空欄にした場合は現在MO2で適用しているプロファイルが選択され、指定した場合は指定したプロファイルを選択して処理します。",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(3, 3, 3, 10),
        };
        inner.SetColumnSpan(explanation, 3);
        inner.Controls.Add(explanation, 0, explanationRow);

        void AddOverrideRow(string label, TextBox box)
        {
            var btn = AddSettingRow(inner, label, box, "参照...", () => BrowseFolder(box));
            if (btn != null) settingsButtons.Add(btn);
        }
        AddOverrideRow("modsフォルダ", _txtMo2ModsDirOverride);
        AddOverrideRow("プロファイルフォルダ", _txtMo2ProfileDirOverride);
        AddOverrideRow("overwriteフォルダ", _txtMo2OverwriteDirOverride);
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
        _txtMo2ModsDirOverride.Text = settings.Mo2ModsDirOverride;
        _txtMo2ProfileDirOverride.Text = settings.Mo2ProfileDirOverride;
        _txtMo2OverwriteDirOverride.Text = settings.Mo2OverwriteDirOverride;
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
        settings.Mo2ModsDirOverride = _txtMo2ModsDirOverride.Text.Trim();
        settings.Mo2ProfileDirOverride = _txtMo2ProfileDirOverride.Text.Trim();
        settings.Mo2OverwriteDirOverride = _txtMo2OverwriteDirOverride.Text.Trim();
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

    private void BrowseMo2Folder() => BrowseFolder(_txtMo2Dir, "MO2インスタンスフォルダを選択");

    private void BrowseFolder(TextBox box, string description = "フォルダを選択")
    {
        using var dlg = new FolderBrowserDialog { Description = description };
        if (!string.IsNullOrWhiteSpace(box.Text) && Directory.Exists(box.Text))
            dlg.SelectedPath = box.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            box.Text = dlg.SelectedPath;
    }
}
