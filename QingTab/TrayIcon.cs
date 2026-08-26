using System;
using System.Drawing;
using System.Windows.Forms;
using QingTab.Helpers;
using QingTab.Hooks;

namespace QingTab;

public sealed class TrayIcon : IDisposable
{
    private readonly Action _exitRequested;
    private readonly Func<string> _diagnosticsTextProvider;
    private readonly Icon _icon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _directOpenItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private ExplorerConnectionStatus _watcherStatus = new(
        ExplorerConnectionState.Connecting,
        "○ 正在连接文件资源管理器…");
    private DateTimeOffset _lastFallbackNoticeUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public event Action<bool>? DirectOpenEnabledChanged;

    public TrayIcon(Action exitRequested, Func<string>? diagnosticsTextProvider = null)
    {
        _exitRequested = exitRequested ?? throw new ArgumentNullException(nameof(exitRequested));
        _diagnosticsTextProvider = diagnosticsTextProvider ?? (() => "轻页暂无诊断信息。");
        _icon = Icon.ExtractAssociatedIcon(AppPaths.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
        var menuPresentation = TrayMenuPresentation.Create();

        _autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true
        };
        _autoStartItem.Click += AutoStartItem_Click;

        _directOpenItem = new ToolStripMenuItem(menuPresentation.DirectOpenItemText)
        {
            CheckOnClick = true,
            ToolTipText = "只接管普通“打开”；不接管左侧栏和原生“新窗口/新标签页”"
        };
        _directOpenItem.Click += DirectOpenItem_Click;

        _statusItem = new ToolStripMenuItem("○ 正在连接文件资源管理器…")
        {
            Enabled = false
        };
        var diagnosticsItem = new ToolStripMenuItem("复制诊断信息");
        diagnosticsItem.Click += (_, _) => CopyDiagnostics();
        var guideItem = new ToolStripMenuItem("使用说明");
        guideItem.Click += (_, _) => ShowGuide();
        var aboutItem = new ToolStripMenuItem("关于与开源许可");
        aboutItem.Click += (_, _) => ShowAbout();
        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_directOpenItem);
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(diagnosticsItem);
        _menu.Items.Add(guideItem);
        _menu.Items.Add(aboutItem);
        _menu.Items.Add(new ToolStripSeparator());
        foreach (var exitItemText in menuPresentation.ExitItemTexts)
        {
            var exitItem = new ToolStripMenuItem(exitItemText);
            exitItem.Click += (_, _) => DisableAndExit();
            _menu.Items.Add(exitItem);
        }
        _menu.Opening += (_, _) => RefreshMenuState();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "轻页：普通文件夹直接打开为新标签",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowGuide();

