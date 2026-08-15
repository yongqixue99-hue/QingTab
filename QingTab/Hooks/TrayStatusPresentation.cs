namespace QingTab.Hooks;

public enum QingTabOperationalState
{
    Disabled,
    Connecting,
    Ready,
    Reconnecting,
    Unavailable
}

public sealed class TrayStatusPresentation
{
    private TrayStatusPresentation(
        QingTabOperationalState state,
        string statusText,
        string toolTipText)
    {
        State = state;
        StatusText = statusText;
        ToolTipText = toolTipText;
    }

    public QingTabOperationalState State { get; }
    public string StatusText { get; }
    public string ToolTipText { get; }

    public static TrayStatusPresentation Create(
        bool directOpenEnabled,
        ExplorerConnectionState explorerState)
    {
        if (!directOpenEnabled)
        {
            return new TrayStatusPresentation(
                QingTabOperationalState.Disabled,
                "○ 新标签接管已关闭：保持 Windows 原生行为",
                "轻页：新标签接管已关闭");
        }

        switch (explorerState)
        {
            case ExplorerConnectionState.Ready:
                return new TrayStatusPresentation(
                    QingTabOperationalState.Ready,
                    "● 已开启：普通文件夹 → 新标签",
                    "轻页：普通文件夹打开为新标签");
            case ExplorerConnectionState.Reconnecting:
                return new TrayStatusPresentation(
                    QingTabOperationalState.Reconnecting,
                    "○ 新标签接管已开启，Shell 正在重新连接…",
                    "轻页：正在重新连接文件资源管理器");
            case ExplorerConnectionState.Unavailable:
                return new TrayStatusPresentation(
                    QingTabOperationalState.Unavailable,
                    "● 新标签接管已开启，但 Explorer 暂不可用",
                    "轻页：文件资源管理器暂不可用");
            case ExplorerConnectionState.Disabled:
            case ExplorerConnectionState.Connecting:
            default:
                return new TrayStatusPresentation(
                    QingTabOperationalState.Connecting,
                    "○ 新标签接管已开启，正在连接 Explorer…",
                    "轻页：正在连接文件资源管理器");
        }
    }
}
