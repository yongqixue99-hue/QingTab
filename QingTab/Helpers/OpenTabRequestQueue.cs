using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace QingTab.Helpers;

public enum OpenTabEnqueueResult
{
    Accepted,
    Duplicate,
    Full
}

/// <summary>
/// One monotonic time budget shared by queueing and Explorer work. This keeps
/// the request deadline stable even if the system clock changes.
/// </summary>
public sealed class RequestTimeBudget
{
    public const int DefaultTimeoutMilliseconds = 15_000;

    private readonly TimeSpan _timeout;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public RequestTimeBudget(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    public int RemainingMilliseconds => CalculateRemainingMilliseconds(_timeout, _stopwatch.Elapsed);
    public bool IsExpired => RemainingMilliseconds == 0;

    public int LimitMilliseconds(int maximumMilliseconds)
    {
        if (maximumMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(maximumMilliseconds));
        return Math.Min(maximumMilliseconds, RemainingMilliseconds);
    }

    public static int CalculateRemainingMilliseconds(TimeSpan timeout, TimeSpan elapsed)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));

        var remaining = timeout - elapsed;
        if (remaining <= TimeSpan.Zero) return 0;
        if (remaining.TotalMilliseconds >= int.MaxValue) return int.MaxValue;
        return (int)Math.Ceiling(remaining.TotalMilliseconds);
    }
}

public sealed class OpenTabRequest
{
    public OpenTabRequest(string path, nint preferredWindow, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("文件夹路径不能为空。", nameof(path));

        Path = ShellFolderOpenRequest.NormalizePath(path);
        PreferredWindow = preferredWindow;
        ReceivedAt = receivedAt;
        TimeBudget = new RequestTimeBudget(
            TimeSpan.FromMilliseconds(RequestTimeBudget.DefaultTimeoutMilliseconds));
    }

    public string Path { get; }
    public nint PreferredWindow { get; }
    public DateTimeOffset ReceivedAt { get; }
    public RequestTimeBudget TimeBudget { get; }
    internal OpenTabOperationTrace? Trace { get; set; }
}

/// <summary>
/// Small, thread-safe FIFO for Shell requests. Rapid duplicate Shell
/// invocations are collapsed, while requests aimed at another Explorer window
/// or made after the duplicate window are preserved.
/// </summary>
public sealed class OpenTabRequestQueue
{
    public static readonly TimeSpan DefaultDuplicateWindow =
        TimeSpan.FromMilliseconds(300);

    private readonly int _capacity;
    private readonly TimeSpan _duplicateWindow;
    private readonly object _sync = new();
    private readonly Queue<OpenTabRequest> _requests = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAccepted =
        new(StringComparer.Ordinal);

    public OpenTabRequestQueue(int capacity = 10, TimeSpan? duplicateWindow = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

        var effectiveDuplicateWindow = duplicateWindow ?? DefaultDuplicateWindow;
        if (effectiveDuplicateWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duplicateWindow));

        _capacity = capacity;
        _duplicateWindow = effectiveDuplicateWindow;
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _requests.Count;
        }
    }

    public OpenTabEnqueueResult Enqueue(OpenTabRequest request)
    {
        return Enqueue(request, waitMilliseconds: 0);
    }

    /// <summary>
    /// Waits for bounded queue capacity when requested. The pipe listener is
    /// deliberately back-pressured, so later requests cannot bypass older
    /// requests or create an unbounded set of UI callbacks.
    /// </summary>
    public OpenTabEnqueueResult Enqueue(OpenTabRequest request, int waitMilliseconds)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (waitMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(waitMilliseconds));

        var waitStopwatch = Stopwatch.StartNew();

        lock (_sync)
        {
            while (true)
            {
                RemoveExpiredDedupeEntries(request.ReceivedAt);

                var dedupeKey = GetDedupeKey(request);
                if (_lastAccepted.TryGetValue(dedupeKey, out var previousAcceptedAt))
                {
                    var elapsed = request.ReceivedAt - previousAcceptedAt;
                    if (elapsed >= TimeSpan.Zero && elapsed <= _duplicateWindow)
                        return OpenTabEnqueueResult.Duplicate;
                }

                if (_requests.Count < _capacity)
                {
                    _requests.Enqueue(request);
                    _lastAccepted[dedupeKey] = request.ReceivedAt;
                    return OpenTabEnqueueResult.Accepted;
                }

                var remaining = waitMilliseconds - (int)Math.Min(
                    int.MaxValue,
                    waitStopwatch.ElapsedMilliseconds);
                if (remaining <= 0)
                    return OpenTabEnqueueResult.Full;

                Monitor.Wait(_sync, remaining);
            }
        }
    }

    public bool TryDequeue(out OpenTabRequest? request)
    {
        lock (_sync)
        {
            if (_requests.Count == 0)
            {
                request = null;
                return false;
            }

            request = _requests.Dequeue();
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    private void RemoveExpiredDedupeEntries(DateTimeOffset now)
    {
        var cutoff = now - _duplicateWindow;
        foreach (var key in _lastAccepted
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lastAccepted.Remove(key);
        }
    }

    private static string GetDedupeKey(OpenTabRequest request)
    {
        var normalized = NormalizeForComparison(request.Path);
        var isUnc = normalized.Length >= 2
                    && IsDirectorySeparator(normalized[0])
                    && IsDirectorySeparator(normalized[1]);

        // A UNC share can expose a case-sensitive Linux/Samba file system.
        // Preserve its exact case so two distinct folders are never discarded.
        // Local Explorer paths retain Windows' ordinary case-insensitive
        // duplicate behavior by using an invariant uppercase key.
        var comparisonPath = isUnc ? normalized : normalized.ToUpperInvariant();
        return unchecked((long)request.PreferredWindow)
               + (isUnc ? "|UNC|" : "|WIN|")
               + comparisonPath;
    }

    private static string NormalizeForComparison(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // Shell namespaces and temporarily unavailable paths can still be
            // compared textually without blocking the real open operation.
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool IsDirectorySeparator(char character)
    {
        return character == Path.DirectorySeparatorChar
               || character == Path.AltDirectorySeparatorChar;
    }
}
