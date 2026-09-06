namespace SkyrimJPStringPatcherGui;

/// <summary>
/// v0.52.1a: 実行ログ専用の独立ウィンドウ。以前はベース画面(MainForm)に埋め込まれて
/// いたが、翻訳前ウィンドウ等を（当時は）ShowDialogでモーダル表示するようにした結果、
/// モーダル中はベース画面の操作ができず、ログも見られなくなっていた。
///
/// 当初はShowDialogのownerだけが無効化されると考えTopMost=trueで凌ごうとしたが、
/// 実際にはForm.ShowDialogは（そのownerだけでなく）プロセスが開いている全ての
/// トップレベルウィンドウを無効化する（モーダルメッセージループのための内部実装）。
/// つまりLogWindowも無効化されており、TopMostにしても「無効化されたまま最前面に
/// 描画される」だけで操作はできなかった。根本対応として、モーダル表示側を
/// Services/PseudoModal.cs（対象のownerだけを無効化する自前の擬似モーダル）に
/// 切り替えたため、LogWindowはTopMostなしで常に操作可能になっている。
///
/// 閉じるボタンを押しても実際には破棄せず隠すだけ（下記FormClosing）——ログの
/// 唯一の表示先なので、誤って閉じてもベース画面の「実行ログウィンドウを開く」
/// ボタンでいつでも呼び戻せるようにするため。
///
/// 流れてくるテキストを表示するだけのウィンドウ——読み取り専用の複数行テキスト
/// ボックスと、その上に現在の実行状態を示す1行ステータス表示。MainFormが起動時に
/// 一度だけShow()し、以後はログ行が増えるたびにAppendLineを、実行中/完了の切り替わり
/// のたびにSetStatusを呼ぶだけの受け皿として使う。
///
/// v0.52.1a: ステータス表示（「準備完了」「実行中: ...」）は元々MainForm自身の上部に
/// あったが、実行内容を追うならログと同じ場所で見える方が自然という判断で、ここに
/// 移した。
/// </summary>
public sealed class LogWindow : Form
{
    private readonly Label _lblStatus = new() { Text = "準備完了", AutoSize = true, Padding = new Padding(6), Margin = new Padding(0, 3, 0, 0) };

    // v0.53.0a: 実行中のみ押せる「キャンセル」ボタン——既知の課題15.（処理中断機能）。
    // MainFormが実行の開始/終了(SetBusy)に合わせてSetCancelEnabledを呼び、ここでは
    // 押されたことをCancelRequestedイベントで中継するだけにとどめ、実際に何をキャンセル
    // 対象にするか（どの実行を止めるか）はMainForm側の責務のままにしている——既存の
    // 「GUIはCLI起動の薄い層」という設計方針（DESIGN_NOTES.md）を崩さないため。
    // v0.53.0a: PickUpTarget/DSD生成では使えない（「翻訳実行」専用）ことが
    // ボタン名からも分かるようにしておく。
    private readonly Button _btnCancel = new() { Text = "翻訳処理キャンセル", AutoSize = true, Enabled = false, Margin = new Padding(6, 3, 6, 3) };

