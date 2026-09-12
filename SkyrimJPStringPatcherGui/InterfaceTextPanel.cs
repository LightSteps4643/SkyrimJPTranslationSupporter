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
/// SettingsForm・TranslationDetailFormという、純粋に
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
public sealed class InterfaceTextPanel : Form
{
    // --- CLI実行基盤（旧ベース画面から） ---
    // 実行ログウィンドウはMainForm側と共有する（1つのアプリに1つだけ表示する
    // ため）——コンストラクタで受け取る。自前で new しない。
    private readonly LogWindow _logWindow;

    // AppSettingsもMainForm側のインスタンスをコンストラクタ経由で共有する
    // （LogWindowと同じ理由）。MainForm自身は_settingsフィールドをMainForm_Load
    // （コンストラクタより後）でAppSettings.Load()により丸ごと差し替えるため、
    // オブジェクト参照をコンストラクタ時点でそのまま受け取ると、差し替え後の
    // 最新インスタンスを見失う（既知の課題：AppSettings staleness）。そのため
    // 値そのものではなく、呼ぶたびにMainFormの「今の」_settingsを返すデリゲート
    // (Func&lt;AppSettings&gt;) を受け取る。
    private readonly Func<AppSettings> _getSettings;
    private string? _productRoot;

    internal AppSettings Settings => _getSettings();
    internal string ProductRoot => _productRoot ?? throw new InvalidOperationException("Product root not resolved.");
    internal string Mo2Dir => _getSettings().Mo2InstanceDir;
    internal string LlmEndpoint => _getSettings().LlmEndpoint;
    internal string LlmModel => _getSettings().LlmModel;
    internal string LlmApiKey => _getSettings().LlmApiKey;
    internal bool UseClaudeCodeCli => _getSettings().UseClaudeCodeCli;
    internal string ClaudeCodeExePath => _getSettings().ClaudeCodeExePath;
    internal string ClaudeCodeModel => _getSettings().ClaudeCodeModel;
    internal string CloudAiEndpoint => _getSettings().CloudAiEndpoint;
    internal string CloudAiApiKey => _getSettings().CloudAiApiKey;

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

    // v0.52.1a: ⑤ローカルLLM・⑥生成AI翻訳は独立したチェーン（CLI側もllm-local/
    // llm-cloudの2つを独立に受け取れる）なので、両方同時にONで構わない——⑤で
    // 解決できなかったものだけが⑥に回る。
    // v0.58.1: 従来は同じ行に並べていたが、それぞれ独自の文字数上限（右隣）を
    // 持たせるようになったため、行を分けた（ローカルLLMが上、生成AIが下）。
    private const string LlmCheckboxLabel = "Beta機能: ローカルLLM翻訳（品質中～低）";
    private readonly CheckBox _chkLlm = new() { Text = LlmCheckboxLabel, Checked = false, AutoSize = true };

    private const string CloudAiCheckboxLabel = "Beta機能: 生成AI翻訳（クラウド・品質中～低）";
    private readonly CheckBox _chkCloudAi = new() { Text = CloudAiCheckboxLabel, Checked = false, AutoSize = true };

    // v0.52.1a: 1回のLLM呼び出しにまとめる候補の合計文字数の上限（PromptGenerator.
    // ApplyLlmStep参照、CLI側は--llm-local-batch-char-limit=/--llm-cloud-batch-
    // char-limit=）。サーバー・モデルによって妥当な値が変わりうる（無料枠や
    // 思考系ローカルLLMモデルでは既定値より小さくしたい等）ため、GUIからも
    // 変更できるようにしてある。GUIはCLIをサブプロセス起動するだけの薄い層で
    // PromptGeneratorを直接参照できない（プロジェクト参照が無い）ため、既定値は
    // CLI側と別々に保持している——両方変えるときはPromptGenerator.
    // DefaultLocalLlmBatchCharLimit/DefaultLlmBatchCharLimitとここを揃える。
    // v0.58.1: 従来は⑤⑥共通の1項目・共通の既定値（12000）だったが、独立した
    // 2項目に分割した上で、⑤（ローカルLLM）側の既定値も3000に変更した——実機
    // 検証（`Cloaks_SMP_Patch.esp`、gemma3:12b・gemma4:26b）で、12000のままだと
    // 大きすぎて失敗率が大幅に上がり、逆に小さくしすぎても解決件数は変わらず
    // 実行時間だけ悪化することを確認済み（詳細はDESIGN_NOTES.md既知の課題27.）。
    // ⑥（生成AI・クラウド）側は同じ実機検証をしていないため12000のまま変更していない。
    // v0.59.0: GUI既定値のみ6000へ変更（ユーザー指示）。
    private const int DefaultLlmLocalBatchCharLimit = 6_000;
    private const int DefaultLlmCloudBatchCharLimit = 12_000;
    // v0.58.1: 実測（同フォント・同DPIでのPreferredSize）でCheckBox=22px、
    // Label=21px、NumericUpDown=23pxとほぼ揃っており、単独ではここまでの
    // ズレは説明できなかった——Label/NumericUpDownだけにAnchor=Leftを設定し
    // CheckBoxは既定（Top|Left）のままにしていた不統一が主因と判断し、
    // Anchorは全コントロール既定のまま（明示指定しない）に揃えた。その上で
    // 実測差分の半分程度（1px前後）だけ上マージンを微調整してある。
    private readonly Label _lblLlmBatchCharLimit = new() { Text = "1回あたりの文字数上限", AutoSize = true, Margin = new Padding(8, 4, 4, 3) };
    private readonly NumericUpDown _numLlmBatchCharLimit = new()
    {
        Minimum = 100,
        Maximum = 1_000_000,
        Increment = 1000,
        Value = DefaultLlmLocalBatchCharLimit,
        Width = 90,
        Margin = new Padding(3, 1, 3, 3),
    };
    private readonly Label _lblCloudAiBatchCharLimit = new() { Text = "1回あたりの文字数上限", AutoSize = true, Margin = new Padding(8, 4, 4, 3) };
    private readonly NumericUpDown _numCloudAiBatchCharLimit = new()
    {
        Minimum = 100,
        Maximum = 1_000_000,
        Increment = 1000,
        Value = DefaultLlmCloudBatchCharLimit,
        Width = 90,
        Margin = new Padding(3, 1, 3, 3),
    };

