using SkyrimJPStringPatcherGui.Services;

namespace SkyrimJPStringPatcherGui;

/// <summary>
/// 生成AI（クラウド）連携設定 — v0.52.1a: ベース画面から切り出した専用ウィンドウ。
/// ⑤の解決手段として、①ここ独自のエンドポイントにAPIキー付きでHTTPを投げる方式
/// （OpenAI/OpenRouter/DeepSeek/Groq等、OpenAI互換API全般——ベース画面の
/// 「ローカルLLM エンドポイント」とは別に独立して持つ。ローカルとクラウドを共用
/// すると、切り替えるたびに書き換える必要が出て筋が悪いため）と、
/// ②Claude Code CLI（claudeコマンド）をサブプロセス起動する方式、のどちらを使うかを
/// ここで切り替える。設定はAppSettingsに直接読み書きする（ダイアログを開いている間だけ
/// メモリ上で編集し、OKで確定・キャンセルで破棄——他の設定行と同じ「編集して保存」の
/// パターン）。
///
/// v0.52.1a改: 当初はGroupBox2つ＋排他ラジオボタン（選ばれていない方を丸ごと
/// Enabled=false）だったが、タブの方が「今どちらを使っているか」が一目瞭然で、
/// かつ非選択側の情報を完全に隠せる（グレーアウトした項目を目に入れる必要がない）
/// ため、TabControlに作り直した——タブの選択そのものが「どちらを使うか」を表すので、
/// ラジオボタンや排他制御用のコードも丸ごと不要になった。
/// </summary>
public sealed class CloudAiSettingsForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _openAiTab = new("OpenAI互換API");
    private readonly TabPage _claudeCodeTab = new("Claude Code CLI");

    private readonly TextBox _txtEndpoint = new();
    private readonly TextBox _txtApiKey = new() { UseSystemPasswordChar = true };
    private readonly Button _btnToggleApiKeyVisibility = new() { Text = "APIキーを表示", AutoSize = true };
    private readonly TextBox _txtClaudeCodeExe = new() { Text = "claude" };
    private readonly TextBox _txtClaudeCodeModel = new();
    private readonly Button _btnOk = new() { Text = "OK", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "キャンセル", AutoSize = true };

    private readonly AppSettings _settings;

    public CloudAiSettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Beta機能: 生成AI（クラウド）連携設定";
        Width = 640;
        Height = 320;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildLayout();
        LoadFromSettings();
    }

    private void BuildLayout()
    {
        BuildOpenAiTab();
        BuildClaudeCodeTab();
        // Claude Code CLIは既にログイン済みの環境でそのまま検証できる（APIキー等の
        // 追加準備が要らない）ため、当面の検証の主軸として左端（既定タブ）に置く。
        _tabs.TabPages.Add(_claudeCodeTab);
        _tabs.TabPages.Add(_openAiTab);
        Controls.Add(_tabs);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        _btnOk.Click += BtnOk_Click;
        _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottom.Controls.Add(_btnOk);
        bottom.Controls.Add(_btnCancel);
        ButtonLayout.UnifyWidths(new[] { _btnOk, _btnCancel });
        Controls.Add(bottom);
    }

    private void BuildOpenAiTab()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _openAiTab.Controls.Add(layout);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "エンドポイント", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 0);
        _txtEndpoint.Dock = DockStyle.Fill;
        _txtEndpoint.Margin = new Padding(3, 5, 3, 3);
        grid.Controls.Add(_txtEndpoint, 1, 0);
        grid.Controls.Add(new Label { Text = "APIキー（省略可）", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 1);
        _txtApiKey.Dock = DockStyle.Fill;
        _txtApiKey.Margin = new Padding(3, 5, 3, 3);
        grid.Controls.Add(_txtApiKey, 1, 1);
        _btnToggleApiKeyVisibility.Click += (_, _) =>
        {
            _txtApiKey.UseSystemPasswordChar = !_txtApiKey.UseSystemPasswordChar;
            _btnToggleApiKeyVisibility.Text = _txtApiKey.UseSystemPasswordChar ? "APIキーを表示" : "APIキーを隠す";
        };
        grid.Controls.Add(_btnToggleApiKeyVisibility, 2, 1);
        layout.Controls.Add(grid, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = "Authorizationヘッダー付きでHTTPを送る方式です。エンドポイントをクラウドAPIのURLに\n" +
                   "設定したうえで、必要ならAPIキーも設定してください\n" +
                   "（OpenAI／OpenRouter／DeepSeek／Groq等、OpenAI互換の /v1/chat/completions を実装するサービス全般。\n" +
                   "ベース画面の「ローカルLLM エンドポイント」とは別に、ここで独立して設定します）。\n" +
                   "\n" +
                   "【注意】この方式（OpenAI互換API）は開発時点で実機検証を行っていません。\n" +
                   "動作しない・意図しない結果になる可能性があります。Claude Code CLI方式（もう一方のタブ）は\n" +
                   "実機検証済みです。",
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(3, 8, 3, 3),
        }, 0, 1);
    }

    private void BuildClaudeCodeTab()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _claudeCodeTab.Controls.Add(layout);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label { Text = "claudeコマンドのパス", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 0);
        _txtClaudeCodeExe.Dock = DockStyle.Fill;
        _txtClaudeCodeExe.Margin = new Padding(3, 5, 3, 3);
        grid.Controls.Add(_txtClaudeCodeExe, 1, 0);
        grid.Controls.Add(new Label { Text = "モデル名（省略可）", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 1);
        _txtClaudeCodeModel.Dock = DockStyle.Fill;
        _txtClaudeCodeModel.Margin = new Padding(3, 5, 3, 3);
        grid.Controls.Add(_txtClaudeCodeModel, 1, 1);
        layout.Controls.Add(grid, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = "PATH上のclaudeコマンドをサブプロセスとして起動する方式です。認証はclaude自身の設定（claude login等）を\n" +
                   "そのまま使うため、APIキーの入力は不要です。",
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(3, 8, 3, 3),
        }, 0, 1);
    }

    private void LoadFromSettings()
    {
        _txtEndpoint.Text = _settings.CloudAiEndpoint;
        _txtApiKey.Text = _settings.CloudAiApiKey; // 復号済み・メモリ上のみ
        _txtClaudeCodeExe.Text = string.IsNullOrWhiteSpace(_settings.ClaudeCodeExePath) ? "claude" : _settings.ClaudeCodeExePath;
        _txtClaudeCodeModel.Text = _settings.ClaudeCodeModel;
        _tabs.SelectedTab = _settings.UseClaudeCodeCli ? _claudeCodeTab : _openAiTab;
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        _settings.CloudAiEndpoint = _txtEndpoint.Text.Trim();
        _settings.CloudAiApiKey = _txtApiKey.Text.Trim(); // 暗号化されてAppSettingsへ
        _settings.UseClaudeCodeCli = _tabs.SelectedTab == _claudeCodeTab; // 選択中のタブそのものが「どちらを使うか」
        _settings.ClaudeCodeExePath = string.IsNullOrWhiteSpace(_txtClaudeCodeExe.Text) ? "claude" : _txtClaudeCodeExe.Text.Trim();
        _settings.ClaudeCodeModel = _txtClaudeCodeModel.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
