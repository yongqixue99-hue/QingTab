using System;
using System.Diagnostics;

namespace QingTab.Helpers;

public static class BackgroundInstanceLauncher
{
    public static ProcessStartInfo CreateStartInfo(string executablePath, bool skipRegistrationRepair)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("程序路径不能为空。", nameof(executablePath));

        return new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = skipRegistrationRepair
                ? "--startup --no-registration-repair"
                : "--startup",
            // ShellExecute launches the WinExe without inheritable redirected
            // pipes. On .NET Framework, UseShellExecute=false plus redirection
            // can otherwise inherit unrelated caller handles into the resident
            // process and keep a parent output pipe open indefinitely.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    public static bool TryStart(bool skipRegistrationRepair)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(
                AppPaths.ExecutablePath,
                skipRegistrationRepair));
            return process != null;
        }
        catch
        {
            // The explicit Explorer fallback still guarantees accessibility.
            return false;
        }
    }
}
