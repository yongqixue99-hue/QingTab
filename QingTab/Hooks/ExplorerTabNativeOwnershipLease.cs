using System;
using System.Collections.Generic;
using System.Linq;

namespace QingTab.Hooks;

/// <summary>
/// One immutable native observation used to prove which Explorer child and
/// process generation an operation owns. Time and Win32 calls stay outside the
/// model so its race decisions are deterministic tests rather than heuristics.
/// </summary>
public sealed class ExplorerNativeTabSnapshot
{
    public ExplorerNativeTabSnapshot(
        nint targetWindowHandle,
        int processId,
        long processStartTimeUtcTicks,
        IEnumerable<nint> nativeTabHandles,
        nint activeNativeTabHandle,
        uint lastInputTick,
        bool targetWindowIsForeground)
    {
        if (nativeTabHandles == null)
            throw new ArgumentNullException(nameof(nativeTabHandles));

        TargetWindowHandle = targetWindowHandle;
        ProcessId = processId;
        ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
        NativeTabHandles = nativeTabHandles.ToArray();
        ActiveNativeTabHandle = activeNativeTabHandle;
        LastInputTick = lastInputTick;
        TargetWindowIsForeground = targetWindowIsForeground;
    }

    public nint TargetWindowHandle { get; }
    public int ProcessId { get; }
    public long ProcessStartTimeUtcTicks { get; }
    public IReadOnlyList<nint> NativeTabHandles { get; }
    public nint ActiveNativeTabHandle { get; }
    public uint LastInputTick { get; }
    public bool TargetWindowIsForeground { get; }
}

public enum ExplorerNativeTabClaimDecision
{
    Waiting,
    Claimed,
    UserIntervened,
    Unsafe
}

/// <summary>
/// Separates causal native ownership from later activation permission. User
/// input before the claim means QingTab cannot know which Ctrl+T won and must
/// stop. Input after a claim revokes focus changes but does not erase the exact
/// HWND and COM identity that may still be navigated safely in the background.
/// </summary>
public sealed class ExplorerTabNativeOwnershipLease
{
    private readonly nint _targetWindowHandle;
    private readonly int _processId;
    private readonly long _processStartTimeUtcTicks;
    private readonly nint[] _initialTabHandles;
    private readonly HashSet<nint> _initialTabSet;
    private readonly nint _originalTabHandle;
    private readonly uint _baselineLastInputTick;

    private nint _claimedTabHandle;
    private bool _activationRevoked;
    private bool _knownDuplicateInputAccepted;
    private bool _unsafe;

    private ExplorerTabNativeOwnershipLease(ExplorerNativeTabSnapshot initial)
    {
        _targetWindowHandle = initial.TargetWindowHandle;
        _processId = initial.ProcessId;
        _processStartTimeUtcTicks = initial.ProcessStartTimeUtcTicks;
        _initialTabHandles = initial.NativeTabHandles.ToArray();
        _initialTabSet = new HashSet<nint>(_initialTabHandles);
        _originalTabHandle = initial.ActiveNativeTabHandle;
        _baselineLastInputTick = initial.LastInputTick;
    }

    public nint ClaimedTabHandle => _claimedTabHandle;
    public nint OriginalTabHandle => _originalTabHandle;
    public int InitialTabCount => _initialTabHandles.Length;
    public uint BaselineLastInputTick => _baselineLastInputTick;
    public bool IsOwnershipClaimed => _claimedTabHandle != 0;
    public bool ActivationRevoked => _activationRevoked;
    public bool IsUnsafe => _unsafe;

    public static bool TryCreate(
        ExplorerNativeTabSnapshot first,
        ExplorerNativeTabSnapshot second,
        out ExplorerTabNativeOwnershipLease lease)
    {
        lease = null!;
        if (!IsValid(first)
            || !IsValid(second)
            || !first.TargetWindowIsForeground
            || !SnapshotsEqual(first, second))
            return false;

        lease = new ExplorerTabNativeOwnershipLease(second);
        return true;
    }

    /// <summary>
    /// Final side-effect guard immediately before QingTab posts its one private
    /// new-tab command. It never mutates the lease, so a stale observation can
    /// only deny the command and cannot be mistaken for ownership later.
    /// </summary>
    public bool CanPostCommand(ExplorerNativeTabSnapshot snapshot)
    {
        return !_unsafe
               && !_activationRevoked
               && _claimedTabHandle == 0
               && MatchesProcessGeneration(snapshot)
               && snapshot.TargetWindowIsForeground
               && snapshot.LastInputTick == _baselineLastInputTick
               && snapshot.ActiveNativeTabHandle == _originalTabHandle
               && snapshot.NativeTabHandles.SequenceEqual(_initialTabHandles);
    }

