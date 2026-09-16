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
/// 「編集してOK確定・キャンセルで破棄」パターン（v0.57.0でこのウィンドウも
/// 揃えた）——ただし常時表示のメインウィンドウと違い、ダイアログとして
/// PseudoModalで開く（Services/PseudoModal.cs）。
///
/// v0.58.3: 生成AI（クラウド）連携設定は別ウィンドウ（旧CloudAiSettingsForm、
/// タブ切替）だったが、ここへ統合した——タブではなくラジオボタン＋非選択側
/// グレーアウトに変更（AddCloudAiSettingsGroup参照）。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly MainForm _owner;

    private readonly TextBox _txtMo2Dir = new();
    private readonly TextBox _txtMo2ModsDirOverride = new();
    private readonly TextBox _txtMo2ProfileDirOverride = new();
    private readonly TextBox _txtMo2OverwriteDirOverride = new();

    // 2026-09-17: import/outフォルダの任意パス設定（プラグイン翻訳用・
    // Interface翻訳用でそれぞれ独立）。既定ではimport/{plugin,interface}・
    // outに統合済みだが、任意の場所へ変更したいユーザー向けの上書き項目。
    private readonly TextBox _txtPluginImportDirOverride = new();
    private readonly TextBox _txtInterfaceImportDirOverride = new();
    private readonly TextBox _txtPluginOutDirOverride = new();
    private readonly TextBox _txtInterfaceOutDirOverride = new();

    private readonly TextBox _txtLlmEndpoint = new();
    private readonly TextBox _txtLlmModel = new();
    // v0.58.1: 既定ON——AppSettings.LlmLocalReasoningOffの説明コメント参照。
    private readonly CheckBox _chkLlmReasoningOff = new() { Text = "※チェック推奨", Checked = true, AutoSize = true };

    // v0.58.3: 元CloudAiSettingsForm（別ウィンドウ）から統合。タブではなく
    // ラジオボタン＋非選択側グレーアウトにした（設定ウィンドウ全体の他の項目と
    // 同じ「1画面で完結」の構成に揃えるため）。
    private readonly RadioButton _radClaudeCodeCli = new() { Text = "Claude Code CLI", AutoSize = true };
    private readonly RadioButton _radOpenAiApi = new() { Text = "OpenAI互換API", AutoSize = true };
    private readonly TextBox _txtClaudeCodeExe = new() { Text = "claude" };
    private readonly TextBox _txtClaudeCodeModel = new();
    private readonly TextBox _txtCloudAiEndpoint = new();
    private readonly TextBox _txtCloudAiApiKey = new() { UseSystemPasswordChar = true };
    private readonly Button _btnToggleCloudAiApiKeyVisibility = new() { Text = "APIキーを表示", AutoSize = true };
    private TableLayoutPanel _claudeCodeGrid = null!;
    private TableLayoutPanel _openAiGrid = null!;

    private readonly Button _btnOk = new() { Text = "OK", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "キャンセル", AutoSize = true };

    public SettingsForm(MainForm owner)
    {
        _owner = owner;
        Text = "設定";
        Width = 800;
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

        // 2026-09-17: import/outフォルダの任意パス設定。MO2パス個別設定と
        // 同じGroupBox＋説明文パターンに揃えた。
        AddImportOutPathOverridesGroup(grid, settingsButtons);

        // v0.58.2: エンドポイント・モデル名・思考OFFの3項目をMO2パス個別設定と
        // 同様にGroupBoxへまとめた（従来は他の単発設定行と並列で紛れていた）。
        AddLlmLocalSettingsGroup(grid);
        // v0.58.3: 生成AI連携設定は別ウィンドウ（旧CloudAiSettingsForm）を廃止し、
        // ここへ統合した。
        AddCloudAiSettingsGroup(grid);
        // v0.54.0: 既知の課題——CLI実行ファイル(.exe)は以前ここで手動指定できたが、
        // GUI・CLIは常に同じ製品フォルダの兄弟として配置される前提であり、
        // CliLocator.TryAutoDetect()が確実に見つけられるため、ユーザーが変更する
        // 必要は無いと判断し設定項目ごと削除した（見つからない場合はRunCliAsyncが
        // エラーダイアログで再ビルド/再インストールを促す）。
        // v0.58.2: xTranslatorインポートフォルダ／DSD出力先フォルダを開くボタンは、
        // ベース画面（MainForm）側に既に同等のボタンが追加済みのため重複となり、
        // ここから削除した。

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

        ClientSize = new Size(800, root.PreferredSize.Height + 20);
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

    /// <summary>2026-09-17: import（xTranslator XML／Interface用*_japanese.txt）・
    /// out（最終出力）フォルダの既定値は、製品ルート直下のimport/{plugin,interface}・
    /// outに統合済み（1つのoutフォルダをMO2に導入するだけで両方の翻訳が反映される）。
    /// 空欄のままなら常にこの既定値が使われ、指定した場合のみ個別に上書きされる——
    /// プラグイン翻訳用・Interface翻訳用は完全に独立しており、片方だけ上書きしても
    /// 他方には影響しない。</summary>
    private void AddImportOutPathOverridesGroup(TableLayoutPanel parentGrid, List<Button> settingsButtons)
    {
        var row = parentGrid.RowCount++;
        parentGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var group = new GroupBox
        {
            Text = "import/outフォルダの個別設定",
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

        void AddOverrideRow(string label, TextBox box)
        {
            var btn = AddSettingRow(inner, label, box, "参照...",
                () => BrowseFolder(box, resolveForExistenceCheck: p => Path.Combine(_owner.ProductRoot, p)));
            if (btn != null) settingsButtons.Add(btn);
        }
        AddOverrideRow("プラグイン翻訳 importフォルダ", _txtPluginImportDirOverride);
        AddOverrideRow("Interface翻訳 importフォルダ", _txtInterfaceImportDirOverride);
        AddOverrideRow("プラグイン翻訳 outフォルダ", _txtPluginOutDirOverride);
        AddOverrideRow("Interface翻訳 outフォルダ", _txtInterfaceOutDirOverride);
    }

    /// <summary>v0.58.2: MO2パス個別設定と同様、ローカルLLM関連の3項目
    /// （エンドポイント・モデル名・思考OFF）をGroupBoxへまとめた。</summary>
    private void AddLlmLocalSettingsGroup(TableLayoutPanel parentGrid)
    {
        var row = parentGrid.RowCount++;
        parentGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var group = new GroupBox
        {
            Text = "ローカルLLM翻訳設定",
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

        AddSettingRow(inner, "ローカルLLM エンドポイント", _txtLlmEndpoint, null, null);
        AddSettingRow(inner, "ローカルLLM モデル名", _txtLlmModel, null, null);
        AddLlmReasoningOffRow(inner);
    }

    /// <summary>v0.58.3: 旧CloudAiSettingsForm（別ウィンドウ・タブ切替）を廃止し、
    /// ここへ統合。「どちらの方式を使うか」を上部のラジオボタンで選び、選ばれて
    /// いない方の入力欄一式（TableLayoutPanel丸ごと）をEnabled=falseでグレー
    /// アウト・操作不可にする——タブと違い両方式の存在が常に見えるが、
    /// 設定ウィンドウ全体を「複数の別ウィンドウを開き回る」から「1画面で完結」
    /// する構成に揃えたいという方針を優先した。</summary>
    private void AddCloudAiSettingsGroup(TableLayoutPanel parentGrid)
    {
        var row = parentGrid.RowCount++;
        parentGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var group = new GroupBox
        {
            Text = "生成AI（クラウド）翻訳設定",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 8),
            Margin = new Padding(3, 6, 3, 6),
        };
        parentGrid.SetColumnSpan(group, 3);
        parentGrid.Controls.Add(group, 0, row);

        var outer = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        group.Controls.Add(outer);

        // v0.58.4: 各方式ごとに「ラジオボタン→説明文→入力フォーム」を縦に並べる
        // 構成に変更した（従来は両方のラジオボタンを先頭にまとめて並べていたが、
        // どちらの説明・フォームがどちらのラジオボタンに対応するか分かりにくい
        // というフィードバックを受けた）。グレーアウト対象は入力フォーム
        // （_claudeCodeGrid/_openAiGrid）のみ——ラジオボタン自体とその説明文は
        // 常に操作・閲覧可能なままにする。
        _radClaudeCodeCli.Margin = new Padding(3, 3, 3, 3);
        outer.Controls.Add(_radClaudeCodeCli);

        var claudeCodeNote = new Label
        {
            Text = "Claude Code CLIで翻訳する方式です。\n" +
                   "CLI上でログイン作業を事前に実行しておいてください。",
            AutoSize = true,
            Margin = new Padding(21, 0, 3, 6),
        };
        outer.Controls.Add(claudeCodeNote);

        _claudeCodeGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(18, 0, 0, 12) };
        _claudeCodeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _claudeCodeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _claudeCodeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddSettingRow(_claudeCodeGrid, "claude.exeのパス", _txtClaudeCodeExe, null, null);
        AddSettingRow(_claudeCodeGrid, "モデル名（省略可）", _txtClaudeCodeModel, null, null);
        outer.Controls.Add(_claudeCodeGrid);

        _radOpenAiApi.Margin = new Padding(3, 3, 3, 3);
        outer.Controls.Add(_radOpenAiApi);

        var openAiNote = new Label
        {
            Text = "OpenAI互換のAPIを用いて各サービスで翻訳する方式です（OpenAI／OpenRouter／DeepSeek／Groq等）。\n" +
                   "【注意】未検証のため、動作しない・意図しない結果になる可能性があります。",
            AutoSize = true,
            Margin = new Padding(21, 0, 3, 6),
        };
        outer.Controls.Add(openAiNote);

        _openAiGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(18, 0, 0, 0) };
        _openAiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _openAiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _openAiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddSettingRow(_openAiGrid, "エンドポイント", _txtCloudAiEndpoint, null, null);
        var apiKeyRow = _openAiGrid.RowCount++;
        _openAiGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _openAiGrid.Controls.Add(new Label { Text = "APIキー（省略可）", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, apiKeyRow);
        _txtCloudAiApiKey.Dock = DockStyle.Fill;
        _txtCloudAiApiKey.Margin = new Padding(3, 5, 3, 3);
        _openAiGrid.Controls.Add(_txtCloudAiApiKey, 1, apiKeyRow);
        _btnToggleCloudAiApiKeyVisibility.Margin = new Padding(3, 3, 3, 3);
        _btnToggleCloudAiApiKeyVisibility.Click += (_, _) =>
        {
            _txtCloudAiApiKey.UseSystemPasswordChar = !_txtCloudAiApiKey.UseSystemPasswordChar;
            _btnToggleCloudAiApiKeyVisibility.Text = _txtCloudAiApiKey.UseSystemPasswordChar ? "APIキーを表示" : "APIキーを隠す";
        };
        _openAiGrid.Controls.Add(_btnToggleCloudAiApiKeyVisibility, 2, apiKeyRow);
        outer.Controls.Add(_openAiGrid);

        _radClaudeCodeCli.CheckedChanged += (_, _) => UpdateCloudAiPanelsEnabled();
        _radOpenAiApi.CheckedChanged += (_, _) => UpdateCloudAiPanelsEnabled();
    }

    private void UpdateCloudAiPanelsEnabled()
    {
        _claudeCodeGrid.Enabled = _radClaudeCodeCli.Checked;
        _openAiGrid.Enabled = _radOpenAiApi.Checked;
    }

    /// <summary>v0.58.1: "thinking"対応モデル（Ollamaのgemma4等）向けの
    /// reasoning_effort=none送信可否。AppSettings.LlmLocalReasoningOffの説明
    /// コメント参照——既定ON。ユーザーがOFF（＝思考を有効なまま）にしようとした
    /// 場合のみ警告する（既定ONのままなら何も表示しない）。</summary>
    private void AddLlmReasoningOffRow(TableLayoutPanel grid)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "ローカルLLM思考OFF", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, row);
        grid.Controls.Add(_chkLlmReasoningOff, 1, row);
        _chkLlmReasoningOff.CheckedChanged += (_, _) =>
        {
            if (_chkLlmReasoningOff.Checked) return;
            MessageBox.Show(this,
                "思考モードを有効にすると:\n\n" +
                "・思考に対応していないモデルでは、この設定自体が無視され何も変わりません。\n" +
                "・思考に対応しているモデル（gemma4等）では、翻訳の実行に大幅に時間がかかるようになったり、\n" +
                "　候補によっては翻訳結果が得られず未解決のまま残る可能性が高くなります\n" +
                "　（実機検証では、同条件でバッチの成功率が大きく下がることを確認しています）。\n\n" +
                "よく分からない場合は、チェックを入れたまま（既定）にしておくことを推奨します。",
                "思考モードについて", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
    }

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
        // 2026-09-17: 空欄のままだと「既定値が何か」が分からず不安という指摘を
        // 受け、常に相対パス表記の既定値を表示しておく（MainForm.PluginImportDir
        // 等のプロパティは、相対パスなら製品ルート基準で解決するため、未変更の
        // まま保存されても既定と同じ場所を指す——PluginImportDir参照）。
        _txtPluginImportDirOverride.Text = string.IsNullOrWhiteSpace(settings.PluginImportDirOverride)
            ? Path.Combine("import", "plugin") : settings.PluginImportDirOverride;
        _txtInterfaceImportDirOverride.Text = string.IsNullOrWhiteSpace(settings.InterfaceImportDirOverride)
            ? Path.Combine("import", "interface") : settings.InterfaceImportDirOverride;
        _txtPluginOutDirOverride.Text = string.IsNullOrWhiteSpace(settings.PluginOutDirOverride)
            ? "out" : settings.PluginOutDirOverride;
        _txtInterfaceOutDirOverride.Text = string.IsNullOrWhiteSpace(settings.InterfaceOutDirOverride)
            ? "out" : settings.InterfaceOutDirOverride;
        _txtLlmEndpoint.Text = settings.LlmEndpoint;
        _txtLlmModel.Text = settings.LlmModel;
        _chkLlmReasoningOff.Checked = settings.LlmLocalReasoningOff;
        _initialLlmEndpoint = settings.LlmEndpoint;
        _initialLlmModel = settings.LlmModel;

        _txtCloudAiEndpoint.Text = settings.CloudAiEndpoint;
        _txtCloudAiApiKey.Text = settings.CloudAiApiKey; // 復号済み・メモリ上のみ
        _txtClaudeCodeExe.Text = string.IsNullOrWhiteSpace(settings.ClaudeCodeExePath) ? "claude" : settings.ClaudeCodeExePath;
        _txtClaudeCodeModel.Text = settings.ClaudeCodeModel;
        _radClaudeCodeCli.Checked = settings.UseClaudeCodeCli;
        _radOpenAiApi.Checked = !settings.UseClaudeCodeCli;
        UpdateCloudAiPanelsEnabled();
    }

    private void SaveToSettings()
    {
        var settings = _owner.Settings;
        settings.Mo2InstanceDir = _txtMo2Dir.Text.Trim();
        settings.Mo2ModsDirOverride = _txtMo2ModsDirOverride.Text.Trim();
        settings.Mo2ProfileDirOverride = _txtMo2ProfileDirOverride.Text.Trim();
        settings.Mo2OverwriteDirOverride = _txtMo2OverwriteDirOverride.Text.Trim();
        settings.PluginImportDirOverride = _txtPluginImportDirOverride.Text.Trim();
        settings.InterfaceImportDirOverride = _txtInterfaceImportDirOverride.Text.Trim();
        settings.PluginOutDirOverride = _txtPluginOutDirOverride.Text.Trim();
        settings.InterfaceOutDirOverride = _txtInterfaceOutDirOverride.Text.Trim();
        settings.LlmEndpoint = _txtLlmEndpoint.Text.Trim();
        settings.LlmModel = _txtLlmModel.Text.Trim();
        settings.LlmLocalReasoningOff = _chkLlmReasoningOff.Checked;

        settings.CloudAiEndpoint = _txtCloudAiEndpoint.Text.Trim();
        settings.CloudAiApiKey = _txtCloudAiApiKey.Text.Trim(); // 暗号化されてAppSettingsへ
        settings.UseClaudeCodeCli = _radClaudeCodeCli.Checked;
        settings.ClaudeCodeExePath = string.IsNullOrWhiteSpace(_txtClaudeCodeExe.Text) ? "claude" : _txtClaudeCodeExe.Text.Trim();
        settings.ClaudeCodeModel = _txtClaudeCodeModel.Text.Trim();

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

    /// <summary>2026-09-17: import/outフォルダ設定の入力欄は既定値を相対パス
    /// 表記（例: "import/plugin"）で表示している——実際の場所はGUI自身の
    /// カレントディレクトリではなく製品ルート基準で解決される（MainForm.
    /// PluginImportDir等参照）ため、「参照...」ダイアログの初期位置もそこを
    /// 基準に解決する必要がある（さもないと相対パスがGUIプロセス自身のCWD
    /// 基準で解決され、存在しないと判定されて初期位置が効かない）。
    ///
    /// <see cref="FolderBrowserDialog.InitialDirectory"/>（.NET 9時点で既定
    /// 有効なモダン版ダイアログ、<see cref="FolderBrowserDialog.AutoUpgradeEnabled"/>
    /// 既定true、が持つプロパティ）を使う——<see cref="FolderBrowserDialog.SelectedPath"/>
    /// は「対象フォルダ自体を選択済み状態にする」ため、ダイアログは親フォルダの
    /// 階層内でそれがハイライトされた状態で開いてしまう（対象フォルダの中身が
    /// 見えない）。InitialDirectoryは「そのフォルダ自体を表示（中身を開いた状態）
    /// して始める」ためのプロパティで、ユーザーが望む挙動そのもの。</summary>
    private void BrowseFolder(TextBox box, string description = "フォルダを選択", Func<string, string>? resolveForExistenceCheck = null)
    {
        using var dlg = new FolderBrowserDialog { Description = description };
        var candidate = box.Text;
        if (!string.IsNullOrWhiteSpace(candidate) && resolveForExistenceCheck != null)
            candidate = resolveForExistenceCheck(candidate);
        if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            dlg.InitialDirectory = candidate;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            box.Text = dlg.SelectedPath;
    }
}
