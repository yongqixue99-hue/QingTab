using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using QingTab.Helpers;
using QingTab.Interop;
using QingTab.WinAPI;

namespace QingTab.Hooks;

public enum ExplorerConnectionState
{
    Disabled,
    Connecting,
    Ready,
    Reconnecting,
    Unavailable
}

public sealed class ExplorerConnectionStatus
{
    public ExplorerConnectionStatus(ExplorerConnectionState state, string displayText)
    {
        State = state;
        DisplayText = displayText ?? throw new ArgumentNullException(nameof(displayText));
    }

    public ExplorerConnectionState State { get; }
    public string DisplayText { get; }
    public bool IsReady => State == ExplorerConnectionState.Ready;
}

/// <summary>
/// Connects QingTab's direct Folder-open requests to an already-open Explorer
/// window. It deliberately does not watch, hide, or convert new top-level
/// Explorer windows, so Windows' native opennewtab/opennewwindow verbs remain
/// untouched.
/// </summary>
public sealed class ExplorerWatcher : IDisposable
{
    private static Guid ShellBrowserGuid = typeof(IShellBrowser).GUID;

    private readonly SynchronizationContext _syncContext;
    private readonly object _processLock = new();
    private readonly object _preCommandGate = new();
    private readonly object _registrationTimingLock = new();
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private readonly Queue<int> _recentRegistrationDurations = new();
    private readonly ExplorerOperationLifetime _operationLifetime;
    private readonly int _ownerThreadId;

    private object? _shellApp;
    private object? _shellWindows;
    private Process? _mainExplorerProcess;
    private int _mainExplorerProcessId;
    private int _initializationPending;
    private int _enabled;
    private Timer? _explorerCheckTimer;
    private int _disposed;
    private int _disposeRequested;
    private int _disableRequested;
#if QINGTAB_EXPERIMENTAL
    private int _visualMaskPrewarmPending;
#endif
    private bool _connectionResetPending;

    public event Action<ExplorerConnectionStatus>? StatusChanged;
    public ExplorerConnectionStatus Status { get; private set; } = new(
        ExplorerConnectionState.Connecting,
        "○ 正在连接文件资源管理器…");

    public ExplorerWatcher(bool enabled = true)
    {
        _syncContext = SynchronizationContext.Current
                       ?? throw new InvalidOperationException("ExplorerWatcher 必须在 UI 线程中创建。");
        _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        _operationLifetime = new ExplorerOperationLifetime(enabled);
        if (enabled)
        {
            Volatile.Write(ref _enabled, 1);
            StartExplorerProcessCheck();
        }
        else
        {
            Status = new ExplorerConnectionStatus(
                ExplorerConnectionState.Disabled,
                "○ 新标签接管已关闭：未连接文件资源管理器");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposeRequested) != 0) return;
        if (!enabled)
        {
            lock (_preCommandGate)
                Volatile.Write(ref _disableRequested, 1);
        }
        if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
        {
            _syncContext.Post(_ => SetEnabledCore(enabled), null);
            return;
        }

        SetEnabledCore(enabled);
    }

    private void SetEnabledCore(bool enabled)
    {
        if (Volatile.Read(ref _disposeRequested) != 0
            || Volatile.Read(ref _disposed) != 0)
            return;

        if (enabled)
        {
            if (Interlocked.Exchange(ref _enabled, 1) != 0) return;
            bool connectionReady;
            bool waitingForRetirement;
            lock (_processLock)
            {
                waitingForRetirement = _connectionResetPending;
                if (!waitingForRetirement)
                    _operationLifetime.Activate();
                connectionReady = !waitingForRetirement
                                  && _mainExplorerProcessId != 0
                                  && _shellApp != null
                                  && _shellWindows != null;
            }
            lock (_preCommandGate)
                Volatile.Write(ref _disableRequested, 0);

            if (connectionReady)
            {
                SetStatus(
                    ExplorerConnectionState.Ready,
                    "● 已就绪：等待普通文件夹打开请求");
            }
            else
            {
                SetStatus(
                    waitingForRetirement
                        ? ExplorerConnectionState.Reconnecting
                        : ExplorerConnectionState.Connecting,
                    waitingForRetirement
                        ? "○ 正在等待旧请求结束并重新连接…"
                        : "○ 正在连接文件资源管理器…");
                if (!waitingForRetirement)
                    StartExplorerProcessCheck();
            }
            return;
        }

        if (Interlocked.Exchange(ref _enabled, 0) == 0) return;
        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;
        RetiredShellState? retiredState = null;
        var cleanupAuthorized = false;
        lock (_processLock)
        {
            // The same pending flag also protects a normal off/on transition:
            // re-enable must not create a new Shell RCW while FinalRelease is
            // still pumping messages for the exact retired bundle.
            _connectionResetPending = true;
            cleanupAuthorized = _operationLifetime.Retire();
            if (cleanupAuthorized)
            {
                retiredState = DetachShellStateUnderLock();
            }
        }
        DisposeRetiredShellState(retiredState);
        var shouldReconnect = cleanupAuthorized
                              && FinalizeConnectionResetAfterRelease();
        if (shouldReconnect)
            StartExplorerProcessCheck();

        if (Volatile.Read(ref _enabled) == 0)
            SetStatus(
                ExplorerConnectionState.Disabled,
                "○ 新标签接管已关闭：未连接文件资源管理器");
    }

    /// <summary>
    /// Creates and navigates a tab in an already-open Explorer window without
    /// first creating a temporary top-level Explorer window.
    /// </summary>
    public async Task<OpenTabResult> OpenPathInNewTabAsync(
        string path,
        nint preferredWindow = 0,
        OpenTabOperationTrace? trace = null,
        RequestTimeBudget? timeBudget = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return OpenTabResult.Failed(OpenTabResultKind.InvalidRequest);
        if (Volatile.Read(ref _disposeRequested) != 0
            || Volatile.Read(ref _disposed) != 0)
            return OpenTabResult.Failed(OpenTabResultKind.Disposed);
        if (Volatile.Read(ref _disableRequested) != 0
            || Volatile.Read(ref _enabled) == 0)
            return OpenTabResult.Failed(OpenTabResultKind.FeatureDisabled);

        // Shell.Application, ShellWindows and every tab item belong to the
        // WinForms owner STA. Fail closed if a future caller bypasses that
        // contract; no COM object is ever touched from a worker thread.
        if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
            return OpenTabResult.Failed(OpenTabResultKind.ExplorerUnavailable);

        var budget = timeBudget ?? new RequestTimeBudget(
            TimeSpan.FromMilliseconds(RequestTimeBudget.DefaultTimeoutMilliseconds));
        if (budget.IsExpired)
            return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);

        // Diagnostic setup happens before a lifetime ticket exists. Once a
        // ticket is counted, control immediately enters the try/finally that
        // guarantees CompleteOperationLifetime.
        var debug025 = Debug025Trace.TryCreate();
        if (!_operationLifetime.TryBegin(out var operationTicket)
            || operationTicket == null)
        {
            debug025?.Flush();
            return OpenTabResult.Failed(
                Volatile.Read(ref _disposeRequested) != 0
                || Volatile.Read(ref _disposed) != 0
                    ? OpenTabResultKind.Disposed
                    : Volatile.Read(ref _enabled) == 0
                        ? OpenTabResultKind.FeatureDisabled
                        : OpenTabResultKind.ExplorerUnavailable);
        }

        var lockTaken = false;
        try
        {
            // A tray instance launched by the Shell may receive its first
            // request just before the asynchronous connection has completed.
            // The lifetime ticket is deliberately acquired first so disable,
            // restart and dispose cannot retire shared COM objects underneath
            // this wait or the operation that follows it.
            var initializationStartedAt = Stopwatch.GetTimestamp();
            var initializationTimeout = budget.LimitMilliseconds(8_000);
            while ((_shellApp == null || _shellWindows == null)
                   && Volatile.Read(ref _enabled) != 0
                   && Volatile.Read(ref _disposed) == 0
                   && _operationLifetime.IsCurrent(operationTicket)
                   && !budget.IsExpired
                   && !Helper.IsTimeUp(initializationStartedAt, initializationTimeout))
            {
                var delay = budget.LimitMilliseconds(20);
                if (delay == 0) break;
                await Task.Delay(delay);
            }

            if (Volatile.Read(ref _disposed) != 0)
                return OpenTabResult.Failed(OpenTabResultKind.Disposed);
            if (Volatile.Read(ref _enabled) == 0)
                return OpenTabResult.Failed(OpenTabResultKind.FeatureDisabled);
            if (!_operationLifetime.IsCurrent(operationTicket))
                return OpenTabResult.Failed(OpenTabResultKind.ExplorerUnavailable);
            if (budget.IsExpired)
                return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);
            if (_shellApp == null || _shellWindows == null)
                return OpenTabResult.Failed(OpenTabResultKind.ExplorerUnavailable);

            var lockTimeout = budget.RemainingMilliseconds;
            if (lockTimeout == 0 || !await _openLock.WaitAsync(lockTimeout))
                return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);
            lockTaken = true;

            if (Volatile.Read(ref _disposed) != 0)
                return OpenTabResult.Failed(OpenTabResultKind.Disposed);
            if (Volatile.Read(ref _enabled) == 0
                || !_operationLifetime.IsCurrent(operationTicket))
                return OpenTabResult.Failed(OpenTabResultKind.FeatureDisabled);
            if (budget.IsExpired)
                return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);
            if (_shellApp == null || _shellWindows == null)
                return OpenTabResult.Failed(OpenTabResultKind.ExplorerUnavailable);

            trace?.Mark(OpenTabStage.ExplorerReady);

            var targetWindow = GetPreferredTargetWindow(preferredWindow);
            debug025?.Mark("target-window-found");
            if (targetWindow == 0)
                return OpenTabResult.Failed(OpenTabResultKind.TargetWindowUnavailable);

            debug025?.Mark(
                $"window-target-{unchecked((long)targetWindow):X}");
            return await OpenPathInResponsiveNewTabAsync(
                path,
                targetWindow,
                trace,
                budget,
                operationTicket,
                debug025);
        }
        catch (COMException ex) when (IsPermanentShellDisconnect(ex.HResult))
        {
            ResetShellConnectionAfterPermanentDisconnect(ex);
            return OpenTabResult.Failed(OpenTabResultKind.ShellDisconnected, ex.HResult);
        }
        catch (COMException ex)
        {
            ErrorLog.Write(ex, "open-tab-com-failed");
            return OpenTabResult.Failed(OpenTabResultKind.NavigationFailed, ex.HResult);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "open-tab-direct-failed");
            return OpenTabResult.Failed(OpenTabResultKind.NavigationFailed);
        }
        finally
        {
            try
            {
                if (lockTaken)
                    _openLock.Release();
            }
            finally
            {
                try
                {
                    CompleteOperationLifetime(operationTicket);
                }
                finally
                {
                    debug025?.Flush();
                }
            }
        }
    }

    /// <summary>
    /// The latency-first 0.2.4 pipeline. It posts Explorer's native new-tab
    /// command immediately, binds the resulting Shell tab and submits the
    /// requested path. It deliberately performs no UIA capture, visual mask,
    /// old-tab restoration or post-navigation ReadyState wait.
    /// </summary>
    private async Task<OpenTabResult> OpenPathInResponsiveNewTabAsync(
        string path,
        nint targetWindow,
        OpenTabOperationTrace? trace,
        RequestTimeBudget budget,
        ExplorerOperationTicket operationTicket,
        Debug025Trace? debug025)
    {
        object? createdTabItem = null;
        var keepCreatedTab = false;
        try
        {
            debug025?.Mark("responsive-native-direct");
            var foregroundRootAtStart = WinApi.GetAncestor(
                WinApi.GetForegroundWindow(),
                WinApi.GA_ROOT);
            var currentTabs = Helper.GetAllExplorerTabs(targetWindow).ToArray();
            if (budget.IsExpired)
                return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);

            var commandPosted = false;
            lock (_preCommandGate)
            {
                if (CanPerformPreCommandSideEffect(operationTicket))
                    commandPosted = RequestToOpenNewTab(targetWindow);
            }

            if (!commandPosted)
                return OpenTabResult.Failed(
                    Volatile.Read(ref _enabled) == 0
                    || !_operationLifetime.IsCurrent(operationTicket)
                        ? OpenTabResultKind.FeatureDisabled
                        : OpenTabResultKind.PrivateTabCommandUnavailable);

            trace?.Mark(OpenTabStage.TabCommandSent);
            debug025?.Mark("responsive-tab-command-sent");

            var tabHandleTimeout = budget.LimitMilliseconds(2_000);
            if (tabHandleTimeout == 0)
                return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(
                targetWindow,
                currentTabs,
                searchTimeMs: tabHandleTimeout,
                sleepMs: 5);
            if (newTabHandle == 0)
            {
                return OpenTabResult.Failed(
                    budget.IsExpired
                        ? OpenTabResultKind.RequestTimedOut
                        : OpenTabResultKind.TabHandleTimedOut);
            }

            trace?.Mark(OpenTabStage.TabHandleFound);
            debug025?.Mark("responsive-tab-found");

            var registrationStartedAt = Stopwatch.GetTimestamp();
            createdTabItem = await WaitForTabItemAsync(
                newTabHandle,
                budget,
                GetShellRegistrationTimeoutMilliseconds(
                    backgroundNavigation: false));
            if (createdTabItem == null)
            {
                return OpenTabResult.Failed(
                    Volatile.Read(ref _enabled) == 0
                        ? OpenTabResultKind.FeatureDisabled
                        : budget.IsExpired
                            ? OpenTabResultKind.RequestTimedOut
                            : OpenTabResultKind.ShellRegistrationTimedOut);
            }

            RecordShellRegistrationDuration(registrationStartedAt);
            trace?.Mark(OpenTabStage.ShellRegistrationFound);
            debug025?.Mark("responsive-shell-found");

            if (budget.IsExpired)
                return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);
            trace?.Mark(OpenTabStage.NavigationStarted);
            var navigation = await NavigateExactWithDispositionAsync(
                createdTabItem,
                path,
                budget,
                validateExactIdentityBeforeRetry: () =>
                    WinApi.IsWindow(newTabHandle)
                    && WinApi.GetAncestor(newTabHandle, WinApi.GA_ROOT) == targetWindow
                    && GetTabHandle(createdTabItem) == newTabHandle);
            trace?.Mark(OpenTabStage.NavigationCompleted);
            debug025?.Mark("responsive-navigation-" + navigation.Disposition);

            keepCreatedTab = !ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                navigation.Disposition,
                navigation.ExactIdentityPreserved);
            if (navigation.FailureKind == ExplorerComFailureKind.Disconnected
                && navigation.HResult.HasValue)
            {
                ResetShellConnectionAfterPermanentDisconnect(
                    new COMException(
                        "Explorer navigation connection disconnected.",
                        navigation.HResult.Value));
            }

            if (navigation.Disposition == ExplorerNavigationDisposition.Unknown)
            {
                return OpenTabResult.Suppressed(
                    OpenTabResultKind.NavigationOutcomeUnknown,
                    navigation.HResult);
            }

            if (!navigation.ExactIdentityPreserved)
                return OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened);

            if (navigation.Disposition == ExplorerNavigationDisposition.KnownRejected)
            {
                return OpenTabResult.Failed(
                    OpenTabResultKind.ShellBusy,
                    navigation.HResult);
            }

            if (navigation.Disposition != ExplorerNavigationDisposition.Accepted)
            {
                return OpenTabResult.Failed(
                    navigation.FailureKind == ExplorerComFailureKind.Disconnected
                        ? OpenTabResultKind.ShellDisconnected
                        : OpenTabResultKind.NavigationFailed,
                    navigation.HResult);
            }

            var foregroundRootNow = WinApi.GetAncestor(
                WinApi.GetForegroundWindow(),
                WinApi.GA_ROOT);
            if (foregroundRootNow == targetWindow
                || foregroundRootNow == foregroundRootAtStart)
                WinApi.RestoreWindowToForeground(targetWindow);

            keepCreatedTab = true;
            return OpenTabResult.Opened();
        }
        finally
        {
            if (createdTabItem != null)
            {
                if (!keepCreatedTab)
                {
                    try
                    {
                        ((dynamic)createdTabItem).Quit();
                    }
                    catch
                    {
                        // Best effort: only the tab matched by the new native HWND
                        // is eligible for cleanup.
                    }
                }

                ReleaseComObject(createdTabItem);
            }
        }
    }

