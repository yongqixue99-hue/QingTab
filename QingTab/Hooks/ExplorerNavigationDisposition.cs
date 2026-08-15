namespace QingTab.Hooks;

/// <summary>
/// Describes what is known after submitting a folder navigation to Explorer.
/// Unknown is deliberately distinct from rejection: a cross-process call may
/// have been accepted even when its caller observes an exception.
/// </summary>
public enum ExplorerNavigationDisposition
{
    NotIssued,
    KnownRejected,
    Unknown,
    Accepted
}

public static class ExplorerNavigationDispositionPolicy
{
    public static bool ShouldOpenFallback(ExplorerNavigationDisposition disposition)
    {
        return disposition == ExplorerNavigationDisposition.NotIssued
               || disposition == ExplorerNavigationDisposition.KnownRejected;
    }

    public static bool ShouldOpenFallback(
        ExplorerNavigationDisposition disposition,
        bool exactIdentityPreserved)
    {
        return exactIdentityPreserved && ShouldOpenFallback(disposition);
    }

}