        RefreshMenuState();
    }

    public void SetWatcherStatus(ExplorerConnectionStatus status)
    {
        if (_disposed) return;
        _watcherStatus = status ?? throw new ArgumentNullException(nameof(status));
        RefreshMenuState();
    }

    public void ShowFirstRunChoice(bool autoStartEnabled)
    {
        var startupText = autoStartEnabled
            ? "开机自启已开启，可随时在托盘菜单关闭。"
            : "开机自启当前未开启，可稍后在托盘菜单重试。";
        var choice = MessageBox.Show(
            "是否立即开启“普通打开文件夹 → 新标签”？\n\n" +
            "开启后只接管普通文件夹打开；左侧导航栏、原生“在新窗口中打开”和 Win + E 保持不变。\n\n" +
            startupText,
            "欢迎使用轻页",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (choice != DialogResult.Yes)
        {
            RefreshMenuState();
            return;
        }

        if (!FolderOpenIntegrationManager.TrySetEnabled(true, out var error))
        {
            RefreshMenuState();
            MessageBox.Show(
                $"轻页已经开始驻留，但暂时无法开启新标签接管。\n\n{error}",
                "轻页",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        DirectOpenEnabledChanged?.Invoke(true);
        RefreshMenuState();
        _notifyIcon.ShowBalloonTip(
            4500,
            "新标签打开已开启",
            "普通文件夹会直接进入已有 Explorer 窗口的新标签。",
            ToolTipIcon.Info);
    }

    private void RefreshMenuState()
    {
        var directOpenEnabled = FolderOpenIntegrationManager.IsEnabled();
        var presentation = TrayStatusPresentation.Create(
            directOpenEnabled,
            _watcherStatus.State);
        _directOpenItem.Checked = directOpenEnabled;
        _autoStartItem.Checked = AutoStartManager.IsEnabled();
        _statusItem.Text = presentation.StatusText;
        _notifyIcon.Text = presentation.ToolTipText;
    }

    private void DirectOpenItem_Click(object? sender, EventArgs e)
    {
        var enabled = _directOpenItem.Checked;
        if (FolderOpenIntegrationManager.TrySetEnabled(enabled, out var error))
        {
            DirectOpenEnabledChanged?.Invoke(enabled);
            RefreshMenuState();
            _notifyIcon.ShowBalloonTip(
                4000,
                enabled ? "新标签打开已开启" : "新标签打开已关闭",
                enabled
                    ? "普通文件夹打开会直接进入已有窗口的新标签；左侧导航栏保持 Windows 原生行为。"
                    : "已恢复 Windows 原来的普通文件夹打开命令。",
                ToolTipIcon.Info);
            return;
        }

        RefreshMenuState();
        MessageBox.Show(
            $"无法修改新标签开关。\n\n{error}",
            "轻页",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void AutoStartItem_Click(object? sender, EventArgs e)
    {
        var enabled = _autoStartItem.Checked;
        if (AutoStartManager.TrySetEnabled(enabled, out var error))
        {
            RefreshMenuState();
            return;
        }

        RefreshMenuState();
        MessageBox.Show(
            $"无法修改开机自启设置。\n\n{error}",
            "轻页",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void CopyDiagnostics()
    {
        try
        {
            var report = _diagnosticsTextProvider();
            Clipboard.SetText(report, TextDataFormat.UnicodeText);
            _notifyIcon.ShowBalloonTip(
                2500,
                "诊断信息已复制",
                "已复制最近请求的阶段耗时；不会包含完整文件夹路径。",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "copy-diagnostics-failed");
            MessageBox.Show(
                $"暂时无法复制诊断信息。\n\n{ex.Message}",
                "轻页",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    public void ShowFallbackNotice(string failureCode)
    {
        if (_disposed || !FolderOpenIntegrationManager.IsEnabled()) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastFallbackNoticeUtc < TimeSpan.FromSeconds(30)) return;
        _lastFallbackNoticeUtc = now;

        _notifyIcon.ShowBalloonTip(
            3500,
            "本次已改用普通窗口打开",
            $"Explorer 暂时无法创建新标签（{failureCode}），文件夹仍已正常打开。",
            ToolTipIcon.Info);
    }

    private void DisableAndExit()
    {
        bool RestoreWindowsFolderOpen(out string error)
        {
            return FolderOpenIntegrationManager.TrySetEnabled(false, out error);
        }

        var preparation = ApplicationExitPolicy.Prepare(RestoreWindowsFolderOpen);
        if (!preparation.CanExit)
        {
            MessageBox.Show(
                $"无法恢复 Windows 原来的文件夹打开方式，因此轻页暂未退出。\n\n{preparation.Error}",
                "轻页",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        DirectOpenEnabledChanged?.Invoke(false);
        _exitRequested();
    }

    private static void ShowGuide()
    {
        MessageBox.Show(
            "【普通打开文件夹 → 新标签】\n" +
            "右键托盘图标并勾选该开关。轻页会在 Windows 创建新窗口之前接管普通文件夹打开命令，直接在已有资源管理器窗口中新建标签，因此不会先创建一扇临时顶层窗口。\n\n" +
            "新版采用响应优先模式：不再用整页截图遮住 Explorer，也不冻结画面等待目标页。新标签注册期间可能短暂显示 Windows 自己的默认页，但点击后会立即看到原生反馈，不会因为遮罩而误以为没有响应。\n\n" +
            "它针对桌面文件夹、资源管理器内容区中的文件夹，以及其他程序发出的普通“打开文件夹”动作。若希望内容区双击也走这条路径，请在“文件夹选项”中保留“在不同窗口中打开不同的文件夹”。\n\n" +
            "【不接管的地方】\n" +
            "左侧导航栏左键保持原生切换，中键保持原生新标签。右键菜单中的“在新标签页中打开”和“在新窗口中打开”都完整交还 Windows；后者会真的保留一扇独立窗口。Win + E、任务栏 Explorer、显式 explorer.exe 也保持 Windows 原生行为。\n\n" +
            "没有任何资源管理器窗口时，第一扇窗口必须正常打开；有窗口后才能直接增加标签。新标签路径在尚未创建标签时失败，会退回正常开窗，保证文件夹仍然能打开。\n\n" +
            "【退出】\n" +
            "托盘中的“退出”会先恢复 Windows 原来的文件夹打开方式，再结束轻页驻留；退出后点击文件夹不会把轻页重新拉起。开机自动启动是独立设置：若不希望下次登录后再次启动，请先取消“开机自动启动”。",
            "轻页 · 使用说明",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void ShowAbout()
    {
        var version = typeof(TrayIcon).Assembly.GetName().Version;
        var versionText = version == null ? "0.2.7" : version.ToString(3);

        MessageBox.Show(
            $"轻页 QingTab v{versionText}\n\n" +
            "适用于 Windows 11 22H2（Build 22621）或更高版本。\n" +
            "无需管理员权限，不含键盘钩子、鼠标钩子、自动更新或使用数据收集。新标签开关只写入当前用户的 Folder 打开命令，关闭或卸载时会进行所有权校验后恢复。\n\n" +
            "基于 MIT 许可项目 w4po/ExplorerTabUtility、Yafeiml/ExplorerTabUtility 的核心思路，并参考 Adstrax/E-Tab 的精简实现。完整许可见随程序附带的 LICENSE 与 THIRD-PARTY-NOTICES.md。",
            "关于轻页",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