    /// <summary>
    /// Claims only an identical pair of post-command observations containing
    /// the original set plus exactly one active child. LASTINPUTINFO must still
    /// equal the pre-command baseline, catching a physical Ctrl+T before its
    /// second HWND has appeared.
    /// </summary>
    public ExplorerNativeTabClaimDecision TryClaimCreated(
        ExplorerNativeTabSnapshot first,
        ExplorerNativeTabSnapshot second,
        out nint claimedTabHandle,
        bool allowInputChangeFromKnownDuplicateRequest = false)
    {
        claimedTabHandle = _claimedTabHandle;
        if (_unsafe) return ExplorerNativeTabClaimDecision.Unsafe;
        if (_activationRevoked
            && _claimedTabHandle == 0
            && !(_knownDuplicateInputAccepted
                 && allowInputChangeFromKnownDuplicateRequest))
            return ExplorerNativeTabClaimDecision.UserIntervened;

        if (!MatchesProcessGeneration(first) || !MatchesProcessGeneration(second))
            return MarkUnsafe();

        if (!first.TargetWindowIsForeground
            || !second.TargetWindowIsForeground)
        {
            _activationRevoked = true;
            return ExplorerNativeTabClaimDecision.UserIntervened;
        }

        if (first.LastInputTick != _baselineLastInputTick
            || second.LastInputTick != _baselineLastInputTick)
        {
            if (!allowInputChangeFromKnownDuplicateRequest)
            {
                _activationRevoked = true;
                return ExplorerNativeTabClaimDecision.UserIntervened;
            }

            // The matching IPC request proves that this input was another
            // attempt to open the same folder in the same Explorer window.
            // Exact native/COM identity may still be completed, but focus
            // restoration remains permanently revoked.
            _knownDuplicateInputAccepted = true;
            _activationRevoked = true;
        }

        var firstState = AnalyzeTopology(first, out var firstAdded);
        var secondState = AnalyzeTopology(second, out var secondAdded);
        if (firstState == TopologyState.Unsafe
            || secondState == TopologyState.Unsafe)
            return MarkUnsafe();

        if (firstState == TopologyState.Added
            && (secondState != TopologyState.Added || firstAdded != secondAdded))
            return MarkUnsafe();

        if (!SnapshotsEqual(first, second))
            return ExplorerNativeTabClaimDecision.Waiting;

        if (_claimedTabHandle != 0)
        {
            if (secondState != TopologyState.Added
                || secondAdded != _claimedTabHandle
                || second.ActiveNativeTabHandle != _claimedTabHandle)
                return MarkUnsafe();

            claimedTabHandle = _claimedTabHandle;
            return ExplorerNativeTabClaimDecision.Claimed;
        }

        if (secondState != TopologyState.Added
            || secondAdded == 0
            || second.ActiveNativeTabHandle != secondAdded)
            return ExplorerNativeTabClaimDecision.Waiting;

        _claimedTabHandle = secondAdded;
        claimedTabHandle = secondAdded;
        return ExplorerNativeTabClaimDecision.Claimed;
    }

