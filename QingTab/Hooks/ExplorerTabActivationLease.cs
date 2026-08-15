using System;
using System.Collections.Generic;
using System.Linq;

namespace QingTab.Hooks;

/// <summary>
/// One coherent observation of Explorer's native active tab and UIA tab strip.
/// The operation lease consumes these observations without depending on either
/// automation technology, which keeps the identity and user-intent rules
/// deterministic and directly testable.
/// </summary>
public sealed class TabStripObservation
{
    public TabStripObservation(
        nint activeNativeTabHandle,
        IEnumerable<string> runtimeIds,
        string selectedRuntimeId,
        bool targetWindowIsForeground)
    {
        if (runtimeIds == null) throw new ArgumentNullException(nameof(runtimeIds));

        ActiveNativeTabHandle = activeNativeTabHandle;
        RuntimeIds = runtimeIds.ToArray();
        SelectedRuntimeId = selectedRuntimeId ?? string.Empty;
        TargetWindowIsForeground = targetWindowIsForeground;
    }

    public nint ActiveNativeTabHandle { get; }
    public IReadOnlyList<string> RuntimeIds { get; }
    public string SelectedRuntimeId { get; }
    public bool TargetWindowIsForeground { get; }
}

/// <summary>
/// Tracks ownership and activation permission for exactly one Explorer tab
/// creation. Once positive evidence of user intervention or identity drift is
/// observed, activation permission is revoked permanently for this operation.
/// </summary>
public sealed class ExplorerTabActivationLease
{
    private readonly nint _targetWindowHandle;
    private readonly nint _originalTabHandle;
    private readonly string[] _initialRuntimeIds;
    private readonly HashSet<string> _initialRuntimeIdSet;
    private readonly string _originalRuntimeId;

    private nint _createdTabHandle;
    private string? _createdRuntimeId;
    private string[]? _boundRuntimeOrder;
    private bool _restoreAuthorized;
    private bool _originalRestored;
    private bool _activationCancelled;
    private bool _navigationCommitted;

    public ExplorerTabActivationLease(
        nint targetWindowHandle,
        nint originalTabHandle,
        IEnumerable<string> initialRuntimeIds,
        string originalRuntimeId)
    {
        if (targetWindowHandle == 0)
            throw new ArgumentOutOfRangeException(nameof(targetWindowHandle));
        if (originalTabHandle == 0)
            throw new ArgumentOutOfRangeException(nameof(originalTabHandle));
        if (initialRuntimeIds == null)
            throw new ArgumentNullException(nameof(initialRuntimeIds));
        if (string.IsNullOrWhiteSpace(originalRuntimeId))
            throw new ArgumentException("An original runtime ID is required.", nameof(originalRuntimeId));

        _initialRuntimeIds = initialRuntimeIds.ToArray();
        _initialRuntimeIdSet = new HashSet<string>(_initialRuntimeIds, StringComparer.Ordinal);
        if (_initialRuntimeIds.Length == 0
            || _initialRuntimeIdSet.Count != _initialRuntimeIds.Length
            || !_initialRuntimeIdSet.Contains(originalRuntimeId))
        {
            throw new ArgumentException(
                "Initial runtime IDs must be non-empty, unique, and contain the original tab.",
                nameof(initialRuntimeIds));
        }

        _targetWindowHandle = targetWindowHandle;
        _originalTabHandle = originalTabHandle;
        _originalRuntimeId = originalRuntimeId;
    }

    public nint CreatedTabHandle => _createdTabHandle;
    public string? CreatedRuntimeId => _createdRuntimeId;
    public bool IsActivationCancelled => _activationCancelled;
    public bool IsNavigationCommitted => _navigationCommitted;

    /// <summary>
    /// A native fallback is safe only before Explorer accepted navigation on
    /// the owned tab. Readiness or later activation failures must not duplicate
    /// an already-committed folder open in another window.
    /// </summary>
    public bool ShouldOpenNativeFallback => !_navigationCommitted;

