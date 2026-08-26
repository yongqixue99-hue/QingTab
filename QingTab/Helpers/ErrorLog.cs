using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace QingTab.Helpers;

public static class ErrorLog
{
    private static readonly RotatingTextLog Log = new(
        LogPath,
        maximumBytes: 256 * 1024,
        archiveCount: 2);
    private static int _legacyMigrationAttempted;

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
    /// Removes path-bearing exception messages and stack traces left by builds
    /// that predate the support-safe structured log format. The migration runs
    /// once when the resident starts and never creates a log when none exists.
    /// </summary>
    public static void TryMigrateLegacyLogs()
    {
        if (Interlocked.Exchange(ref _legacyMigrationAttempted, 1) != 0) return;

        try
        {
            SanitizeLegacyLogs(LogPath, archiveCount: 2, DateTimeOffset.Now);
        }
        catch
        {
            // Privacy migration must not prevent the tray process from starting.
        }
    }

    /// <summary>
    /// Testable file seam for the one-time migration. Returns the number of
    /// active/archive files whose legacy contents were replaced.
    /// </summary>
    public static int SanitizeLegacyLogs(
        string logPath,
        int archiveCount,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            throw new ArgumentException("日志路径不能为空。", nameof(logPath));
        if (archiveCount < 0)
            throw new ArgumentOutOfRangeException(nameof(archiveCount));

        var redacted = 0;
        for (var index = 0; index <= archiveCount; index++)
        {
            var path = index == 0 ? logPath : logPath + "." + index;
            if (!File.Exists(path)) continue;

            var content = File.ReadAllText(path, new UTF8Encoding(false));
            if (IsSupportSafeContent(content)) continue;

            File.WriteAllText(
                path,
                BuildLegacyRedactionEntry(timestamp),
                new UTF8Encoding(false));
            redacted++;
        }

        return redacted;
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

    private static bool IsSupportSafeContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;

        foreach (var rawLine in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (IsSafeTimestampRecord(line))
                continue;
            if (line.StartsWith("Code=", StringComparison.Ordinal)
                && IsSafeErrorCode(line.Substring("Code=".Length)))
                continue;
            if (IsSafeExceptionRecord(line)) continue;
            return false;
        }

        return true;
    }

    private static bool IsSafeErrorCode(string value)
    {
        return value.Length > 0
               && value.Length <= 64
               && value.All(character =>
                   char.IsLetterOrDigit(character)
                   || character == '-'
                   || character == '_'
                   || character == '.');
    }

    private static bool IsSafeExceptionRecord(string line)
    {
        var isException = line.StartsWith("Exception=", StringComparison.Ordinal);
        var equalsIndex = line.IndexOf('=');
        var isInner = equalsIndex > "Inner".Length
                      && line.StartsWith("Inner", StringComparison.Ordinal)
                      && line.Substring("Inner".Length, equalsIndex - "Inner".Length)
                          .All(char.IsDigit);
        if (!isException && !isInner) return false;

        const string marker = "; HResult=0x";
        var markerIndex = line.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= equalsIndex) return false;

        var typeName = line.Substring(equalsIndex + 1, markerIndex - equalsIndex - 1);
        if (typeName.Length == 0
            || !typeName.All(character =>
                char.IsLetterOrDigit(character)
                || character == '.'
                || character == '_'
                || character == '+'
                || character == '`'))
            return false;

        var hresult = line.Substring(markerIndex + marker.Length);
        return hresult.Length == 8 && hresult.All(IsHexDigit);
    }

    private static bool IsSafeTimestampRecord(string line)
    {
        if (line.Length < 2
            || line[0] != '['
            || line[line.Length - 1] != ']')
            return false;

        return DateTimeOffset.TryParseExact(
            line.Substring(1, line.Length - 2),
            "yyyy-MM-dd HH:mm:ss.fff zzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    private static bool IsHexDigit(char character)
    {
        return character >= '0' && character <= '9'
               || character >= 'A' && character <= 'F'
               || character >= 'a' && character <= 'f';
    }

    private static string BuildLegacyRedactionEntry(DateTimeOffset timestamp)
    {
        return $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}]\r\n"
               + "Code=legacy-log-redacted\r\n"
               + "Exception=QingTab.LegacyLogRedaction; HResult=0x00000000\r\n\r\n";
    }
}
