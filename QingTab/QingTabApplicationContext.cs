using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using QingTab.Helpers;
using QingTab.Hooks;
using QingTab.WinAPI;
using Microsoft.Win32;

namespace QingTab;

internal sealed class QingTabApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _syncContext;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly EventWaitHandle _readyEvent;
    private readonly DiagnosticHistory _diagnostics = new(capacity: 20);
    private readonly OpenTabRequestQueue _requestQueue = new(capacity: 10);
    private OpenTabIpc.Server? _openTabServer;
    private ExplorerWatcher? _explorerWatcher;
    private TrayIcon? _trayIcon;
    private int _requestPumpScheduled;
    private int _disposed;

    public QingTabApplicationContext(
        string[] args,
        EventWaitHandle exitEvent,
        EventWaitHandle readyEvent)
    {
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _readyEvent = readyEvent ?? throw new ArgumentNullException(nameof(readyEvent));

        var portableMode = args.Any(arg => string.Equals(arg, "--portable", StringComparison.OrdinalIgnoreCase));
        bool firstRun;
        bool autoStartEnabled;
        if (portableMode)
        {
            firstRun = false;
            autoStartEnabled = AutoStartManager.IsEnabled();
        }
        else
        {
            firstRun = AutoStartManager.InitializeFirstRun(out autoStartEnabled);
        }

        var skipRegistrationRepair = args.Any(arg =>
            string.Equals(arg, "--no-registration-repair", StringComparison.OrdinalIgnoreCase));
        if (!skipRegistrationRepair
            && !FolderOpenIntegrationManager.TryRepairMovedExecutableRegistration(out var repairError)
            && !string.IsNullOrWhiteSpace(repairError))
        {
            ErrorLog.Write(new InvalidOperationException(repairError), "registration-repair-failed");
        }

        var testDirectOpenEnabled = skipRegistrationRepair && args.Any(arg =>
            string.Equals(arg, "--test-enable-direct-open", StringComparison.OrdinalIgnoreCase));
        var directOpenEnabled = FolderOpenIntegrationManager.IsEnabled()
                                || testDirectOpenEnabled;
        _explorerWatcher = new ExplorerWatcher(directOpenEnabled);
        _trayIcon = new TrayIcon(ExitThread, BuildDiagnosticReport);
        _explorerWatcher.StatusChanged += ExplorerWatcher_StatusChanged;
        _trayIcon.DirectOpenEnabledChanged += TrayIcon_DirectOpenEnabledChanged;
        _trayIcon.SetWatcherStatus(_explorerWatcher.Status);
        UpdateReadySignal(_explorerWatcher.Status);
        _openTabServer = new OpenTabIpc.Server(OpenTabRequestReceived);
        _openTabServer.Start();
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            exitEvent,
            (_, _) => _syncContext.Post(_ => ExitThread(), null),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        SystemEvents.SessionEnding += SystemEvents_SessionEnding;

        var launchedInBackground = args.Any(arg =>
            string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        if (firstRun && !launchedInBackground)
            _trayIcon.ShowFirstRunChoice(autoStartEnabled);
    }

    private OpenTabIpcResponse OpenTabRequestReceived(string path)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !ShellFolderOpenRequest.ShouldHandleDirectOpen(path))
            return OpenTabIpcResponse.Rejected;

        var preferredWindow = CaptureForegroundExplorerWindow();
        var request = new OpenTabRequest(path, preferredWindow, DateTimeOffset.Now);
        request.Trace = _diagnostics.Begin(request.Path, request.PreferredWindow);

        switch (_requestQueue.Enqueue(request))
        {
            case OpenTabEnqueueResult.Accepted:
                ScheduleRequestPump();
                return OpenTabIpcResponse.Accepted;
            case OpenTabEnqueueResult.Duplicate:
                request.Trace.Complete(OpenTabOutcome.DuplicateIgnored);
                return OpenTabIpcResponse.Duplicate;
            case OpenTabEnqueueResult.Full:
                request.Trace.Complete(OpenTabOutcome.Failed, "ipc-rejected-queue-full");
                return OpenTabIpcResponse.Rejected;
            default:
                request.Trace.Complete(OpenTabOutcome.Failed, "ipc-rejected-unknown");
                return OpenTabIpcResponse.Rejected;
        }
    }

    private void ScheduleRequestPump()
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _requestPumpScheduled, 1, 0) != 0)
            return;

        _syncContext.Post(_ => ProcessQueuedRequestsOnUiThread(), null);
    }

    private async void ProcessQueuedRequestsOnUiThread()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0
                   && _requestQueue.TryDequeue(out var request)
                   && request != null)
            {
                await ProcessOpenTabRequestAsync(request);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "request-pump-failed");
        }
        finally
        {
            Volatile.Write(ref _requestPumpScheduled, 0);
            if (Volatile.Read(ref _disposed) == 0 && _requestQueue.Count > 0)
                ScheduleRequestPump();
        }
    }

    private async System.Threading.Tasks.Task ProcessOpenTabRequestAsync(OpenTabRequest request)
    {
        var trace = request.Trace ?? _diagnostics.Begin(request.Path, request.PreferredWindow);
        trace.Mark(OpenTabStage.QueueStarted);

        if (request.TimeBudget.IsExpired)
        {
            OpenFallback(request, OpenTabOutcome.OpenedInWindowFallback, "request-queue-timeout");
            return;
        }

        var result = OpenTabResult.Failed(OpenTabResultKind.ExplorerUnavailable);
        try
        {
            if (_explorerWatcher != null)
            {
                result = await _explorerWatcher.OpenPathInNewTabAsync(
                    request.Path,
                    request.PreferredWindow,
                    trace,
                    request.TimeBudget);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "request-open-failed");
            result = OpenTabResult.Failed(OpenTabResultKind.NavigationFailed);
        }

        if (result.IsSuccess)
        {
            trace.Complete(OpenTabOutcome.OpenedInTab);
            return;
        }

        if (!result.ShouldOpenFallback)
        {
            trace.Complete(OpenTabOutcome.StoppedWithoutFallback, result.FailureCode);
            return;
        }

        OpenFallback(request, OpenTabOutcome.OpenedInWindowFallback, result.FailureCode);
    }

    private void OpenFallback(OpenTabRequest request, OpenTabOutcome outcome, string reason)
    {
        var trace = request.Trace ?? _diagnostics.Begin(request.Path, request.PreferredWindow);
        if (ExplorerLauncher.TryOpenFolder(request.Path, out var fallbackError))
        {
            trace.Complete(outcome, reason);
            if (!string.Equals(reason, "target-window-unavailable", StringComparison.Ordinal)
                && !string.Equals(reason, "feature-disabled", StringComparison.Ordinal))
            {
                _trayIcon?.ShowFallbackNotice(reason);
            }
            return;
        }

        trace.Complete(OpenTabOutcome.Failed, "explorer-fallback-failed");
        ErrorLog.Write(new InvalidOperationException(fallbackError), "explorer-fallback-failed");
    }

    private static nint CaptureForegroundExplorerWindow()
    {
        var root = WinApi.GetAncestor(WinApi.GetForegroundWindow(), WinApi.GA_ROOT);
        return root != 0
               && WinApi.IsWindow(root)
               && WinApi.IsWindowHasClassName(root, "CabinetWClass")
            ? root
            : 0;
    }

    private string BuildDiagnosticReport()
    {
        var version = typeof(QingTabApplicationContext).Assembly.GetName().Version;
        return _diagnostics.BuildReport(
            version == null ? "0.2.7" : version.ToString(3),
            FolderOpenIntegrationManager.IsEnabled(),
            AutoStartManager.IsEnabled());
    }

    private void SystemEvents_SessionEnding(object sender, SessionEndingEventArgs e)
    {
        _syncContext.Post(_ => ExitThread(), null);
    }

    private void ExplorerWatcher_StatusChanged(ExplorerConnectionStatus status)
    {
        UpdateReadySignal(status);
        _trayIcon?.SetWatcherStatus(status);
    }

    private void TrayIcon_DirectOpenEnabledChanged(bool enabled)
    {
        _explorerWatcher?.SetEnabled(enabled);
    }

    private void UpdateReadySignal(ExplorerConnectionStatus status)
    {
        try
        {
            if (status.IsReady)
                _readyEvent.Set();
            else
                _readyEvent.Reset();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown may race a final Explorer status notification.
        }
    }

    protected override void ExitThreadCore()
    {
        DisposeResources();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeResources();
        base.Dispose(disposing);
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        try
        {
            _readyEvent.Reset();
        }
        catch (ObjectDisposedException)
        {
            // The Program-owned handle may already be closing.
        }
        _exitWait.Unregister(null);

        _openTabServer?.Dispose();
        _openTabServer = null;

        try
        {
            if (_explorerWatcher != null)
                _explorerWatcher.StatusChanged -= ExplorerWatcher_StatusChanged;
            _explorerWatcher?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "shutdown-watcher-failed");
        }
        finally
        {
            _explorerWatcher = null;
        }

        if (_trayIcon != null)
            _trayIcon.DirectOpenEnabledChanged -= TrayIcon_DirectOpenEnabledChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
