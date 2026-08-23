namespace SkyrimJPStringPatcherGui.Services;

/// <summary>v0.52.1a: shared by MainForm/SettingsForm — a row of buttons
/// whose text lengths differ (e.g. "参照..." next to "APIキーを表示") looks
/// uneven left AutoSize, since each button only grows to fit its own text.
/// Measuring while still AutoSize (so PreferredSize reflects the actual text),
/// then switching to a fixed Width equal to the widest one, gets every button in
/// the group to line up without hand-tuning pixel widths per button.</summary>
public static class ButtonLayout
{
    public static void UnifyWidths(IReadOnlyList<Button> buttons)
    {
        var maxWidth = buttons.Max(b => b.PreferredSize.Width);
        foreach (var btn in buttons)
        {
            btn.AutoSize = false;
            btn.Width = maxWidth;
        }
    }
}