#if QINGTAB_EXPERIMENTAL
    private async Task<OpenTabResult> OpenPathInActiveNewTabAsync(
        string path,
        nint targetWindow,
        OpenTabOperationTrace? trace,
        RequestTimeBudget budget,
        ExplorerOperationTicket operationTicket,
        Debug025Trace? debug025,
        Func<bool>? knownDuplicateRequest)
    {
        var presentationMode = ExplorerOpenExperiencePolicy.DefaultMode;
        ExplorerVisualMaskLease? visualMask = null;
        ExplorerTabSelectionSnapshot? selection = null;
        ExplorerTabNativeOwnershipLease? ownership = null;
        object? createdTabItem = null;
        var disposition = ExplorerNavigationDisposition.NotIssued;
        var fallbackFinalized = false;
        var tabMovedToBackground = false;

        try
        {
            if (ExplorerOpenExperiencePolicy.UsesVisualMask(presentationMode))
            {
                ExplorerVisualMaskBounds? maskBounds;
                if (ExplorerOpenExperiencePolicy.RestoresOriginalDuringNavigation(
                        presentationMode))
                {
                    selection = ExplorerTabSelectionSnapshot.TryCapture(targetWindow);
                    debug025?.Mark("selection-captured");
                    maskBounds = selection != null
                                 && selection.TryGetVisualMaskBounds(out var capturedBounds)
                        ? capturedBounds
                        : null;
                }
                else
                {
                    var boundsTimeout = budget.LimitMilliseconds(350);
                    if (boundsTimeout == 0)
                        return OpenTabResult.Failed(OpenTabResultKind.RequestTimedOut);
                    maskBounds = await ExplorerVisualMaskBoundsCapture.TryCaptureAsync(
                        targetWindow,
                        boundsTimeout,
                        CancellationToken.None);
                }

                if (!CanPerformPreCommandSideEffect(operationTicket))
                    return GetPreCommandStopResult();

                var visualMaskHardTimeoutMilliseconds =
                    ExplorerOpenExperiencePolicy.GetVisualMaskHardTimeoutMilliseconds(
                        presentationMode);
                var maskAvailable = maskBounds.HasValue
                                    && ExplorerVisualMaskLease.TryStart(
                                        targetWindow,
                                        maskBounds.Value,
                                        visualMaskHardTimeoutMilliseconds,
                                        ExplorerOpenExperiencePolicy.GetVisualMaskAppearance(
                                            presentationMode),
                                        ExplorerVisualMaskPresentation.CreateLoadingMessage(path),
                                        out visualMask)
                                    && visualMask != null;
                presentationMode =
                    ExplorerOpenExperiencePolicy.ResolveAfterVisualMaskAttempt(
                        presentationMode,
                        maskAvailable);
                if (maskAvailable)
                {
                    debug025?.Mark(
                        "active-mask-presented-ttl-" + visualMaskHardTimeoutMilliseconds);
                }
                else
                {
                    debug025?.Mark("visual-mask-unavailable-native-degrade");
                }
            }
            else
            {
                debug025?.Mark("responsive-native");
            }

            ownership = await TryCaptureStableNativeOwnershipAsync(
                targetWindow,
                budget);
            if (!CanPerformPreCommandSideEffect(operationTicket))
                return GetPreCommandStopResult();
            if (ownership == null)
                return OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened);
            if (selection != null
                && selection.InitialCount != ownership.InitialTabCount)
                return OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened);

            if (visualMask != null && !visualMask.TryRenew()
                || !ExplorerNativeTabSnapshotCapture.TryCapture(
                    targetWindow,
                    out var preCommand)
                || !ownership.CanPostCommand(preCommand))
                return OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened);

            var commandPosted = false;
            lock (_preCommandGate)
            {
                if (CanPerformPreCommandSideEffect(operationTicket))
                    commandPosted = PostNewTabCommand(ownership.OriginalTabHandle);
            }
            if (!commandPosted)
                return OpenTabResult.Failed(
                    OpenTabResultKind.PrivateTabCommandUnavailable);
            trace?.Mark(OpenTabStage.TabCommandSent);
            debug025?.Mark("active-tab-command-sent");

            var claim = await WaitForOwnedNativeTabAsync(
                ownership,
                targetWindow,
                budget,
                maximumMilliseconds: 2_000,
                // Once the command is posted, retiring the feature must not
                // strand a delayed blank tab. Native identity/input guards,
                // rather than the pre-command ticket gate, decide whether a
                // best-effort exact claim may safely finish.
                continueWaiting: () => true,
                allowInputChangeFromKnownDuplicateRequest: () =>
                    ExplorerOpenExperiencePolicy.AllowsKnownDuplicateRequestInput(
                        presentationMode)
                    && knownDuplicateRequest?.Invoke() == true);
            debug025?.Mark("active-native-claim-" + claim.Decision);
            if (claim.Decision == ExplorerNativeTabClaimDecision.UserIntervened
                || claim.Decision == ExplorerNativeTabClaimDecision.Unsafe)
                return OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened);
            if (claim.Decision != ExplorerNativeTabClaimDecision.Claimed
                || claim.TabHandle == 0)
                return OpenTabResult.Suppressed(
                    OpenTabResultKind.TabCreationOutcomeUnknown);
            trace?.Mark(OpenTabStage.TabHandleFound);
            debug025?.Mark("active-native-claimed");

            // Restore the old native tab as soon as the new child is causally
            // claimed. Shell registration may still take seconds, but Explorer
            // stays usable and the blank default page remains covered for the
            // only interval in which it could be visible.
            if (ExplorerOpenExperiencePolicy.RestoresOriginalDuringNavigation(
                    presentationMode)
                && ExplorerOpenExperiencePolicy.RestoresOriginalBeforeNavigationSubmission(
                    presentationMode)
                && selection != null)
            {
                tabMovedToBackground = await TryMoveNewTabToBackgroundAsync(
                    targetWindow,
                    ownership.OriginalTabHandle,
                    claim.TabHandle,
                    selection,
                    visualMask,
                    budget,
                    debug025);
                debug025?.Mark("background-result-" + tabMovedToBackground);
                if (tabMovedToBackground)
                    visualMask = null;
            }

            var expectedActiveDuringNavigationSubmission = tabMovedToBackground
                ? ownership.OriginalTabHandle
                : ownership.ClaimedTabHandle;

            var registrationStartedAt = Stopwatch.GetTimestamp();
            createdTabItem = await WaitForExactTabItemAsync(
                targetWindow,
                ownership,
                budget,
                GetShellRegistrationTimeoutMilliseconds(tabMovedToBackground),
                expectedActiveDuringNavigationSubmission);
            if (createdTabItem == null)
            {
                return ownership.ActivationRevoked
                    ? OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened)
                    : OpenTabResult.Suppressed(
                        OpenTabResultKind.TabCreationOutcomeUnknown);
            }
            RecordShellRegistrationDuration(registrationStartedAt);
            trace?.Mark(OpenTabStage.ShellRegistrationFound);
            debug025?.Mark("active-exact-shell-claimed");

            if (!TryObserveExactComOwnership(
                    createdTabItem,
                    targetWindow,
                    ownership,
                    expectedActiveDuringNavigationSubmission))
                return OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened);

            trace?.Mark(OpenTabStage.NavigationStarted);
            debug025?.Mark("active-navigate-start");
            var navigation = await NavigateExactWithDispositionAsync(
                createdTabItem,
                path,
                budget,
                validateExactIdentityBeforeRetry: () =>
                    TryObserveExactComOwnership(
                        createdTabItem,
                        targetWindow,
                        ownership,
                        expectedActiveDuringNavigationSubmission));
            disposition = navigation.Disposition;
            trace?.Mark(OpenTabStage.NavigationCompleted);
            debug025?.Mark("active-navigate-" + disposition);

            if (ExplorerOpenExperiencePolicy.RestoresOriginalDuringNavigation(
                    presentationMode)
                && !ExplorerOpenExperiencePolicy.RestoresOriginalBeforeNavigationSubmission(
                    presentationMode)
                && selection != null)
            {
                tabMovedToBackground = await TryMoveNewTabToBackgroundAsync(
                    targetWindow,
                    ownership.OriginalTabHandle,
                    claim.TabHandle,
                    selection,
                    visualMask,
                    budget,
                    debug025);
                debug025?.Mark("background-result-" + tabMovedToBackground);
                if (tabMovedToBackground)
                    visualMask = null;
            }

            if (navigation.FailureKind == ExplorerComFailureKind.Disconnected
                && navigation.HResult.HasValue)
                ResetShellConnectionAfterPermanentDisconnect(
                    new COMException(
                        "Explorer navigation connection disconnected.",
                        navigation.HResult.Value));

            if (disposition == ExplorerNavigationDisposition.Unknown)
            {
                // The cross-process call may already have been accepted, so a
                // second top-level fallback would risk opening the folder twice.
                AbandonVisualMaskToTimeout(ref visualMask);
                return OpenTabResult.Suppressed(
                    OpenTabResultKind.NavigationOutcomeUnknown,
                    navigation.HResult);
            }
            if (disposition == ExplorerNavigationDisposition.KnownRejected)
            {
                var finalized = await FinalizeFallbackableFailureAsync(
                    OpenTabResult.Failed(
                        OpenTabResultKind.ShellBusy,
                        navigation.HResult),
                    createdTabItem,
                    targetWindow,
                    ownership,
                    disposition,
                    navigation.ExactIdentityPreserved);
                fallbackFinalized = true;
                if (finalized.AbandonMask && visualMask != null)
                {
                    visualMask.AbandonToTimeout();
                    visualMask = null;
                }
                return finalized.Result;
            }
            if (disposition != ExplorerNavigationDisposition.Accepted)
            {
                var finalized = await FinalizeFallbackableFailureAsync(
                    OpenTabResult.Failed(
                        navigation.FailureKind == ExplorerComFailureKind.Disconnected
                            ? OpenTabResultKind.ShellDisconnected
                            : OpenTabResultKind.NavigationFailed,
                        navigation.HResult),
                    createdTabItem,
                    targetWindow,
                    ownership,
                    disposition,
                    navigation.ExactIdentityPreserved);
                fallbackFinalized = true;
                if (finalized.AbandonMask && visualMask != null)
                {
                    visualMask.AbandonToTimeout();
                    visualMask = null;
                }
                return finalized.Result;
            }

            if (!ExplorerOpenExperiencePolicy.WaitsForTargetReady(presentationMode))
            {
                if (!ExplorerNativeTabSnapshotCapture.TryCapture(
                        targetWindow,
                        out var responsiveFinalSnapshot)
                    || !ownership.ObserveActivationIntent(
                        responsiveFinalSnapshot,
                        ownership.ClaimedTabHandle))
                    return OpenTabResult.OpenedInBackground();

                return OpenTabResult.Opened();
            }

            var readyResult = await WaitForNavigationReadyWithFailureAsync(
                createdTabItem,
                path,
                budget,
                maximumMilliseconds: 5_000,
                continueWaiting: () => tabMovedToBackground
                    ? IsBackgroundNavigationTabAlive(targetWindow, ownership)
                    : IsPostNavigationUserIntentUnchanged(
                        targetWindow,
                        ownership));
            debug025?.Mark("active-ready-" + readyResult.IsReady);
            if (readyResult.FailureKind == ExplorerComFailureKind.Disconnected
                && readyResult.HResult.HasValue)
                ResetShellConnectionAfterPermanentDisconnect(
                    new COMException(
                        "Explorer readiness connection disconnected.",
                        readyResult.HResult.Value));

            if (tabMovedToBackground)
            {
                bool FinalActivationGuard()
                {
                    return ExplorerNativeTabSnapshotCapture.TryCapture(
                               targetWindow,
                               out var backgroundFinalSnapshot)
                           && ownership.ObserveActivationIntent(
                               backgroundFinalSnapshot,
                               ownership.OriginalTabHandle);
                }

                if (!readyResult.IsReady || selection == null || !FinalActivationGuard())
                    return OpenTabResult.OpenedInBackground();

                var fastOrdinalSelection =
                    ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                        selection.InitialCount)
                    && selection.InitialCount == ownership.InitialTabCount;
                var selectionPosted = fastOrdinalSelection
                    ? selection.TrySelectAppendedNewTabFast(FinalActivationGuard)
                    : selection.TryRememberNewTab()
                      && selection.TrySelectNewTabIfUserIntentIsUnchanged(
                          FinalActivationGuard);
                debug025?.Mark(
                    fastOrdinalSelection
                        ? "new-tab-fast-selection-posted-" + selectionPosted
                        : "new-tab-uia-selection-posted-" + selectionPosted);
                if (!selectionPosted
                    || !await WaitForSelectedNewTabAsync(
                        targetWindow,
                        ownership.ClaimedTabHandle,
                        selection,
                        budget,
                        maximumMilliseconds: 750,
                        requireUiaSelection: !fastOrdinalSelection))
                    return OpenTabResult.OpenedInBackground();

                debug025?.Mark("new-tab-selected");
                return OpenTabResult.Opened();
            }

            if (!ExplorerVisualNavigationPolicy.ShouldReleaseVisualMaskImmediately(
                    disposition,
                    readyResult.IsReady))
            {
                AbandonVisualMaskToTimeout(ref visualMask);
                return OpenTabResult.OpenedInBackground();
            }

            // Only a confirmed target view may remove the old rendered page
            // early. Every uncertain result leaves cleanup to the independent
            // input/focus/geometry guards and absolute fail-open deadline.
            debug025?.Mark("active-mask-release-ready");
            visualMask?.Dispose();
            visualMask = null;

            if (!ExplorerNativeTabSnapshotCapture.TryCapture(
                    targetWindow,
                    out var finalSnapshot)
                || !ownership.ObserveActivationIntent(
                    finalSnapshot,
                    ownership.ClaimedTabHandle))
                return OpenTabResult.OpenedInBackground();

            return OpenTabResult.Opened();
        }
        catch (COMException ex) when (IsPermanentShellDisconnect(ex.HResult))
        {
            if (disposition == ExplorerNavigationDisposition.Accepted
                || disposition == ExplorerNavigationDisposition.Unknown)
                AbandonVisualMaskToTimeout(ref visualMask);
            ResetShellConnectionAfterPermanentDisconnect(ex);
            if (disposition == ExplorerNavigationDisposition.Accepted)
                return OpenTabResult.OpenedInBackground();
            if (disposition == ExplorerNavigationDisposition.Unknown)
                return OpenTabResult.Suppressed(
                    OpenTabResultKind.NavigationOutcomeUnknown,
                    ex.HResult);
            var finalized = await FinalizeFallbackableFailureAsync(
                OpenTabResult.Failed(
                    OpenTabResultKind.ShellDisconnected,
                    ex.HResult),
                createdTabItem,
                targetWindow,
                ownership,
                disposition,
                exactIdentityPreserved: true);
            fallbackFinalized = true;
            if (finalized.AbandonMask && visualMask != null)
            {
                visualMask.AbandonToTimeout();
                visualMask = null;
            }
            return finalized.Result;
        }
        catch (COMException ex)
        {
            if (disposition == ExplorerNavigationDisposition.Accepted
                || disposition == ExplorerNavigationDisposition.Unknown)
                AbandonVisualMaskToTimeout(ref visualMask);
            ErrorLog.Write(ex, "active-tab-open-com-failed");
            if (disposition == ExplorerNavigationDisposition.Accepted)
                return OpenTabResult.OpenedInBackground();
            if (disposition == ExplorerNavigationDisposition.Unknown)
                return OpenTabResult.Suppressed(
                    OpenTabResultKind.NavigationOutcomeUnknown,
                    ex.HResult);
            var finalized = await FinalizeFallbackableFailureAsync(
                OpenTabResult.Failed(
                    OpenTabResultKind.NavigationFailed,
                    ex.HResult),
                createdTabItem,
                targetWindow,
                ownership,
                disposition,
                exactIdentityPreserved: true);
            fallbackFinalized = true;
            if (finalized.AbandonMask && visualMask != null)
            {
                visualMask.AbandonToTimeout();
                visualMask = null;
            }
            return finalized.Result;
        }
        catch (Exception ex)
        {
            if (disposition == ExplorerNavigationDisposition.Accepted
                || disposition == ExplorerNavigationDisposition.Unknown)
                AbandonVisualMaskToTimeout(ref visualMask);
            ErrorLog.Write(ex, "active-tab-open-failed");
            if (disposition == ExplorerNavigationDisposition.Accepted)
                return OpenTabResult.OpenedInBackground();
            if (disposition == ExplorerNavigationDisposition.Unknown)
                return OpenTabResult.Suppressed(
                    OpenTabResultKind.NavigationOutcomeUnknown);
            var finalized = await FinalizeFallbackableFailureAsync(
                OpenTabResult.Failed(OpenTabResultKind.NavigationFailed),
                createdTabItem,
                targetWindow,
                ownership,
                disposition,
                exactIdentityPreserved: true);
            fallbackFinalized = true;
            if (finalized.AbandonMask && visualMask != null)
            {
                visualMask.AbandonToTimeout();
                visualMask = null;
            }
            return finalized.Result;
        }
        finally
        {
            try
            {
                if (createdTabItem != null
                    && ownership != null
                    && !fallbackFinalized
                    && ExplorerNavigationDispositionPolicy.ShouldOpenFallback(disposition))
                {
                    var closeRequested = TryCloseExactUnnavigatedTab(
                        createdTabItem,
                        targetWindow,
                        ownership,
                        disposition);
                    var disappeared = closeRequested
                                      && await WaitForNativeTabToDisappearAsync(
                                          targetWindow,
                                          ownership.ClaimedTabHandle,
                                          maximumMilliseconds: 250);
                    if (!disappeared && visualMask != null)
                    {
                        visualMask.AbandonToTimeout();
                        visualMask = null;
                    }
                }
            }
            finally
            {
                ReleaseComObject(createdTabItem);
                visualMask?.Dispose();
            }
        }
    }

    private static void AbandonVisualMaskToTimeout(
        ref ExplorerVisualMaskLease? visualMask)
    {
        if (visualMask == null) return;
        visualMask.AbandonToTimeout();
        visualMask = null;
    }

