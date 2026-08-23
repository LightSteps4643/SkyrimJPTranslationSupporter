namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// v0.52.1a: shows a form "modal to just its owner" without using
/// <see cref="Form.ShowDialog()"/>.
///
/// The GUI's sub-windows (設定, 翻訳状況の詳細, 生成AI連携設定 etc.) were
/// originally made modal via ShowDialog specifically to stop the user from
/// triggering a second, concurrent CLI run against the same output files (e.g.
/// re-scanning while a translation was mid-write). That reasoning only calls
/// for disabling the ONE window the sub-window was opened from — but
/// <c>Form.ShowDialog</c> does more than that: internally it disables EVERY
/// top-level window the process currently has open (via
/// <c>Application.OpenForms</c> bookkeeping for its modal message loop), not
/// just the <c>owner</c> argument passed in. This silently swept up
/// <see cref="LogWindow"/> too — a plain, always-visible log display with no
/// file-access implications at all — leaving it visible but inert
/// (Win32-disabled) any time another window was showing modally, which is why
/// toggling <c>TopMost</c> on it "fixed" nothing: a disabled window doesn't
/// accept input no matter what layer it renders on.
///
/// This helper disables only the specific <paramref name="owner"/> passed in,
/// re-enabling it when <paramref name="form"/> closes — everything else
/// (including LogWindow) is untouched and stays fully interactive.
///
/// v0.52.1a: MainFormが「翻訳前の状況」を吸収して実質1つのウィンドウに統合された
/// ことで、以前ここに一時的に加えていた複数所有元対応（TranslationDetailForm用に
/// 2つの別ウィンドウを同時ロックする必要があった）は不要になった——ロックすべき
/// 相手は常にMainFormひとつだけになったため、単純な単一owner版に戻した。
/// </summary>
public static class PseudoModal
{
    public static void Show(Form form, Form owner)
    {
        owner.Enabled = false;
        form.FormClosed += (_, _) =>
        {
            if (owner.IsDisposed) return;
            owner.Enabled = true;
            owner.Activate();
        };
        form.Show(owner);
    }
}
