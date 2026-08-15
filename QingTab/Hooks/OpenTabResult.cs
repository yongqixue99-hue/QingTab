using System;

namespace QingTab.Hooks;

public enum OpenTabResultKind
{
    Opened,
    OpenedInBackground,
    UserIntervened,
    TabCreationOutcomeUnknown,
    NavigationOutcomeUnknown,
    InvalidRequest,
    FeatureDisabled,
    Disposed,
    RequestTimedOut,
    ExplorerUnavailable,
    TargetWindowUnavailable,
    VisualMaskUnavailable,
    PrivateTabCommandUnavailable,
    TabHandleTimedOut,
    ShellRegistrationTimedOut,
    ShellBusy,
    ShellDisconnected,
    NavigationFailed
}

/// <summary>
/// A stable, privacy-safe result for one direct Explorer-tab request.
/// It deliberately exposes a small failure code instead of exception text or paths.
/// </summary>
public sealed class OpenTabResult
{
    private OpenTabResult(OpenTabResultKind kind, int? hresult, bool shouldOpenFallback)
    {
        Kind = kind;
        HResult = hresult;
        ShouldOpenFallback = shouldOpenFallback;
    }

    public OpenTabResultKind Kind { get; }
    public int? HResult { get; }
    public bool ShouldOpenFallback { get; }
    public bool IsSuccess => Kind == OpenTabResultKind.Opened
                             || Kind == OpenTabResultKind.OpenedInBackground;
    public string FailureCode => GetFailureCode(Kind);

    public static OpenTabResult Opened() =>
        new(OpenTabResultKind.Opened, null, shouldOpenFallback: false);

    public static OpenTabResult OpenedInBackground() =>
        new(OpenTabResultKind.OpenedInBackground, null, shouldOpenFallback: false);

    public static OpenTabResult Suppressed(OpenTabResultKind kind, int? hresult = null)
    {
        if (kind != OpenTabResultKind.UserIntervened
            && kind != OpenTabResultKind.TabCreationOutcomeUnknown
            && kind != OpenTabResultKind.NavigationOutcomeUnknown)
            throw new ArgumentException(
                "Only user-intervention and unknown tab/navigation outcomes may suppress fallback.",
                nameof(kind));

        return new OpenTabResult(kind, hresult, shouldOpenFallback: false);
    }

    public static OpenTabResult Failed(OpenTabResultKind kind, int? hresult = null)
    {
        if (kind == OpenTabResultKind.Opened || kind == OpenTabResultKind.OpenedInBackground)
            throw new ArgumentException("成功结果必须通过 Opened() 创建。", nameof(kind));
        if (kind == OpenTabResultKind.UserIntervened
            || kind == OpenTabResultKind.TabCreationOutcomeUnknown
            || kind == OpenTabResultKind.NavigationOutcomeUnknown)
            throw new ArgumentException(
                "禁止回退的结果必须通过 Suppressed() 创建。",
                nameof(kind));

        return new OpenTabResult(kind, hresult, shouldOpenFallback: true);
    }

    private static string GetFailureCode(OpenTabResultKind kind)
    {
        switch (kind)
        {
            case OpenTabResultKind.Opened: return string.Empty;
            case OpenTabResultKind.OpenedInBackground: return string.Empty;
            case OpenTabResultKind.UserIntervened: return "user-intervened";
            case OpenTabResultKind.TabCreationOutcomeUnknown: return "tab-creation-outcome-unknown";
            case OpenTabResultKind.NavigationOutcomeUnknown: return "navigation-outcome-unknown";
            case OpenTabResultKind.InvalidRequest: return "invalid-request";
            case OpenTabResultKind.FeatureDisabled: return "feature-disabled";
            case OpenTabResultKind.Disposed: return "opener-disposed";
            case OpenTabResultKind.RequestTimedOut: return "request-timeout";
            case OpenTabResultKind.ExplorerUnavailable: return "explorer-unavailable";
            case OpenTabResultKind.TargetWindowUnavailable: return "target-window-unavailable";
            case OpenTabResultKind.VisualMaskUnavailable: return "visual-mask-unavailable";
            case OpenTabResultKind.PrivateTabCommandUnavailable: return "private-tab-command-unavailable";
            case OpenTabResultKind.TabHandleTimedOut: return "tab-handle-timeout";
            case OpenTabResultKind.ShellRegistrationTimedOut: return "shell-registration-timeout";
            case OpenTabResultKind.ShellBusy: return "shell-busy";
            case OpenTabResultKind.ShellDisconnected: return "shell-disconnected";
            case OpenTabResultKind.NavigationFailed: return "navigation-failed";
            default: return "unknown-open-tab-failure";
        }
    }
}