#endif

    private bool CanPerformPreCommandSideEffect(ExplorerOperationTicket operationTicket)
    {
        return Volatile.Read(ref _disposeRequested) == 0
               && Volatile.Read(ref _disableRequested) == 0
               && Volatile.Read(ref _disposed) == 0
               && Volatile.Read(ref _enabled) != 0
               && _operationLifetime.IsCurrent(operationTicket);
    }

    private OpenTabResult GetPreCommandStopResult()
    {
        if (Volatile.Read(ref _disableRequested) != 0)
            return OpenTabResult.Failed(OpenTabResultKind.FeatureDisabled);
        if (Volatile.Read(ref _disposeRequested) != 0
            || Volatile.Read(ref _disposed) != 0)
            return OpenTabResult.Failed(OpenTabResultKind.Disposed);
        if (Volatile.Read(ref _enabled) == 0)
            return OpenTabResult.Failed(OpenTabResultKind.FeatureDisabled);
        return OpenTabResult.Failed(OpenTabResultKind.ExplorerUnavailable);
    }

#if QINGTAB_EXPERIMENTAL
    private bool TryObserveExactComOwnership(
        object item,
        nint targetWindow,
        ExplorerTabNativeOwnershipLease ownership,
        nint expectedActiveTabHandle)
    {
        if (!ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var current))
            return false;

        // This permanently records user takeover, but exact HWND/COM ownership
        // remains usable for a safe background navigation after the claim.
        ownership.ObserveActivationIntent(
            current,
            expectedActiveTabHandle);
        var actualTab = GetTabHandle(item);
        var actualTopLevel = WinApi.GetAncestor(actualTab, WinApi.GA_ROOT);
        return ownership.CanUseExactComItem(actualTopLevel, actualTab, current);
    }

    private static bool TryObserveFallbackPermission(
        nint targetWindow,
        ExplorerTabNativeOwnershipLease ownership)
    {
        return ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var current)
               && ownership.ObserveActivationIntent(
                   current,
                   ownership.ClaimedTabHandle);
    }

    private static bool IsPostNavigationUserIntentUnchanged(
        nint targetWindow,
        ExplorerTabNativeOwnershipLease ownership)
    {
        return VisualMaskNative.TryGetLastInputTick(out var lastInputTick)
               && lastInputTick == ownership.BaselineLastInputTick
               && WinApi.GetAncestor(WinApi.GetForegroundWindow(), WinApi.GA_ROOT)
               == targetWindow
               && WinApi.FindWindowEx(
                   targetWindow,
                   0,
                   "ShellTabWindowClass",
                   null) == ownership.ClaimedTabHandle;
    }

    private static bool IsBackgroundNavigationTabAlive(
        nint targetWindow,
        ExplorerTabNativeOwnershipLease ownership)
    {
        return targetWindow != 0
               && ownership.ClaimedTabHandle != 0
               && WinApi.IsWindow(targetWindow)
               && WinApi.IsWindow(ownership.ClaimedTabHandle);
    }

    private async Task<FallbackFinalizationResult>
        FinalizeFallbackableFailureAsync(
            OpenTabResult provisionalResult,
            object? createdTabItem,
            nint targetWindow,
            ExplorerTabNativeOwnershipLease? ownership,
            ExplorerNavigationDisposition disposition,
            bool exactIdentityPreserved)
    {
        if (!provisionalResult.ShouldOpenFallback
            || !ExplorerNavigationDispositionPolicy.ShouldOpenFallback(disposition))
            return new FallbackFinalizationResult(
                provisionalResult,
                abandonMask: false);

        var closeRequested = false;
        var disappeared = false;
        if (createdTabItem != null
            && ownership != null
            && ownership.IsOwnershipClaimed)
        {
            closeRequested = TryCloseExactUnnavigatedTab(
                createdTabItem,
                targetWindow,
                ownership,
                disposition);
            if (closeRequested)
                disappeared = await WaitForNativeTabToDisappearAsync(
                    targetWindow,
                    ownership.ClaimedTabHandle,
                    maximumMilliseconds: 250);
        }

        var finalOwnershipIsSafe = exactIdentityPreserved;
        if (ownership != null && ownership.IsOwnershipClaimed)
        {
            if (disappeared)
            {
                finalOwnershipIsSafe = ExplorerNativeTabSnapshotCapture.TryCapture(
                                           targetWindow,
                                           out var afterClose)
                                       && ownership.CanOpenFallbackAfterOwnedTabClosed(
                                           afterClose);
            }
            else
            {
                finalOwnershipIsSafe = finalOwnershipIsSafe
                                       && TryObserveFallbackPermission(
                                           targetWindow,
                                           ownership);
            }
        }

        var result = ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
            disposition,
            finalOwnershipIsSafe)
            ? provisionalResult
            : OpenTabResult.Suppressed(OpenTabResultKind.UserIntervened);
        return new FallbackFinalizationResult(
            result,
            abandonMask: ownership?.IsOwnershipClaimed == true && !disappeared);
    }

