namespace QingTab.Hooks;

/// <summary>
/// Controls how QingTab presents Explorer's two-stage new-tab/navigation flow.
/// MaskedUntilReady keeps the newly created tab active while preserving the
/// previous rendered content until the requested folder is confirmed ready.
/// </summary>
public enum ExplorerOpenPresentationMode
{
    ResponsiveNative,
    MaskedUntilReady,
    BackgroundUntilReady
}

public static class ExplorerOpenExperiencePolicy
{
    public const int BackgroundTransitionMaskHardTimeoutMilliseconds = 2_000;

    // Two 60 Hz frames plus scheduling margin. This is intentionally a
    // release condition, not a fixed delay on the entire open operation.
    public const int BackgroundRestoreStabilityMilliseconds = 40;

    public static ExplorerOpenPresentationMode DefaultMode =>
        ExplorerOpenPresentationMode.ResponsiveNative;

    public static ExplorerVisualMaskAppearance GetVisualMaskAppearance(
        ExplorerOpenPresentationMode mode)
    {
        return mode == ExplorerOpenPresentationMode.MaskedUntilReady
            ? ExplorerVisualMaskAppearance.LoadingPlaceholder
            : ExplorerVisualMaskAppearance.Snapshot;
    }

    public static ExplorerOpenPresentationMode ResolveAfterVisualMaskAttempt(
        ExplorerOpenPresentationMode requestedMode,
        bool maskAvailable)
    {
        // Visual polish must never become a functional dependency. If UIA or
        // the transient overlay is unavailable, continue with the proven
        // native 0.2.4-style path instead of losing the Folder-open request.
        return UsesVisualMask(requestedMode) && !maskAvailable
            ? ExplorerOpenPresentationMode.ResponsiveNative
            : requestedMode;
    }

    public static int GetVisualMaskHardTimeoutMilliseconds(
        ExplorerOpenPresentationMode mode)
    {
        return mode == ExplorerOpenPresentationMode.BackgroundUntilReady
            ? BackgroundTransitionMaskHardTimeoutMilliseconds
            : ExplorerVisualMaskStopPolicy.MaximumHardTimeoutMilliseconds;
    }

    public static bool AllowsKnownDuplicateRequestInput(
        ExplorerOpenPresentationMode mode)
    {
        // The duplicate signal is produced by the request queue from another
        // identical Folder-open request. It describes the input's ownership,
        // not how the Explorer transition is presented.
        return mode == ExplorerOpenPresentationMode.ResponsiveNative
               || mode == ExplorerOpenPresentationMode.MaskedUntilReady
               || mode == ExplorerOpenPresentationMode.BackgroundUntilReady;
    }

    public static bool UsesVisualMask(ExplorerOpenPresentationMode mode)
    {
        return mode == ExplorerOpenPresentationMode.MaskedUntilReady
               || mode == ExplorerOpenPresentationMode.BackgroundUntilReady;
    }

    public static bool UsesDirectResponsivePipeline(
        ExplorerOpenPresentationMode mode)
    {
        return mode == ExplorerOpenPresentationMode.ResponsiveNative;
    }

    public static bool RequiresForegroundTarget(
        ExplorerOpenPresentationMode mode)
    {
        return !UsesDirectResponsivePipeline(mode);
    }

    public static bool ShouldPrewarmVisualMask(ExplorerOpenPresentationMode mode)
    {
        return UsesVisualMask(mode);
    }

    public static bool WaitsForTargetReady(ExplorerOpenPresentationMode mode)
    {
        return mode == ExplorerOpenPresentationMode.MaskedUntilReady
               || mode == ExplorerOpenPresentationMode.BackgroundUntilReady;
    }

    public static bool RestoresOriginalDuringNavigation(
        ExplorerOpenPresentationMode mode)
    {
        return mode == ExplorerOpenPresentationMode.BackgroundUntilReady;
    }

    public static bool RestoresOriginalBeforeNavigationSubmission(
        ExplorerOpenPresentationMode mode)
    {
        return mode == ExplorerOpenPresentationMode.BackgroundUntilReady;
    }

    public static bool CanReleaseBackgroundTransitionMask(
        bool originalNativeTabIsActive,
        int stableMilliseconds)
    {
        return originalNativeTabIsActive
               && stableMilliseconds >= BackgroundRestoreStabilityMilliseconds;
    }

    public static bool CanSelectAppendedNewTabWithoutUia(int initialTabCount)
    {
        return initialTabCount > 0
               && initialTabCount < int.MaxValue
               && CanSelectPrivateOrdinalWithoutUia(initialTabCount + 1);
    }

    public static bool CanSelectPrivateOrdinalWithoutUia(int oneBasedOrdinal)
    {
        // Current Explorer range-checks this private one-based ordinal against
        // its real tab count. Isolated native/UIA probes verified 10, 11, 12,
        // 20 and 36 (4 ms away, 101-133 ms restore), ruling out the documented
        // Ctrl+number shortcut's superficial nine-tab boundary.
        return oneBasedOrdinal > 0;
    }
}

public static class ExplorerVisualNavigationPolicy
{
    public static bool ShouldReleaseVisualMaskImmediately(
        ExplorerNavigationDisposition disposition,
        bool targetIsReady)
    {
        return disposition == ExplorerNavigationDisposition.Accepted
               && targetIsReady;
    }
}
