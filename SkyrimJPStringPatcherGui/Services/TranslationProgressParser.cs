using System.Text.RegularExpressions;

namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// v0.60.0: parses PromptGenerator.RunMany/RunAll's own existing
/// "Target: {plugin} (N candidates, M resolved (①〜⑥))" console line — printed
/// AFTER that plugin's translation work is fully written, so it doubles as a
/// "this plugin just finished" signal for free. No new CLI-side output was
/// added for this: the line already existed for humans reading the log, and
/// the user explicitly said reusing it (rather than inventing a separate
/// machine-readable marker like "##SJPTS_ISSUES##") was fine.
///
/// The plugin name is captured greedily up to the LAST " (" so a plugin
/// filename that itself contains parentheses (e.g. "[Caenarvon] Cosplay Pack
/// Gala.esp" doesn't have one, but nothing rules it out in general) still
/// resolves correctly — regex backtracking naturally prefers the rightmost
/// split point that still lets the rest of the pattern match.
/// </summary>
public static class TranslationProgressParser
{
    private static readonly Regex TargetLine = new(
        @"^Target: (.+) \(\d+ candidates, \d+ resolved \(①〜⑥\)\)$",
        RegexOptions.Compiled);

    public static bool TryParsePluginCompleted(string line, out string plugin)
    {
        var match = TargetLine.Match(line);
        if (match.Success)
        {
            plugin = match.Groups[1].Value;
            return true;
        }
        plugin = "";
        return false;
    }

    /// <summary>Parses SJPTS_InterfaceText/Program.cs's own "##SJPTS_MOD_DONE##
    /// {mod}" line (ForEachTargetMod) — unlike the ESP CLI's "Target: ..." line
    /// above, this one IS a purpose-built machine-readable marker (InterfaceText
    /// has no equivalent pre-existing human-facing "done" line to piggyback on),
    /// printed once per mod regardless of that mod's success/failure so the
    /// progress bar still advances on a skipped one.</summary>
    private const string ModDoneMarkerPrefix = "##SJPTS_MOD_DONE## ";

    public static bool TryParseModCompleted(string line, out string mod)
    {
        if (line.StartsWith(ModDoneMarkerPrefix, StringComparison.Ordinal))
        {
            mod = line[ModDoneMarkerPrefix.Length..].Trim();
            return true;
        }
        mod = "";
        return false;
    }
}