    // Width指定は付けない — v0.52.1a: BuildLayoutの最後でButtonLayout.UnifyWidthsが
    // 実際の文言の幅を測って一律に揃えるため、ここで決め打ちすると測定前に上書きされる。
    private readonly Button _btnReloadMo2 = new() { Text = "MO2対象Interfaceフォルダ読込み＆初期化", AutoSize = true };
    private readonly Button _btnOpenImportFolder = new() { Text = "importフォルダを開く", AutoSize = true };
    private readonly Button _btnTranslate = new() { Text = "翻訳実行", AutoSize = true };
    private readonly Button _btnGenerateDsd = new() { Text = "翻訳ファイル出力", AutoSize = true };
    private readonly Button _btnOpenOutFolder = new() { Text = "翻訳ファイル出力フォルダを開く", AutoSize = true };

    /// <summary>True once "翻訳実行" has completed successfully at least once in
    /// this window — drives the warning if "翻訳ファイル出力" is pressed first
    /// (untranslated candidates would otherwise silently ship in English).</summary>
    private bool _translationExecuted;
    private readonly Label _lblSummary = new() { AutoSize = true };

    /// <summary>Mods the user has unchecked — remembered across MOD一覧の再構築
    /// (「MO2対象Interfaceフォルダ読込み＆初期化」等) so an intentional exclusion
    /// isn't silently lost.</summary>
    private readonly HashSet<string> _deselectedMods = new(StringComparer.OrdinalIgnoreCase);

    // v0.58.6: 既知の課題（v0.58.1で一度対処した「bottomパネルのAutoSize計算が
    // 信頼できず、高さが実際の中身より大きい値のまま固定される」現象）が、
    // 「詳細を確認」等の別ウィンドウを開閉した後や、CLI実行（翻訳実行等）の
    // 完了後にも再発する、と実機で報告された。BuildLayout内のbottom.Layout
    // イベントだけでは、これらの操作がbottomパネル自身のLayoutを必ずしも
    // 誘発しないため補正が働かないタイミングがある——正確な再発トリガーを
    // 特定できなかったため、代わりに「主要な操作の節目ごとに毎回補正し直す」
    // という保険的な対応にした（RecalculateBottomHeight参照）。3つとも
    // BuildLayoutで初期化される。
    private TableLayoutPanel? _bottomPanel;
    private TableLayoutPanel? _bottomOptionsPanel;
    private TableLayoutPanel? _bottomActionsPanel;

    public InterfaceTextPanel(LogWindow logWindow, Func<AppSettings> getSettings)
    {
        _logWindow = logWindow;
        _getSettings = getSettings;
        Text = Services.AppVersion.FormatWindowTitle("Skyrim JP Translation Supporter", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
        Width = 1150;
        Height = 850;
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildGridColumns();
        InitTableColumns();

        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
        _logWindow.CancelRequested += LogWindow_CancelRequested;

        // v0.58.6: bottomパネル自身のLayoutイベントだけに頼らず、ウィンドウが
        // 再アクティブ化された時（別ウィンドウを閉じて戻ってきた時を含む）・
        // リサイズされた時にも高さを補正し直す。
        Activated += (_, _) => RecalculateBottomHeight();
        Resize += (_, _) => RecalculateBottomHeight();
    }

    /// <summary>bottomパネル（下部の①〜④/⑤/⑥チェックボックス列＋ボタン列）の
    /// 高さを、実際に配置されたoptions/actionsの下端座標から直接計算して
    /// 強制設定する——BuildLayout内のbottom.Layoutイベントハンドラと全く同じ
    /// 計算式。複数のトリガーから安全に何度呼んでも副作用が無いよう、
    /// 既に正しい値ならno-op（Layoutイベントの再入も自然に防がれる）。</summary>
    private void RecalculateBottomHeight()
    {
        if (_bottomPanel == null || _bottomOptionsPanel == null || _bottomActionsPanel == null) return;
        var desired = Math.Max(_bottomOptionsPanel.Bottom, _bottomActionsPanel.Bottom) + _bottomPanel.Padding.Bottom;
        if (_bottomPanel.Height != desired)
            _bottomPanel.Height = desired;
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
            "・すぐには止まりません。現在処理中のMODの完了を待ってから停止します。\n" +
            "・既に生成AI（クラウド）へ送信済みの呼び出し分の課金は取り消せません。\n" +
            "・それ以降の未処理MODは翻訳されないまま残りますが、\n" +
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
        // ログウィンドウの表示・位置決めはMainForm側が行う（共有インスタンス、
        // 1つのアプリに1つだけ表示するため）——ここでは何もしない。

        // AppSettingsはMainForm側と共有（_getSettings）——ここで自前にLoad()しない。
        _productRoot = CliLocator.TryGetProductRoot();
        _numLlmBatchCharLimit.Value = Math.Clamp(_getSettings().LlmLocalBatchCharLimit, (int)_numLlmBatchCharLimit.Minimum, (int)_numLlmBatchCharLimit.Maximum);
        _numCloudAiBatchCharLimit.Value = Math.Clamp(_getSettings().LlmCloudBatchCharLimit, (int)_numCloudAiBatchCharLimit.Minimum, (int)_numCloudAiBatchCharLimit.Maximum);

        // plugin_summary.txtがまだ無くても（一度もスキャンしていなくても）
        // LoadDataは空一覧として扱うので、常時ロードして問題ない。
        LoadData();
    }

    private void BuildLayout()
    {
        // 「設定」「ログ」はMainForm側の共有MenuStripに任せる——このタブ自身は
        // メニューを持たない（v0.52.1aで一度自前のMenuStripを持たせていたが、
        // TabControlに載せると「設定 ログ」が二重に表示されてしまうため削除した）。

        // 3 rows: 説明文＋すべて選択/解除 → 表 → 下部パネル.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(root);

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

        // v0.58.1: RowStyles/RowCountを明示してもbottomのAutoSize計算が
        // 内容（options=183px・actions=180px）よりかなり大きい値（374px）に
        // 固定されたまま変わらないことを実機で確認した——この環境の
        // TableLayoutPanel（列0がPercentスタイル）のAutoSize計算がどこかで
        // 信頼できないと判断し、AutoSizeには頼らず、実際に子要素を配置した後の
        // 座標から高さを直接計算して強制的に設定する方式に切り替えた。
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = false, Padding = new Padding(8), ColumnCount = 2, RowCount = 1 };
        _bottomPanel = bottom;
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // v0.58.1: ①〜④（自動解決手法）・⑤ローカルLLM（＋その文字数上限）・
        // ⑥生成AI翻訳（クラウド、＋その文字数上限）を3行に分けた。GroupBoxで
        // 囲む案も試したが、この環境ではAutoSizeのGroupBoxに非Dockの子を
        // 入れてもサイズが正しく縮まらない（タイトル欠落・大きな余白）という
        // 問題が実機で確認されたため、枠なしの単純な行区切りに落ち着いた。
        // ローカルLLM・生成AIの2行は横位置を互いに揃える必要はない（実測でも
        // 高さの差はわずかで、無理に揃えようとしたのがかえってズレの原因に
        // なっていた）。
        // actions（ボタン列）側が3行（MO2対象Interfaceフォルダ読込み＆初期化／
        // 翻訳実行／出力フォルダを開く＋翻訳ファイル出力）なのに対し、こちらは
        // 内容行が2行（ローカルLLM／生成AI）しかなく、bottomの共有行高が
        // actions側に引っ張られる分だけ左側の下に空白ができる（ESP側と同じ
        // 現象、RecalculateBottomHeight参照）。actionsの行数に合わせて
        // 空き行を1行だけ追加する。
        var options = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 3 };
        _bottomOptionsPanel = options;

