using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingTab.WinAPI;

namespace QingTab.Helpers;

public static class Helper
{
    public static async Task<T> DoUntilNotDefaultAsync<T>(
        Func<T> action,
        int timeMs = 500,
        int sleepMs = 20,
        CancellationToken cancellationToken = default)
    {
        return await DoUntilConditionAsync(
            action,
            result => !EqualityComparer<T?>.Default.Equals(result, default),
            timeMs,
            sleepMs,
            cancellationToken);
    }

    private static async Task<T> DoUntilConditionAsync<T>(
        Func<T> action,
        Predicate<T> predicate,
        int timeMs = 500,
        int sleepMs = 20,
        CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            var result = action();
            if (predicate(result)) return result;
            await Task.Delay(sleepMs, cancellationToken);
        }

        return action();
    }

    public static bool IsTimeUp(long startTicks, int timeMs)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
        return elapsedTicks * 1000.0 / Stopwatch.Frequency >= timeMs;
    }

    public static IEnumerable<nint> GetAllExplorerTabs(nint window)
    {
        return WinApi.FindAllWindowsEx("ShellTabWindowClass", window);
    }

    public static Task<nint> ListenForNewExplorerTabAsync(
        nint window,
        IReadOnlyCollection<nint> currentTabs,
        int searchTimeMs = 1_000,
        int sleepMs = 20)
    {
        return DoUntilNotDefaultAsync(
            () => GetAllExplorerTabs(window).Except(currentTabs).FirstOrDefault(),
            searchTimeMs,
            sleepMs);
    }

    /// <summary>
    /// Confirms that the candidate is the only native tab added to an otherwise
    /// unchanged Explorer frame. This is the lightweight production guard used
    /// before navigation and before best-effort cleanup; ambiguous concurrent
    /// user tab activity must fail closed rather than target the wrong tab.
    /// </summary>
    public static bool IsSingleNewExplorerTab(
        IReadOnlyCollection<nint> initialTabs,
        IReadOnlyCollection<nint> currentTabs,
        nint candidate)
    {
        if (initialTabs == null) throw new ArgumentNullException(nameof(initialTabs));
        if (currentTabs == null) throw new ArgumentNullException(nameof(currentTabs));
        if (candidate == 0) return false;

        var initialSet = new HashSet<nint>(initialTabs);
        var currentSet = new HashSet<nint>(currentTabs);
        return initialSet.Count == initialTabs.Count
               && currentSet.Count == currentTabs.Count
               && currentSet.Count == initialSet.Count + 1
               && !initialSet.Contains(candidate)
               && currentSet.Contains(candidate)
               && initialSet.All(currentSet.Contains);
    }

    public static Process? GetMainExplorerProcess()
    {
        Process? best = null;
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var expectedPath = System.IO.Path.Combine(windowsFolder, "explorer.exe");
        var bestStart = DateTime.MaxValue;

        foreach (var hWnd in WinApi.FindAllWindowsEx("Shell_TrayWnd"))
        {
            if (WinApi.GetWindowThreadProcessId(hWnd, out var pid) <= 0) continue;

            var processPath = WinApi.GetProcessPath((int)pid);
            if (!string.Equals(processPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Process? proc = null;
                try
                {
                    proc = Process.GetProcessById((int)pid);
                    var startTime = proc.StartTime;
                    if (startTime < bestStart)
                    {
                        best?.Dispose();
                        bestStart = startTime;
                        best = proc;
                        proc = null;
                    }
                }
                finally
                {
                    proc?.Dispose();
                }
            }
            catch
            {
                // The process may have terminated.
            }
        }

        return best;
    }
}
