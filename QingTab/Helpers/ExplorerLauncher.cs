using System;
using System.Diagnostics;
using System.IO;

namespace QingTab.Helpers;

public static class ExplorerLauncher
{
    /// <summary>
    /// Opens Explorer explicitly, bypassing QingTab's Folder shell command so
    /// a failed direct-tab request can never recurse back into QingTab.
    /// </summary>
    public static bool TryOpenFolder(string path, out string error)
    {
        try
        {
            var explorerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");
            var safePath = ShellFolderOpenRequest.NormalizePath(path).Replace("\"", string.Empty);
            Process.Start(new ProcessStartInfo
            {
                FileName = explorerPath,
                Arguments = ShellFolderOpenRequest.QuoteArgument(safePath),
                UseShellExecute = false
            });
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