    // v0.60.0: 翻訳実行中の進捗表示（プラグイン単位、未翻訳文字数ベース）。
    // MainForm.RunCliAsyncが"translation"実行時のみ値を渡す——他のCLI呼び出し
    // （pickuptarget/generatedsdfile）では非表示のまま。厳密な100%到達は保証
    // しない（失敗した文字数も分子に含めてしまうし、②③④のような一瞬で終わる
    // 手法と⑤⑥のように時間のかかる手法が混在する）——「全体のどのあたりまで
    // 進んだか」がざっくり分かれば十分、という要件（ユーザーとの合意）。
    private readonly ProgressBar _progressBar = new() { Width = 200, Minimum = 0, Maximum = 1000, Visible = false, Margin = new Padding(6, 3, 6, 3) };
    private readonly Label _lblProgressPercent = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 6, 6, 3) };

    // v0.53.0a: ステータス文字列（_lblStatus）は実行中の内容によって長さが大きく
    // 変わる（例:「実行中: translation ... --plugins-file=... --llm-batch-char-limit=...」）。
    // 以前はLeftToRightで横に並べていたため、文字列が長いとボタンが画面外へ押し出されたり
    // 表示のたびに位置が動いたりしていた——TopDownにしてボタンをステータス文字列の
    // 「下」の固定行に置くことで、ステータスの長さに関わらずボタンの位置が動かないように
    // する。
    private readonly FlowLayoutPanel _topPanel = new()
    {
        Dock = DockStyle.Top,
        FlowDirection = FlowDirection.TopDown,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
    };

    private readonly TextBox _txtLog = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9F),
        WordWrap = false,
    };

    /// <summary>「キャンセル」ボタンが押されたことをMainFormへ中継するだけのイベント
    /// ——実際にキャンセル要求（確認ダイアログ・フラグファイル書き込み等）を行うのは
    /// MainForm側。</summary>
    public event EventHandler? CancelRequested;

    public LogWindow()
    {
        Text = "実行ログ — Skyrim JP Translation Supporter";
        Width = 700;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;

        // v0.60.0: キャンセルボタンと進捗表示を同じ行に横並びさせるため、
        // その2つ（＋進捗バー・パーセント表示）だけをネストしたLeftToRightの
        // パネルにまとめる——_topPanel自体はTopDownのまま（ステータス行の
        // 「下」の固定行、という既存レイアウトを崩さない）。
        var actionsRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        actionsRow.Controls.Add(_btnCancel);
        actionsRow.Controls.Add(_progressBar);
        actionsRow.Controls.Add(_lblProgressPercent);

        _topPanel.Controls.Add(_lblStatus);
        _topPanel.Controls.Add(actionsRow);
        Controls.Add(_txtLog);
        Controls.Add(_topPanel);

        _btnCancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);

        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>v0.60.0: prefixes each non-blank line with a "[HH:mm:ss] "
    /// timestamp — a user watching a long run had no way to tell how much
    /// time had passed between two log lines (e.g. how long a stuck-looking
    /// LLM batch call had actually been running). An empty line is passed
    /// through untouched (no "[HH:mm:ss] " on its own) — see MainForm's
    /// RunCliAsync, which appends one blank line after every CLI invocation
    /// finishes, purely as a visual separator between operations; stamping
    /// that separator too would just be clutter.</summary>
    public void AppendLine(string line)
    {
        if (IsDisposed) return;
        if (_txtLog.InvokeRequired)
        {
            _txtLog.BeginInvoke(() => AppendLine(line));
            return;
        }
        var prefixed = line.Length == 0 ? line : $"[{DateTime.Now:HH:mm:ss}] {line}";
        _txtLog.AppendText(prefixed + Environment.NewLine);
    }

    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (_lblStatus.InvokeRequired)
        {
            _lblStatus.BeginInvoke(() => SetStatus(text));
            return;
        }
        _lblStatus.Text = text;
    }

    /// <summary>v0.60.0: 翻訳実行中の進捗（0.0〜1.0）を表示する。nullを渡すと
    /// 非表示に戻す——"translation"以外のCLI呼び出しや、実行完了後はこちら。
    /// 値は`Math.Clamp`で0〜1に丸めるので、呼び出し側が多少ズレた値（分子が
    /// 分母を超える等）を渡しても表示が壊れることはない。</summary>
    public void SetProgress(double? fraction)
    {
        if (IsDisposed) return;
        if (_progressBar.InvokeRequired)
        {
            _progressBar.BeginInvoke(() => SetProgress(fraction));
            return;
        }
        if (fraction is null)
        {
            _progressBar.Visible = false;
            _lblProgressPercent.Visible = false;
            return;
        }
        var clamped = Math.Clamp(fraction.Value, 0, 1);
        _progressBar.Value = (int)Math.Round(clamped * _progressBar.Maximum);
        _lblProgressPercent.Text = $"{clamped:P0}";
        _progressBar.Visible = true;
        _lblProgressPercent.Visible = true;
    }

    /// <summary>MainFormがSetBusy(true/false)と連動して呼ぶ——「押せる＝対象の実行が
    /// 存在する」という単純な対応関係にしておくことで、実行の合間の隙間で押されても
    /// 何も起きない、という状態を作らない（既知の課題15.の検討メモ参照）。</summary>
    public void SetCancelEnabled(bool enabled)
    {
        if (IsDisposed) return;
        if (_btnCancel.InvokeRequired)
        {
            _btnCancel.BeginInvoke(() => SetCancelEnabled(enabled));
            return;
        }
        _btnCancel.Enabled = enabled;
    }

    /// <summary>ベース画面の「実行ログウィンドウを開く」ボタンから呼ぶ——閉じた
    /// （＝隠れた）あとでも、あるいは他のウィンドウの背後に隠れているだけのときも、
    /// これ一つで確実に手前に呼び戻せる。</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
}