    /// <summary>
    /// Records whether an automatic restore/final activation is still allowed.
    /// Cancellation is permanent, while the already-proven ownership remains.
    /// </summary>
    public bool ObserveActivationIntent(
        ExplorerNativeTabSnapshot snapshot,
        nint expectedActiveTabHandle)
    {
        if (_activationRevoked || _unsafe || _claimedTabHandle == 0)
            return false;

        if (!MatchesProcessGeneration(snapshot)
            || snapshot.LastInputTick != _baselineLastInputTick
            || !snapshot.TargetWindowIsForeground
            || snapshot.ActiveNativeTabHandle != expectedActiveTabHandle
            || !HasExactClaimedTopology(snapshot))
        {
            _activationRevoked = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The ShellWindows item must independently report the claimed
    /// IShellBrowser child HWND. The child's native root supplies the target
    /// top-level Explorer HWND, while the current native snapshot proves the
    /// process generation and exact one-child topology. Focus/input may change
    /// after the claim; that only prevents later focus stealing.
    /// </summary>
    public bool CanUseExactComItem(
        nint actualTopLevelWindow,
        nint actualTabHandle,
        ExplorerNativeTabSnapshot current)
    {
        return !_unsafe
               && _claimedTabHandle != 0
               && actualTopLevelWindow == _targetWindowHandle
               && actualTabHandle == _claimedTabHandle
               && MatchesProcessGeneration(current)
               && current.NativeTabHandles.Count(
                   handle => handle == _claimedTabHandle) == 1;
    }

    public bool CanCloseExactComItem(
        nint actualTopLevelWindow,
        nint actualTabHandle,
        ExplorerNativeTabSnapshot current,
        ExplorerNavigationDisposition disposition)
    {
        if (_activationRevoked
            || !ExplorerNavigationDispositionPolicy.ShouldOpenFallback(disposition)
            || !CanUseExactComItem(actualTopLevelWindow, actualTabHandle, current)
            || current.LastInputTick != _baselineLastInputTick
            || !current.TargetWindowIsForeground
            || !HasExactClaimedTopology(current))
            return false;

        return current.NativeTabHandles.Count >= 2;
    }

    /// <summary>
    /// Final fallback guard after QingTab has closed its exact un-navigated
    /// tab. The claimed handle is expected to be gone; the original process,
    /// input epoch, foreground window and original native topology must all be
    /// restored before a top-level fallback window may be opened.
    /// </summary>
    public bool CanOpenFallbackAfterOwnedTabClosed(
        ExplorerNativeTabSnapshot snapshot)
    {
        var currentSet = new HashSet<nint>(snapshot.NativeTabHandles);
        var allowed = !_activationRevoked
                      && !_unsafe
                      && _claimedTabHandle != 0
                      && MatchesProcessGeneration(snapshot)
                      && snapshot.LastInputTick == _baselineLastInputTick
                      && snapshot.TargetWindowIsForeground
                      && snapshot.ActiveNativeTabHandle == _originalTabHandle
                      && snapshot.NativeTabHandles.Count == _initialTabHandles.Length
                      && currentSet.Count == _initialTabSet.Count
                      && _initialTabSet.All(currentSet.Contains);
        if (!allowed)
            _activationRevoked = true;
        return allowed;
    }

    private ExplorerNativeTabClaimDecision MarkUnsafe()
    {
        _unsafe = true;
        _activationRevoked = true;
        return ExplorerNativeTabClaimDecision.Unsafe;
    }

    private bool MatchesProcessGeneration(ExplorerNativeTabSnapshot snapshot)
    {
        return IsValid(snapshot)
               && snapshot.TargetWindowHandle == _targetWindowHandle
               && snapshot.ProcessId == _processId
               && snapshot.ProcessStartTimeUtcTicks == _processStartTimeUtcTicks;
    }

    private TopologyState AnalyzeTopology(
        ExplorerNativeTabSnapshot snapshot,
        out nint added)
    {
        added = 0;
        var handles = snapshot.NativeTabHandles;
        if (handles.Count != new HashSet<nint>(handles).Count
            || !_initialTabSet.All(handles.Contains))
            return TopologyState.Unsafe;

        if (handles.Count == _initialTabHandles.Length)
            return handles.All(_initialTabSet.Contains)
                ? TopologyState.Initial
                : TopologyState.Unsafe;
        if (handles.Count != _initialTabHandles.Length + 1)
            return TopologyState.Unsafe;

        var additions = handles.Where(handle => !_initialTabSet.Contains(handle)).ToArray();
        if (additions.Length != 1) return TopologyState.Unsafe;
        added = additions[0];
        return TopologyState.Added;
    }

    private bool HasExactClaimedTopology(ExplorerNativeTabSnapshot snapshot)
    {
        if (snapshot.NativeTabHandles.Count != _initialTabHandles.Length + 1)
            return false;

        var set = new HashSet<nint>(snapshot.NativeTabHandles);
        return set.Count == snapshot.NativeTabHandles.Count
               && set.Contains(_claimedTabHandle)
               && _initialTabSet.All(set.Contains);
    }

    private static bool IsValid(ExplorerNativeTabSnapshot? snapshot)
    {
        if (snapshot == null
            || snapshot.TargetWindowHandle == 0
            || snapshot.ProcessId <= 0
            || snapshot.ProcessStartTimeUtcTicks <= 0
            || snapshot.NativeTabHandles.Count == 0
            || snapshot.NativeTabHandles.Any(handle => handle == 0)
            || snapshot.NativeTabHandles.Count
               != new HashSet<nint>(snapshot.NativeTabHandles).Count)
            return false;

        return snapshot.NativeTabHandles.Count(
                   handle => handle == snapshot.ActiveNativeTabHandle) == 1;
    }

    private static bool SnapshotsEqual(
        ExplorerNativeTabSnapshot first,
        ExplorerNativeTabSnapshot second)
    {
        return first.TargetWindowHandle == second.TargetWindowHandle
               && first.ProcessId == second.ProcessId
               && first.ProcessStartTimeUtcTicks == second.ProcessStartTimeUtcTicks
               && first.ActiveNativeTabHandle == second.ActiveNativeTabHandle
               && first.LastInputTick == second.LastInputTick
               && first.TargetWindowIsForeground == second.TargetWindowIsForeground
               && first.NativeTabHandles.SequenceEqual(second.NativeTabHandles);
    }

    private enum TopologyState
    {
        Initial,
        Added,
        Unsafe
    }
}
