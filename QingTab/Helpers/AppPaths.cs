using System.Diagnostics;
using System.Reflection;

namespace QingTab.Helpers;

public static class AppPaths
{
    public static string ExecutablePath
    {
        get
        {
            var entryPath = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(entryPath))
                return entryPath!;

            using (var process = Process.GetCurrentProcess())
                return process.MainModule?.FileName ?? string.Empty;
        }
    }
}
