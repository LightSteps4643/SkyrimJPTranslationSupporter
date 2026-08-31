namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Finds the console app's built exe so the GUI can launch it as a subprocess.
/// The GUI and console projects are siblings under the same product folder,
/// so the exe's location is predictable from the GUI's own base directory —
/// but build configuration (Debug/Release) may differ, and the release layout
/// has changed once already (see below), so several are tried.
/// v0.54.0: no user-facing override setting exists any more — this pairing is
/// structural (the two projects always ship together), so a manual path never
/// applied to a real installation; RunCliAsync surfaces an error dialog (asking
/// to rebuild/reinstall) if auto-detection fails instead.
/// </summary>
public static class CliLocator
{
    private const string ExeName = "SkyrimJPStringPatcher.exe";

    /// <summary>v0.58.0: the CLI's own subfolder name in the current release
    /// layout (see <see cref="TryGetProductRoot"/> case 1) — matches the CLI
    /// project's own name, symmetric to how the GUI's dev-tree folder is
    /// already named "SkyrimJPStringPatcherGui".</summary>
    private const string CliSubfolderName = "SkyrimJPStringPatcher";

    /// <summary>The product root — the folder holding Data/PickUpTarget/Translation/etc.
    /// Also where the console app must be launched FROM (its relative default
    /// paths like "PickUpTarget/out_temp" are resolved against the current
    /// working directory, which CliRunner always sets to this folder regardless
    /// of where the CLI's own exe physically sits).
    ///
    /// Three possible layouts, tried in order:
    /// 1. **Release layout (v0.58.0+)** — the GUI exe sits directly at the
    ///    product root (double-clickable with no launcher needed), with the CLI
    ///    exe nested in its own <see cref="CliSubfolderName"/> subfolder next to
    ///    it. Changed from the flat layout below specifically so a curious user
    ///    browsing the release folder isn't tempted to run the CLI directly
    ///    (see DESIGN_NOTES.md's known-issues list, item 25) — but a curious
    ///    user running SkyrimJPStringPatcher.exe by mistake even inside the
    ///    subfolder isn't the main protection here; the point is it's simply
    ///    not the first thing you see at the top level any more.
    /// 2. **Old release layout (v0.54.0–v0.57.x)** — a self-contained
    ///    `dotnet publish` of both projects into ONE flat folder (both .exe's
    ///    directly side by side, alongside Data/). Kept as a fallback purely so
    ///    an already-unpacked older release folder a user hasn't re-downloaded
    ///    yet still works if this exact GUI build somehow ran against it — the
    ///    two are never actually mixed in practice since a release always ships
    ///    both exes together. Product root = the GUI's own folder.
    /// 3. **Dev layout** — the source tree, GUI nested under
    ///    `&lt;root&gt;/SkyrimJPStringPatcherGui/bin/&lt;Config&gt;/net9.0-windows/`. Falls
    ///    back to this only when neither release layout matches, so local
    ///    `dotnet build` runs keep working unchanged.</summary>
    /// <param name="guiBaseDirectory">v0.58.0: the GUI's own base directory to
    /// search from — defaults to the real <see cref="AppContext.BaseDirectory"/>
    /// (every production call site relies on the default). Only tests pass an
    /// explicit value, standing in for a synthetic release-folder layout —
    /// AppContext.BaseDirectory itself can't be swapped out at test time (it's
    /// the test assembly's own build output, not anything resembling a real
    /// release), so this parameter is what makes the three layouts in the
    /// class remarks above actually testable in isolation.</param>
    public static string? TryGetProductRoot(string? guiBaseDirectory = null)
    {
        var baseDir = guiBaseDirectory ?? AppContext.BaseDirectory;

        if (File.Exists(Path.Combine(baseDir, CliSubfolderName, ExeName)))
            return baseDir;

        if (File.Exists(Path.Combine(baseDir, ExeName)))
            return baseDir;

        var dir = new DirectoryInfo(baseDir);
        for (var d = dir; d != null; d = d.Parent)
        {
            if (d.Name.Equals("SkyrimJPStringPatcherGui", StringComparison.OrdinalIgnoreCase))
                return d.Parent?.FullName;
        }
        return null;
    }

    /// <summary>v0.54.0: 製品ルートからの**相対パス**を返す（従来は絶対パスを返して
    /// いた）——絶対パスのまま`AppSettings.CliExePath`に保存されると、ユーザーが
    /// ツール一式のフォルダを別の場所へ移動・展開し直した際に古いパスのまま残って
    /// 動かなくなる（Nexus配布を想定すると典型的に起こりうる）。実際に起動する際は
    /// <see cref="ResolveAbsolute"/>で製品ルートと組み合わせて絶対パスに戻す。
    ///
    /// v0.58.0: 現行リリース版（CLIが`CliSubfolderName`サブフォルダの中）をまず
    /// 試し、無ければ旧リリース版（フラット配置）、それも無ければ開発時の
    /// bin/Debug・bin/Release配置を試す——<see cref="TryGetProductRoot"/>の
    /// 3パターンにそれぞれ対応。</summary>
    /// <param name="guiBaseDirectory">See <see cref="TryGetProductRoot"/> — passed
    /// straight through; only tests supply an explicit value.</param>
    public static string? TryAutoDetect(string? guiBaseDirectory = null)
    {
        var root = TryGetProductRoot(guiBaseDirectory);
        if (root == null) return null;

        var nested = Path.Combine(CliSubfolderName, ExeName);
        if (File.Exists(Path.Combine(root, nested))) return nested;

        if (File.Exists(Path.Combine(root, ExeName))) return ExeName;

        foreach (var config in new[] { "Debug", "Release" })
        {
            var relative = Path.Combine("bin", config, "net9.0", ExeName);
            if (File.Exists(Path.Combine(root, relative))) return relative;
        }
        return null;
    }

    /// <summary>`AppSettings.CliExePath`に保存されている値（相対パスのことが多いが、
    /// 製品ルート外を指す手動ブラウズの結果は絶対パスのまま保存されることもある）を、
    /// 実際に起動可能な絶対パスへ解決する。</summary>
    public static string ResolveAbsolute(string productRoot, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return "";
        return Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(productRoot, storedPath);
    }

    /// <param name="path">既に絶対パスへ解決済みのパス（<see cref="ResolveAbsolute"/>参照）。</param>
    public static bool Validate(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "CLI実行ファイルのパスが設定されていません。";
            return false;
        }
        if (!File.Exists(path))
        {
            error = $"CLI実行ファイルが見つかりません: {path}\n先に本体（SkyrimJPStringPatcher.csproj）をビルドしてください。";
            return false;
        }
        error = "";
        return true;
    }
}
