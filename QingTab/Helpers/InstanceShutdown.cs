using System;
using System.Threading;

namespace QingTab.Helpers;

/// <summary>
/// Provides a bounded acknowledgement that the resident has actually released
/// its single-instance mutex after an exit request.
/// </summary>
public static class InstanceShutdown
{
    public static bool WaitUntilReleased(string mutexName, int timeoutMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
            throw new ArgumentException("互斥体名称不能为空。", nameof(mutexName));
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        Mutex? mutex = null;
        try
        {
            try
            {
                mutex = Mutex.OpenExisting(mutexName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return true;
            }

            try
            {
                if (!mutex.WaitOne(timeoutMilliseconds)) return false;
                mutex.ReleaseMutex();
                return true;
            }
            catch (AbandonedMutexException)
            {
                // The owner terminated without an orderly release. The mutex
                // is nevertheless no longer held by a resident process.
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            mutex?.Dispose();
        }
    }
}