#endif

    private void StartExplorerProcessCheck()
    {
        if (Volatile.Read(ref _disableRequested) != 0
            || Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _enabled) == 0)
            return;

        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 0, 1_000);
    }

    private void CheckForMainExplorer(object? state)
    {
        if (Volatile.Read(ref _disableRequested) != 0
            || Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _enabled) == 0)
            return;

        using var process = Helper.GetMainExplorerProcess();
        if (process == null) return;
        var processId = process.Id;

        if (Interlocked.CompareExchange(ref _initializationPending, 1, 0) != 0)
            return;

        _syncContext.Post(_ =>
        {
            try
            {
                if (Volatile.Read(ref _disableRequested) != 0
                    || Volatile.Read(ref _disposeRequested) != 0
                    || Volatile.Read(ref _disposed) != 0
                    || Volatile.Read(ref _enabled) == 0)
                    return;

                RetiredShellState? previousState = null;
                var alreadyReady = false;
                lock (_processLock)
                {
                    alreadyReady = _mainExplorerProcessId != 0
                                   && _shellApp != null
                                   && _shellWindows != null;
                    if (!alreadyReady)
                        previousState = DetachShellStateUnderLock();
                }
                DisposeRetiredShellState(previousState);

                if (alreadyReady)
                {
                    _explorerCheckTimer?.Dispose();
                    _explorerCheckTimer = null;
                    SetStatus(
                        ExplorerConnectionState.Ready,
                        "● 已就绪：等待普通文件夹打开请求");
                    return;
                }

                if (Volatile.Read(ref _disableRequested) != 0
                    || Volatile.Read(ref _disposeRequested) != 0
                    || Volatile.Read(ref _disposed) != 0
                    || Volatile.Read(ref _enabled) == 0)
                    return;

                var candidateState = CreateShellState(processId);
                var attached = false;
                lock (_processLock)
                {
                    if (Volatile.Read(ref _disableRequested) == 0
                        && Volatile.Read(ref _disposeRequested) == 0
                        && Volatile.Read(ref _disposed) == 0
                        && Volatile.Read(ref _enabled) != 0
                        && !_connectionResetPending
                        && _mainExplorerProcess == null
                        && _shellApp == null
                        && _shellWindows == null)
                    {
                        AttachShellStateUnderLock(candidateState);
                        attached = true;
                    }
                }
                if (!attached)
                {
                    DisposeRetiredShellState(candidateState);
                    return;
                }

                _explorerCheckTimer?.Dispose();
                _explorerCheckTimer = null;
                SetStatus(
                    ExplorerConnectionState.Ready,
                    "● 已就绪：等待普通文件夹打开请求");
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "shell-initialization-failed");
                RetiredShellState? failedState;
                lock (_processLock)
                    failedState = DetachShellStateUnderLock();
                DisposeRetiredShellState(failedState);
                if (Volatile.Read(ref _disableRequested) == 0
                    && Volatile.Read(ref _disposeRequested) == 0
                    && Volatile.Read(ref _disposed) == 0
                    && Volatile.Read(ref _enabled) != 0)
                {
                    SetStatus(
                        ExplorerConnectionState.Unavailable,
                        "● 暂时不可用，正在重新连接…");
                    StartExplorerProcessCheck();
                }
            }
            finally
            {
                Volatile.Write(ref _initializationPending, 0);
            }
        }, null);
    }

    private RetiredShellState CreateShellState(int processId)
    {
        Process? process = null;
        object? shellApp = null;
        object? shellWindows = null;
        try
        {
            process = Process.GetProcessById(processId);
            process.EnableRaisingEvents = true;
            process.Exited += OnExplorerProcessTerminated;

            var shellType = Type.GetTypeFromProgID("Shell.Application")
                            ?? throw new InvalidOperationException("无法连接 Windows Shell。");
            shellApp = Activator.CreateInstance(shellType)
                       ?? throw new InvalidOperationException("无法创建 Windows Shell COM 对象。");
            shellWindows = (object?)((dynamic)shellApp).Windows()
                           ?? throw new InvalidOperationException(
                               "ShellWindows collection is unavailable.");
            return new RetiredShellState(
                process,
                processId,
                shellApp,
                shellWindows);
        }
        catch
        {
            DisposeRetiredShellState(new RetiredShellState(
                process,
                processId,
                shellApp,
                shellWindows));
            throw;
        }
    }

    private object? FindTabItem(nint expectedTabHandle)
    {
        if (_shellWindows == null) return null;

        try
        {
            var count = (int)((dynamic)_shellWindows).Count;
            // ShellWindows appends a newly-created Explorer tab. Searching
            // backwards avoids walking every older tab on each polling pass.
            for (var index = count - 1; index >= 0; index--)
            {
                object? item = null;
                var matched = false;
                try
                {
                    item = (object)((dynamic)_shellWindows).Item(index);
                    matched = GetTabHandle(item) == expectedTabHandle;
                    if (matched)
                        return item;
                }
                catch (COMException ex) when (IsPermanentShellDisconnect(ex.HResult))
                {
                    throw;
                }
                catch
                {
                    // A Shell window may disappear while the collection is read.
                }
                finally
                {
                    if (item != null && !matched)
                        ReleaseComObject(item);
                }
            }
        }
        catch (COMException ex) when (IsPermanentShellDisconnect(ex.HResult))
        {
            throw;
        }
        catch
        {
            // ShellWindows can be temporarily unavailable during Explorer restart.
        }
        return null;
    }

    private static bool IsPermanentShellDisconnect(int hresult)
    {
        return ExplorerComPolicy.Classify(hresult) == ExplorerComFailureKind.Disconnected;
    }

    private void ResetShellConnectionAfterPermanentDisconnect(COMException exception)
    {
        ErrorLog.Write(exception, "shell-windows-disconnected");
        if (Volatile.Read(ref _disposeRequested) != 0
            || Volatile.Read(ref _disposed) != 0)
            return;

        var shouldReportReconnect = Volatile.Read(ref _enabled) != 0;
        RequestConnectionReset();
        if (shouldReportReconnect
            && Volatile.Read(ref _disableRequested) == 0
            && Volatile.Read(ref _disposeRequested) == 0
            && Volatile.Read(ref _disposed) == 0
            && Volatile.Read(ref _enabled) != 0)
            SetStatus(
                ExplorerConnectionState.Reconnecting,
                "● Shell 连接已断开，正在重新连接…");
    }

    private static bool RequestToOpenNewTab(nint windowHandle)
    {
        var activeTabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (activeTabHandle == 0) return false;

        // Explorer has no documented public API for creating a tab. This is the
        // same internal WM_COMMAND used by the upstream utilities for Ctrl+T.
        return WinApi.PostMessage(activeTabHandle, WinApi.WM_COMMAND, 0xA21B, 0);
    }