    /// <summary>
    /// Binds the independently discovered child HWND to exactly one newly-added
    /// UIA tab, but only while both technologies report that new tab selected.
    /// A not-yet-updated selection is treated as transient; structural drift is
    /// terminal because ownership can no longer be proven.
    /// </summary>
    public bool TryBindCreatedTab(nint createdTabHandle, TabStripObservation observation)
    {
        if (observation == null) throw new ArgumentNullException(nameof(observation));
        if (_activationCancelled) return false;

        if (_createdRuntimeId != null)
        {
            if (createdTabHandle != _createdTabHandle)
            {
                CancelActivation();
                return false;
            }

            return ObservationMatchesBoundNewTab(observation);
        }

        if (createdTabHandle == 0 || createdTabHandle == _originalTabHandle)
        {
            CancelActivation();
            return false;
        }

        if (!observation.TargetWindowIsForeground)
        {
            CancelActivation();
            return false;
        }

        var ids = observation.RuntimeIds.ToArray();
        if (ids.SequenceEqual(_initialRuntimeIds, StringComparer.Ordinal))
            return false;

        if (!TryFindUniqueAddedRuntimeId(ids, out var addedRuntimeId))
        {
            CancelActivation();
            return false;
        }

        // UIA and native selection can settle a few milliseconds apart. Do not
        // bind until they agree, but do not convert that transient into a false
        // user-intervention signal either.
        if (observation.ActiveNativeTabHandle != createdTabHandle
            || !string.Equals(
                observation.SelectedRuntimeId,
                addedRuntimeId,
                StringComparison.Ordinal))
            return false;

        _createdTabHandle = createdTabHandle;
        _createdRuntimeId = addedRuntimeId;
        _boundRuntimeOrder = ids;
        return true;
    }

    /// <summary>
    /// Fast-path binding used after Explorer has already restored the exact
    /// original native tab. The serialized operation owns one newly-created
    /// child HWND; an unchanged strip with exactly one added UIA item supplies
    /// the matching UIA identity without holding the default page on screen
    /// while UI Automation is temporarily busy.
    /// </summary>
    public bool TryBindCreatedTabAfterOriginalRestore(
        nint createdTabHandle,
        TabStripObservation observation)
    {
        if (observation == null) throw new ArgumentNullException(nameof(observation));
        if (_activationCancelled) return false;

        if (_createdRuntimeId != null)
        {
            if (createdTabHandle != _createdTabHandle)
            {
                CancelActivation();
                return false;
            }

            return ObservationMatchesRestoredOriginal(observation);
        }

        if (createdTabHandle == 0 || createdTabHandle == _originalTabHandle)
        {
            CancelActivation();
            return false;
        }

        if (!observation.TargetWindowIsForeground)
        {
            CancelActivation();
            return false;
        }

        var ids = observation.RuntimeIds.ToArray();
        if (!TryFindUniqueAddedRuntimeId(ids, out var addedRuntimeId))
        {
            CancelActivation();
            return false;
        }

        // Native and UIA selection can report the restore on adjacent samples.
        // Keep waiting until both identify the captured original tab.
        if (observation.ActiveNativeTabHandle != _originalTabHandle
            || !string.Equals(
                observation.SelectedRuntimeId,
                _originalRuntimeId,
                StringComparison.Ordinal))
            return false;

        _createdTabHandle = createdTabHandle;
        _createdRuntimeId = addedRuntimeId;
        _boundRuntimeOrder = ids;
        _restoreAuthorized = true;
        _originalRestored = true;
        return true;
    }

    /// <summary>
    /// Revalidates the complete post-create order immediately before an ordinal
    /// restore command may be sent. This prevents a stale index from selecting
    /// a different tab after a close or reorder.
    /// </summary>
    public bool TryAuthorizeOriginalRestore(TabStripObservation observation)
    {
        if (observation == null) throw new ArgumentNullException(nameof(observation));
        if (_activationCancelled || _createdRuntimeId == null) return false;

        if (!ObservationMatchesBoundNewTab(observation))
        {
            CancelActivation();
            return false;
        }

        _restoreAuthorized = true;
        return true;
    }

    /// <summary>
    /// Polls the restore transition. Remaining on the created tab is expected
    /// while Explorer handles its posted command; any third selection or strip
    /// mutation is treated as user intervention and permanently cancels.
    /// </summary>
    public bool ObserveOriginalRestore(TabStripObservation observation)
    {
        if (observation == null) throw new ArgumentNullException(nameof(observation));
        if (_activationCancelled || !_restoreAuthorized || _createdRuntimeId == null)
            return false;

        if (!HasExactBoundOrder(observation.RuntimeIds)
            || !observation.TargetWindowIsForeground)
        {
            CancelActivation();
            return false;
        }

        if (observation.ActiveNativeTabHandle == _originalTabHandle
            && string.Equals(
                observation.SelectedRuntimeId,
                _originalRuntimeId,
                StringComparison.Ordinal))
        {
            _originalRestored = true;
            return true;
        }

        var nativeSelectionIsInRestoreTransition =
            observation.ActiveNativeTabHandle == _createdTabHandle
            || observation.ActiveNativeTabHandle == _originalTabHandle;
        var uiaSelectionIsInRestoreTransition =
            string.Equals(
                observation.SelectedRuntimeId,
                _createdRuntimeId,
                StringComparison.Ordinal)
            || string.Equals(
                observation.SelectedRuntimeId,
                _originalRuntimeId,
                StringComparison.Ordinal);
        if (nativeSelectionIsInRestoreTransition
            && uiaSelectionIsInRestoreTransition)
            return false;

        CancelActivation();
        return false;
    }

