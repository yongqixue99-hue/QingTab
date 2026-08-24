using System;
using System.Text;

namespace QingTab.Helpers;

/// <summary>
/// Normalizes paths received from a Folder shell command and quotes paths that
/// QingTab passes to another Windows process.
/// </summary>
public static class ShellFolderOpenRequest
{
    /// <summary>
    /// Returns true only for absolute file-system locations that QingTab may
    /// safely redirect into an Explorer tab. Virtual Shell parsing names such
    /// as Recycle Bin, Control Panel and Libraries must remain on Windows'
    /// native open path.
    ///
    /// This is intentionally a syntax-only check: probing Directory.Exists
    /// would add latency and could block on unavailable UNC locations.
    /// </summary>
    public static bool ShouldHandleDirectOpen(string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.Length >= 3
            && char.IsLetter(normalized[0])
            && normalized[1] == ':'
            && IsDirectorySeparator(normalized[2]))
        {
            return true;
        }

        return normalized.Length > 2
               && IsDirectorySeparator(normalized[0])
               && IsDirectorySeparator(normalized[1]);
    }

    /// <summary>
    /// Repairs the command-line parsing artifact produced by a quoted path that
    /// ends in a backslash. For example, Windows parses "E:\" as E:".
    /// A double quote cannot be part of a normal Windows file-system path, so
    /// restoring the trailing directory separator is unambiguous here.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith("\"", StringComparison.Ordinal))
            return path;

        return path.Substring(0, path.Length - 1) + "\\";
    }

    /// <summary>
    /// Applies the escaping rules used by CommandLineToArgvW. In particular,
    /// trailing backslashes are doubled before the closing quote.
    /// </summary>
    public static string QuoteArgument(string argument)
    {
        if (argument == null) throw new ArgumentNullException(nameof(argument));

        var requiresQuotes = argument.Length == 0;
        for (var index = 0; index < argument.Length && !requiresQuotes; index++)
        {
            var character = argument[index];
            requiresQuotes = char.IsWhiteSpace(character) || character == '"';
        }

        if (!requiresQuotes) return argument;

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashCount = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashCount * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashCount);
                result.Append(character);
            }

            backslashCount = 0;
        }

        result.Append('\\', backslashCount * 2);
        result.Append('"');
        return result.ToString();
    }

    private static bool IsDirectorySeparator(char character)
    {
        return character == '\\' || character == '/';
    }
}