#if QINGTAB_EXPERIMENTAL
    private static bool PostNewTabCommand(nint exactOriginalTabHandle)
    {
        return exactOriginalTabHandle != 0
               && WinApi.IsWindow(exactOriginalTabHandle)
               && WinApi.PostMessage(
                   exactOriginalTabHandle,
                   WinApi.WM_COMMAND,
                   0xA21B,
                   0);
    }

    private static async Task<ExplorerTabNativeOwnershipLease?>
        TryCaptureStableNativeOwnershipAsync(
            nint targetWindow,
            RequestTimeBudget budget)
    {
        if (!ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var first))
            return null;

        var delay = budget.LimitMilliseconds(8);
        if (delay == 0) return null;
        await Task.Delay(delay);

        return ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var second)
               && ExplorerTabNativeOwnershipLease.TryCreate(first, second, out var lease)
            ? lease
            : null;
    }

    private static async Task<NativeClaimResult> WaitForOwnedNativeTabAsync(
        ExplorerTabNativeOwnershipLease ownership,
        nint targetWindow,
        RequestTimeBudget budget,
        int maximumMilliseconds,
        Func<bool> continueWaiting,
        Func<bool>? allowInputChangeFromKnownDuplicateRequest = null)
    {
        const int knownDuplicateSignalGraceMilliseconds = 500;
        var timeout = budget.LimitMilliseconds(maximumMilliseconds);
        if (timeout == 0)
            return new NativeClaimResult(ExplorerNativeTabClaimDecision.Waiting, 0);

        var startedAt = Stopwatch.GetTimestamp();
        long inputChangeObservedAt = 0;
        while (!budget.IsExpired && !Helper.IsTimeUp(startedAt, timeout))
        {
            if (!continueWaiting())
                return new NativeClaimResult(
                    ExplorerNativeTabClaimDecision.UserIntervened,
                    0);
            if (!ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var first))
                return new NativeClaimResult(ExplorerNativeTabClaimDecision.Unsafe, 0);

            var delay = Math.Min(8, budget.RemainingMilliseconds);
            if (delay == 0) break;
            await Task.Delay(delay);

            if (!continueWaiting())
                return new NativeClaimResult(
                    ExplorerNativeTabClaimDecision.UserIntervened,
                    0);
            if (!ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var second))
                return new NativeClaimResult(ExplorerNativeTabClaimDecision.Unsafe, 0);

            var knownDuplicate = false;
            try
            {
                knownDuplicate = allowInputChangeFromKnownDuplicateRequest?.Invoke() == true;
            }
            catch
            {
                // A diagnostic/activity signal can only grant a narrow input
                // exception; failure to read it must fail closed.
            }

            var inputChanged = first.LastInputTick != ownership.BaselineLastInputTick
                               || second.LastInputTick != ownership.BaselineLastInputTick;
            if (inputChanged
                && first.TargetWindowIsForeground
                && second.TargetWindowIsForeground
                && !knownDuplicate)
            {
                if (inputChangeObservedAt == 0)
                    inputChangeObservedAt = Stopwatch.GetTimestamp();
                if (!Helper.IsTimeUp(
                        inputChangeObservedAt,
                        knownDuplicateSignalGraceMilliseconds))
                    continue;
            }

            var decision = ownership.TryClaimCreated(
                first,
                second,
                out var claimed,
                allowInputChangeFromKnownDuplicateRequest: knownDuplicate);
            if (decision != ExplorerNativeTabClaimDecision.Waiting)
                return new NativeClaimResult(decision, claimed);
        }

        return new NativeClaimResult(ExplorerNativeTabClaimDecision.Waiting, 0);
    }

    private object? FindExactTabItem(
        nint expectedTopLevelWindow,
        nint expectedTabHandle,
        ExplorerTabNativeOwnershipLease ownership)
    {
        if (_shellWindows == null) return null;

        try
        {
            var count = (int)((dynamic)_shellWindows).Count;
            for (var index = count - 1; index >= 0; index--)
            {
                object? item = null;
                var matched = false;
                try
                {
                    item = (object?)((dynamic)_shellWindows).Item(index);
                    if (item == null) continue;

                    var actualTab = GetTabHandle(item);
                    if (actualTab != expectedTabHandle) continue;

                    // IShellBrowser.GetWindow returns the globally unique tab
                    // child HWND. Its native root proves the owning Explorer
                    // frame without invoking the much slower IWebBrowser2.HWND
                    // property while a new tab is still initializing.
                    var actualTopLevel = WinApi.GetAncestor(actualTab, WinApi.GA_ROOT);
                    if (actualTopLevel != expectedTopLevelWindow) continue;

                    if (!ExplorerNativeTabSnapshotCapture.TryCapture(
                            expectedTopLevelWindow,
                            out var current)
                        || !ownership.CanUseExactComItem(
                            actualTopLevel,
                            actualTab,
                            current))
                        continue;

                    matched = true;
                    return item;
                }
                catch (COMException ex) when (IsPermanentShellDisconnect(ex.HResult))
                {
                    throw;
                }
                catch
                {
                    // The live ShellWindows collection may change mid-pass.
                }
                finally
                {
                    if (item != null && !matched)
                        ReleaseComObject(item);
                }
            }
        }
        catch (COMException ex) when (IsPermanentShellDisconnect(ex.HResult))
        {
            throw;
        }
        catch
        {
            // A later bounded polling pass may observe the registration.
        }

        return null;
    }

    private async Task<object?> WaitForExactTabItemAsync(
        nint targetWindow,
        ExplorerTabNativeOwnershipLease ownership,
        RequestTimeBudget budget,
        int maximumMilliseconds,
        nint expectedActiveTabHandle)
    {
        var timeout = budget.LimitMilliseconds(maximumMilliseconds);
        if (timeout == 0) return null;

        var startedAt = Stopwatch.GetTimestamp();
        while (!budget.IsExpired && !Helper.IsTimeUp(startedAt, timeout))
        {
            if (ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var current))
                ownership.ObserveActivationIntent(
                    current,
                    expectedActiveTabHandle);

            var item = FindExactTabItem(
                targetWindow,
                ownership.ClaimedTabHandle,
                ownership);
            if (item != null) return item;

            var elapsed = (Stopwatch.GetTimestamp() - startedAt)
                          * 1000.0 / Stopwatch.Frequency;
            var delay = Math.Min(
                elapsed < 250 ? 10 : 40,
                budget.RemainingMilliseconds);
            if (delay == 0) break;
            await Task.Delay(delay);
        }

        return null;
    }

