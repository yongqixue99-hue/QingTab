using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace QingTab.Helpers;

internal sealed class Debug025Trace
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _marks = new();

    private Debug025Trace()
    {
        Mark("request-start");
    }

    public static Debug025Trace? TryCreate()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("QINGTAB_DEBUG_025"),
            "1",
            StringComparison.Ordinal)
            ? new Debug025Trace()
            : null;
    }

    public void Mark(string stage)
    {
        _marks.Add($"{_clock.ElapsedMilliseconds}:{stage}");
    }

    public void Flush()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingTab");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "DEBUG-025.log"),
                "[DEBUG-025] " + string.Join(" | ", _marks) + Environment.NewLine);
        }
        catch
        {
            // Diagnostic-only tracing must never affect the request.
        }
    }
}
