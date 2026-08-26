using System;
using System.Collections.Generic;
using System.Linq;

namespace QingTab.Hooks;

public enum ExplorerComFailureKind
{
    Busy,
    Disconnected,
    Other
}

/// <summary>
/// Centralizes the small set of Explorer COM failures for which QingTab has
/// a safe recovery action. Unknown HRESULTs are never retried blindly.
/// </summary>
public static class ExplorerComPolicy
{
    public static ExplorerComFailureKind Classify(int hresult)
    {
        switch (hresult)
        {
            case unchecked((int)0x80010001): // RPC_E_CALL_REJECTED
            case unchecked((int)0x80010109): // RPC_E_RETRY
            case unchecked((int)0x8001010A): // RPC_E_SERVERCALL_RETRYLATER
            case unchecked((int)0x8001010B): // RPC_E_SERVERCALL_REJECTED
                return ExplorerComFailureKind.Busy;

            case unchecked((int)0x80010006): // RPC_E_CONNECTION_TERMINATED
            case unchecked((int)0x80010108): // RPC_E_DISCONNECTED
            case unchecked((int)0x800401FD): // CO_E_OBJNOTCONNECTED
            case unchecked((int)0x80010007): // RPC_E_SERVER_DIED
            case unchecked((int)0x80010012): // RPC_E_SERVER_DIED_DNE
            case unchecked((int)0x800706BA): // RPC_S_SERVER_UNAVAILABLE
                return ExplorerComFailureKind.Disconnected;

            default:
                return ExplorerComFailureKind.Other;
        }
    }

    public static int GetRetryDelayMilliseconds(int failedAttemptIndex)
    {
        switch (failedAttemptIndex)
        {
            case 0: return 25;
            case 1: return 75;
            case 2: return 150;
            default: return 0;
        }
    }
}

public static class ShellRegistrationTimeoutPolicy
{
    private const int ColdStartMilliseconds = 3_000;
    private const int MinimumMilliseconds = 2_000;
    private const int MaximumMilliseconds = 4_000;
    private const int BackgroundNavigationMilliseconds = 8_000;

    public static int CalculateMaximumMilliseconds(
        IEnumerable<int> recentDurationsMilliseconds,
        bool backgroundNavigation = false)
    {
        if (recentDurationsMilliseconds == null)
            throw new ArgumentNullException(nameof(recentDurationsMilliseconds));

        var sorted = recentDurationsMilliseconds
            .Where(duration => duration > 0)
            .OrderBy(duration => duration)
            .ToArray();
        if (backgroundNavigation) return BackgroundNavigationMilliseconds;
        if (sorted.Length == 0) return ColdStartMilliseconds;

        var p95Index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
        var predicted = Math.Min((long)MaximumMilliseconds, (long)sorted[p95Index] * 2);
        return (int)Math.Max(MinimumMilliseconds, predicted);
    }
}