        var llmRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 3, 0, 0) };
        llmRow.Controls.Add(_chkLlm);
        llmRow.Controls.Add(_lblLlmBatchCharLimit);
        llmRow.Controls.Add(_numLlmBatchCharLimit);
        options.Controls.Add(llmRow, 0, 0);
        _chkLlm.CheckedChanged += ChkLlm_CheckedChanged;

        var cloudAiRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 0) };
        cloudAiRow.Controls.Add(_chkCloudAi);
        cloudAiRow.Controls.Add(_lblCloudAiBatchCharLimit);
        cloudAiRow.Controls.Add(_numCloudAiBatchCharLimit);
        options.Controls.Add(cloudAiRow, 0, 1);
        _chkCloudAi.CheckedChanged += ChkCloudAi_CheckedChanged;

        // ボタン基準の高さに固定した空パネルで1行分だけ埋める（actions側の
        // 行数=3に合わせる）。
        var optionsSpacerHeight = _btnReloadMo2.PreferredSize.Height;
        options.Controls.Add(new Panel { AutoSize = false, Height = optionsSpacerHeight, Width = 1 }, 0, 2);

        // v0.53.0a: 変更を即座にAppSettingsへ反映・保存する——以前はGUI上でしか
        // 保持されず、次回起動時に既定値へ戻ってしまっていた不具合の修正。
        _numLlmBatchCharLimit.ValueChanged += (_, _) =>
        {
            var settings = _getSettings();
            settings.LlmLocalBatchCharLimit = (int)_numLlmBatchCharLimit.Value;
            settings.Save();
        };
        _numCloudAiBatchCharLimit.ValueChanged += (_, _) =>
        {
            var settings = _getSettings();
            settings.LlmCloudBatchCharLimit = (int)_numCloudAiBatchCharLimit.Value;
            settings.Save();
        };

        bottom.Controls.Add(options, 0, 0);

        // Stacked top-to-bottom: MO2対象Interfaceフォルダ読込み＆初期化 →
        // 翻訳実行 → 翻訳ファイル出力. 「左隣に開くボタン」を、入れ子の
        // FlowLayoutPanelではなく3行×2列のTableLayoutPanelで実現する——
        // FlowLayoutPanelを入れ子にすると、各行の幅がまちまちになり左端が
        // 揃わず見た目が崩れた（実機で確認済み、ESP側の同種レイアウトと同じ理由）。
        var actions = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 3 };
        _bottomActionsPanel = actions;
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 3; i++) actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _btnReloadMo2.Click += BtnReloadMo2_Click;
        _btnOpenImportFolder.Click += (_, _) => FolderOpener.OpenOrWarn(this, InterfaceTextImportDir);
        _btnTranslate.Click += BtnTranslate_Click;
        _btnGenerateDsd.Click += BtnGenerateDsd_Click;
        _btnOpenOutFolder.Click += (_, _) => FolderOpener.OpenOrWarn(this, InterfaceTextOutDir);
        _btnReloadMo2.Margin = new Padding(3, 3, 3, 3);
        _btnOpenImportFolder.Margin = new Padding(3, 3, 3, 3);
        _btnTranslate.Margin = new Padding(3, 3, 3, 3);
        _btnGenerateDsd.Margin = new Padding(3, 3, 3, 3);
        _btnOpenOutFolder.Margin = new Padding(3, 3, 3, 3);
        // 列0はAutoSizeで content 幅ぴったりになるはずだが、念のため右揃えに
        // ピン留めして列1（既存ボタン列）にぴったり隣接させる。
        _btnOpenImportFolder.Anchor = AnchorStyles.Right;
        _btnOpenOutFolder.Anchor = AnchorStyles.Right;
        actions.Controls.Add(_btnReloadMo2, 1, 0);
        actions.Controls.Add(_btnOpenImportFolder, 0, 1);
        actions.Controls.Add(_btnTranslate, 1, 1);
        actions.Controls.Add(_btnOpenOutFolder, 0, 2);
        actions.Controls.Add(_btnGenerateDsd, 1, 2);
        ButtonLayout.UnifyWidths(new[] { _btnReloadMo2, _btnTranslate, _btnGenerateDsd });
        bottom.Controls.Add(actions, 1, 0);

        // v0.58.1: AutoSizeに頼らず、実際に配置されたoptions/actionsの下端座標
        // から高さを直接計算して強制設定する（上記コメント参照）。Layoutイベントに
        // 掛けて、初回表示時・以降のレイアウト変更時どちらでも正しい値になるようにする。
        // v0.58.6: 計算式そのものはRecalculateBottomHeightへ切り出した——ここでは
        // それを呼ぶだけ（bottom.Height代入がこのLayoutイベント自身を再度誘発しても、
        // RecalculateBottomHeight内のno-opガードで再入は自然に止まる）。
        bottom.Layout += (_, _) => RecalculateBottomHeight();

        root.Controls.Add(bottom, 0, 2);
    }


    private void BuildGridColumns()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "選択", HeaderText = "選択", DataPropertyName = "選択", Width = 50 });
        // 2026-09-12: 「MOD名」は表示専用（VFSでこのファイルを実際に提供した
        // MODフォルダ名、mod_folder_name.txt由来）——内部の識別子（作業フォルダ名・
        // --mods-file=・最終出力ファイル名）は常にTarget（*_english.txtのファイル名
        // 由来）を使う。ファイル名≠MOD名のケース（実例: "aaa_english.txt"を
        // 同梱する「Oblivion Interaction Icons DSD 1.4.3 patch」）があるため、
        // 表示とロジックで別々の値を持つ必要がある。Targetは非表示列として
        // グリッドに保持する（DataGridViewのセルとして参照できるようにするため、
        // _tableだけでなく_grid.Columnsにも追加し、Visible=falseで隠す）。
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", DataPropertyName = "Target", Visible = false });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "MOD名", HeaderText = "MOD名", DataPropertyName = "MOD名", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
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
            // TODO(次のステップ): CLI呼び出し先をSJPTS_InterfaceText側に揃えるまでは
            // 誤動作（ESP用CLIを呼んでしまう）を防ぐため無効化しておく。
            ReadOnly = true,
        });
        _grid.CellContentClick += Grid_CellContentClick;
    }

    /// <summary>Opens the mod's interface_translations.tsv (SJPTS_InterfaceText's own
    /// per-mod output) in a read-only/editable viewer. Only meaningful after
    /// `detect` has actually written that mod's folder; otherwise says so
    /// instead of showing an empty grid with no explanation.</summary>
    private async void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var columnName = _grid.Columns[e.ColumnIndex].Name;
        if (columnName != DetailColumnName && columnName != ResetColumnName) return;

        var target = (string)_grid.Rows[e.RowIndex].Cells["Target"].Value!;
        var displayModName = (string)_grid.Rows[e.RowIndex].Cells["MOD名"].Value!;

        if (columnName == ResetColumnName)
        {
            await ResetMod(target);
            return;
        }

        var path = Path.Combine(InterfaceTextWorkDir, target, "interface_translations.tsv");
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"まだこのMODの翻訳結果がありません:\n{path}\n先に「MO2対象Interfaceフォルダ読込み＆初期化」を行ってください。",
                "ファイルが見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var detail = new InterfaceTextDetailForm(displayModName, path, onSaved: () => RefreshRowsFromTranslations(new[] { target }));
        // 擬似モーダル — 開いている間はこのウィンドウをロックする（ファイルを
        // 直接編集する操作なので、MO2再読込＆初期化等が走って同じファイルを
        // 上書きされると困る）。ウィンドウが1つに統合されたため、ロック先は
        // このウィンドウだけでよい（Services/PseudoModal.cs参照）。
        PseudoModal.Show(detail, this);
    }

    /// <summary>「翻訳状況を初期化」— TODO(次のステップ): 現在はグリッドの
    /// ReadOnly=trueにより実際には呼ばれない（ボタン配線はまだ着手していない）。
    /// ESP版のResetPluginを踏襲した形だけ残してあるが、中身はSJPTS_InterfaceText
    /// 側のCLI呼び出しに置き換える必要がある。</summary>
    private async Task ResetMod(string mod)
    {
        var plugin = mod;
        var confirm = MessageBox.Show(this,
            $"「{plugin}」の翻訳状況を初期化します。\n" +
            "このプラグインの翻訳結果（手動での編集を含む）をすべて消去し、初期状態に戻します。\n" +
            "元に戻せません。よろしいですか？",
            "翻訳状況を初期化", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        // TODO(次のステップ): このメソッド自体がESP版ResetPluginの未変換のまま
        // （グリッドの「初期化」列はReadOnly=trueで実際には呼ばれない）——
        // SJPTS_InterfaceText側のCLI呼び出しに置き換える際、バックアップ先も
        // InterfaceTextWorkDir/"interface_translations.tsv"に揃えること。
        TranslationBackup.Backup(Path.Combine(ProductRoot, "Translation", "out_temp"), new[] { PluginFolderName.From(plugin) }, "translations.tsv");

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

    /// <param name="ModName">表示専用——このファイルを実際に提供したMODフォルダ名
    /// （mod_folder_name.txt由来）。</param>
    /// <param name="Target">内部識別子——*_english.txtのファイル名由来。作業フォルダ名・
    /// --mods-file=・最終出力ファイル名（&lt;Target&gt;_japanese.txt）はすべてこちらを使う。
    /// ModNameとTargetは一致しないことがある（例: "aaa_english.txt"を同梱する
    /// 「Oblivion Interaction Icons DSD 1.4.3 patch」）。</param>
    private sealed record Row(string ModName, string Target, int Total, int Untranslated, double Ratio, long UntranslatedChars, double CharsRatio);

    /// <summary>SJPTS_InterfaceText（別CLI）が書き出す中間ファイルの置き場所。
    /// ESP側の`Translation/out_temp`と対称になるよう`InterfaceText/Translation/
    /// out_temp/&lt;MOD名&gt;/interface_translations.tsv`（Key/English/Japanese/
    /// Resolvedの4列）＋`interfacetext.log`＋`prompt_batch*.txt`をまとめる
    /// （design/interface_translations.md、2026-09-12のフォルダ構成整理参照）。
    /// PickUpTarget相当のフォルダは無い——この処理の候補収集（VFSスキャン）は
    /// 軽量でMOD単位で完結するため、独立した中間成果物にする実益が無いと判断した。</summary>
    private string InterfaceTextWorkDir => Path.Combine(ProductRoot, "InterfaceText", "Translation", "out_temp");

    /// <summary>ユーザーが用意した*_japanese.txt（このツール自身が読み書きする
    /// $Key&lt;TAB&gt;Text形式そのもの——ESP側のTranslation/import、xTranslator
    /// XML形式とは別物）の置き場所。`detect`実行時、MOD自身のロードオーダーが
    /// 持つ_japanese.txtより優先される（Program.cs/DetectOneMod参照）。</summary>
    private string InterfaceTextImportDir => Path.Combine(ProductRoot, "InterfaceText", "Translation", "import");

    /// <summary>最終的にマージされた*_japanese.txtの出力先。ESP側の`out/`とは
    /// 別系統（旧設計では共用していたが、生成物の場所が分かりにくいとの指摘で
    /// 分離した）——`InterfaceText/GenerateTranslationFile/out/`。</summary>
    private string InterfaceTextOutDir => Path.Combine(ProductRoot, "InterfaceText", "GenerateTranslationFile", "out");

    /// <summary>「再スキャン（読み取りのみ）」ボタンにも、起動直後の初期表示にも
    /// 使う共通の読み込み処理。<see cref="InterfaceTextWorkDir"/>配下の各MODフォルダの
    /// interface_translations.tsvを直接読んで組み立てる——ESP側のScanTranslationsOutTemp
    /// と同じ考え方（スナップショットではなく実ファイルを毎回読む）。該当フォルダが
    /// 無ければ（まだ一度もdetectを実行していない）空一覧になるだけなので、
    /// 常時呼んで問題ない。</summary>
    private void InitTableColumns()
    {
        _table.Columns.Add("選択", typeof(bool));
        _table.Columns.Add("Target", typeof(string));
        _table.Columns.Add("MOD名", typeof(string));
        _table.Columns.Add("全体件数", typeof(int));
        _table.Columns.Add("未翻訳件数", typeof(int));
        _table.Columns.Add("翻訳率(件数)", typeof(string));
        _table.Columns.Add("未翻訳文字数", typeof(long));
        _table.Columns.Add("翻訳率(文字数)", typeof(string));
    }

    private void LoadData()
    {
        _rows = ScanInterfaceTranslations();

        _table.Rows.Clear();

        foreach (var r in _rows)
        {
            var selected = !_deselectedMods.Contains(r.Target);
            _table.Rows.Add(selected, r.Target, r.ModName, r.Total, r.Untranslated, $"{r.Ratio:F1}%", r.UntranslatedChars, $"{r.CharsRatio:F1}%");
        }

        _grid.DataSource = _table;
        _maxUntranslatedChars = _rows.Count > 0 ? _rows.Max(r => r.UntranslatedChars) : 0;
        UpdateSummaryLabel();
    }

    private long _maxUntranslatedChars;

    private void UpdateSummaryLabel()
    {
        if (_rows.Count == 0)
        {
            _lblSummary.Text = "まだ翻訳データがありません。「MO2対象Interfaceフォルダ読込み＆初期化」を押すと、MODの一覧を読み込んで翻訳作業を開始できます。";
            return;
        }

        var selectedCount = _table.Rows.Cast<DataRow>().Count(r => r["選択"] is true);
        var totalUntranslated = _rows.Sum(r => r.Untranslated);
        var totalUntranslatedChars = _rows.Sum(r => r.UntranslatedChars);
        _lblSummary.Text = $"{_rows.Count}MOD中 {selectedCount}件選択中 ／ 全体の未翻訳 {totalUntranslated}件・{totalUntranslatedChars:N0}字";
    }

    private void SetAllSelected(bool selected)
    {
        foreach (DataRow row in _table.Rows)
            row["選択"] = selected;
        UpdateSummaryLabel();
    }

    /// <summary>戻り値は内部識別子（Target）のリスト——CLIの--mods-file=・
    /// RefreshRowsFromTranslations等、内部処理に渡すのはこちら（MOD名の表示名ではない）。</summary>
    private List<string> GetSelectedPlugins() =>
        _table.Rows.Cast<DataRow>().Where(r => r["選択"] is true).Select(r => (string)r["Target"]).ToList();

    /// <summary>Updates the grid's 未翻訳件数/文字数 for just the given mods by
    /// re-reading their own interface_translations.tsv.</summary>
    /// <param name="targets">内部識別子（Target）のリスト——表示名ではない。</param>
    private void RefreshRowsFromTranslations(IEnumerable<string> targets)
    {
        foreach (var target in targets)
        {
            var path = Path.Combine(InterfaceTextWorkDir, target, "interface_translations.tsv");
            var rows = ReadInterfaceTranslationsTsv(path);
            if (rows.Count == 0) continue;

            var (total, untranslatedCount, ratio, untranslatedChars, charsRatio) = ComputeStats(rows);

            var index = _rows.FindIndex(r => r.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            _rows[index] = _rows[index] with { Total = total, Untranslated = untranslatedCount, Ratio = ratio, UntranslatedChars = untranslatedChars, CharsRatio = charsRatio };

            var dataRow = _table.Rows.Cast<DataRow>().FirstOrDefault(r => (string)r["Target"] == target);
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

    /// <summary><see cref="InterfaceTextWorkDir"/>直下の各MODフォルダのinterface_translations.tsv
    /// を直接読んでグリッドの行を組み立てる。</summary>
    private List<Row> ScanInterfaceTranslations()
    {
        var rows = new List<Row>();
        if (!Directory.Exists(InterfaceTextWorkDir)) return rows;

        foreach (var modDir in Directory.GetDirectories(InterfaceTextWorkDir))
        {
            // 2026-09-12: 0件（英語ソースのパース失敗等でkeyが1つも取れなかった
            // MOD）も除外せず一覧に出す——除外すると、out_tempには実在するのに
            // 一覧には出ない、という不整合が生じる（実データ調査で発見）。
            var tsvPath = Path.Combine(modDir, "interface_translations.tsv");
            var tsvRows = ReadInterfaceTranslationsTsv(tsvPath);

            var target = Path.GetFileName(modDir);
            // mod_folder_name.txt（detect/Program.csが書く、実際にこのファイルを
            // 提供したMODフォルダ名）が無ければTargetをそのまま表示名として使う
            // ——古いdetect実行結果との後方互換、またはファイルが何らかの理由で
            // 欠けていた場合のフォールバック。
            var modFolderNamePath = Path.Combine(modDir, "mod_folder_name.txt");
            var displayModName = File.Exists(modFolderNamePath) ? File.ReadAllText(modFolderNamePath).Trim() : target;
            if (displayModName.Length == 0) displayModName = target;

            var (total, untranslatedCount, ratio, untranslatedChars, charsRatio) = ComputeStats(tsvRows);
            rows.Add(new Row(displayModName, target, total, untranslatedCount, ratio, untranslatedChars, charsRatio));
        }

        return rows.OrderByDescending(r => r.UntranslatedChars).ToList();
    }

    /// <summary>SJPTS_InterfaceText/InterfaceTranslationsTsv.csのRead/Writeと同じ4列
    /// 形式（Key/English/Japanese/Resolved）——InterfaceTextDetailForm.csの
    /// InterfaceTranslationRow/ReadTsvと同じ理由（GUIはCore/Translationへの参照を
    /// 持たない）で、ここでも小さく重複させている。</summary>
    private static List<(string Key, string English, string Japanese, bool Resolved)> ReadInterfaceTranslationsTsv(string path)
    {
        var rows = new List<(string, string, string, bool)>();
        if (!File.Exists(path)) return rows;
        foreach (var line in File.ReadAllLines(path, System.Text.Encoding.UTF8).Skip(1))
        {
            if (line.Length == 0) continue;
            var cols = line.Split('\t');
            if (cols.Length < 4) continue;
            rows.Add((cols[0], cols[1], cols[2], cols[3] == "1"));
        }
        return rows;
    }

    /// <summary>InterfaceText形式の値は改行・タブを含み得ない（1行1値のファイル
    /// 形式そのものの制約）ため、ESP側のComputeStatsと違いUnescapeは不要。</summary>
    private static (int Total, int Untranslated, double Ratio, long UntranslatedChars, double CharsRatio) ComputeStats(
        List<(string Key, string English, string Japanese, bool Resolved)> rows)
    {
        var untranslatedRows = rows.Where(r => !r.Resolved).ToList();
        var untranslatedCount = untranslatedRows.Count;
        var untranslatedChars = untranslatedRows.Sum(r => (long)r.English.Length);
        var totalChars = rows.Sum(r => (long)r.English.Length);
        var ratio = rows.Count == 0 ? 100.0 : 100.0 * (rows.Count - untranslatedCount) / rows.Count;
        var charsRatio = totalChars == 0 ? 100.0 : 100.0 * (totalChars - untranslatedChars) / totalChars;
        return (rows.Count, untranslatedCount, ratio, untranslatedChars, charsRatio);
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

    /// <summary>「MO2対象Interfaceフォルダ読込み＆初期化」— SJPTS_InterfaceTextの
    /// `detect`をMO2インスタンス全体（--mod=/--mods-file=省略）で実行し、
    /// interface/translations配下の全MODを再スキャンする。detectは対象MOD
    /// それぞれのinterface_translations.tsvを毎回新規に書き出す（既存分は破棄・
    /// 再生成）破壊的操作——手動編集・ローカルLLM/生成AI翻訳結果もここで消える
    /// ため、実行前に確認する（ESP側のBtnReloadMo2_Clickと同じ考え方）。
    /// 実行前にTranslationBackup（ESP側と共有、汎用化済み）で対象MOD全件を
    /// InterfaceText/Translation/bak/へバックアップする。</summary>
    private async void BtnReloadMo2_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Mo2Dir) || !Directory.Exists(Mo2Dir))
        {
            MessageBox.Show(this, "MO2インスタンスフォルダを「設定」で正しく指定してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            "MO2のInterface\\Translationsを対象MOD全体で再読込し、翻訳状況を初期化します。\n" +
            "全MODの翻訳結果（手動での編集・生成AI/ローカルLLMでの翻訳結果を含む）を\n" +
            "すべて消去し、初期状態に戻します。よろしいですか？\n" +
            "（実行前の状態はInterfaceText\\Translation\\bak\\に自動でバックアップされます）",
            "MO2対象Interfaceフォルダ読込み＆初期化", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        // v0.55.0のESP側と同じ考え方——バックアップ対象は「これから破壊される
        // 全MOD」で、現在InterfaceTextWorkDir配下に実在するMODフォルダをそのまま
        // 列挙する（画面の選択状態とは無関係）。
        var existingModFolderNames = Directory.Exists(InterfaceTextWorkDir)
            ? Directory.GetDirectories(InterfaceTextWorkDir).Select(Path.GetFileName).OfType<string>()
            : Enumerable.Empty<string>();
        TranslationBackup.Backup(InterfaceTextWorkDir, existingModFolderNames, "interface_translations.tsv");

        // Remember the current selection before the table gets rebuilt.
        _deselectedMods.Clear();
        foreach (DataRow row in _table.Rows)
            if (row["選択"] is false)
                _deselectedMods.Add((string)row["Target"]);

        SetBusy(true);
        try
        {
            var args = new List<string> { "detect", $"--mo2-instance={Mo2Dir}", $"--work={InterfaceTextWorkDir}", $"--import={InterfaceTextImportDir}" };
            // ESP側のBuildPickupTargetArgsと同じ——非標準MO2構成（ポータブル
            // インスタンス等）向けの上書き設定。SJPTS_InterfaceText側も
            // Mo2InstanceReader.Read（Core共通コード）自体は既に対応済みだったが、
            // このCLIの引数解析が受け取っていなかった（2026-09-12修正済み）。
            var settings = _getSettings();
            if (!string.IsNullOrWhiteSpace(settings.Mo2ModsDirOverride))
                args.Add($"--mods-dir={settings.Mo2ModsDirOverride}");
            if (!string.IsNullOrWhiteSpace(settings.Mo2ProfileDirOverride))
                args.Add($"--profile-dir={settings.Mo2ProfileDirOverride}");
            if (!string.IsNullOrWhiteSpace(settings.Mo2OverwriteDirOverride))
                args.Add($"--overwrite-dir={settings.Mo2OverwriteDirOverride}");
            if (!await RunCliAsync(args)) return;
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

        var selectedMods = GetSelectedPlugins();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show(this, "翻訳対象のMODを少なくとも1つ選択してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        // SJPTS_InterfaceTextの`translate`は--mods-file=で複数MODを1回の起動で
        // まとめて処理できる（ESP側の--plugins-file=と同じ考え方）。
        var modsFilePath = Path.Combine(Path.GetTempPath(), $"sjpts_interfacetext_mods_{Guid.NewGuid():N}.txt");
        // ESP側のBtnTranslate_Clickと同じ仕組み——パスだけ生成してCLIに渡し、
        // 実際にキャンセルが要求されたときだけこのパスにファイルを作る
        // （LogWindow_CancelRequested参照）。CLI側はMODの区切りごとにこのパスの
        // 存在を確認する（Program.csのForEachTargetMod、ESPのプラグイン単位の
        // 粒度と同じ）。
        _activeCancelFlagPath = Path.Combine(Path.GetTempPath(), $"sjpts_interfacetext_cancel_{Guid.NewGuid():N}.flag");
        _cancelRequestedForCurrentRun = false;
        _logWindow.SetCancelEnabled(true);
        try
        {
            await File.WriteAllLinesAsync(modsFilePath, selectedMods);

            // 進捗バー用——選択MODそれぞれの未翻訳文字数（一覧で既に計算済み、
            // _rows参照）。ESP側のBtnTranslate_Clickと同じ考え方。
            var modCharCounts = selectedMods.ToDictionary(
                m => m,
                m => _rows.FirstOrDefault(r => r.Target.Equals(m, StringComparison.OrdinalIgnoreCase))?.UntranslatedChars ?? 0L,
                StringComparer.OrdinalIgnoreCase);

            // `translate`は1回の起動につきローカルLLM／生成AI（クラウド）の
            // どちらか片方しか受け取れない（--local-llm-endpoint=の有無で判定）
            // ため、ESP側と違い両方チェックされている場合は2回に分けて呼ぶ。
            // 2回目の呼び出しは既に解決済みのkeyを再送しない（Program.cs参照）。
            if (_chkLlm.Checked)
            {
                var localArgs = new List<string> { "translate", $"--mods-file={modsFilePath}", $"--work={InterfaceTextWorkDir}",
                    $"--local-llm-endpoint={LlmEndpoint}", $"--local-llm-model={LlmModel}", $"--cancel-flag-path={_activeCancelFlagPath}" };
                // APIキーは引数に含めない——RunCliAsync→CliRunnerがSKYRIMJPSP_LLM_API_KEY
                // 環境変数として子プロセスへ渡す（ESP側と同じ方式、CLIのProgram.cs参照）。
                // v0.58.1: 設定ウィンドウの「思考モードOFF」チェック（既定ON）に連動——
                // 未対応のままだと、gemma4等の思考系ローカルモデルが応答の文字数上限を
                // 内部思考トレースで使い切り、常に空応答で失敗する（実機で確認済み）。
                if (_getSettings().LlmLocalReasoningOff)
                    localArgs.Add("--local-llm-reasoning-effort=none");
                // SJPTS_InterfaceText側の内部既定値（3000、InterfaceTextPromptGenerator.
                // DefaultLocalLlmBatchCharLimit）はGUIの既定値（6000）と異なるため、
                // ESP側のように「既定値と同じなら省略」はできない——常に明示的に渡す。
                localArgs.Add($"--char-limit={(int)_numLlmBatchCharLimit.Value}");
                if (!await RunCliAsync(localArgs, modCharCounts)) return;
            }

            if (_chkCloudAi.Checked && !_cancelRequestedForCurrentRun)
            {
                var cloudArgs = new List<string> { "translate", $"--mods-file={modsFilePath}", $"--work={InterfaceTextWorkDir}", $"--cancel-flag-path={_activeCancelFlagPath}" };
                if (UseClaudeCodeCli)
                {
                    if (!string.IsNullOrWhiteSpace(ClaudeCodeExePath)) cloudArgs.Add($"--claude-code-exe={ClaudeCodeExePath}");
                    if (!string.IsNullOrWhiteSpace(ClaudeCodeModel)) cloudArgs.Add($"--claude-code-model={ClaudeCodeModel}");
                }
                else
                {
                    // OpenAI互換API方式——ESP側（Program.cs --llm-cloud-provider=http）
                    // と同じ引数名。CloudAiApiKeyはRunCliAsync→CliRunnerが
                    // SKYRIMJPSP_CLOUD_LLM_API_KEY環境変数として子プロセスへ渡す
                    // （プレーンな引数には含めない）。
                    if (string.IsNullOrWhiteSpace(CloudAiEndpoint) || string.IsNullOrWhiteSpace(LlmModel))
                    {
                        MessageBox.Show(this, "生成AI翻訳（クラウド・OpenAI互換API）を使うには、「設定」でエンドポイントとモデル名を指定してください。",
                            "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    cloudArgs.Add("--llm-cloud-provider=http");
                    cloudArgs.Add($"--llm-cloud-endpoint={CloudAiEndpoint}");
                    cloudArgs.Add($"--llm-cloud-model={LlmModel}");
                }
                // ローカルLLM側と同じ理由で、生成AI（クラウド）側も常に明示的に渡す
                // （SJPTS_InterfaceText側の内部既定値はInterfaceTextPromptGenerator.
                // DefaultLlmBatchCharLimit=12000でGUIの既定値と一致するが、将来ズレた
                // 場合に同じ事故を防ぐため、ここも省略しない）。
                cloudArgs.Add($"--char-limit={(int)_numCloudAiBatchCharLimit.Value}");
                if (!await RunCliAsync(cloudArgs, modCharCounts)) return;
            }

            _translationExecuted = true;
            RefreshRowsFromTranslations(selectedMods);

            if (_cancelRequestedForCurrentRun)
            {
                MessageBox.Show(this,
                    "ユーザーの要求により、処理を中断しました。\n" +
                    "中断までに完了したMODの翻訳結果は保存されています。\n" +
                    "残りのMODは、改めて「翻訳実行」を行うと続きから処理されます。",
                    "処理を中断しました", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // TODO(次のステップ): interface_translations.tsvにはESP側のNotes列（解決手法の
            // タグ）が無いため、「⑤/⑥のどちらが失敗したか」の判定はできない
            // （design/interface_translations.md記載の既知の簡略化）。ここでは
            // 「選択MODに1件でも未解決が残っているか」だけを見る。
            var stillUntranslated = selectedMods.Sum(p => _rows.FirstOrDefault(r => r.Target.Equals(p, StringComparison.OrdinalIgnoreCase))?.Untranslated ?? 0);
            if ((_chkCloudAi.Checked || _chkLlm.Checked) && stillUntranslated > 0)
            {
                _logWindow.ShowAndActivate();
                MessageBox.Show(this,
                    "未解決のまま残ったkeyがあります。\n\n" +
                    "・全く翻訳されない場合は、設定（生成AIの接続情報・ログイン状態・\n" +
                    "　ローカルLLMの起動状況等）を確認してください。\n" +
                    "・ローカルLLM/生成AIの応答は毎回安定するとは限らないため、\n" +
                    "　「翻訳実行」を複数回行うと解決することもあります。\n\n" +
                    "実行ログウィンドウに詳しい失敗理由が出力されています。",
                    "一部のkeyが未解決です", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(this, "翻訳が完了しました。翻訳内容を確認してください（「詳細を確認」ボタン）。\n" +
                    "内容に問題なければ「翻訳ファイル出力」で出力してください。",
                    "翻訳完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally
        {
            SetBusy(false);
            _logWindow.SetCancelEnabled(false);
            try { File.Delete(modsFilePath); } catch { /* best-effort cleanup */ }
            try { if (_activeCancelFlagPath != null) File.Delete(_activeCancelFlagPath); } catch { /* best-effort cleanup */ }
            _activeCancelFlagPath = null;
        }
    }

    private async void BtnGenerateDsd_Click(object? sender, EventArgs e)
    {
        var selectedMods = GetSelectedPlugins();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show(this, "出力対象のMODを少なくとも1つ選択してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_translationExecuted)
        {
            var result = MessageBox.Show(this,
                "このウィンドウではまだ「翻訳実行」を行っていません。\n" +
                "翻訳されていない文字列は、原文（英語等）のまま出力されます。\n\n" +
                "このまま出力しますか？（翻訳してから出力する場合は「キャンセル」を押してください）",
                "翻訳が未実行です", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK) return;
        }

        SetBusy(true);
        var modsFilePath = Path.Combine(Path.GetTempPath(), $"sjpts_interfacetext_mods_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(modsFilePath, selectedMods);
            var outDir = InterfaceTextOutDir;
            var args = new[] { "output", $"--mods-file={modsFilePath}", $"--work={InterfaceTextWorkDir}", $"--out={outDir}" };
            if (!await RunCliAsync(args)) return;

            MessageBox.Show(this, $"翻訳ファイルの出力が完了しました。出力先フォルダを確認してください:\n{outDir}",
                "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            SetBusy(false);
            try { File.Delete(modsFilePath); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Disables every action button for the duration of a run — CLI runs
    /// share the same on-disk out_temp folders, so two overlapping runs would
    /// corrupt each other's output.</summary>
    private void SetBusy(bool busy)
    {
        _btnReloadMo2.Enabled = !busy;
        _btnTranslate.Enabled = !busy;
        _btnGenerateDsd.Enabled = !busy;
        _btnSelectAll.Enabled = !busy;
        _btnSelectNone.Enabled = !busy;
        _grid.Enabled = !busy;
        _chkLlm.Enabled = !busy;
        _chkCloudAi.Enabled = !busy;
        _numLlmBatchCharLimit.Enabled = !busy;
        _numCloudAiBatchCharLimit.Enabled = !busy;
        // v0.58.6: CLI実行（翻訳実行・再スキャン・初期化等）の完了直後にも
        // bottomパネルの高さを補正し直す（RecalculateBottomHeightの他の
        // 呼び出し元と同じ保険的対応）。
        if (!busy) RecalculateBottomHeight();
    }

    private void AppendLog(string line) => _logWindow.AppendLine(line);

    private void SetStatus(string text) => _logWindow.SetStatus(text);

    /// <summary>Validates settings and runs the CLI, returning whether it exited 0.
    /// Every action in this GUI funnels through here — see DESIGN_NOTES.md's GUI
    /// architecture note: the GUI's only responsibilities are argument-building,
    /// log relay, and this kind of pre-flight error checking.</summary>
    /// <param name="pluginCharsForProgress">v0.60.0: plugin -> its pre-scan
    /// untranslated-char-count (from `_rows`, computed before this run even
    /// starts). Only "翻訳実行" (BtnTranslate_Click) passes this — pickuptarget/
    /// generatedsdfile leave it null, so the log window's progress bar simply
    /// never appears for those. When provided, each "Target: {plugin} (...)"
    /// line (see TranslationProgressParser) adds that plugin's already-known
    /// char count to a running total, shown as a fraction of the selection's
    /// full total. Deliberately approximate — a failed candidate's chars still
    /// count as "processed" once its plugin finishes, and the percentage isn't
    /// guaranteed to land on exactly 100% — because the actual requirement is
    /// just "roughly how far along, and how much is left", not an exact
    /// accounting (per-plugin granularity was an explicit choice: ⑤⑥ batch
    /// multiple candidates per LLM call, so per-candidate granularity wouldn't
    /// line up with the real unit of work anyway).</param>
    internal async Task<bool> RunCliAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, long>? pluginCharsForProgress = null)
    {
        if (_productRoot == null)
        {
            MessageBox.Show(this, "実行フォルダを特定できませんでした。GUIの配置場所を確認してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        // CLI実行ファイルのパスはユーザー設定にせず、GUI・CLIが常に同じ製品
        // フォルダの兄弟として配置される前提で毎回自動検出する（ESP側の
        // CliLocatorと同じ考え方——このタブはSJPTS_InterfaceText.exeを呼ぶ）。
        var cliExePath = InterfaceTextCliLocator.ResolveAbsolute(_productRoot, InterfaceTextCliLocator.TryAutoDetect() ?? "");
        if (!InterfaceTextCliLocator.Validate(cliExePath, out var cliError))
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
        var progressTotalChars = pluginCharsForProgress?.Values.Sum() ?? 0;
        var progressDoneChars = 0L;
        if (progressTotalChars > 0) _logWindow.SetProgress(0);
        void OnOutputLine(string line)
        {
            AppendLog(line);
            if (pluginCharsForProgress != null && progressTotalChars > 0
                && Services.TranslationProgressParser.TryParseModCompleted(line, out var completedPlugin)
                && pluginCharsForProgress.TryGetValue(completedPlugin, out var chars))
            {
                progressDoneChars += chars;
                _logWindow.SetProgress((double)progressDoneChars / progressTotalChars);
            }
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
            if (progressTotalChars > 0) _logWindow.SetProgress(null);
            // v0.60.0: 空行を1つ挟んで区切りにする——実行ログウィンドウは
            // 複数のCLI呼び出し（MO2再読込＆初期化はpickuptarget+translationの
            // 2回、翻訳実行→続けてDSDファイル生成、等）の出力が続けて流れ込むため、
            // どこからどこまでが1回の操作の出力かが分かりにくいという指摘への対応。
            // 空行自体にはタイムスタンプを付けない（LogWindow.AppendLine参照）。
            AppendLog("");
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
