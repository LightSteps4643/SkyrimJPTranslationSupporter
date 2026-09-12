namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Finds SJPTS_InterfaceText.exe so the GUI's "UIテキスト翻訳" tab can launch it
/// as a subprocess — copied from (and kept structurally identical to)
/// <see cref="CliLocator"/>, just pointed at the other CLI. See that class's
/// own remarks for the three-layout rationale (release/old-release/dev); not
/// repeated here since it's the exact same logic, only the exe/subfolder name
/// differs (design/interface_translations.md: "CliLocator.csのコピー改造").
/// </summary>
public static class InterfaceTextCliLocator
{
    private const string ExeName = "SJPTS_InterfaceText.exe";
    private const string CliSubfolderName = "SJPTS_InterfaceText";

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

    public static string? TryAutoDetect(string? guiBaseDirectory = null)
    {
        var root = TryGetProductRoot(guiBaseDirectory);
        if (root == null) return null;

        var nested = Path.Combine(CliSubfolderName, ExeName);
        if (File.Exists(Path.Combine(root, nested))) return nested;

        if (File.Exists(Path.Combine(root, ExeName))) return ExeName;

        foreach (var config in new[] { "Debug", "Release" })
        {
            var relative = Path.Combine(CliSubfolderName, "bin", config, "net9.0", ExeName);
            if (File.Exists(Path.Combine(root, relative))) return relative;
        }
        return null;
    }

    public static string ResolveAbsolute(string productRoot, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return "";
        return Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(productRoot, storedPath);
    }

    public static bool Validate(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "SJPTS_InterfaceText実行ファイルのパスが設定されていません。";
            return false;
        }
        if (!File.Exists(path))
        {
            error = $"SJPTS_InterfaceText実行ファイルが見つかりません: {path}\n先に本体（SJPTS_InterfaceText.csproj）をビルドしてください。";
            return false;
        }
        error = "";
        return true;
    }
}
