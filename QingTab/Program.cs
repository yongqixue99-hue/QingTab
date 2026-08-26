using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QingTab.Helpers;
using QingTab.Hooks;

namespace QingTab;

internal static class Program
{
    private const string LegacyMutexName = @"Local\QingTab.SingleInstance";
    private const string LegacyExitEventName = @"Local\QingTab.ExitRequested";
    private static readonly InstanceObjectNames ObjectNames = InstanceObjectNames.Current;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            if (!FolderOpenIntegrationManager.TrySetEnabled(false, out var integrationError))
            {
                ErrorLog.Write(new InvalidOperationException(integrationError), "uninstall-integration-failed");
                Environment.ExitCode = 1;
                return;
            }

            if (!AutoStartManager.TryRemoveAll(out var autoStartError))
            {
                ErrorLog.Write(new InvalidOperationException(autoStartError), "uninstall-autostart-failed");
                Environment.ExitCode = 1;
            }
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--enable-direct-open", StringComparison.OrdinalIgnoreCase)))
        {
            if (!FolderOpenIntegrationManager.TrySetEnabled(true, out var error))
            {
                ErrorLog.Write(new InvalidOperationException(error), "enable-direct-open-failed");
                Environment.ExitCode = 1;
            }
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--disable-direct-open", StringComparison.OrdinalIgnoreCase)))
        {
            if (!FolderOpenIntegrationManager.TrySetEnabled(false, out var error))
            {
                ErrorLog.Write(new InvalidOperationException(error), "disable-direct-open-failed");
                Environment.ExitCode = 1;
            }
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--remove-startup", StringComparison.OrdinalIgnoreCase)))
        {
            AutoStartManager.TrySetEnabled(false, out _);
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            bool RestoreWindowsFolderOpen(out string error)
            {
                return FolderOpenIntegrationManager.TrySetEnabled(false, out error);
            }

            var preparation = ApplicationExitPolicy.Prepare(RestoreWindowsFolderOpen);
            if (!preparation.CanExit)
            {
                ErrorLog.Write(
                    new InvalidOperationException(preparation.Error),
                    "exit-integration-restore-failed");
                Environment.ExitCode = 1;
                return;
            }

            var currentSignalled = TrySignalExit(ObjectNames.ExitEventName);
            var legacySignalled = TrySignalExit(LegacyExitEventName);
            var mutexToWaitFor = currentSignalled
                ? ObjectNames.MutexName
                : legacySignalled
                    ? LegacyMutexName
                    : null;
            if (mutexToWaitFor != null
                && !InstanceShutdown.WaitUntilReleased(
                    mutexToWaitFor,
                    timeoutMilliseconds: 5_000))
            {
                ErrorLog.Write(
                    new TimeoutException("The QingTab resident did not exit within the bounded wait."),
                    "exit-resident-timeout");
                Environment.ExitCode = 1;
            }
            return;
        }

        var openTabPath = GetOptionValue(args, "--open-tab");
        var backgroundLaunch = args.Any(arg =>
            string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(openTabPath))
        {
            openTabPath = ShellFolderOpenRequest.NormalizePath(openTabPath!);
            if (!ShellFolderOpenRequest.ShouldHandleDirectOpen(openTabPath!))
            {
                if (!ExplorerLauncher.TryOpenFolder(openTabPath!, out var nativeOpenError))
                {
                    ErrorLog.Write(
                        new InvalidOperationException(nativeOpenError),
                        "native-shell-open-failed");
                    Environment.ExitCode = 1;
                }
                return;
            }

            var readiness = InstanceReadiness.Probe(ObjectNames.ReadyEventName);
            if (readiness != InstanceReadinessState.Missing
                && OpenTabIpc.TrySend(
                    openTabPath!,
                    timeoutMilliseconds: readiness == InstanceReadinessState.Ready ? 500 : 150,
                    out var initialResponse,
                    out _))
            {
                if (initialResponse != OpenTabIpcResponse.Rejected)
                    return;

                ExplorerLauncher.TryOpenFolder(openTabPath!, out _);
                return;
            }

            var skipRegistrationRepair = args.Any(arg =>
                string.Equals(arg, "--no-registration-repair", StringComparison.OrdinalIgnoreCase));
            BackgroundInstanceLauncher.TryStart(skipRegistrationRepair);
            InstanceReadiness.WaitUntilReady(ObjectNames.ReadyEventName, timeoutMilliseconds: 5_000);
            if (OpenTabIpc.TrySend(
                    openTabPath!,
                    timeoutMilliseconds: 3_000,
                    out var response,
                    out _)
                && response != OpenTabIpcResponse.Rejected)
            {
                return;
            }

            ExplorerLauncher.TryOpenFolder(openTabPath!, out _);
            return;
        }

        // Keep the legacy mutex during the transition from v0.2.1 so an older
        // resident copy and this version can never own the same pipe together.
        using var legacyMutex = new Mutex(true, LegacyMutexName, out var legacyCreatedNew);
        if (!legacyCreatedNew)
        {
            ShowAlreadyRunning(backgroundLaunch);
            return;
        }

        using var mutex = new Mutex(true, ObjectNames.MutexName, out var createdNew);
        if (!createdNew)
        {
            ShowAlreadyRunning(backgroundLaunch);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!WindowsVersion.IsSupported(out var buildNumber))
        {
            MessageBox.Show(
                $"轻页需要 Windows 11 22H2（Build 22621）或更高版本。\n\n当前检测到的系统 Build：{buildNumber}",
                "轻页无法启动",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        ErrorLog.TryMigrateLegacyLogs();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => HandleFatalException(eventArgs.Exception, true);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            HandleFatalException(eventArgs.ExceptionObject as Exception ?? new Exception("未知的未处理异常。"), false);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            ErrorLog.Write(eventArgs.Exception, "unobserved-task-exception");
            eventArgs.SetObserved();
        };

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        try
        {
            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ObjectNames.ExitEventName);
            using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ObjectNames.ReadyEventName);
            readyEvent.Reset();
            using var context = new QingTabApplicationContext(args, exitEvent, readyEvent);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            HandleFatalException(ex, true);
        }
    }

    private static void HandleFatalException(Exception exception, bool showMessage)
    {
        ErrorLog.Write(exception, "fatal-unhandled-exception");

        if (showMessage)
        {
            MessageBox.Show(
                $"轻页遇到错误并已停止。\n\n" +
                $"错误日志：{ErrorLog.LogPath}\n\n{exception.Message}",
                "轻页",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        Application.ExitThread();
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static bool TrySignalExit(string eventName)
    {
        try
        {
            using var existingExitEvent = EventWaitHandle.OpenExisting(eventName);
            existingExitEvent.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No resident instance owns this version of the event.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Per-user names prevent QingTab from controlling another user.
            return false;
        }
    }

    private static void ShowAlreadyRunning(bool backgroundLaunch)
    {
        if (backgroundLaunch) return;

        MessageBox.Show(
            "轻页已经在系统托盘中运行。",
            "轻页",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