#endif

    private static async Task<ExplorerNavigationSubmissionResult> NavigateExactWithDispositionAsync(
        object item,
        string path,
        RequestTimeBudget budget,
        Func<bool> validateExactIdentityBeforeRetry)
    {
        object? shell = null;
        object? folder = null;
        object navigationTarget = path;
        try
        {
            if (path.Contains("#") || path.Contains("%23"))
            {
                shell = CreateShell();
                folder = (object?)((dynamic)shell).NameSpace(path);
                if (folder == null)
                    return new ExplorerNavigationSubmissionResult(
                        ExplorerNavigationDisposition.NotIssued,
                        null);
                navigationTarget = folder;
            }

            return await ExplorerNavigationSubmission.SubmitAsync(
                () =>
                {
                    ((dynamic)item).Navigate2(navigationTarget);
                },
                budget,
                validateExactIdentityBeforeRetry);
        }
        catch (COMException ex)
        {
            return new ExplorerNavigationSubmissionResult(
                ExplorerNavigationDisposition.NotIssued,
                ex.HResult,
                ExplorerComPolicy.Classify(ex.HResult));
        }
        catch
        {
            return new ExplorerNavigationSubmissionResult(
                ExplorerNavigationDisposition.NotIssued,
                null);
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

#if QINGTAB_EXPERIMENTAL
    private bool TryCloseExactUnnavigatedTab(
        object item,
        nint targetWindow,
        ExplorerTabNativeOwnershipLease ownership,
        ExplorerNavigationDisposition disposition)
    {
        try
        {
            var actualTab = GetTabHandle(item);
            var actualTopLevel = WinApi.GetAncestor(actualTab, WinApi.GA_ROOT);
            if (!ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var current)
                || !ownership.CanCloseExactComItem(
                    actualTopLevel,
                    actualTab,
                    current,
                    disposition))
                return false;

            ((dynamic)item).Quit();
            return true;
        }
        catch
        {
            // Cleanup is optional; never guess through an identity failure.
            return false;
        }
    }

    private static async Task<bool> WaitForNativeTabToDisappearAsync(
        nint targetWindow,
        nint claimedTabHandle,
        int maximumMilliseconds)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (!Helper.IsTimeUp(startedAt, maximumMilliseconds))
        {
            if (!WinApi.IsWindow(targetWindow)) return true;
            if (ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var current)
                && !current.NativeTabHandles.Contains(claimedTabHandle))
                return true;
            await Task.Delay(8);
        }

        return !WinApi.IsWindow(targetWindow)
               || ExplorerNativeTabSnapshotCapture.TryCapture(targetWindow, out var final)
               && !final.NativeTabHandles.Contains(claimedTabHandle);
    }

    private readonly struct NativeClaimResult
    {
        public NativeClaimResult(ExplorerNativeTabClaimDecision decision, nint tabHandle)
        {
            Decision = decision;
            TabHandle = tabHandle;
        }

        public ExplorerNativeTabClaimDecision Decision { get; }
        public nint TabHandle { get; }
    }

    private readonly struct NavigationReadyResult
    {
        public NavigationReadyResult(
            bool isReady,
            int? hresult = null,
            ExplorerComFailureKind? failureKind = null)
        {
            IsReady = isReady;
            HResult = hresult;
            FailureKind = failureKind;
        }

        public bool IsReady { get; }
        public int? HResult { get; }
        public ExplorerComFailureKind? FailureKind { get; }
    }

    private readonly struct NavigationReadyObservation
    {
        public NavigationReadyObservation(
            bool isReady,
            int? hresult = null,
            ExplorerComFailureKind? failureKind = null)
        {
            IsReady = isReady;
            HResult = hresult;
            FailureKind = failureKind;
        }

        public bool IsReady { get; }
        public int? HResult { get; }
        public ExplorerComFailureKind? FailureKind { get; }
    }

    private readonly struct FallbackFinalizationResult
    {
        public FallbackFinalizationResult(
            OpenTabResult result,
            bool abandonMask)
        {
            Result = result;
            AbandonMask = abandonMask;
        }

        public OpenTabResult Result { get; }
        public bool AbandonMask { get; }
    }

    private static async Task<bool> TryMoveNewTabToBackgroundAsync(
        nint windowHandle,
        nint originalTabHandle,
        nint newTabHandle,
        ExplorerTabSelectionSnapshot selection,
        ExplorerVisualMaskLease? visualMask,
        RequestTimeBudget budget,
        Debug025Trace? debug025)
    {
        bool FailWithoutExposingAnUnrestoredPage()
        {
            visualMask?.AbandonToTimeout();
            return false;
        }

        var activationTimeout = budget.LimitMilliseconds(500);
        if (activationTimeout == 0) return FailWithoutExposingAnUnrestoredPage();

        var startedAt = Stopwatch.GetTimestamp();
        while (!budget.IsExpired && !Helper.IsTimeUp(startedAt, activationTimeout))
        {
            if (!WinApi.IsWindow(windowHandle) || !WinApi.IsWindow(newTabHandle))
                return FailWithoutExposingAnUnrestoredPage();

            if (GetActiveNativeTab(windowHandle) == newTabHandle)
            {
                debug025?.Mark("new-tab-native-active");
                break;
            }

            await Task.Delay(Math.Min(2, budget.RemainingMilliseconds));
        }

        if (GetActiveNativeTab(windowHandle) != newTabHandle)
            return FailWithoutExposingAnUnrestoredPage();
        debug025?.Mark("restore-original-start");
        if (!selection.TrySelectCapturedOriginalFast())
            return FailWithoutExposingAnUnrestoredPage();
        debug025?.Mark("restore-original-command-returned");

        var restoreTimeout = budget.LimitMilliseconds(500);
        if (restoreTimeout == 0) return FailWithoutExposingAnUnrestoredPage();
        startedAt = Stopwatch.GetTimestamp();
        long stableAt = 0;
        while (!budget.IsExpired && !Helper.IsTimeUp(startedAt, restoreTimeout))
        {
            var nativeOriginalIsActive = GetActiveNativeTab(windowHandle) == originalTabHandle;
            if (nativeOriginalIsActive)
            {
                if (stableAt == 0)
                {
                    stableAt = Stopwatch.GetTimestamp();
                    debug025?.Mark("original-native-active");
                }

                var stableMilliseconds = (int)Math.Floor(
                    (Stopwatch.GetTimestamp() - stableAt) * 1000.0
                    / Stopwatch.Frequency);
                if (ExplorerOpenExperiencePolicy.CanReleaseBackgroundTransitionMask(
                        originalNativeTabIsActive: true,
                        stableMilliseconds))
                {
                    debug025?.Mark("original-native-stable");
                    break;
                }
            }
            else
            {
                stableAt = 0;
            }

            var delay = Math.Min(8, budget.RemainingMilliseconds);
            if (delay == 0) break;
            await Task.Delay(delay);
        }

        var finalStableMilliseconds = stableAt == 0
            ? 0
            : (int)Math.Floor(
                (Stopwatch.GetTimestamp() - stableAt) * 1000.0
                / Stopwatch.Frequency);
        if (!ExplorerOpenExperiencePolicy.CanReleaseBackgroundTransitionMask(
                GetActiveNativeTab(windowHandle) == originalTabHandle,
                finalStableMilliseconds))
            return FailWithoutExposingAnUnrestoredPage();

        visualMask?.Dispose();
        debug025?.Mark("visual-mask-released");
        return true;
    }

    private static async Task<bool> WaitForSelectedNewTabAsync(
        nint windowHandle,
        nint newTabHandle,
        ExplorerTabSelectionSnapshot selection,
        RequestTimeBudget budget,
        int maximumMilliseconds,
        bool requireUiaSelection)
    {
        var timeout = budget.LimitMilliseconds(maximumMilliseconds);
        if (timeout == 0) return false;

        var startedAt = Stopwatch.GetTimestamp();
        while (!budget.IsExpired && !Helper.IsTimeUp(startedAt, timeout))
        {
            if (GetActiveNativeTab(windowHandle) == newTabHandle
                && (!requireUiaSelection || selection.IsNewTabSelected()))
                return true;
            await Task.Delay(Math.Min(8, budget.RemainingMilliseconds));
        }

        return GetActiveNativeTab(windowHandle) == newTabHandle
               && (!requireUiaSelection || selection.IsNewTabSelected());
    }

    private static nint GetActiveNativeTab(nint windowHandle)
    {
        return WinApi.FindWindowEx(
            windowHandle,
            0,
            "ShellTabWindowClass",
            null);
    }

    private static bool IsNativeUserIntentUnchanged(
        nint windowHandle,
        nint originalTabHandle,
        IReadOnlyCollection<nint> initialTabHandles,
        nint newTabHandle,
        bool assumeTargetForeground)
    {
        if (!WinApi.IsWindow(windowHandle)
            || !WinApi.IsWindow(originalTabHandle)
            || !WinApi.IsWindow(newTabHandle)
            || GetActiveNativeTab(windowHandle) != originalTabHandle)
            return false;

        var foregroundRoot = WinApi.GetAncestor(
            WinApi.GetForegroundWindow(),
            WinApi.GA_ROOT);
        if (!assumeTargetForeground && foregroundRoot != windowHandle)
            return false;

        var currentTabHandles = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (currentTabHandles.Length != initialTabHandles.Count + 1
            || currentTabHandles.Count(handle => handle == newTabHandle) != 1)
            return false;

        return initialTabHandles.All(initial =>
            currentTabHandles.Count(current => current == initial) == 1);
    }

    private static bool TryCaptureTabStripObservation(
        nint windowHandle,
        ExplorerTabSelectionSnapshot selection,
        bool assumeTargetForeground,
        out TabStripObservation observation)
    {
        var foregroundRoot = WinApi.GetAncestor(
            WinApi.GetForegroundWindow(),
            WinApi.GA_ROOT);
        return selection.TryObserve(
            GetActiveNativeTab(windowHandle),
            assumeTargetForeground || foregroundRoot == windowHandle,
            out observation);
    }

    private static bool TryObserveActivationLease(
        nint windowHandle,
        ExplorerTabSelectionSnapshot selection,
        ExplorerTabActivationLease activationLease,
        bool assumeTargetForeground,
        bool duringNavigation,
        out TabStripObservation observation)
    {
        if (!TryCaptureTabStripObservation(
                windowHandle,
                selection,
                assumeTargetForeground,
                out observation))
            return false;

        return duringNavigation
            ? activationLease.ObserveDuringNavigation(observation)
            : activationLease.CanActivateCreatedTab(observation);
    }

    private static Task<bool> BindCreatedTabIdentityAsync(
        nint windowHandle,
        nint newTabHandle,
        ExplorerTabSelectionSnapshot selection,
        ExplorerTabActivationLease activationLease,
        bool assumeTargetForeground,
        RequestTimeBudget budget)
    {
        return Task.Run(() =>
        {
            var timeout = budget.LimitMilliseconds(1_500);
            if (timeout == 0) return false;

            var startedAt = Stopwatch.GetTimestamp();
            while (!budget.IsExpired && !Helper.IsTimeUp(startedAt, timeout))
            {
                if (TryCaptureTabStripObservation(
                        windowHandle,
                        selection,
                        assumeTargetForeground,
                        out var observation)
                    && activationLease.TryBindCreatedTabAfterOriginalRestore(
                        newTabHandle,
                        observation))
                    return true;
                if (activationLease.IsActivationCancelled)
                    return false;

                var delay = Math.Min(4, budget.RemainingMilliseconds);
                if (delay == 0) break;
                Thread.Sleep(delay);
            }

            return false;
        });
    }

