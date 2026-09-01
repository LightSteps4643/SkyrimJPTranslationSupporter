using System.Text.Json;

namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Persisted GUI settings — a plain JSON file next to the GUI exe, matching this
/// project's overall preference for visible, editable plain-text state (TSV/JSON
/// everywhere) over hidden per-user app-data stores. Nothing here is translation
/// logic; it's purely "what to pass on the command line and where to find things."
/// </summary>
public sealed class AppSettings
{
    public string Mo2InstanceDir { get; set; } = "";

    /// <summary>v0.57.0: MO2の「Paths」タブでmods/profiles/overwriteの実体位置を
    /// 標準（&lt;Mo2InstanceDir&gt;直下）から変更している場合のみ使う、任意の個別
    /// 上書き。ModOrganizer.ini自体の位置（Mo2InstanceDir）は「インスタンスフォルダ」
    /// の定義そのものであり上書き対象にならないが、この3つはMO2内で独立に変更
    /// 可能なため個別に持つ。空文字なら従来どおりMo2InstanceDirから自動導出する
    /// （既定・多くのユーザーはここを一切触らない）。</summary>
    public string Mo2ModsDirOverride { get; set; } = "";

    /// <summary>選択中プロファイル自体のフォルダへの完全パス（"profiles"という
    /// 親フォルダではない）。空なら&lt;Mo2InstanceDir&gt;/profiles/&lt;選択中
    /// プロファイル&gt;を自動導出する。</summary>
    public string Mo2ProfileDirOverride { get; set; } = "";

    public string Mo2OverwriteDirOverride { get; set; } = "";
    public string LlmEndpoint { get; set; } = "http://localhost:11434/v1/chat/completions";
    public string LlmModel { get; set; } = "gemma3:12b";

    /// <summary>v0.58.1: 既定でtrue（＝送信する）。"thinking"対応モデル（Ollamaの
    /// gemma4等）は、大きめのバッチだと思考トークンだけで生成上限に達し、実際の
    /// 翻訳結果に一切到達できず失敗することが実機検証で確認された
    /// （`reasoning_effort: "none"`を送ると解消し、副作用も確認されなかった）。
    /// 非思考モデル（gemma3等）にはこのフィールド自体が無視されるだけで実害が
    /// 無いことも実機確認済みのため、既定でONにしてある。オフにする（＝思考を
    /// 有効なままにする）ユーザーには、SettingsForm側でその影響（未対応モデルは
    /// 無効・対応モデルは大幅な時間増加の可能性）を警告する。</summary>
    public bool LlmLocalReasoningOff { get; set; } = true;

    /// <summary>v0.52.1a: DPAPI-encrypted (see <see cref="SecretProtector"/>), NOT
    /// the plaintext key — this is one of two fields in this otherwise-plain-JSON
    /// file that are deliberately not human-readable. Step 5（ローカルLLM）用。
    /// Empty when <see cref="LlmEndpoint"/> points at an unauthenticated local
    /// server (the ordinary case, e.g. Ollama).</summary>
    public string LlmApiKeyEncrypted { get; set; } = "";

    /// <summary>Convenience accessor so callers (CliRunner's env-var setup) never
    /// handle the encrypted form directly — encryption/decryption happens
    /// transparently at the property boundary. Excluded from JSON serialization
    /// so only <see cref="LlmApiKeyEncrypted"/> ever reaches disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LlmApiKey
    {
        get => SecretProtector.Unprotect(LlmApiKeyEncrypted);
        set => LlmApiKeyEncrypted = SecretProtector.Protect(value);
    }

