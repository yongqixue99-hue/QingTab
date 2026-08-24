using System;
using System.Diagnostics;
using System.IO;

namespace QingTab.Helpers;

public static class ExplorerLauncher
{
    /// <summary>
    /// Opens Explorer explicitly, bypassing QingTab's Folder shell command so
    /// a failed direct-tab request or virtual Shell location can never recurse
    /// back into QingTab.
    /// </summary>
    public static bool TryOpenFolder(string path, out string error)
    {
        try
        {
            Process.Start(CreateStartInfo(path));
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds the explicit Explorer launch used for both filesystem fallback
    /// and virtual Shell locations. Exposed as a pure seam so routing can be
    /// regression-tested without opening or focusing an Explorer window.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string path)
    {
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        var safePath = ShellFolderOpenRequest.NormalizePath(path).Replace("\"", string.Empty);
        return new ProcessStartInfo
        {
            FileName = explorerPath,
            Arguments = ShellFolderOpenRequest.QuoteArgument(safePath),
            UseShellExecute = false
        };
    }
}