#endif

    private static nint GetPreferredTargetWindow(nint preferredWindow)
    {
        if (IsUsableTargetWindow(preferredWindow))
            return preferredWindow;

        var foregroundRoot = WinApi.GetAncestor(WinApi.GetForegroundWindow(), WinApi.GA_ROOT);
        if (IsUsableTargetWindow(foregroundRoot))
            return foregroundRoot;

        // FindWindowEx enumerates top-level windows in Z order, so the first
        // usable window is the most recently active practical fallback.
        return WinApi.FindAllWindowsEx("CabinetWClass")
            .FirstOrDefault(IsUsableTargetWindow);
    }

    private static bool IsUsableTargetWindow(nint handle)
    {
        return handle != 0
               && WinApi.IsWindow(handle)
               && WinApi.IsWindowVisible(handle)
               && !WinApi.IsWindowCloaked(handle)
               && WinApi.IsWindowHasClassName(handle, "CabinetWClass");
    }

    private nint GetTabHandle(object item)
    {
        if (item is not QingTab.Interop.IServiceProvider serviceProvider) return 0;

        var queryResult = serviceProvider.QueryService(
            ref ShellBrowserGuid,
            ref ShellBrowserGuid,
            out var shellBrowser);
        if (queryResult < 0)
        {
            if (IsPermanentShellDisconnect(queryResult))
                throw new COMException("Explorer ShellBrowser service disconnected.", queryResult);
            return 0;
        }
        if (shellBrowser == null) return 0;

        try
        {
            var getWindowResult = shellBrowser.GetWindow(out nint handle);
            if (getWindowResult < 0)
            {
                if (IsPermanentShellDisconnect(getWindowResult))
                    throw new COMException("Explorer ShellBrowser window disconnected.", getWindowResult);
                return 0;
            }
            return handle;
        }
        finally
        {
            Marshal.ReleaseComObject(shellBrowser);
        }
    }

    private async Task<object?> WaitForTabItemAsync(
        nint tabHandle,
        RequestTimeBudget budget,
        int maximumMilliseconds)
    {
        var timeoutMilliseconds = budget.LimitMilliseconds(maximumMilliseconds);
        if (timeoutMilliseconds == 0) return null;

        var startedAt = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _enabled) != 0
               && !budget.IsExpired
               && !Helper.IsTimeUp(startedAt, timeoutMilliseconds))
        {
            var item = FindTabItem(tabHandle);
            if (item != null) return item;

            var elapsedMilliseconds = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            var stepRemaining = Math.Max(0, timeoutMilliseconds - (int)elapsedMilliseconds);
            var delay = Math.Min(
                elapsedMilliseconds < 250 ? 10 : 40,
                Math.Min(stepRemaining, budget.RemainingMilliseconds));
            if (delay == 0) break;
            await Task.Delay(delay);
        }

        return budget.IsExpired || Volatile.Read(ref _enabled) == 0
            ? null
            : FindTabItem(tabHandle);
    }

    private int GetShellRegistrationTimeoutMilliseconds(bool backgroundNavigation)
    {
        lock (_registrationTimingLock)
        {
            return ShellRegistrationTimeoutPolicy.CalculateMaximumMilliseconds(
                _recentRegistrationDurations,
                backgroundNavigation);
        }
    }

    private void RecordShellRegistrationDuration(long startedAt)
    {
        var elapsed = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
        var milliseconds = Math.Max(1, (int)Math.Ceiling(elapsed));

        lock (_registrationTimingLock)
        {
            while (_recentRegistrationDurations.Count >= 20)
                _recentRegistrationDurations.Dequeue();
            _recentRegistrationDurations.Enqueue(milliseconds);
        }
    }

