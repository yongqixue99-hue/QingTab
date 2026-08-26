using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace QingTab.WinAPI;

public static class WinApi
{
    public const int WM_COMMAND = 0x111;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint FindWindowEx(nint parentHandle, nint childAfter, string className, string? windowTitle);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint handle, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hWnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint RealGetWindowClass(nint hwnd, StringBuilder pszType, uint cchType);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hWnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    public static IEnumerable<nint> FindAllWindowsEx(string className, nint parent = 0, string? windowTitle = null)
    {
        nint handle = 0;
        do
        {
            handle = FindWindowEx(parent, handle, className, windowTitle);
            if (handle == 0) continue;
            yield return handle;
        } while (handle != 0);
    }

    public static void RestoreWindowToForeground(nint window)
    {
        if (IsIconic(window))
            ShowWindow(window, SW_SHOWNOACTIVATE);

        SetForegroundWindow(window);
    }

    public static string GetWindowClassName(nint hWnd, int maxClassNameLength = 254)
    {
        if (hWnd == 0) return string.Empty;

        var className = new StringBuilder(maxClassNameLength + 1);
        RealGetWindowClass(hWnd, className, (uint)className.Capacity);
        return className.ToString();
    }

    public static bool IsWindowHasClassName(nint hWnd, string className, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        // Read the complete class name. A buffer sized only to the expected
        // value can truncate "CabinetWClassSomething" to "CabinetWClass" and
        // accidentally accept a non-Explorer window.
        var currentClassName = GetWindowClassName(hWnd);
        return string.Equals(currentClassName, className, comparison);
    }

    public static bool IsWindowCloaked(nint hWnd)
    {
        const uint dwmwaCloaked = 14;
        return DwmGetWindowAttribute(hWnd, dwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    public static string? GetProcessPath(int pid)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var procHandle = OpenProcess(processQueryLimitedInformation, false, (uint)pid);
        if (procHandle == 0) return null;

        try
        {
            var capacity = 260;
            var sb = new StringBuilder(capacity);
            return QueryFullProcessImageName(procHandle, 0, sb, ref capacity) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(procHandle);
        }
    }
}
