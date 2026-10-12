namespace SkyrimJPStringPatcher.SkyPatcher;

/// <summary>SkyPatcher.ini, the part we need: the [Patcher] section's "iEnable&lt;Type&gt;Patching=1|0"
/// toggles (all on by default; off skips the type's whole subfolder — grammar §8).</summary>
public sealed class SkyPatcherSettings
{
    private readonly HashSet<string> _disabled;

    private SkyPatcherSettings(HashSet<string> disabled) => _disabled = disabled;

    /// <summary>No SkyPatcher.ini at all: everything enabled.</summary>
    public static SkyPatcherSettings Default { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public static SkyPatcherSettings Parse(IEnumerable<string> lines)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inPatcher = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                inPatcher = line.Equals("[Patcher]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inPatcher) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            if (int.TryParse(line[(eq + 1)..].Trim(), out var value) && value == 0)
                disabled.Add(key);
        }
        return new SkyPatcherSettings(disabled);
    }

    public bool IsEnabled(string toggle) => !_disabled.Contains(toggle);
}
