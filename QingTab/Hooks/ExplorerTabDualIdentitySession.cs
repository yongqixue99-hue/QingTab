using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingTab.Helpers;
using QingTab.WinAPI;

namespace QingTab.Hooks;

public sealed class ExplorerTabDualIdentity
{
    internal ExplorerTabDualIdentity(nint nativeTabHandle, string runtimeId)
    {
        NativeTabHandle = nativeTabHandle;
        RuntimeId = runtimeId;
    }

    public nint NativeTabHandle { get; }
    public string RuntimeId { get; }
}

/// <summary>
/// Performs pre-navigation Explorer tab ownership binding without exposing any
/// AutomationElement or SelectionItemPattern outside one dedicated MTA. UIA
/// calls are bounded from the caller's perspective; a timeout poisons the one
/// dispatcher permanently so blocked provider calls cannot accumulate.
/// </summary>
public sealed class ExplorerTabDualIdentitySession
{
    private const int BindingStabilityMilliseconds = 16;
    private const int PollMilliseconds = 2;

    private static readonly BoundedUiaDispatcher UiaDispatcher =
        new BoundedUiaDispatcher();

    private readonly nint _window;
    private readonly ExplorerTabSelectionSnapshot _selection;
    private readonly ExplorerTabDualIdentityPolicy _policy;

    private ExplorerTabDualIdentity? _createdIdentity;
    private int _cancelled;
    private int _restorePosted;

    private ExplorerTabDualIdentitySession(
        nint window,
        ExplorerTabSelectionSnapshot selection,
        ExplorerTabDualIdentityPolicy policy)
    {
        _window = window;
        _selection = selection;
        _policy = policy;
        OriginalIdentity = new ExplorerTabDualIdentity(
            policy.OriginalNativeTabHandle,
            policy.OriginalRuntimeId);
    }

    public ExplorerTabDualIdentity OriginalIdentity { get; }
    public ExplorerTabDualIdentity? CreatedIdentity => _createdIdentity;
    public uint BaselineLastInputTick => _policy.BaselineLastInputTick;
    public bool CanPostOriginalRestore => _selection.OriginalIndex >= 0
                                          && ExplorerOpenExperiencePolicy
                                              .CanSelectPrivateOrdinalWithoutUia(
                                                  _selection.OriginalIndex + 1);
    public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;
    public bool IsRestorePosted => Volatile.Read(ref _restorePosted) != 0;

