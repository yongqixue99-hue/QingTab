using System;
using System.Diagnostics;
using System.Threading;

namespace QingTab.Helpers;

public enum InstanceReadinessState
{
    Missing,
    Starting,
    Ready
}

public static class InstanceReadiness
{
    public static InstanceReadinessState Probe(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("就绪事件名称不能为空。", nameof(eventName));

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(eventName);
            return readyEvent.WaitOne(0)
                ? InstanceReadinessState.Ready
                : InstanceReadinessState.Starting;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return InstanceReadinessState.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return InstanceReadinessState.Missing;
        }
    }

    public static bool WaitUntilReady(string eventName, int timeoutMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("就绪事件名称不能为空。", nameof(eventName));
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        var stopwatch = Stopwatch.StartNew();
        do
        {
            try
            {
                using var readyEvent = EventWaitHandle.OpenExisting(eventName);
                var remaining = Math.Max(0, timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
                return readyEvent.WaitOne(remaining);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The background instance may not have created the event yet.
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            var sleepMilliseconds = Math.Min(25, timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
            if (sleepMilliseconds > 0)
                Thread.Sleep(sleepMilliseconds);
        } while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds);

        return false;
    }
}
