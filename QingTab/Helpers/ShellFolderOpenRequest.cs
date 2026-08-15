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
}
