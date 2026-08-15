using System;
using Microsoft.Win32;

namespace QingTab.Helpers;

public static class WindowsVersion
{
    private const int MinimumBuild = 22621;

    public static bool IsSupported(out int buildNumber)
    {
        buildNumber = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            var value = key?.GetValue("CurrentBuildNumber") as string
                        ?? key?.GetValue("CurrentBuild") as string;
            if (!int.TryParse(value, out buildNumber))
                buildNumber = Environment.OSVersion.Version.Build;
        }
        catch
        {
            buildNumber = Environment.OSVersion.Version.Build;
        }

        return buildNumber >= MinimumBuild;
    }
}