    /// <summary>
    /// Captures N1/UIA1/N2/UIA2/N3 and accepts the operation only when all
    /// native samples and both UIA identity/selection samples are identical.
    /// </summary>
    public static async Task<ExplorerTabDualIdentitySession?> TryCaptureInitialAsync(
        nint window,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (window == 0 || timeoutMilliseconds <= 0) return null;

        var dispatch = await UiaDispatcher.TryInvokeAsync(
                guard => TryCaptureInitialOnDispatcher(window, guard),
                timeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
        return dispatch.Completed ? dispatch.Value : null;
    }

    /// <summary>
    /// Waits for exactly one added HWND and one added runtime ID, requires two
    /// coherent active/selected samples, then holds that exact binding stable
    /// for one frame. A second Ctrl+T observed before completion cancels the
    /// session instead of guessing ownership.
    /// </summary>
    public async Task<ExplorerTabDualIdentity?> TryBindCreatedAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (timeoutMilliseconds <= 0
            || IsCancelled
            || Volatile.Read(ref _restorePosted) != 0)
            return null;

        var dispatch = await UiaDispatcher.TryInvokeAsync(
                guard => TryBindCreatedOnDispatcher(guard),
                timeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
        if (!dispatch.Completed)
        {
            Interlocked.Exchange(ref _cancelled, 1);
            return null;
        }

        return dispatch.Value;
    }

    /// <summary>
    /// Re-samples both technologies twice, checks the exact bound order, then
    /// performs one final UIA read and a generation/deadline/native guard at
    /// the last possible point before PostMessage. No timed-out work item can
    /// later post the restore command.
    /// </summary>
    public async Task<bool> TryPostOriginalRestoreAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (timeoutMilliseconds <= 0
            || IsCancelled
            || _createdIdentity == null
            || !CanPostOriginalRestore
            || Interlocked.CompareExchange(ref _restorePosted, 0, 0) != 0)
            return false;

        var dispatch = await UiaDispatcher.TryInvokeAsync(
                guard => TryPostOriginalRestoreOnDispatcher(guard),
                timeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
        if (!dispatch.Completed)
        {
            Interlocked.Exchange(ref _cancelled, 1);
            return Volatile.Read(ref _restorePosted) != 0;
        }

        return dispatch.Value;
    }

    private static ExplorerTabDualIdentitySession? TryCaptureInitialOnDispatcher(
        nint window,
        UiaOperationGuard guard)
    {
        if (!guard.CanContinue
            || !NativeTabSample.TryCapture(window, out var native1))
            return null;

        var selection = ExplorerTabSelectionSnapshot.TryCaptureIsolated(window);
        if (!guard.CanContinue
            || selection == null
            || !NativeTabSample.TryCapture(window, out var native2)
            || !native1.Equals(native2))
            return null;

        var initialRuntimeIds = selection.CopyInitialRuntimeIds();
        var first = new ExplorerTabIdentityPolicySnapshot(
            native1.Handles,
            native1.ActiveHandle,
            initialRuntimeIds,
            selection.OriginalRuntimeId,
            native1.IsForeground,
            native1.LastInputTick);

        if (!selection.TryObserve(
                native2.ActiveHandle,
                native2.IsForeground,
                out var secondObservation)
            || !guard.CanContinue
            || !NativeTabSample.TryCapture(window, out var native3)
            || !native2.Equals(native3))
            return null;

        var second = new ExplorerTabIdentityPolicySnapshot(
            native3.Handles,
            native3.ActiveHandle,
            secondObservation.RuntimeIds,
            secondObservation.SelectedRuntimeId,
            native3.IsForeground,
            native3.LastInputTick);
        if (!ExplorerTabDualIdentityPolicy.TryCreate(
                first,
                second,
                out var policy)
            || !guard.CanContinue)
            return null;

        return new ExplorerTabDualIdentitySession(window, selection, policy);
    }

    private ExplorerTabDualIdentity? TryBindCreatedOnDispatcher(
        UiaOperationGuard guard)
    {
        long firstBoundAt = 0;
        while (guard.CanContinue && Volatile.Read(ref _cancelled) == 0)
        {
            var capture = TryCapturePair(guard, out var first, out var second);
            if (capture == PairCaptureResult.Unsafe)
                return CancelSession();
            if (capture == PairCaptureResult.Retry)
            {
                firstBoundAt = 0;
                SleepBriefly(guard);
                continue;
            }

            var decision = _policy.ObserveForBinding(first, second, out var binding);
            if (decision == ExplorerTabIdentityPolicyDecision.Unsafe)
                return CancelSession();
            if (decision != ExplorerTabIdentityPolicyDecision.Bound || binding == null)
            {
                firstBoundAt = 0;
                SleepBriefly(guard);
                continue;
            }

            if (firstBoundAt == 0)
            {
                firstBoundAt = Stopwatch.GetTimestamp();
                SleepBriefly(guard);
                continue;
            }

            if (!Elapsed(firstBoundAt, BindingStabilityMilliseconds))
            {
                SleepBriefly(guard);
                continue;
            }

            if (!guard.CanContinue) return CancelSession();
            _createdIdentity = new ExplorerTabDualIdentity(
                binding.CreatedNativeTabHandle,
                binding.CreatedRuntimeId);
            return _createdIdentity;
        }

        return CancelSession();
    }

    private bool TryPostOriginalRestoreOnDispatcher(UiaOperationGuard guard)
    {
        if (!guard.CanContinue
            || Volatile.Read(ref _cancelled) != 0
            || _createdIdentity == null
            || _policy.Binding == null)
            return false;

        var capture = TryCapturePair(guard, out var first, out var second);
        if (capture != PairCaptureResult.Success
            || !_policy.TryAuthorizeOriginalRestore(first, second))
        {
            CancelSession();
            return false;
        }

        var expectedObservation = second.ToTabStripObservation();
        var binding = _policy.Binding;
        var posted = _selection.TryPostSelectOriginalIfObservationIsCurrent(
            expectedObservation,
            () => guard.CanCommitSideEffect
                  && Volatile.Read(ref _cancelled) == 0
                  && NativeStateMatchesBoundCreated(binding));
        if (!posted)
        {
            CancelSession();
            return false;
        }

        Interlocked.Exchange(ref _restorePosted, 1);
        return true;
    }

    private PairCaptureResult TryCapturePair(
        UiaOperationGuard guard,
        out ExplorerTabIdentityPolicySnapshot first,
        out ExplorerTabIdentityPolicySnapshot second)
    {
        first = null!;
        second = null!;
        var firstResult = TryCaptureOne(guard, out first);
        if (firstResult != PairCaptureResult.Success) return firstResult;

        var secondResult = TryCaptureOne(guard, out second);
        return secondResult;
    }

    private PairCaptureResult TryCaptureOne(
        UiaOperationGuard guard,
        out ExplorerTabIdentityPolicySnapshot snapshot)
    {
        snapshot = null!;
        if (!guard.CanContinue
            || !NativeTabSample.TryCapture(_window, out var before))
            return PairCaptureResult.Unsafe;

        if (!_selection.TryObserve(
                before.ActiveHandle,
                before.IsForeground,
                out var observation))
            return guard.CanContinue
                ? PairCaptureResult.Retry
                : PairCaptureResult.Unsafe;

        if (!guard.CanContinue
            || !NativeTabSample.TryCapture(_window, out var after)
            || !before.Equals(after))
            return PairCaptureResult.Unsafe;

        snapshot = new ExplorerTabIdentityPolicySnapshot(
            after.Handles,
            after.ActiveHandle,
            observation.RuntimeIds,
            observation.SelectedRuntimeId,
            after.IsForeground,
            after.LastInputTick);
        return PairCaptureResult.Success;
    }

    private bool NativeStateMatchesBoundCreated(
        ExplorerTabIdentityPolicyBinding binding)
    {
        return NativeTabSample.TryCapture(_window, out var current)
               && current.IsForeground
               && current.LastInputTick == _policy.BaselineLastInputTick
               && current.ActiveHandle == binding.CreatedNativeTabHandle
               && current.Handles.SequenceEqual(binding.BoundNativeOrder);
    }

    private ExplorerTabDualIdentity? CancelSession()
    {
        _policy.CancelOwnership();
        Interlocked.Exchange(ref _cancelled, 1);
        return null;
    }

    private static void SleepBriefly(UiaOperationGuard guard)
    {
        if (guard.CanContinue) Thread.Sleep(PollMilliseconds);
    }

    private static bool Elapsed(long startedAt, int milliseconds)
    {
        return (Stopwatch.GetTimestamp() - startedAt) * 1000.0
               / Stopwatch.Frequency >= milliseconds;
    }

    private enum PairCaptureResult
    {
        Success,
        Retry,
        Unsafe
    }

    private sealed class NativeTabSample : IEquatable<NativeTabSample>
    {
        private NativeTabSample(
            nint[] handles,
            nint activeHandle,
            bool isForeground,
            uint lastInputTick)
        {
            Handles = handles;
            ActiveHandle = activeHandle;
            IsForeground = isForeground;
            LastInputTick = lastInputTick;
        }

        public nint[] Handles { get; }
        public nint ActiveHandle { get; }
        public bool IsForeground { get; }
        public uint LastInputTick { get; }

        public static bool TryCapture(nint window, out NativeTabSample sample)
        {
            sample = null!;
            if (!WinApi.IsWindow(window)
                || !WinApi.IsWindowHasClassName(window, "CabinetWClass")
                || !VisualMaskNative.TryGetLastInputTick(out var lastInputTick))
                return false;

            var handles = Helper.GetAllExplorerTabs(window).ToArray();
            var active = WinApi.FindWindowEx(
                window,
                0,
                "ShellTabWindowClass",
                null);
            if (handles.Length == 0
                || active == 0
                || handles.Any(handle => handle == 0 || !WinApi.IsWindow(handle))
                || handles.Distinct().Count() != handles.Length
                || handles.Count(handle => handle == active) != 1)
                return false;

            var foregroundRoot = WinApi.GetAncestor(
                WinApi.GetForegroundWindow(),
                WinApi.GA_ROOT);
            sample = new NativeTabSample(
                handles,
                active,
                foregroundRoot == window,
                lastInputTick);
            return true;
        }

        public bool Equals(NativeTabSample? other)
        {
            return other != null
                   && ActiveHandle == other.ActiveHandle
                   && IsForeground == other.IsForeground
                   && LastInputTick == other.LastInputTick
                   && Handles.SequenceEqual(other.Handles);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as NativeTabSample);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = ActiveHandle.GetHashCode();
                hash = (hash * 397) ^ IsForeground.GetHashCode();
                hash = (hash * 397) ^ LastInputTick.GetHashCode();
                foreach (var handle in Handles)
                    hash = (hash * 397) ^ handle.GetHashCode();
                return hash;
            }
        }
    }
}

