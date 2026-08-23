namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Finds the console app's built exe so the GUI can launch it as a subprocess.
/// The GUI and console projects are siblings under the same product folder
/// (.../&lt;ProductRoot&gt;/SkyrimJPStringPatcherGui/... and .../&lt;ProductRoot&gt;/bin/...),
/// so the exe's location is predictable from the GUI's own base directory — but
/// build configuration (Debug/Release) may differ, so both are tried.
/// v0.54.0: no user-facing override setting exists any more — this pairing is
/// structural (the two projects always ship together), so a manual path never
/// applied to a real installation; RunCliAsync surfaces an error dialog (asking
/// to rebuild/reinstall) if auto-detection fails instead.
/// </summary>
public static class CliLocator
{
    private const string ExeName = "SkyrimJPStringPatcher.exe";

    /// <summary>The product root — the folder holding Data/PickUpTarget/Translation/etc.
    /// and the console app's own .csproj. Also where the console app must be launched
    /// FROM (its relative default paths like "PickUpTarget/out_temp" are resolved
    /// against the current working directory).
    ///
    /// v0.54.0: two possible layouts, tried in order:
    /// 1. **Release layout** — a self-contained `dotnet publish` of both projects into
    ///    ONE flat folder (both .exe's directly side by side, alongside Data/). This is
    ///    what Nexus users actually get. Product root = the GUI's own folder.
    /// 2. **Dev layout** — the source tree, GUI nested under
    ///    `&lt;root&gt;/SkyrimJPStringPatcherGui/bin/&lt;Config&gt;/net9.0-windows/`. Falls
    ///    back to this only when layout 1 doesn't match, so local `dotnet build` runs
    ///    keep working unchanged.</summary>
    public static string? TryGetProductRoot()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, ExeName)))
            return AppContext.BaseDirectory;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
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
    /// リリース版（フラット配置、両.exeが同じフォルダに並ぶ）ではルート直下に
    /// そのまま存在するので、まずそれを試し、無ければ開発時のbin/Debug・
    /// bin/Release配置を試す。</summary>
    public static string? TryAutoDetect()
    {
        var root = TryGetProductRoot();
        if (root == null) return null;

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
