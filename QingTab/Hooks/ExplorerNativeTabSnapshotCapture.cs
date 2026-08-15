using System;
using System.Diagnostics;
using System.Linq;
using QingTab.Helpers;
using QingTab.WinAPI;

namespace QingTab.Hooks;

/// <summary>
/// Thin Win32 adapter for the pure native-ownership module. It captures only
/// values and never retains Process, HWND wrappers, UIA objects, or COM RCWs.
/// </summary>
internal static class ExplorerNativeTabSnapshotCapture
{
    public static bool TryCapture(
        nint targetWindow,
        out ExplorerNativeTabSnapshot snapshot)
    {
        snapshot = null!;
        try
        {
            if (targetWindow == 0
                || !WinApi.IsWindow(targetWindow)
                || !WinApi.IsWindowVisible(targetWindow)
                || WinApi.IsWindowCloaked(targetWindow)
                || !WinApi.IsWindowHasClassName(targetWindow, "CabinetWClass")
                || WinApi.GetWindowThreadProcessId(targetWindow, out var rawProcessId) == 0
                || rawProcessId == 0
                || rawProcessId > int.MaxValue
                || !VisualMaskNative.TryGetLastInputTick(out var lastInputTick))
                return false;

            var processId = (int)rawProcessId;
            long processStartTimeUtcTicks;
            using (var process = Process.GetProcessById(processId))
            {
                if (!string.Equals(
                        process.ProcessName,
                        "explorer",
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                processStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }

            var nativeTabs = Helper.GetAllExplorerTabs(targetWindow).ToArray();
            var activeTab = WinApi.FindWindowEx(
                targetWindow,
                0,
                "ShellTabWindowClass",
                null);
            if (nativeTabs.Length == 0
                || activeTab == 0
                || nativeTabs.Any(tab => tab == 0 || !WinApi.IsWindow(tab))
                || nativeTabs.Distinct().Count() != nativeTabs.Length
                || nativeTabs.Count(tab => tab == activeTab) != 1)
                return false;

            var foregroundRoot = WinApi.GetAncestor(
                WinApi.GetForegroundWindow(),
                WinApi.GA_ROOT);
            snapshot = new ExplorerNativeTabSnapshot(
                targetWindow,
                processId,
                processStartTimeUtcTicks,
                nativeTabs,
                activeTab,
                lastInputTick,
                targetWindowIsForeground: foregroundRoot == targetWindow);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
