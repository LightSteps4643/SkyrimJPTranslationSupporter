using System.Data;
using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcherGui;

/// <summary>
/// メインウィンドウ — v0.52.1a: 旧「翻訳前の状況」ウィンドウ（PreTranslationForm）を
/// 昇格させ、CLI実行基盤（RunCliAsync・CliLocator・LogWindow・AppSettings保持）を
/// 統合したもの。
///
/// 経緯: 旧ベース画面（MainForm）の実質的な役割は、②プラグイン一覧表示（削除——MO2
/// 自体で確認できるため不要と判断）、③翻訳状態をスキャン（「翻訳前の状況」ウィンドウ
/// 自身の「再スキャン」ボタンと完全に重複）を除くと、ほぼ「設定」だけになっていた。
/// 一方「翻訳前の状況」ウィンドウは、①〜⑥の自動解決状況の確認・翻訳実行・DSD生成という
/// 実質的な操作のほぼ全てを担っていた。そこで「翻訳前の状況」ウィンドウをそのまま
/// メインウィンドウに昇格させ、旧ベース画面の設定関連（MO2フォルダ・各種パス・
/// LLM設定・①MO2ロード）は<see cref="SettingsForm"/>へ丸ごと切り出した。
///
/// 副次効果として、ウィンドウが実質1つに統合されたことで、以前検討していた
/// 「CLI実行中はウィンドウをまたいで排他制御する」ための共有ロック機構が丸ごと
/// 不要になった——このウィンドウ自身のSetBusyだけで完結する。残るPseudoModalは
/// SettingsForm・CloudAiSettingsForm・TranslationDetailFormという、純粋に
/// 「開いている間だけこのウィンドウをロックしたい」一時的な子ウィンドウ用途のみ。
///
/// "翻訳" and DSD generation are deliberately separate steps (not one combined
/// action): after `translation` finishes, translations.tsv is sitting on disk
/// and reviewable/editable (e.g. via the "詳細を確認" viewer, or directly in a
/// spreadsheet) BEFORE it gets baked into the final DSD json. "DSDファイル生成"
/// commits whatever is currently in translations.tsv — including any manual
/// edits — whenever the user is ready, not automatically right after translating.
///
/// The "high translation load" emphasis is a continuous color scale on the
/// untranslated-character column, not a pass/fail judgment — a fixed threshold
/// was deliberately removed from the CLI's own plugin_summary.txt in v0.20.0→
/// v0.21.0 because it hid real cases (few records, huge char count). Repeating
/// that mistake here would undo that fix, just relocated into the GUI.
///
/// Per-plugin selection (the "選択" checkbox column) maps to a single CLI
/// invocation with `--plugins-file=&lt;temp file listing the checked plugins&gt;`
/// (PromptGenerator.RunMany) — NOT one invocation per plugin. An earlier version
/// looped single-plugin invocations, which repeated the CLI's ~10s corpus/
/// dictionary setup once per selected plugin (up to 175 times); RunMany does
/// that setup once regardless of how many plugins are selected.
/// </summary>
public sealed class MainForm : Form
{
    // --- CLI実行基盤（旧ベース画面から） ---
    private readonly LogWindow _logWindow = new();
    // v0.52.1a: 自前のボタン行（FlowLayoutPanel/TableLayoutPanelの組み合わせ）は
    // 幅の確定タイミングでレイアウトが崩れやすく（実際に「設定」ボタンの位置が
    // おかしくなる・「ログ」ボタンが消える不具合が起きた）、上部に不要な余白も
    // 生まれていた。標準のMenuStripに置き換えることで、両方まとめて解消する。
    private readonly MenuStrip _menuStrip = new();
    private readonly ToolStripMenuItem _menuSettings = new("設定");
    private readonly ToolStripMenuItem _menuLog = new("ログ");

    private AppSettings _settings = new();
    private string? _productRoot;

    internal AppSettings Settings => _settings;
    internal string ProductRoot => _productRoot ?? throw new InvalidOperationException("Product root not resolved.");
    internal string Mo2Dir => _settings.Mo2InstanceDir;

    /// <summary>v0.57.0: "pickuptarget" args for the current MO2 dir, with the
    /// optional mods/profile/overwrite path overrides appended when set.
    /// v0.57.3: SettingsForm used to have its own "MO2フォルダをロード" call
    /// site here too, but it only ran pickuptarget (never translation), so
    /// clicking it never actually updated this window's own plugin list
    /// (which reads Translation/out_temp, not PickUpTarget/out_temp) — a
    /// button that visibly did nothing, confirmed as dead weight and removed.
    /// This is now this window's own sole call site (below).</summary>
    internal string[] BuildPickupTargetArgs(string mo2Dir)
    {
        var args = new List<string> { "pickuptarget", mo2Dir };
        if (!string.IsNullOrWhiteSpace(_settings.Mo2ModsDirOverride))
            args.Add($"--mods-dir={_settings.Mo2ModsDirOverride}");
        if (!string.IsNullOrWhiteSpace(_settings.Mo2ProfileDirOverride))
            args.Add($"--profile-dir={_settings.Mo2ProfileDirOverride}");
        if (!string.IsNullOrWhiteSpace(_settings.Mo2OverwriteDirOverride))
            args.Add($"--overwrite-dir={_settings.Mo2OverwriteDirOverride}");
        return args.ToArray();
    }
    internal string LlmEndpoint => _settings.LlmEndpoint;
    internal string LlmModel => _settings.LlmModel;
    internal string LlmApiKey => _settings.LlmApiKey;
    internal bool UseClaudeCodeCli => _settings.UseClaudeCodeCli;
    internal string ClaudeCodeExePath => _settings.ClaudeCodeExePath;
    internal string ClaudeCodeModel => _settings.ClaudeCodeModel;
    internal string CloudAiEndpoint => _settings.CloudAiEndpoint;
    internal string CloudAiApiKey => _settings.CloudAiApiKey;

    /// <summary>A CLI subprocess launched via RunCliAsync doesn't stop just
    /// because the GUI window closes — without this, closing mid-run leaves
    /// SkyrimJPStringPatcher.exe running invisibly in the background. Cancelling
    /// this token makes CliRunner.RunAsync kill the process (and its tree) before
    /// the exception propagates back up — see the OperationCanceledException
    /// handling in RunCliAsync below, which stays silent (no error dialog) since
    /// this is an intentional user shutdown, not a failure.</summary>
    private CancellationTokenSource? _currentRunCts;

    /// <summary>v0.53.0a: 「翻訳実行」中だけ非nullになる、キャンセル要求用の一時
    /// フラグファイルのパス（既知の課題15.）。「翻訳実行」以外のCLI実行（MO2再読込・
    /// DSD生成等、すぐ終わる処理）にはキャンセルボタンを出さない（ユーザーの明示的な
    /// スコープ決定）ため、他のRunCliAsync呼び出しではnullのままにしておく。
    /// _currentRunCtsの強制kill（ウィンドウを閉じたとき用）とは別系統——こちらは
    /// CLI自身がプラグインの区切りで自発的に止まる、協調的な中断。</summary>
    private string? _activeCancelFlagPath;

    /// <summary>_activeCancelFlagPathが有効な間にキャンセルが要求されたかどうか——
    /// 「翻訳実行」の完了ダイアログを、通常完了とキャンセルによる途中終了とで
    /// 出し分けるために使う（CLIはどちらもexit code 0で正常終了するため、
    /// RunCliAsyncの戻り値だけでは区別できない）。</summary>
    private bool _cancelRequestedForCurrentRun;