/// <summary>
/// A single process-wide UIA MTA. A provider hang sacrifices this dispatcher
/// for the remainder of the process instead of hanging the UI thread or
/// leaking one worker thread per folder open.
/// </summary>
internal sealed class BoundedUiaDispatcher
{
    private readonly BlockingCollection<IUiaWorkItem> _queue =
        new BlockingCollection<IUiaWorkItem>();
    private readonly Thread _thread;

    private int _poisoned;
    private int _lastGeneration;
    private int _activeGeneration;

    public BoundedUiaDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "QingTab bounded UIA MTA"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public async Task<UiaDispatchResult<T>> TryInvokeAsync<T>(
        Func<UiaOperationGuard, T> action,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (timeoutMilliseconds <= 0
            || cancellationToken.IsCancellationRequested
            || Volatile.Read(ref _poisoned) != 0)
            return UiaDispatchResult<T>.Failed;

        var generation = Interlocked.Increment(ref _lastGeneration);
        // Leave a small completion margin so a successful PostMessage cannot
        // race the caller-side timeout and be reported as an unknown outcome.
        var operationMilliseconds = Math.Max(1, timeoutMilliseconds - 12);
        var deadlineTicks = Stopwatch.GetTimestamp()
                            + (long)(operationMilliseconds
                                     * (double)Stopwatch.Frequency / 1000.0);
        var item = new UiaWorkItem<T>(
            this,
            generation,
            deadlineTicks,
            cancellationToken,
            action);

        try
        {
            _queue.Add(item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return UiaDispatchResult<T>.Failed;
        }
        catch (InvalidOperationException)
        {
            return UiaDispatchResult<T>.Failed;
        }

        var timeoutTask = Task.Delay(timeoutMilliseconds, cancellationToken);
        await Task.WhenAny(item.Task, timeoutTask).ConfigureAwait(false);
        if (item.Task.IsCompleted)
            return await item.Task.ConfigureAwait(false);

        Interlocked.Exchange(ref _poisoned, 1);
        return UiaDispatchResult<T>.Failed;
    }

    internal bool CanRun(int generation, long deadlineTicks, CancellationToken token)
    {
        return Volatile.Read(ref _poisoned) == 0
               && Volatile.Read(ref _activeGeneration) == generation
               && !token.IsCancellationRequested
               && Stopwatch.GetTimestamp() < deadlineTicks;
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (Volatile.Read(ref _poisoned) != 0)
            {
                item.Reject();
                continue;
            }

            Volatile.Write(ref _activeGeneration, item.Generation);
            try
            {
                item.Execute();
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _activeGeneration,
                    0,
                    item.Generation);
            }
        }
    }

    private interface IUiaWorkItem
    {
        int Generation { get; }
        void Execute();
        void Reject();
    }

    private sealed class UiaWorkItem<T> : IUiaWorkItem
    {
        private readonly BoundedUiaDispatcher _dispatcher;
        private readonly long _deadlineTicks;
        private readonly CancellationToken _cancellationToken;
        private readonly Func<UiaOperationGuard, T> _action;
        private readonly TaskCompletionSource<UiaDispatchResult<T>> _completion =
            new TaskCompletionSource<UiaDispatchResult<T>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public UiaWorkItem(
            BoundedUiaDispatcher dispatcher,
            int generation,
            long deadlineTicks,
            CancellationToken cancellationToken,
            Func<UiaOperationGuard, T> action)
        {
            _dispatcher = dispatcher;
            Generation = generation;
            _deadlineTicks = deadlineTicks;
            _cancellationToken = cancellationToken;
            _action = action;
        }

        public int Generation { get; }
        public Task<UiaDispatchResult<T>> Task => _completion.Task;

        public void Execute()
        {
            var guard = new UiaOperationGuard(
                _dispatcher,
                Generation,
                _deadlineTicks,
                _cancellationToken);
            if (!guard.CanContinue)
            {
                Reject();
                return;
            }

            try
            {
                var value = _action(guard);
                _completion.TrySetResult(
                    guard.CanContinue
                        ? UiaDispatchResult<T>.Succeeded(value)
                        : UiaDispatchResult<T>.Failed);
            }
            catch
            {
                _completion.TrySetResult(UiaDispatchResult<T>.Failed);
            }
        }

        public void Reject()
        {
            _completion.TrySetResult(UiaDispatchResult<T>.Failed);
        }
    }
}

internal sealed class UiaOperationGuard
{
    private readonly BoundedUiaDispatcher _dispatcher;
    private readonly int _generation;
    private readonly long _deadlineTicks;
    private readonly CancellationToken _cancellationToken;

    public UiaOperationGuard(
        BoundedUiaDispatcher dispatcher,
        int generation,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        _dispatcher = dispatcher;
        _generation = generation;
        _deadlineTicks = deadlineTicks;
        _cancellationToken = cancellationToken;
    }

    public bool CanContinue => _dispatcher.CanRun(
        _generation,
        _deadlineTicks,
        _cancellationToken);

    public bool CanCommitSideEffect => CanContinue;
}

internal readonly struct UiaDispatchResult<T>
{
    private UiaDispatchResult(bool completed, T value)
    {
        Completed = completed;
        Value = value;
    }

    public bool Completed { get; }
    public T Value { get; }

    public static UiaDispatchResult<T> Failed =>
        new UiaDispatchResult<T>(false, default!);

    public static UiaDispatchResult<T> Succeeded(T value)
    {
        return new UiaDispatchResult<T>(true, value);
    }
}