    /// <summary>
    /// Records user-intent observations while navigation runs in the background.
    /// Cancellation is a latch: a later observation of the original tab cannot
    /// resurrect delayed automatic activation.
    /// </summary>
    public bool ObserveDuringNavigation(TabStripObservation observation)
    {
        if (observation == null) throw new ArgumentNullException(nameof(observation));
        if (_activationCancelled || !_originalRestored) return false;

        if (!ObservationMatchesRestoredOriginal(observation))
        {
            CancelActivation();
            return false;
        }

        return true;
    }

    public bool CanActivateCreatedTab(TabStripObservation observation)
    {
        return ObserveDuringNavigation(observation);
    }

    public void MarkNavigationCommitted()
    {
        _navigationCommitted = true;
    }

    /// <summary>
    /// Cleanup is intentionally stricter than COM identity alone: both the
    /// bound HWND and UIA identity must still exist, the item must belong to the
    /// exact target window, and another native tab must remain.
    /// </summary>
    public bool CanCloseCreatedTab(
        nint actualTopLevelWindow,
        nint actualTabHandle,
        IEnumerable<nint> currentNativeTabHandles,
        IEnumerable<string> currentRuntimeIds)
    {
        if (currentNativeTabHandles == null)
            throw new ArgumentNullException(nameof(currentNativeTabHandles));
        if (currentRuntimeIds == null)
            throw new ArgumentNullException(nameof(currentRuntimeIds));

        if (_activationCancelled
            || _navigationCommitted
            || _createdTabHandle == 0
            || _createdRuntimeId == null
            || actualTopLevelWindow != _targetWindowHandle
            || actualTabHandle != _createdTabHandle)
            return false;

        var nativeHandles = currentNativeTabHandles.ToArray();
        if (nativeHandles.Length < 2
            || nativeHandles.Count(handle => handle == _createdTabHandle) != 1)
            return false;

        var runtimeIds = currentRuntimeIds.ToArray();
        return HasExactBoundOrder(runtimeIds)
               && runtimeIds.Count(id => string.Equals(
                   id,
                   _createdRuntimeId,
                   StringComparison.Ordinal)) == 1;
    }

    private bool ObservationMatchesBoundNewTab(TabStripObservation observation)
    {
        return observation.TargetWindowIsForeground
               && observation.ActiveNativeTabHandle == _createdTabHandle
               && string.Equals(
                   observation.SelectedRuntimeId,
                   _createdRuntimeId,
                   StringComparison.Ordinal)
               && HasExactBoundOrder(observation.RuntimeIds);
    }

    private bool ObservationMatchesRestoredOriginal(TabStripObservation observation)
    {
        return observation.TargetWindowIsForeground
               && observation.ActiveNativeTabHandle == _originalTabHandle
               && string.Equals(
                   observation.SelectedRuntimeId,
                   _originalRuntimeId,
                   StringComparison.Ordinal)
               && HasExactBoundOrder(observation.RuntimeIds);
    }

    private bool HasExactBoundOrder(IEnumerable<string> runtimeIds)
    {
        return _boundRuntimeOrder != null
               && runtimeIds.SequenceEqual(_boundRuntimeOrder, StringComparer.Ordinal);
    }

    private bool TryFindUniqueAddedRuntimeId(
        IReadOnlyList<string> currentRuntimeIds,
        out string addedRuntimeId)
    {
        addedRuntimeId = string.Empty;
        if (currentRuntimeIds.Count != _initialRuntimeIds.Length + 1)
            return false;

        var currentSet = new HashSet<string>(currentRuntimeIds, StringComparer.Ordinal);
        if (currentSet.Count != currentRuntimeIds.Count)
            return false;

        var added = currentRuntimeIds
            .Where(id => !_initialRuntimeIdSet.Contains(id))
            .ToArray();
        if (added.Length != 1)
            return false;

        var retainedOriginalOrder = currentRuntimeIds
            .Where(id => _initialRuntimeIdSet.Contains(id));
        if (!retainedOriginalOrder.SequenceEqual(_initialRuntimeIds, StringComparer.Ordinal))
            return false;

        addedRuntimeId = added[0];
        return true;
    }

    private void CancelActivation()
    {
        _activationCancelled = true;
    }
}