#if QINGTAB_EXPERIMENTAL
    private static async Task<NavigationReadyResult>
        WaitForNavigationReadyWithFailureAsync(
            object item,
            string path,
            RequestTimeBudget budget,
            int maximumMilliseconds,
            Func<bool>? continueWaiting = null)
    {
        var timeout = budget.LimitMilliseconds(maximumMilliseconds);
        if (timeout == 0) return new NavigationReadyResult(isReady: false);

        var startedAt = Stopwatch.GetTimestamp();
        var consecutiveReadySamples = 0;
        while (!budget.IsExpired && !Helper.IsTimeUp(startedAt, timeout))
        {
            if (continueWaiting != null && !continueWaiting())
                return new NavigationReadyResult(isReady: false);

            var observation = ObserveNavigationReady(item, path);
            if (observation.FailureKind == ExplorerComFailureKind.Disconnected)
                return new NavigationReadyResult(
                    isReady: false,
                    observation.HResult,
                    observation.FailureKind);

            consecutiveReadySamples = observation.IsReady
                ? consecutiveReadySamples + 1
                : 0;
            if (consecutiveReadySamples >= 2)
                return new NavigationReadyResult(isReady: true);

            var delay = Math.Min(16, budget.RemainingMilliseconds);
            if (delay == 0) break;
            await Task.Delay(delay);
        }

        return new NavigationReadyResult(isReady: false);
    }

    private static NavigationReadyObservation ObserveNavigationReady(
        object item,
        string expectedPath)
    {
        try
        {
            dynamic window = item;
            var locationUrl = (string?)window.LocationURL;
            if (!TryNormalizeLocation(locationUrl, out var actualLocation)
                || !TryNormalizeLocation(expectedPath, out var expectedLocation)
                || !string.Equals(
                    actualLocation,
                    expectedLocation,
                    StringComparison.OrdinalIgnoreCase))
                return new NavigationReadyObservation(
                    isReady: false);

            var isBusy = Convert.ToBoolean(window.Busy);
            var readyState = Convert.ToInt32(window.ReadyState);

            return new NavigationReadyObservation(
                !isBusy && readyState == 4);
        }
        catch (COMException ex)
        {
            return new NavigationReadyObservation(
                isReady: false,
                hresult: ex.HResult,
                failureKind: ExplorerComPolicy.Classify(ex.HResult));
        }
        catch
        {
            return new NavigationReadyObservation(isReady: false);
        }
    }

    private static bool TryNormalizeLocation(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : value;
            var fullPath = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetPathRoot(fullPath);
            normalized = string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryCloseCreatedTabItem(
        object item,
        nint expectedTopLevelWindow,
        nint expectedTabHandle,
        ExplorerTabSelectionSnapshot? selection,
        ExplorerTabActivationLease? activationLease)
    {
        if (expectedTopLevelWindow == 0
            || expectedTabHandle == 0)
            return;

        var currentNativeTabs = Helper.GetAllExplorerTabs(expectedTopLevelWindow).ToArray();
        if (currentNativeTabs.Length < 2) return;

        try
        {
            var actualTopLevelWindow = (nint)Convert.ToInt64(((dynamic)item).HWND);
            var actualTabHandle = GetTabHandle(item);
            if (actualTopLevelWindow != expectedTopLevelWindow
                || actualTabHandle != expectedTabHandle)
                return;

            if (activationLease != null)
            {
                if (selection == null
                    || !selection.TryObserve(
                        GetActiveNativeTab(expectedTopLevelWindow),
                        targetWindowIsForeground: false,
                        out var observation)
                    || !activationLease.CanCloseCreatedTab(
                        actualTopLevelWindow,
                        actualTabHandle,
                        currentNativeTabs,
                        observation.RuntimeIds))
                    return;
            }

            ((dynamic)item).Quit();
        }
        catch
        {
            // Never risk closing an unrelated tab or the last Explorer window.
        }
    }

#endif

    private static dynamic CreateShell()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
                        ?? throw new InvalidOperationException("无法连接 Windows Shell。");
        return (dynamic)(Activator.CreateInstance(shellType)
                         ?? throw new InvalidOperationException("无法创建 Windows Shell COM 对象。"));
    }

    private void OnExplorerProcessTerminated(object? sender, EventArgs eventArgs)
    {
        // A disabled watcher can still have an in-flight ticket retaining this
        // exact connection. Do not lose its exit notification: mark retirement
        // now, clean it after the final ticket, and reconnect only if enabled.
        if (Volatile.Read(ref _disposed) != 0) return;

        var terminatedProcess = sender as Process;
        _syncContext.Post(_ =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            var cleanupAuthorized = false;
            var shouldReportReconnect = false;
            RetiredShellState? retiredState = null;
            lock (_processLock)
            {
                if (!ReferenceEquals(terminatedProcess, _mainExplorerProcess)) return;

                cleanupAuthorized = RequestConnectionResetUnderLock(out retiredState);
                shouldReportReconnect = Volatile.Read(ref _enabled) != 0;
            }
            DisposeRetiredShellState(retiredState);
            var shouldReconnectNow = cleanupAuthorized
                                     && FinalizeConnectionResetAfterRelease();
            if (shouldReportReconnect
                && Volatile.Read(ref _disableRequested) == 0
                && Volatile.Read(ref _disposeRequested) == 0
                && Volatile.Read(ref _disposed) == 0
                && Volatile.Read(ref _enabled) != 0)
                SetStatus(
                    ExplorerConnectionState.Reconnecting,
                    "○ 文件资源管理器已重启，正在重新连接…");
            if (shouldReconnectNow)
                StartExplorerProcessCheck();
        }, null);
    }

    private void RequestConnectionReset()
    {
        var cleanupAuthorized = false;
        RetiredShellState? retiredState = null;
        lock (_processLock)
            cleanupAuthorized = RequestConnectionResetUnderLock(out retiredState);
        DisposeRetiredShellState(retiredState);
        var shouldReconnectNow = cleanupAuthorized
                                 && FinalizeConnectionResetAfterRelease();
        if (shouldReconnectNow)
            StartExplorerProcessCheck();
    }

    private bool RequestConnectionResetUnderLock(out RetiredShellState? retiredState)
    {
        retiredState = null;
        _connectionResetPending = true;
        if (!_operationLifetime.Retire()) return false;

        retiredState = DetachShellStateUnderLock();
        return true;
    }

    private bool FinalizeConnectionResetAfterRelease()
    {
        lock (_processLock)
        {
            if (!_connectionResetPending) return false;

            _connectionResetPending = false;
            if (Volatile.Read(ref _disposeRequested) != 0
                || Volatile.Read(ref _disposed) != 0
                || Volatile.Read(ref _enabled) == 0)
                return false;

            _operationLifetime.Activate();
            return true;
        }
    }

    private void SetStatus(ExplorerConnectionState state, string displayText)
    {
        var status = new ExplorerConnectionStatus(state, displayText);
        Status = status;
        StatusChanged?.Invoke(status);
    }

#if QINGTAB_EXPERIMENTAL
    private void ScheduleVisualMaskPrewarm()
    {
        if (Interlocked.CompareExchange(ref _visualMaskPrewarmPending, 1, 0) != 0)
            return;

        var targetWindow = GetPreferredTargetWindow(0);
        if (targetWindow == 0)
        {
            Volatile.Write(ref _visualMaskPrewarmPending, 0);
            return;
        }

        _ = PrewarmVisualMaskAsync(targetWindow);
    }

    private async Task PrewarmVisualMaskAsync(nint targetWindow)
    {
        try
        {
            // Read-only and deliberately longer than the request-time budget:
            // startup work happens off the UI thread and should populate the
            // cache before the user's first folder click, not compete with it.
            await ExplorerVisualMaskBoundsCapture.TryCaptureAsync(
                targetWindow,
                timeoutMilliseconds: 1_000,
                CancellationToken.None);
        }
        catch
        {
            // Masking is optional. The real request path will degrade to the
            // responsive native behavior if UIA is unavailable.
        }
        finally
        {
            Volatile.Write(ref _visualMaskPrewarmPending, 0);
        }
    }
#endif

    /// <summary>
    /// Atomically removes the shared connection from discoverable fields.
    /// The returned bundle must be released only after leaving _processLock so
    /// COM message pumping cannot re-enter a half-retired shared state.
    /// </summary>
    private RetiredShellState DetachShellStateUnderLock()
    {
        var state = new RetiredShellState(
            _mainExplorerProcess,
            _mainExplorerProcessId,
            _shellApp,
            _shellWindows);
        _mainExplorerProcess = null;
        _mainExplorerProcessId = 0;
        _shellApp = null;
        _shellWindows = null;
        return state;
    }

    private void AttachShellStateUnderLock(RetiredShellState state)
    {
        _mainExplorerProcess = state.ExplorerProcess;
        _mainExplorerProcessId = state.ExplorerProcessId;
        _shellApp = state.ShellApp;
        _shellWindows = state.ShellWindows;
        state.ClearOwnership();
    }

    private void DisposeRetiredShellState(RetiredShellState? state)
    {
        if (state == null) return;

        if (state.ExplorerProcess != null)
        {
            try
            {
                state.ExplorerProcess.Exited -= OnExplorerProcessTerminated;
            }
            catch
            {
                // The process may already have fully terminated.
            }
        }

        if (state.ShellWindows != null)
        {
            try
            {
                if (Marshal.IsComObject(state.ShellWindows))
                    Marshal.FinalReleaseComObject(state.ShellWindows);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "shell-windows-disposal-failed");
            }
        }

        if (state.ShellApp != null)
        {
            try
            {
                if (Marshal.IsComObject(state.ShellApp))
                    Marshal.FinalReleaseComObject(state.ShellApp);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "shell-disposal-failed");
            }
        }

        if (state.ExplorerProcess != null)
        {
            try
            {
                state.ExplorerProcess.Dispose();
            }
            catch
            {
                // The process may already have fully terminated.
            }
        }

        state.ClearOwnership();
    }

    private sealed class RetiredShellState
    {
        public RetiredShellState(
            Process? explorerProcess,
            int explorerProcessId,
            object? shellApp,
            object? shellWindows)
        {
            ExplorerProcess = explorerProcess;
            ExplorerProcessId = explorerProcessId;
            ShellApp = shellApp;
            ShellWindows = shellWindows;
        }

        public Process? ExplorerProcess { get; private set; }
        public int ExplorerProcessId { get; private set; }
        public object? ShellApp { get; private set; }
        public object? ShellWindows { get; private set; }

        public void ClearOwnership()
        {
            ExplorerProcess = null;
            ExplorerProcessId = 0;
            ShellApp = null;
            ShellWindows = null;
        }
    }

    private void CompleteOperationLifetime(ExplorerOperationTicket ticket)
    {
        RetiredShellState? retiredState = null;
        lock (_processLock)
        {
            if (!_operationLifetime.Complete(ticket)) return;

            retiredState = DetachShellStateUnderLock();
        }
        DisposeRetiredShellState(retiredState);
        var shouldReconnect = FinalizeConnectionResetAfterRelease();
        if (shouldReconnect)
            StartExplorerProcessCheck();
    }

    private static void ReleaseComObject(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch
        {
            // Explorer may revoke an object while a request is completing.
        }
    }

    public void Dispose()
    {
        var firstRequest = false;
        lock (_preCommandGate)
            firstRequest = Interlocked.Exchange(ref _disposeRequested, 1) == 0;
        if (!firstRequest) return;
        if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
        {
            _syncContext.Post(_ => DisposeCore(), null);
            GC.SuppressFinalize(this);
            return;
        }

        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _enabled, 0);

        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;

        RetiredShellState? retiredState = null;
        lock (_processLock)
        {
            if (_operationLifetime.Retire())
            {
                retiredState = DetachShellStateUnderLock();
                _connectionResetPending = false;
            }
        }
        DisposeRetiredShellState(retiredState);

        // SemaphoreSlim has no native resource that needs eager disposal. An
        // in-flight request may still execute its finally block and Release().
    }
}
