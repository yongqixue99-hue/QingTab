using System;
using System.IO;
using System.Text;

namespace QingTab.Helpers;

public static class ErrorLog
{
    private static readonly RotatingTextLog Log = new(
        LogPath,
        maximumBytes: 256 * 1024,
        archiveCount: 2);

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QingTab",
        "QingTab-error.log");

    public static void Write(Exception exception)
    {
        Write(exception, "unclassified-exception");
    }

    public static void Write(Exception exception, string errorCode)
    {
        try
        {
            Log.Append(FormatEntry(exception, DateTimeOffset.Now, errorCode));
        }
        catch
        {
            // Logging must never cause another application failure.
        }
    }

    /// <summary>
    /// Produces a support-safe record without exception messages, stack-source
    /// paths, command lines, or folder names. Diagnostics use stable codes and
    /// exception types instead of persisting user data.
    /// </summary>
    public static string FormatEntry(
        Exception exception,
        DateTimeOffset timestamp,
        string errorCode)
    {
        if (exception == null) throw new ArgumentNullException(nameof(exception));

        var builder = new StringBuilder();
        builder.AppendLine($"[{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}]");
        builder.AppendLine($"Code={SanitizeErrorCode(errorCode)}");

        Exception? current = exception;
        var depth = 0;
        while (current != null && depth < 5)
        {
            var prefix = depth == 0 ? "Exception" : $"Inner{depth}";
            builder.AppendLine(
                $"{prefix}={current.GetType().FullName}; HResult=0x{current.HResult:X8}");
            current = current.InnerException;
            depth++;
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string SanitizeErrorCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unspecified";

        var builder = new StringBuilder(Math.Min(value.Length, 64));
        for (var index = 0; index < value.Length && index < 64; index++)
        {
            var character = value[index];
            builder.Append(char.IsLetterOrDigit(character)
                           || character == '-'
                           || character == '_'
                           || character == '.'
                ? character
                : '-');
        }

        return builder.ToString();
    }
}