    /// <summary>v0.52.1a: 生成AI（クラウド）連携設定 — 「生成AI翻訳（クラウド）」
    /// （ステップ6）の実行方式を、OpenAI互換API（<see cref="CloudAiEndpoint"/>＋
    /// <see cref="CloudAiApiKey"/>経由のHTTP）から、Claude Code CLI（<c>claude</c>
    /// コマンド）のサブプロセス起動に切り替えるかどうか。切り替えた場合、APIキーは
    /// 使わない（claude自身の認証をそのまま使う）。
    /// v0.53.0a: 既定をClaude Code CLI側（true）に変更——OpenAI互換API側は
    /// エンドポイント/モデル名/APIキーの個別設定が必要なのに対し、Claude Code CLIは
    /// ログイン済みのclaudeコマンドがあればそのまま使えるため、初見のユーザーにとって
    /// 迷いが少ない。既存の設定ファイルで明示的にfalseが保存されている場合はそちらが
    /// 優先される（この初期値が効くのは設定ファイルにこの項目が無い場合のみ）。</summary>
    public bool UseClaudeCodeCli { get; set; } = true;

    /// <summary>v0.52.1a: 「生成AI翻訳（クラウド）」がOpenAI互換API側を使う場合の
    /// エンドポイント。当初はベース画面の<see cref="LlmEndpoint"/>（ローカルLLM用）を
    /// 流用していたが、ローカル（Ollama）とクラウド（OpenAI等）は別々に切り替えたい
    /// ことが多く、共用は筋が悪いという判断で分離した。</summary>
    public string CloudAiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";

    /// <summary>ステップ6（生成AI翻訳・クラウド）がOpenAI互換API側を使う場合の
    /// APIキー。<see cref="LlmApiKey"/>（ステップ5用）とは別に持つ——ローカルと
    /// クラウドを同時に有効化したとき、互いのキーを上書きしてしまわないように。
    /// DPAPI暗号化はLlmApiKeyと同じ仕組み。</summary>
    public string CloudAiApiKeyEncrypted { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string CloudAiApiKey
    {
        get => SecretProtector.Unprotect(CloudAiApiKeyEncrypted);
        set => CloudAiApiKeyEncrypted = SecretProtector.Protect(value);
    }

    /// <summary>claudeコマンドの実行ファイルパス。空ならPATH上の"claude"を使う。</summary>
    public string ClaudeCodeExePath { get; set; } = "claude";

    /// <summary>claude --model に渡すモデル名。空なら省略し、claude自身の既定モデルを使う。</summary>
    public string ClaudeCodeModel { get; set; } = "";

    /// <summary>v0.53.0a: 「1回あたりの文字数上限」（⑤ローカルLLM翻訳向け）。従来は
    /// GUI上の値を変更してもどこにも保存されず、次回起動時に既定値へ戻ってしまって
    /// いた不具合の修正——他の設定と同様この設定ファイルに永続化する。
    /// v0.58.1: 生成AI翻訳・ローカルLLM翻訳共通の1項目だったものを、独立した2項目
    /// （こちらと<see cref="LlmCloudBatchCharLimit"/>）に分割した上で、既定値も
    /// `PromptGenerator.DefaultLocalLlmBatchCharLimit`（実機検証で決めた3000）に
    /// 変更した——⑥と違い従量課金が無いため大きくまとめる動機が薄い上、大きすぎる
    /// バッチは思考系・非思考系どちらのモデルでも失敗率が上がることを実機確認した。</summary>
    public int LlmLocalBatchCharLimit { get; set; } = 3_000;

    /// <summary>v0.58.1: 上記<see cref="LlmLocalBatchCharLimit"/>参照。こちらは
    /// 生成AI翻訳（クラウド）向け——既定値は`PromptGenerator.DefaultLlmBatchCharLimit`
    /// （12000）のまま変更していない。</summary>
    public int LlmCloudBatchCharLimit { get; set; } = 12_000;

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "gui_settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    // A settings file saved before LlmModel had a default (or one
                    // the user cleared) would otherwise carry "" forward forever —
                    // deserialization overwrites the property initializer above,
                    // it doesn't fall back to it.
                    if (string.IsNullOrWhiteSpace(loaded.LlmModel))
                        loaded.LlmModel = new AppSettings().LlmModel;
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupted/unreadable settings file isn't worth failing startup over —
            // just fall back to defaults, same as "no settings file yet".
        }
        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