    // --- 翻訳前の状況（旧PreTranslationForm、このクラスに統合済み） ---
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        AutoGenerateColumns = false, // manual columns — see BuildGridColumns; needed to keep the unbound "詳細" button column across DataSource rebinds
    };
    private readonly DataTable _table = new();

    private const string DetailColumnName = "詳細";
    private const string ResetColumnName = "初期化";

    private readonly Button _btnSelectAll = new() { Text = "すべて選択", AutoSize = true };
    private readonly Button _btnSelectNone = new() { Text = "すべて解除", AutoSize = true };

    private readonly CheckBox _chkVanillaCorpus = new() { Text = "バニラコーパス（常時適用）", Checked = true, Enabled = false, AutoSize = true };
    private readonly CheckBox _chkMeaning = new() { Text = "意味翻訳（品質中）", Checked = true, AutoSize = true };
    private readonly CheckBox _chkTranslit = new() { Text = "音訳分解（品質中）", Checked = true, AutoSize = true };
    private readonly CheckBox _chkNameFallback = new() { Text = "簡易名前解決（品質中～低）", Checked = true, AutoSize = true };
    private const string LlmCheckboxLabel = "Beta機能: ローカルLLM翻訳（品質中～低）";
    private readonly CheckBox _chkLlm = new() { Text = LlmCheckboxLabel, Checked = false, AutoSize = true };

    // v0.52.1a: ローカルLLMとは別扱いの独立チェックボックス。「生成AI（クラウド）
    // 連携設定」ウィンドウで選ばれている方式（Claude Code CLI／OpenAI互換API）を
    // 使う。⑤ローカルLLM→⑥生成AI翻訳の順のチェーンとして動く独立ステップなので
    // （CLI側もllm-local/llm-cloudの2つを独立に受け取れる）、両方同時にONで
    // 構わない——⑤で解決できなかったものだけが⑥に回る。
    private const string CloudAiCheckboxLabel = "Beta機能: 生成AI翻訳（クラウド・品質中～低）";
    private readonly CheckBox _chkCloudAi = new() { Text = CloudAiCheckboxLabel, Checked = false, AutoSize = true };

    // v0.52.1a: ⑤⑥共通——1回のLLM呼び出しにまとめる候補の合計文字数の上限
    // （PromptGenerator.ApplyLlmStep参照、CLI側は--llm-batch-char-limit=）。
    // 生成AIサービス・契約プランによって妥当な値が変わりうる（無料枠では既定値
    // より小さくしたい等）ため、GUIからも変更できるようにしてある。GUIはCLIを
    // サブプロセス起動するだけの薄い層でPromptGeneratorを直接参照できない
    // （プロジェクト参照が無い）ため、既定値12000はCLI側と別々に保持している
    // ——両方変えるときはPromptGenerator.DefaultLlmBatchCharLimitとここを揃える。
    private const int DefaultLlmBatchCharLimit = 12_000;
    private readonly Label _lblBatchCharLimit = new() { Text = "LLM一括翻訳: 1回あたりの文字数上限（生成AI翻訳・ローカルLLM翻訳共通）", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
    private readonly NumericUpDown _numBatchCharLimit = new()
    {
        Minimum = 100,
        Maximum = 1_000_000,
        Increment = 1000,
        Value = DefaultLlmBatchCharLimit,
        Width = 90,
    };

    // Width指定は付けない — v0.52.1a: BuildLayoutの最後でButtonLayout.UnifyWidthsが
    // 実際の文言の幅を測って一律に揃えるため、ここで決め打ちすると測定前に上書きされる。
    private readonly Button _btnResetSelected = new() { Text = "選択プラグインを一括初期化", AutoSize = true };
    // v0.52.1a: 「再スキャン」は読み取り専用に変更——Translation/out_temp配下の
    // translations.tsvを直接スキャンするだけで、CLIは一切呼ばない（既存の
    // 翻訳結果を壊さない）。新規プラグインの取り込み・コーパス更新の反映には
    // pickuptarget＋translationの実行が必要なため、それは別ボタン
    // （_btnReloadMo2、破壊的操作なので確認ダイアログ付き）に分離した。
    private readonly Button _btnRescan = new() { Text = "再スキャン（読み取りのみ）", AutoSize = true };
    private readonly Button _btnReloadMo2 = new() { Text = "MO2再読込＆初期化", AutoSize = true };
    private readonly Button _btnTranslate = new() { Text = "翻訳実行", AutoSize = true };
    private readonly Button _btnGenerateDsd = new() { Text = "DSDファイル生成", AutoSize = true };
    // v0.54.2（既知の課題22.）: 設定画面には既にimport/outフォルダを開く導線が
    // あるが、ベース画面からも直接開けるようにする——2つとも主要アクション
    // ボタンではないため、UnifyWidthsの幅統一対象には含めない。
    private readonly Button _btnOpenImportFolder = new() { Text = "xTranslator翻訳XMLインポートフォルダを開く", AutoSize = true };
    private readonly Button _btnOpenOutFolder = new() { Text = "DSD出力フォルダを開く", AutoSize = true };

    /// <summary>True once "翻訳実行" has completed successfully at least once in
    /// this window — drives the warning if "DSDファイル生成" is pressed first
    /// (untranslated candidates would otherwise silently ship in English).</summary>
    private bool _translationExecuted;
    private readonly Label _lblSummary = new() { AutoSize = true };

    /// <summary>Plugins the user has unchecked — remembered across "再スキャン"
    /// reloads (which rebuild the whole table) so an intentional exclusion isn't
    /// silently lost when re-scanning after collecting more xTranslator files.</summary>
    private readonly HashSet<string> _deselectedPlugins = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "Skyrim JP Translation Supporter";
        Width = 1150;
        Height = 850;
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildGridColumns();
        InitTableColumns();
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
        _logWindow.CancelRequested += LogWindow_CancelRequested;
    }

    /// <summary>v0.53.0a: 「キャンセル」ボタンが押された（LogWindowから中継された）
    /// ——確認ダイアログを出し、了承されればフラグファイルを作成する。ボタン自体は
    /// _activeCancelFlagPathがnullでない間しか押せない状態にしてある（SetBusy相当の
    /// 連動、BtnTranslate_Click参照）ため、ここに来る時点で対象の実行が存在することは
    /// 保証されている。</summary>
    private void LogWindow_CancelRequested(object? sender, EventArgs e)
    {
        if (_activeCancelFlagPath == null) return;

        var result = MessageBox.Show(_logWindow,
            "処理を中断しますか？\n\n" +
            "・すぐには止まりません。現在処理中のプラグインの完了を待ってから停止します。\n" +
            "・既に生成AI（クラウド）へ送信済みの呼び出し分の課金は取り消せません。\n" +
            "・それ以降の未処理プラグインは翻訳されないまま残りますが、\n" +
            "　次回「翻訳実行」で続きから再開できます（完了済み分は再翻訳されません）。",
            "処理の中断", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;

        try { File.WriteAllText(_activeCancelFlagPath, ""); } catch { /* best-effort */ }
        _cancelRequestedForCurrentRun = true;
        _logWindow.SetCancelEnabled(false);
        SetStatus("キャンセル要求済み（現在のプラグインの完了を待って停止します）");
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _currentRunCts?.Cancel();
        // LogWindowを明示的に閉じる必要はない——Application.Run(MainForm)は
        // MainFormが閉じた時点でプロセスごと終了する（LogWindowがどんな状態でも）。
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        // メインウィンドウのすぐ右隣にログウィンドウを開く（両方CenterScreenだと
        // 完全に重なってしまうため、位置をずらす）。
        _logWindow.StartPosition = FormStartPosition.Manual;
        _logWindow.Location = new Point(Left + Width + 10, Top);
        _logWindow.Show();

        _settings = AppSettings.Load();
        _productRoot = CliLocator.TryGetProductRoot();
        _numBatchCharLimit.Value = Math.Clamp(_settings.LlmBatchCharLimit, (int)_numBatchCharLimit.Minimum, (int)_numBatchCharLimit.Maximum);

        // plugin_summary.txtがまだ無くても（一度もスキャンしていなくても）
        // LoadDataは空一覧として扱うので、常時ロードして問題ない。
        LoadData();
    }

    private void BuildLayout()
    {
        // v0.52.1a: 「設定」「ログ」はドロップダウン項目を持たない単純な
        // メニュー項目——クリックすると即座にそれぞれのウィンドウを開く、
        // アプリによくあるメニューバー形式。MenuStripは自前のDock=Top行を
        // 作るより確実に上部に収まり、余分な余白も生まれない。
        _menuSettings.Click += (_, _) => OpenSettings();
        _menuLog.Click += (_, _) => _logWindow.ShowAndActivate();
        _menuStrip.Items.Add(_menuSettings);
        _menuStrip.Items.Add(_menuLog);
        MainMenuStrip = _menuStrip;

        // 3 rows: 説明文＋すべて選択/解除 → 表 → 下部パネル.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // WinFormsのDock処理順の慣例通り、Dock=FillのrootをDock=Topの
        // MenuStripより先にControlsへ追加する（先に追加した方が背面に回り、
        // 後から追加したTop/Bottom/Left/Right側が自分の分だけ領域を確保する）。
        Controls.Add(root);
        Controls.Add(_menuStrip);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };

        var summaryRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        summaryRow.Controls.Add(_lblSummary);
        top.Controls.Add(summaryRow);

        var selectRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 0) };
        _btnSelectAll.Click += (_, _) => SetAllSelected(true);
        _btnSelectNone.Click += (_, _) => SetAllSelected(false);
        selectRow.Controls.Add(_btnSelectAll);
        selectRow.Controls.Add(_btnSelectNone);
        ButtonLayout.UnifyWidths(new[] { _btnSelectAll, _btnSelectNone });
        top.Controls.Add(selectRow);

        root.Controls.Add(top, 0, 0);

        root.Controls.Add(_grid, 0, 1);
        _grid.CellFormatting += Grid_CellFormatting;
        _grid.CurrentCellDirtyStateChanged += Grid_CurrentCellDirtyStateChanged;
        _grid.CellValueChanged += (_, _) => UpdateSummaryLabel();

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // ①〜④は1行目、⑤⑥（クラウド生成AI・ローカルLLM）は2行目、⑤⑥共通の
        // バッチ文字数上限は3行目 — 一目で「クラウド系の解決手段とその設定」という
        // グループだと分かるように。
        var options = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 3 };
        var optionsRow1 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        optionsRow1.Controls.Add(_chkVanillaCorpus);
        optionsRow1.Controls.Add(_chkMeaning);
        optionsRow1.Controls.Add(_chkTranslit);
        optionsRow1.Controls.Add(_chkNameFallback);
        options.Controls.Add(optionsRow1, 0, 0);

        // 生成AI（クラウド）→ローカルLLM の順で並べる。「この左にクラウド経由の
        // 生成AIオプションを追加したい」という以前からの構想通りの配置。
        var optionsRow2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        optionsRow2.Controls.Add(_chkCloudAi);
        optionsRow2.Controls.Add(_chkLlm);
        options.Controls.Add(optionsRow2, 0, 1);
        _chkLlm.CheckedChanged += ChkLlm_CheckedChanged;
        _chkCloudAi.CheckedChanged += ChkCloudAi_CheckedChanged;

        // v0.52.1a: 生成AI翻訳オプションのすぐ下に配置——⑤⑥どちらにも効く設定
        // なので、片方だけの行に混ぜず独立した行にしてある。
        var optionsRow3 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        optionsRow3.Controls.Add(_lblBatchCharLimit);
        optionsRow3.Controls.Add(_numBatchCharLimit);
        options.Controls.Add(optionsRow3, 0, 2);
        // v0.53.0a: 変更を即座にAppSettingsへ反映・保存する——以前はGUI上でしか
        // 保持されず、次回起動時に既定値へ戻ってしまっていた不具合の修正。
        _numBatchCharLimit.ValueChanged += (_, _) =>
        {
            _settings.LlmBatchCharLimit = (int)_numBatchCharLimit.Value;
            _settings.Save();
        };

        bottom.Controls.Add(options, 0, 0);

        // Stacked top-to-bottom: 選択プラグインを一括初期化 → 再スキャン（読み取りのみ）
        // → MO2再読込＆初期化 → 翻訳実行 → DSDファイル生成. v0.54.2（既知の課題22.）:
        // 「左隣に開くボタン」を、入れ子のFlowLayoutPanelではなく5行×2列の
        // TableLayoutPanelで実現する——FlowLayoutPanelを入れ子にすると、各行の
        // 幅がまちまちになり左端が揃わず見た目が崩れた（実機で確認済み）。
        // グリッドなら列0（開くボタン、3・4行目のみ）と列1（主要アクション、
        // 全5行）が自然に整列する。
        // v0.55.0a: 「MO2再読込＆初期化」と「翻訳実行」の間に、ボタン1つ分程度の
        // 空き行を挟む——押し間違い防止と、破壊的な再初期化ステージと非破壊の
        // 翻訳ステージが別物であることを視覚的に示す狙い（ユーザー要望）。
        var actions = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 6 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 6; i++) actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _btnResetSelected.Click += BtnResetSelected_Click;
        _btnTranslate.Click += BtnTranslate_Click;
        _btnRescan.Click += BtnRescan_Click;
        _btnReloadMo2.Click += BtnReloadMo2_Click;
        _btnGenerateDsd.Click += BtnGenerateDsd_Click;
        _btnOpenImportFolder.Click += (_, _) => FolderOpener.OpenOrWarn(this, Path.Combine(ProductRoot, "Translation", "import"));
        _btnOpenOutFolder.Click += (_, _) => FolderOpener.OpenOrWarn(this, Path.Combine(ProductRoot, "out"));
        _btnResetSelected.Margin = new Padding(3, 3, 3, 3);
        _btnRescan.Margin = new Padding(3, 3, 3, 3);
        _btnReloadMo2.Margin = new Padding(3, 3, 3, 3);
        _btnTranslate.Margin = new Padding(3, 3, 3, 3);
        _btnGenerateDsd.Margin = new Padding(3, 3, 3, 3);
        _btnOpenImportFolder.Margin = new Padding(3, 3, 3, 3);
        _btnOpenOutFolder.Margin = new Padding(3, 3, 3, 3);
        // 列0はAutoSizeで content 幅ぴったりになるはずだが、念のため右揃えに
        // ピン留めして列1（既存ボタン列）にぴったり隣接させる。
        _btnOpenImportFolder.Anchor = AnchorStyles.Right;
        _btnOpenOutFolder.Anchor = AnchorStyles.Right;
        actions.Controls.Add(_btnResetSelected, 1, 0);
        actions.Controls.Add(_btnRescan, 1, 1);
        actions.Controls.Add(_btnOpenImportFolder, 0, 2);
        actions.Controls.Add(_btnReloadMo2, 1, 2);
        var stageSpacer = new Panel { AutoSize = false, Height = _btnReloadMo2.PreferredSize.Height, Width = 1 };
        actions.Controls.Add(stageSpacer, 1, 3);
        actions.Controls.Add(_btnTranslate, 1, 4);
        actions.Controls.Add(_btnOpenOutFolder, 0, 5);
        actions.Controls.Add(_btnGenerateDsd, 1, 5);
        ButtonLayout.UnifyWidths(new[] { _btnResetSelected, _btnRescan, _btnReloadMo2, _btnTranslate, _btnGenerateDsd });
        bottom.Controls.Add(actions, 1, 0);

        root.Controls.Add(bottom, 0, 2);
    }

    private void OpenSettings()
    {
        var form = new SettingsForm(this);
        PseudoModal.Show(form, this);
    }

    private void BuildGridColumns()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "選択", HeaderText = "選択", DataPropertyName = "選択", Width = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "プラグイン", HeaderText = "プラグイン", DataPropertyName = "プラグイン", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "全体件数", HeaderText = "全体件数", DataPropertyName = "全体件数", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "未翻訳件数", HeaderText = "未翻訳件数", DataPropertyName = "未翻訳件数", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "翻訳率(件数)", HeaderText = "翻訳率(件数)", DataPropertyName = "翻訳率(件数)", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "未翻訳文字数", HeaderText = "未翻訳文字数", DataPropertyName = "未翻訳文字数", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "翻訳率(文字数)", HeaderText = "翻訳率(文字数)", DataPropertyName = "翻訳率(文字数)", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = DetailColumnName,
            HeaderText = "",
            Text = "詳細を確認",
            UseColumnTextForButtonValue = true,
            Width = 100,
        });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = ResetColumnName,
            HeaderText = "",
            Text = "翻訳状況を初期化",
            UseColumnTextForButtonValue = true,
            Width = 130,
        });
        _grid.CellContentClick += Grid_CellContentClick;
    }

    /// <summary>Opens Translation/out_temp/&lt;plugin&gt;/translations.tsv — the CLI's
    /// own per-candidate output — in a read-only viewer. Only meaningful after a
    /// scan (or translate) has actually written that plugin's folder; otherwise
    /// says so instead of showing an empty grid with no explanation.</summary>
    private async void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var columnName = _grid.Columns[e.ColumnIndex].Name;
        if (columnName != DetailColumnName && columnName != ResetColumnName) return;

        var plugin = (string)_grid.Rows[e.RowIndex].Cells["プラグイン"].Value!;

        if (columnName == ResetColumnName)
        {
            await ResetPlugin(plugin);
            return;
        }

        var path = Path.Combine(ProductRoot, "Translation", "out_temp", PluginFolderName.From(plugin), "translations.tsv");
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"まだこのプラグインの翻訳結果がありません:\n{path}\n先に「MO2再読込＆初期化」または「翻訳実行」を行ってください。",
                "ファイルが見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var detail = new TranslationDetailForm(plugin, path, onSaved: () => RefreshRowsFromTranslations(new[] { plugin }));
        // 擬似モーダル — 開いている間はこのウィンドウをロックする（ファイルを
        // 直接編集する操作なので、MO2再読込＆初期化等が走って同じファイルを
        // 上書きされると困る）。ウィンドウが1つに統合されたため、ロック先は
        // このウィンドウだけでよい（Services/PseudoModal.cs参照）。
        PseudoModal.Show(detail, this);
    }

    /// <summary>「翻訳状況を初期化」— resets ONE plugin back to the same ①のみ
    /// baseline as a fresh scan (--no-meaning/--no-translit/--no-namefallback),
    /// and discards any "ModifiedByUser" rows for it (--discard-user-edits) —
    /// the per-plugin equivalent of "as if I had just scanned and never touched
    /// this plugin at all."</summary>
    private async Task ResetPlugin(string plugin)
    {
        var confirm = MessageBox.Show(this,
            $"「{plugin}」の翻訳状況を初期化します。\n" +
            "このプラグインの翻訳結果（手動での編集を含む）をすべて消去し、初期状態に戻します。\n" +
            "元に戻せません。よろしいですか？",
            "翻訳状況を初期化", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        TranslationBackup.Backup(ProductRoot, new[] { PluginFolderName.From(plugin) });

        SetBusy(true);
        try
        {
            var args = new[] { "translation", "PickUpTarget/out_temp", "Translation/out_temp", plugin,
                "--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits" };
            if (!await RunCliAsync(args)) return;
            RefreshRowsFromTranslations(new[] { plugin });
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>「選択プラグインを一括初期化」— same reset as the per-row button,
    /// but for every currently-checked（選択）plugin in one CLI invocation via
    /// --plugins-file (see PromptGenerator.RunMany's remarks). Check "すべて選択"
    /// first to reset the entire load order at once.</summary>
    private async void BtnResetSelected_Click(object? sender, EventArgs e)
    {
        var selectedPlugins = GetSelectedPlugins();
        if (selectedPlugins.Count == 0)
        {
            MessageBox.Show(this, "初期化するプラグインを少なくとも1つ選択してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"選択中の{selectedPlugins.Count}プラグインの翻訳状況を初期化します。\n" +
            "対象プラグインの翻訳結果（手動での編集を含む）をすべて消去し、初期状態に戻します。\n" +
            "元に戻せません。よろしいですか？",
            "選択プラグインを一括初期化", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        TranslationBackup.Backup(ProductRoot, selectedPlugins.Select(PluginFolderName.From));

        SetBusy(true);
        var pluginsFilePath = Path.Combine(Path.GetTempPath(), $"sjpts_reset_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(pluginsFilePath, selectedPlugins);
            var args = new[] { "translation", "PickUpTarget/out_temp", "Translation/out_temp", $"--plugins-file={pluginsFilePath}",
                "--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits" };
            if (!await RunCliAsync(args)) return;
            RefreshRowsFromTranslations(selectedPlugins);
        }
        finally
        {
            SetBusy(false);
            try { File.Delete(pluginsFilePath); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Checkbox cells only commit to the bound DataTable when the cell
    /// loses focus — force an immediate commit on toggle so selection state and
    /// the summary count stay accurate right away (the standard WinForms pattern
    /// for an instantly-responsive DataGridView checkbox column).</summary>
    private void Grid_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_grid.CurrentCell is DataGridViewCheckBoxCell)
            _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
    }

    private List<Row> _rows = new();

    private sealed record Row(string Plugin, int Total, int Untranslated, double Ratio, long UntranslatedChars, double CharsRatio);

    /// <summary>「再スキャン（読み取りのみ）」ボタンにも、起動直後の初期表示にも
    /// 使う共通の読み込み処理。
    ///
    /// v0.52.1a: `Translation/out_temp/&lt;plugin&gt;/translations.tsv`を直接
    /// スキャンして組み立てる（`plugin_summary.txt`はもう使わない）。以前は
    /// `translation --all`実行時にだけ書かれる`plugin_summary.txt`（＝直近の
    /// スキャン時点のスナップショット）に依存していたが、それだと「CLIで
    /// 生成AI翻訳した後、GUIを開いただけ」のケースで数字が古いまま（あるいは
    /// 一度もスキャンしていなければ空）になり、最新状況を見るには当時の
    /// 「再スキャン」（今の「MO2再読込＆初期化」相当）を押すしかなく、それが
    /// 翻訳結果を消してしまう、という本末転倒な状況になっていた。
    /// translations.tsv自体が各プラグインの全候補（未翻訳含む）を常に持って
    /// いるため、これを直接読めば実処理（＝reset）を一切挟まずに現状を正確に
    /// 表示できる。該当フォルダが無ければ（起動直後・何もまだ実行していない）
    /// 空一覧になるだけなので、常時呼んで問題ない。</summary>
    /// <summary>v0.53.0a: `_table`の列は一度だけ作る——以前は`LoadData()`が呼ばれる
    /// たびに（「再スキャン」を押すたび、起動時等）`_table.Columns.Clear()`で
    /// 列を丸ごと作り直していたが、その間ずっと`_grid.DataSource`はこの`_table`に
    /// バインドされたままだった。DataGridViewの列ヘッダクリックによる既定のソートを
    /// 一度でも使うと、そのソート用にDataTable内部が保持する`Index`（RBTree実装）が
    /// 直後の`Columns.Clear()`で参照先の列を失い、次の`Rows.Add()`で
    /// `System.Data.Index`内部の`NullReferenceException`が発生していた
    /// （実機で報告・原因特定済み）。列自体はどのタイミングで呼んでも同じ7列なので、
    /// 列の構築だけをコンストラクタで一度きり行い、`LoadData()`は行の入れ替えだけに
    /// 限定することで、バインド済みの`_table`に対して列を触らないようにした。</summary>
    private void InitTableColumns()
    {
        _table.Columns.Add("選択", typeof(bool));
        _table.Columns.Add("プラグイン", typeof(string));
        _table.Columns.Add("全体件数", typeof(int));
        _table.Columns.Add("未翻訳件数", typeof(int));
        _table.Columns.Add("翻訳率(件数)", typeof(string));
        _table.Columns.Add("未翻訳文字数", typeof(long));
        _table.Columns.Add("翻訳率(文字数)", typeof(string));
    }

    private void LoadData()
    {
        _rows = ScanTranslationsOutTemp();

        _table.Rows.Clear();

        foreach (var r in _rows)
        {
            var selected = !_deselectedPlugins.Contains(r.Plugin);
            _table.Rows.Add(selected, r.Plugin, r.Total, r.Untranslated, $"{r.Ratio:F1}%", r.UntranslatedChars, $"{r.CharsRatio:F1}%");
        }

        _grid.DataSource = _table;
        _maxUntranslatedChars = _rows.Count > 0 ? _rows.Max(r => r.UntranslatedChars) : 0;
        UpdateSummaryLabel();
    }

    private long _maxUntranslatedChars;

    private void UpdateSummaryLabel()
    {
        // v0.52.1a: まだ一度もスキャンしていない（＝plugin_summary.txtが存在
        // しない）状態でこのウィンドウが開かれることがありうる（起動直後）。
        // その場合は「0件」という数字だけを見せるより、次に何をすればいいかを
        // 案内する——ただし、DSDファイル生成自体はスキャン不要（out_temp配下の
        // 実ファイルを直接読むため）なので、それも明記しておく。
        if (_rows.Count == 0)
        {
            _lblSummary.Text = "まだ翻訳データがありません。「MO2再読込＆初期化」を押すと、MODの一覧を読み込んで翻訳作業を開始できます。\n" +
                "すでに翻訳ファイルを用意している場合は、そのまま「DSDファイル生成」を行うこともできます。\n" +
                "「再スキャン（読み取りのみ）」は今ある翻訳結果を確認するだけです。";
            return;
        }

        var selectedCount = _table.Rows.Cast<DataRow>().Count(r => r["選択"] is true);
        var totalUntranslated = _rows.Sum(r => r.Untranslated);
        var totalUntranslatedChars = _rows.Sum(r => r.UntranslatedChars);
        _lblSummary.Text = $"{_rows.Count}プラグイン中 {selectedCount}件選択中 ／ 全体の未翻訳 {totalUntranslated}件・{totalUntranslatedChars:N0}字" +
            "　（背景色が濃いほど未翻訳文字数が多いプラグイン）\n" +
            "未翻訳文字数が多い場合、翻訳負荷を軽減するため、別途翻訳ファイルを準備する等の対応を検討してください。\n" +
            "※翻訳対象を含むプラグインのみ表示（対象が1件も無いプラグインは一覧に出ません）";
    }

    private void SetAllSelected(bool selected)
    {
        foreach (DataRow row in _table.Rows)
            row["選択"] = selected;
        UpdateSummaryLabel();
    }

    private List<string> GetSelectedPlugins() =>
        _table.Rows.Cast<DataRow>().Where(r => r["選択"] is true).Select(r => (string)r["プラグイン"]).ToList();

    /// <summary>Updates the grid's 未翻訳件数/文字数 for just the given plugins by
    /// re-reading their own translations.tsv (the CLI's real output) — not by
    /// re-scanning. "翻訳実行" (RunMany) never writes plugin_summary.txt (that's a
    /// --all-only file, see RunMany's remarks), so this is how the grid learns
    /// what just happened without a full "再スキャン" round-trip (which would also
    /// reset everything back to the ①-only baseline).</summary>
    private void RefreshRowsFromTranslations(IEnumerable<string> plugins)
    {
        foreach (var plugin in plugins)
        {
            var path = Path.Combine(ProductRoot, "Translation", "out_temp", PluginFolderName.From(plugin), "translations.tsv");
            var rows = TsvReader.Read(path);
            if (rows.Count == 0) continue;

            var (total, untranslatedCount, ratio, untranslatedChars, charsRatio) = ComputeStats(rows);

            var index = _rows.FindIndex(r => r.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            _rows[index] = _rows[index] with { Total = total, Untranslated = untranslatedCount, Ratio = ratio, UntranslatedChars = untranslatedChars, CharsRatio = charsRatio };

            var dataRow = _table.Rows.Cast<DataRow>().FirstOrDefault(r => (string)r["プラグイン"] == plugin);
            if (dataRow == null) continue;
            dataRow["全体件数"] = total;
            dataRow["未翻訳件数"] = untranslatedCount;
            dataRow["翻訳率(件数)"] = $"{ratio:F1}%";
            dataRow["未翻訳文字数"] = untranslatedChars;
            dataRow["翻訳率(文字数)"] = $"{charsRatio:F1}%";
        }

        _maxUntranslatedChars = _rows.Count > 0 ? _rows.Max(r => r.UntranslatedChars) : 0;
        _grid.Refresh();
        UpdateSummaryLabel();
    }

    /// <summary>v0.53.0: 指定プラグインのtranslations.tsvを読み、Notes列が
    /// <paramref name="methodTag"/>（"TranslationCloudLlm"／"TranslationLocalLlm"）
    /// と一致する行数を数える——「⑤/⑥を有効にしたのに1件も解決できなかった」を
    /// 検知するために使う（BtnTranslate_Click参照）。</summary>
    private int CountResolvedByMethod(IEnumerable<string> plugins, string methodTag)
    {
        var count = 0;
        foreach (var plugin in plugins)
        {
            var path = Path.Combine(ProductRoot, "Translation", "out_temp", PluginFolderName.From(plugin), "translations.tsv");
            var rows = TsvReader.Read(path);
            count += rows.Count(r => r.GetValueOrDefault("Notes", "") == methodTag);
        }
        return count;
    }

    /// <summary>v0.52.1a: `Translation/out_temp`直下の各プラグインフォルダの
    /// translations.tsvを直接読んでグリッドの行を組み立てる——CLIが実際に
    /// 書き出した現物のファイルなので、`plugin_summary.txt`のような
    /// スキャン時点のスナップショットと違い、実行手段（GUI・CLI直接どちらで
    /// 翻訳したか）に関わらず常に現状と一致する。</summary>
    private List<Row> ScanTranslationsOutTemp()
    {
        var rows = new List<Row>();
        var translationOutTempDir = Path.Combine(ProductRoot, "Translation", "out_temp");
        if (!Directory.Exists(translationOutTempDir)) return rows;

        foreach (var pluginDir in Directory.GetDirectories(translationOutTempDir))
        {
            var tsvPath = Path.Combine(pluginDir, "translations.tsv");
            var tsvRows = TsvReader.Read(tsvPath);
            if (tsvRows.Count == 0) continue;

            // フォルダ名はサニタイズされている場合がある（PluginFolderName.From
            // 参照）ため、実際のプラグイン名はファイルの中身（WinningPlugin列）
            // から取る方が確実。
            var plugin = tsvRows[0].GetValueOrDefault("WinningPlugin", Path.GetFileName(pluginDir));
            var (total, untranslatedCount, ratio, untranslatedChars, charsRatio) = ComputeStats(tsvRows);
            rows.Add(new Row(plugin, total, untranslatedCount, ratio, untranslatedChars, charsRatio));
        }

        return rows.OrderByDescending(r => r.UntranslatedChars).ToList();
    }

    private static (int Total, int Untranslated, double Ratio, long UntranslatedChars, double CharsRatio) ComputeStats(
        List<Dictionary<string, string>> rows)
    {
        var untranslatedRows = rows.Where(r => string.IsNullOrEmpty(r.GetValueOrDefault("Japanese"))).ToList();
        var untranslatedCount = untranslatedRows.Count;
        var untranslatedChars = untranslatedRows.Sum(r => (long)Unescape(r.GetValueOrDefault("EnglishText", "")).Length);
        var totalChars = rows.Sum(r => (long)Unescape(r.GetValueOrDefault("EnglishText", "")).Length);
        var ratio = rows.Count == 0 ? 100.0 : 100.0 * (rows.Count - untranslatedCount) / rows.Count;
        var charsRatio = totalChars == 0 ? 100.0 : 100.0 * (totalChars - untranslatedChars) / totalChars;
        return (rows.Count, untranslatedCount, ratio, untranslatedChars, charsRatio);
    }

    // Mirrors Core/TsvEscaping.cs's Unescape — see TranslationDetailForm's identical
    // helper for why this is duplicated rather than referencing Core. v0.55.4:
    // rewritten to a single left-to-right scan — see Core/TsvEscaping.cs's
    // remarks for why the old sequential-Replace version corrupted a literal
    // backslash immediately followed by a literal 'n'/'t' (e.g. a Windows path).
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

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_grid.Columns[e.ColumnIndex].Name != "未翻訳文字数" || _maxUntranslatedChars <= 0) return;
        if (e.Value is not long chars) return;

        // Continuous intensity, not a threshold — every row gets a proportional
        // tint, so nothing is silently classified as "fine" vs "needs attention".
        var t = Math.Clamp((double)chars / _maxUntranslatedChars, 0, 1);
        var r = (int)(255 - t * 30);
        var g = (int)(255 - t * 160);
        var b = (int)(255 - t * 170);
        e.CellStyle!.BackColor = Color.FromArgb(r, g, b);
    }

    /// <summary>v0.52.1a: ⑤ローカルLLM・⑥生成AI翻訳（クラウド）は独立したチェーン
    /// ステップ（CLI側も--llm-local/--llm-cloudの2つを独立に受け取る）——両方
    /// 同時にONで構わない。⑤で解決できなかったものだけが⑥に回る。</summary>
    private List<string> BuildOptionFlags()
    {
        var flags = new List<string>();
        if (!_chkMeaning.Checked) flags.Add("--no-meaning");
        if (!_chkTranslit.Checked) flags.Add("--no-translit");
        if (!_chkNameFallback.Checked) flags.Add("--no-namefallback");

        if (_chkLlm.Checked)
        {
            flags.Add("--llm-local");
            flags.Add($"--llm-local-model={LlmModel}");
            if (!string.IsNullOrWhiteSpace(LlmEndpoint))
                flags.Add($"--llm-local-endpoint={LlmEndpoint}");
        }

        if (_chkCloudAi.Checked)
        {
            // v0.52.1a: 「生成AI（クラウド）連携設定」ウィンドウで選ばれている方式を
            // そのまま使う。Claude Code CLIならサブプロセス起動、OpenAI互換APIなら
            // 専用のクラウドAIエンドポイント（CloudAiEndpoint、「ローカルLLM
            // エンドポイント」とは別物）にAPIキー付きでHTTP——どちらになるかは
            // 設定ウィンドウのタブ選択（UseClaudeCodeCli）次第で、ここではその
            // 結果に従うだけ。
            flags.Add("--llm-cloud");
            if (UseClaudeCodeCli)
            {
                flags.Add("--llm-cloud-provider=claudecode");
                if (!string.IsNullOrWhiteSpace(ClaudeCodeExePath))
                    flags.Add($"--claude-code-exe={ClaudeCodeExePath}");
                // モデル名はclaude側の既定に任せられるため省略可（ローカルLLMと違い必須ではない）。
                if (!string.IsNullOrWhiteSpace(ClaudeCodeModel))
                    flags.Add($"--llm-cloud-model={ClaudeCodeModel}");
            }
            else
            {
                flags.Add("--llm-cloud-provider=http");
                flags.Add($"--llm-cloud-model={LlmModel}");
                if (!string.IsNullOrWhiteSpace(CloudAiEndpoint))
                    flags.Add($"--llm-cloud-endpoint={CloudAiEndpoint}");
            }
        }

        // 既定値と異なるときだけ渡す——不要なフラグでコマンドラインを汚さない。
        var batchCharLimit = (int)_numBatchCharLimit.Value;
        if (batchCharLimit != DefaultLlmBatchCharLimit)
            flags.Add($"--llm-batch-char-limit={batchCharLimit}");

        return flags;
    }

    /// <summary>v0.54.2（既知の課題22.）: SettingsFormがローカルLLMのエンドポイント・
    /// モデル名を実際に変更した場合にだけ呼ぶ。古い接続先のままチェックが
    /// 入りっぱなしになる事故を防ぐ——ChkLlm_CheckedChangedのその場での
    /// 疎通確認とは独立に、単純にオフへ戻すだけ。</summary>
    internal void ResetLocalLlmCheckbox() => _chkLlm.Checked = false;

    private bool _llmCheckInProgress;

    /// <summary>Pre-flight check when the user turns on "ローカルLLM翻訳": probe
    /// the configured endpoint/model with a real minimal request (LlmHealthCheck)
    /// before allowing the checkbox to stay checked. Catches a dead server or a
    /// wrong/unpulled model name immediately instead of after minutes into
    /// `translation --all --llm`. Reverts the checkbox and shows why on failure.</summary>
    private async void ChkLlm_CheckedChanged(object? sender, EventArgs e)
    {
        if (_llmCheckInProgress || !_chkLlm.Checked) return;

        _llmCheckInProgress = true;
        _chkLlm.Enabled = false;
        _chkLlm.Text = $"{LlmCheckboxLabel}（確認中...）";
        _btnTranslate.Enabled = false;
        _btnReloadMo2.Enabled = false;
        try
        {
            var result = await LlmHealthCheck.CheckAsync(LlmEndpoint, LlmModel);
            if (!result.Ok)
            {
                MessageBox.Show(this, result.Error, "ローカルLLMに接続できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _chkLlm.Checked = false;
            }
        }
        finally
        {
            _chkLlm.Text = LlmCheckboxLabel;
            _chkLlm.Enabled = true;
            _btnTranslate.Enabled = true;
            _btnReloadMo2.Enabled = true;
            _llmCheckInProgress = false;
        }
    }

    // v0.54.0: 「生成AI翻訳（クラウド）」にはローカルLLMのようなHTTPプリフライト
    // チェックを設けていない——Claude Code CLI選択時はサブプロセス起動なので
    // "軽い接続テスト"に相当する手段が無く、OpenAI互換API選択時もクラウドAPIへの
    // 毎回の疎通確認はレイテンシ・コストの点で気軽に行うものではないと判断した。
    // 失敗すれば実行時のエラーとしてログ・ダイアログに出る。代わりに、チェックを
    // 入れた瞬間にBeta機能・課金・規模に関する注意喚起を一度出す（Nexus公開後、
    // 初見のユーザーがいきなり大規模実行して想定外のコストを被らないように）。
    private void ChkCloudAi_CheckedChanged(object? sender, EventArgs e)
    {
        if (!_chkCloudAi.Checked) return;
        MessageBox.Show(this,
            "本機能はベータ版です。\n" +
            "また、生成AI翻訳にはトークンを消費します。翻訳文字数や対象が増えるほど大規模になります。\n" +
            "最初は小規模なプラグインを対象に動作を確認し、負荷をチェックすることを推奨します。",
            "Beta機能: 生成AI翻訳（クラウド）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>v0.52.1a: 読み取り専用——Translation/out_temp配下の
    /// translations.tsvを直接再スキャンするだけで、CLIは一切呼ばない。既存の
    /// 翻訳結果（⑤⑥の結果含む）を一切変更しないため、確認ダイアログも不要。
    /// 新規プラグインの取り込み・コーパス更新の反映が必要な場合は
    /// 「MO2再読込＆初期化」（BtnReloadMo2_Click）を使うこと。</summary>
    private void BtnRescan_Click(object? sender, EventArgs e) => LoadData();

    /// <summary>「MO2再読込＆初期化」— pickuptarget＋translation --allを実行し、
    /// 新規プラグインの取り込みやコーパス更新（xTranslatorインポート等）を
    /// 反映する。これは対象プラグイン全ての translations.tsv を①バニラコーパス
    /// のみの状態へ書き戻す破壊的操作（ModifiedByUser行を含め全て）——⑤⑥の生成AI・
    /// ローカルLLM翻訳結果もここで消えるため、「翻訳状況を初期化」等と同様に
    /// 実行前に確認する。</summary>
    private async void BtnReloadMo2_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Mo2Dir) || !Directory.Exists(Mo2Dir))
        {
            MessageBox.Show(this, "MO2インスタンスフォルダを「設定」で正しく指定してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            "MO2を再読込し、翻訳状況を初期化します。\n" +
            "全プラグインの翻訳結果（手動での編集・生成AI/ローカルLLMでの翻訳結果を含む）を\n" +
            "すべて消去し、初期状態に戻します。元に戻せません。よろしいですか？\n\n" +
            "（既存の翻訳結果を消さずに現在の状況を見るだけなら「再スキャン（読み取りのみ）」を使ってください）",
            "MO2再読込＆初期化", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        // v0.55.0: バックアップ対象は「これから破壊される全プラグイン」——
        // pickuptargetの再実行前、現在のTranslation/out_temp配下に実在する
        // プラグインフォルダをそのまま列挙する（画面の選択状態とは無関係）。
        var existingOutTempDir = Path.Combine(ProductRoot, "Translation", "out_temp");
        var allPluginFolderNames = Directory.Exists(existingOutTempDir)
            ? Directory.GetDirectories(existingOutTempDir).Select(Path.GetFileName).OfType<string>()
            : Enumerable.Empty<string>();
        TranslationBackup.Backup(ProductRoot, allPluginFolderNames);

        // Remember the current selection before the table gets rebuilt.
        _deselectedPlugins.Clear();
        foreach (DataRow row in _table.Rows)
            if (row["選択"] is false)
                _deselectedPlugins.Add((string)row["プラグイン"]);

        SetBusy(true);
        try
        {
            if (!await RunCliAsync(BuildPickupTargetArgs(Mo2Dir))) return;
            // Always ①バニラコーパスのみ, regardless of this window's current checkbox
            // state — this is a baseline refresh (e.g. after collecting more
            // xTranslator files), not a preview of what "翻訳実行" would currently
            // do with the checked options.
            // v0.55.2: --discard-user-edits was missing here, so this button
            // silently PRESERVED every already-resolved row instead of the full
            // reset its own confirmation dialog/comment promises ("初期状態に
            // 戻します。元に戻せません。") — see DESIGN_NOTES.md's Integration
            // scenario ⑪ entry for how this was found and confirmed.
            if (!await RunCliAsync(new[] { "translation", "PickUpTarget/out_temp", "Translation/out_temp", "--all", "--no-meaning", "--no-translit", "--no-namefallback", "--discard-user-edits" })) return;
            LoadData();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnTranslate_Click(object? sender, EventArgs e)
    {
        if (_chkLlm.Checked && string.IsNullOrWhiteSpace(LlmModel))
        {
            MessageBox.Show(this, "ローカルLLM翻訳を有効にする場合は、「設定」でモデル名を指定してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Claude Code CLIはモデル名省略可（claude自身の既定モデルに任せられる）ため、
        // このチェックは生成AI（クラウド）がOpenAI互換API側を使う場合のみ。
        if (_chkCloudAi.Checked && !UseClaudeCodeCli && string.IsNullOrWhiteSpace(LlmModel))
        {
            MessageBox.Show(this, "生成AI翻訳（クラウド）をOpenAI互換APIで使う場合は、「設定」でモデル名を指定してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedPlugins = GetSelectedPlugins();
        if (selectedPlugins.Count == 0)
        {
            MessageBox.Show(this, "翻訳対象のプラグインを少なくとも1つ選択してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        // v0.50.1a: one process launch for the whole selection via --plugins-file,
        // not one invocation per plugin — see PromptGenerator.RunMany's remarks.
        // Looping single-plugin invocations was repeating BuildContext's ~10s
        // corpus/dictionary setup once per selected plugin (up to 175 times).
        var pluginsFilePath = Path.Combine(Path.GetTempPath(), $"sjpts_plugins_{Guid.NewGuid():N}.txt");
        // v0.53.0a: 既知の課題15.——このパス自体は作らず、パスだけ生成してCLIに渡す。
        // ユーザーがキャンセルを要求したときだけ実際にファイルを作成し、CLI側は
        // プラグインの区切りごとにこのパスの存在を確認する。
        _activeCancelFlagPath = Path.Combine(Path.GetTempPath(), $"sjpts_cancel_{Guid.NewGuid():N}.flag");
        _cancelRequestedForCurrentRun = false;
        _logWindow.SetCancelEnabled(true);
        try
        {
            await File.WriteAllLinesAsync(pluginsFilePath, selectedPlugins);
            var args = new List<string> { "translation", "PickUpTarget/out_temp", "Translation/out_temp", $"--plugins-file={pluginsFilePath}", $"--cancel-flag-path={_activeCancelFlagPath}" };
            args.AddRange(BuildOptionFlags());
            if (!await RunCliAsync(args)) return;

            _translationExecuted = true;
            RefreshRowsFromTranslations(selectedPlugins);

            if (_cancelRequestedForCurrentRun)
            {
                MessageBox.Show(this,
                    "ユーザーの要求により、処理を中断しました。\n" +
                    "中断までに完了したプラグインの翻訳結果は保存されています。\n" +
                    "残りのプラグインは、改めて「翻訳実行」を行うと続きから処理されます。",
                    "処理を中断しました", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // v0.53.0: 「生成AI翻訳」「ローカルLLM翻訳」を有効にしたのに1件も
            // 解決できなかった場合、CLI自体は（失敗した候補をそのまま未解決に
            // 残すだけで）正常終了するため、ここで検知して警告しないと
            // 「実行はできたので設定は合っているはず」という誤解を招く。
            // 実際にはAPIキー・パス・ログイン状態等の設定ミスの可能性が高い
            // ——詳しい理由は既にtranslation.log/実行ログに出ているので、
            // ここではその存在に気づかせることに専念する。
            var stillUntranslated = selectedPlugins.Sum(p => _rows.FirstOrDefault(r => r.Plugin.Equals(p, StringComparison.OrdinalIgnoreCase))?.Untranslated ?? 0);
            var warnings = new List<string>();
            if (_chkCloudAi.Checked && stillUntranslated > 0 && CountResolvedByMethod(selectedPlugins, "TranslationCloudLlm") == 0)
                warnings.Add("生成AI翻訳（クラウド）を有効にしましたが、1件も翻訳できませんでした。");
            if (_chkLlm.Checked && stillUntranslated > 0 && CountResolvedByMethod(selectedPlugins, "TranslationLocalLlm") == 0)
                warnings.Add("ローカルLLM翻訳を有効にしましたが、1件も翻訳できませんでした。");

            if (warnings.Count > 0)
            {
                _logWindow.ShowAndActivate();
                MessageBox.Show(this,
                    string.Join("\n", warnings) + "\n\n" +
                    "設定（生成AIの接続情報・ログイン状態・ローカルLLMの起動状況等）に問題がある可能性があります。\n" +
                    "実行ログウィンドウに詳しい失敗理由が出力されていますので確認してください。",
                    "生成AI/ローカルLLM翻訳が失敗しています", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(this, "翻訳が完了しました。翻訳内容を確認してください（「詳細を確認」ボタン、またはtranslations.tsvを直接開く）。\n" +
                    "内容に問題なければ「DSDファイル生成」でDSDファイルを作成してください。",
                    "翻訳完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally
        {
            SetBusy(false);
            _logWindow.SetCancelEnabled(false);
            try { File.Delete(pluginsFilePath); } catch { /* best-effort cleanup */ }
            try { if (_activeCancelFlagPath != null) File.Delete(_activeCancelFlagPath); } catch { /* best-effort cleanup */ }
            _activeCancelFlagPath = null;
        }
    }

    private async void BtnGenerateDsd_Click(object? sender, EventArgs e)
    {
        if (!_translationExecuted)
        {
            var result = MessageBox.Show(this,
                "このウィンドウではまだ「翻訳実行」を行っていません。\n" +
                "翻訳されていない文字列は、原文（英語等）のままDSDファイルに出力されます。\n\n" +
                "このままDSDファイルを生成しますか？（翻訳してから生成する場合は「キャンセル」を押してください）",
                "翻訳が未実行です", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK) return;
        }

        SetBusy(true);
        try
        {
            if (!await RunCliAsync(new[] { "generatedsdfile" })) return;

            var outDir = Path.Combine(ProductRoot, "out");
            MessageBox.Show(this, $"DSDファイルの生成が完了しました。出力先フォルダを確認してください:\n{outDir}",
                "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Disables every action button for the duration of a run — CLI runs
    /// share the same on-disk out_temp folders, so two overlapping runs would
    /// corrupt each other's output.</summary>
    private void SetBusy(bool busy)
    {
        _btnResetSelected.Enabled = !busy;
        _btnReloadMo2.Enabled = !busy;
        _btnTranslate.Enabled = !busy;
        _btnGenerateDsd.Enabled = !busy;
        _btnSelectAll.Enabled = !busy;
        _btnSelectNone.Enabled = !busy;
        _grid.Enabled = !busy;
        _chkMeaning.Enabled = !busy;
        _chkTranslit.Enabled = !busy;
        _chkNameFallback.Enabled = !busy;
        _chkLlm.Enabled = !busy;
        _chkCloudAi.Enabled = !busy;
        _numBatchCharLimit.Enabled = !busy;
    }

    private void AppendLog(string line) => _logWindow.AppendLine(line);

    private void SetStatus(string text) => _logWindow.SetStatus(text);

    /// <summary>Validates settings and runs the CLI, returning whether it exited 0.
    /// Every action in this GUI funnels through here — see DESIGN_NOTES.md's GUI
    /// architecture note: the GUI's only responsibilities are argument-building,
    /// log relay, and this kind of pre-flight error checking.</summary>
    internal async Task<bool> RunCliAsync(IReadOnlyList<string> arguments)
    {
        if (_productRoot == null)
        {
            MessageBox.Show(this, "実行フォルダを特定できませんでした。GUIの配置場所を確認してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        // v0.54.0: CLI実行ファイルのパスはユーザー設定にせず、GUI・CLIが常に同じ
        // 製品フォルダの兄弟として配置される前提で毎回自動検出する（既知の課題
        // 参照——手動指定できる設定項目は不要と判断し廃止した）。
        var cliExePath = CliLocator.ResolveAbsolute(_productRoot, CliLocator.TryAutoDetect() ?? "");
        if (!CliLocator.Validate(cliExePath, out var cliError))
        {
            MessageBox.Show(this, cliError, "CLI実行ファイルが見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        var argsDisplay = string.Join(' ', arguments);
        SetBusy(true);
        SetStatus($"実行中: {argsDisplay}");
        AppendLog($"> {Path.GetFileName(cliExePath)} {argsDisplay}");
        _currentRunCts = new CancellationTokenSource();
        // v0.54.2 (既知の課題21.): pickuptargetが不正なプラグイン/レコードを
        // スキップした場合、機械可読な専用プレフィックス("##SJPTS_ISSUES##")の
        // 1行をstdoutへ出す。LogWindowの大量の情報に埋もれさせないよう、この行を
        // 検知したら実行成功時でも明示的なMessageBoxで知らせる（レアケースのため）。
        const string IssuesMarkerPrefix = "##SJPTS_ISSUES##";
        const string IssuesPluginsMarkerPrefix = "##SJPTS_ISSUES_PLUGINS##";
        // v0.57.1: pickuptarget prints "[error] ..." (readable, not a stack
        // trace) for a recoverable MO2 configuration problem (see
        // Mo2InstanceConfigurationException) — captured here so the failure
        // dialog below can show the ACTUAL cause instead of just a bare exit
        // code, which is what a real user reported being unable to make
        // sense of ("終了コード-532462766が表示されて..."). Keeps the last
        // one seen, in case more than one line happens to match.
        const string ErrorMarkerPrefix = "[error] ";
        string? issuesLine = null;
        string? issuesPluginsLine = null;
        string? lastErrorLine = null;
        void OnOutputLine(string line)
        {
            AppendLog(line);
            // より長い方のプレフィックスを先にチェックする——
            // "##SJPTS_ISSUES_PLUGINS##"は"##SJPTS_ISSUES##"では始まらないため
            // 実際は衝突しないが、念のため意図を明確にする順序にしてある。
            if (line.StartsWith(IssuesPluginsMarkerPrefix, StringComparison.Ordinal))
                issuesPluginsLine = line[IssuesPluginsMarkerPrefix.Length..].Trim();
            else if (line.StartsWith(IssuesMarkerPrefix, StringComparison.Ordinal))
                issuesLine = line;
            else if (line.StartsWith(ErrorMarkerPrefix, StringComparison.Ordinal))
                lastErrorLine = line[ErrorMarkerPrefix.Length..];
        }
        try
        {
            var result = await CliRunner.RunAsync(cliExePath, arguments, _productRoot, OnOutputLine, _currentRunCts.Token,
                LlmApiKey.Length > 0 ? LlmApiKey : null, CloudAiApiKey.Length > 0 ? CloudAiApiKey : null);
            if (!result.Succeeded)
            {
                AppendLog($"[終了コード {result.ExitCode}]");
                var message = lastErrorLine != null
                    ? $"処理が失敗しました:\n{lastErrorLine}"
                    : $"処理が失敗しました（終了コード {result.ExitCode}）。ログを確認してください。";
                MessageBox.Show(this, message, "実行エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else if (issuesLine != null)
            {
                var message = "一部のプラグイン、またはレコードを正常に処理できなかったためスキップしました。\n" +
                    "処理自体は完了していますが、詳細はログを確認してください。\n\n" + FormatIssuesSummary(issuesLine);
                if (!string.IsNullOrWhiteSpace(issuesPluginsLine))
                    message += "\n\n対象プラグイン:\n" + string.Join('\n', issuesPluginsLine.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => $"・{p}"));
                MessageBox.Show(this, message, "一部のデータをスキップしました", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            // Window is closing (MainForm_FormClosing cancelled us) — the child
            // process has already been killed by CliRunner; no dialog, the form
            // itself is on its way out.
            return false;
        }
        catch (Exception ex)
        {
            AppendLog($"[例外] {ex.Message}");
            MessageBox.Show(this, $"CLIの起動に失敗しました:\n{ex.Message}", "実行エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _currentRunCts?.Dispose();
            _currentRunCts = null;
            SetBusy(false);
            SetStatus("準備完了");
        }
    }

    /// <summary>"##SJPTS_ISSUES## plugins=0 fields=1 fail_open=0 context_only=0"
    /// という機械可読な行を、MessageBoxにそのまま出すのではなく、0件の項目を除いた
    /// 日本語の箇条書きに変換する。</summary>
    private static string FormatIssuesSummary(string issuesLine)
    {
        var labels = new Dictionary<string, string>
        {
            ["plugins"] = "スキップされたプラグイン",
            ["fields"] = "スキップされたレコード/フィールド",
            ["fail_open"] = "除外判定に失敗し、安全側に倒して含めた候補",
            ["context_only"] = "文脈情報のみ抽出できなかった候補（翻訳への影響なし）",
        };

        var lines = new List<string>();
        foreach (var token in issuesLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0) continue;
            var key = token[..eq];
            if (!labels.TryGetValue(key, out var label)) continue;
            if (!int.TryParse(token[(eq + 1)..], out var count) || count <= 0) continue;
            lines.Add($"・{label}: {count}件");
        }

        return lines.Count > 0 ? string.Join('\n', lines) : "";
    }
}
