using System.Collections.Generic;

namespace QingTab.Hooks;

/// <summary>
/// User-visible tray menu wording and command shape. Keeping this separate
/// prevents implementation details such as the old "zero flicker" label or a
/// resident-only exit command from silently returning during UI changes.
/// </summary>
public sealed class TrayMenuPresentation
{
    private TrayMenuPresentation(
        string directOpenItemText,
        IReadOnlyList<string> exitItemTexts)
    {
        DirectOpenItemText = directOpenItemText;
        ExitItemTexts = exitItemTexts;
    }

    public string DirectOpenItemText { get; }
    public IReadOnlyList<string> ExitItemTexts { get; }

    public static TrayMenuPresentation Create()
    {
        return new TrayMenuPresentation(
            "普通打开文件夹 → 新标签",
            new[] { "退出" });
    }
}
