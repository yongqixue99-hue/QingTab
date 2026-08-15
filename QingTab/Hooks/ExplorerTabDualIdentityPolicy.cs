using System;
using System.Collections.Generic;
using System.Linq;

namespace QingTab.Hooks;

/// <summary>
/// One technology-neutral observation of an Explorer tab strip. Native HWNDs
/// and UIA runtime IDs deliberately remain separate identities; only the
/// simultaneous active/selected pair is allowed to associate them.
/// </summary>
public sealed class ExplorerTabIdentityPolicySnapshot
{
    public ExplorerTabIdentityPolicySnapshot(
        IEnumerable<nint> nativeTabHandles,
        nint activeNativeTabHandle,
        IEnumerable<string> runtimeIds,
        string selectedRuntimeId,
        bool targetWindowIsForeground,
        uint lastInputTick = 0)
    {
        if (nativeTabHandles == null)
            throw new ArgumentNullException(nameof(nativeTabHandles));
        if (runtimeIds == null)
            throw new ArgumentNullException(nameof(runtimeIds));

        NativeTabHandles = nativeTabHandles.ToArray();
        ActiveNativeTabHandle = activeNativeTabHandle;
        RuntimeIds = runtimeIds.ToArray();
        SelectedRuntimeId = selectedRuntimeId ?? string.Empty;
        TargetWindowIsForeground = targetWindowIsForeground;
        LastInputTick = lastInputTick;
    }

    public IReadOnlyList<nint> NativeTabHandles { get; }
    public nint ActiveNativeTabHandle { get; }
    public IReadOnlyList<string> RuntimeIds { get; }
    public string SelectedRuntimeId { get; }
    public bool TargetWindowIsForeground { get; }
    public uint LastInputTick { get; }

    internal TabStripObservation ToTabStripObservation()
    {
        return new TabStripObservation(
            ActiveNativeTabHandle,
            RuntimeIds,
            SelectedRuntimeId,
            TargetWindowIsForeground);
    }
}

public sealed class ExplorerTabIdentityPolicyBinding
{
    internal ExplorerTabIdentityPolicyBinding(
        nint createdNativeTabHandle,
        string createdRuntimeId,
        IEnumerable<nint> boundNativeOrder,
        IEnumerable<string> boundRuntimeOrder)
    {
        CreatedNativeTabHandle = createdNativeTabHandle;
        CreatedRuntimeId = createdRuntimeId;
        BoundNativeOrder = boundNativeOrder.ToArray();
        BoundRuntimeOrder = boundRuntimeOrder.ToArray();
    }

    public nint CreatedNativeTabHandle { get; }
    public string CreatedRuntimeId { get; }
    public IReadOnlyList<nint> BoundNativeOrder { get; }
    public IReadOnlyList<string> BoundRuntimeOrder { get; }
}

public enum ExplorerTabIdentityPolicyDecision
{
    Waiting,
    Bound,
    Unsafe
}

/// <summary>
/// Pure, fail-closed ownership policy for a single Explorer Ctrl+T operation.
/// It contains no UIA or Win32 calls so every race rule can be tested with
/// deterministic snapshots.
/// </summary>
public sealed class ExplorerTabDualIdentityPolicy
{
    private readonly nint[] _initialNativeOrder;
    private readonly HashSet<nint> _initialNativeSet;
    private readonly string[] _initialRuntimeOrder;
    private readonly HashSet<string> _initialRuntimeSet;

    private bool _cancelled;
    private ExplorerTabIdentityPolicyBinding? _binding;

    private ExplorerTabDualIdentityPolicy(
        ExplorerTabIdentityPolicySnapshot initial)
    {
        _initialNativeOrder = initial.NativeTabHandles.ToArray();
        _initialNativeSet = new HashSet<nint>(_initialNativeOrder);
        _initialRuntimeOrder = initial.RuntimeIds.ToArray();
        _initialRuntimeSet = new HashSet<string>(
            _initialRuntimeOrder,
            StringComparer.Ordinal);
        OriginalNativeTabHandle = initial.ActiveNativeTabHandle;
        OriginalRuntimeId = initial.SelectedRuntimeId;
        BaselineLastInputTick = initial.LastInputTick;
    }

    public nint OriginalNativeTabHandle { get; }
    public string OriginalRuntimeId { get; }
    public uint BaselineLastInputTick { get; }
    public bool IsCancelled => _cancelled;
    public ExplorerTabIdentityPolicyBinding? Binding => _binding;

    /// <summary>
    /// Establishes the original HWND/runtime-ID association only from two
    /// identical, self-consistent foreground samples.
    /// </summary>
    public static bool TryCreate(
        ExplorerTabIdentityPolicySnapshot first,
        ExplorerTabIdentityPolicySnapshot second,
        out ExplorerTabDualIdentityPolicy policy)
    {
        policy = null!;
        if (first == null || second == null
            || !IsDomainValid(first)
            || !IsDomainValid(second)
            || first.NativeTabHandles.Count != first.RuntimeIds.Count
            || second.NativeTabHandles.Count != second.RuntimeIds.Count
            || !SnapshotsEqual(first, second))
            return false;

        policy = new ExplorerTabDualIdentityPolicy(second);
        return true;
    }

    /// <summary>
    /// Binds exactly one new native child to exactly one new UIA item. Normal
    /// native/UIA settling may return Waiting. Any deletion, second addition,
    /// reorder of retained UIA identities, or third-tab selection latches
    /// Unsafe permanently.
    /// </summary>
    public ExplorerTabIdentityPolicyDecision ObserveForBinding(
        ExplorerTabIdentityPolicySnapshot first,
        ExplorerTabIdentityPolicySnapshot second,
        out ExplorerTabIdentityPolicyBinding? binding)
    {
        binding = _binding;
        if (_cancelled) return ExplorerTabIdentityPolicyDecision.Unsafe;
        if (first == null || second == null
            || !IsDomainValid(first)
            || !IsDomainValid(second))
            return Cancel();
        if (first.LastInputTick != BaselineLastInputTick
            || second.LastInputTick != BaselineLastInputTick)
            return Cancel();

        if (_binding != null)
        {
            if (!SnapshotsEqual(first, second)
                || !MatchesBoundCreatedSelection(first, _binding))
                return Cancel();

            binding = _binding;
            return ExplorerTabIdentityPolicyDecision.Bound;
        }

        var firstNative = AnalyzeNative(first.NativeTabHandles, out var firstAddedNative);
        var secondNative = AnalyzeNative(second.NativeTabHandles, out var secondAddedNative);
        var firstUia = AnalyzeRuntimeIds(first.RuntimeIds, out var firstAddedRuntimeId);
        var secondUia = AnalyzeRuntimeIds(second.RuntimeIds, out var secondAddedRuntimeId);

        if (firstNative == TopologyState.Unsafe
            || secondNative == TopologyState.Unsafe
            || firstUia == TopologyState.Unsafe
            || secondUia == TopologyState.Unsafe)
            return Cancel();

        // A created identity disappearing or changing between the two samples
        // is never a normal forward-settling transition.
        if ((firstNative == TopologyState.Added
             && (secondNative != TopologyState.Added
                 || firstAddedNative != secondAddedNative))
            || (firstUia == TopologyState.Added
                && (secondUia != TopologyState.Added
                    || !string.Equals(
                        firstAddedRuntimeId,
                        secondAddedRuntimeId,
                        StringComparison.Ordinal))))
            return Cancel();

        if (!SelectionCanStillBeOurs(first, firstAddedNative, firstAddedRuntimeId)
            || !SelectionCanStillBeOurs(second, secondAddedNative, secondAddedRuntimeId))
            return Cancel();

        if (!SnapshotsEqual(first, second)
            || secondNative != TopologyState.Added
            || secondUia != TopologyState.Added)
            return ExplorerTabIdentityPolicyDecision.Waiting;

        if (second.ActiveNativeTabHandle != secondAddedNative
            || !string.Equals(
                second.SelectedRuntimeId,
                secondAddedRuntimeId,
                StringComparison.Ordinal))
            return ExplorerTabIdentityPolicyDecision.Waiting;

        _binding = new ExplorerTabIdentityPolicyBinding(
            secondAddedNative,
            secondAddedRuntimeId,
            second.NativeTabHandles,
            second.RuntimeIds);
        binding = _binding;
        return ExplorerTabIdentityPolicyDecision.Bound;
    }

    /// <summary>
    /// Authorizes a restore only while two fresh samples reproduce the exact
    /// bound orders and both technologies still select the created tab.
    /// </summary>
    public bool TryAuthorizeOriginalRestore(
        ExplorerTabIdentityPolicySnapshot first,
        ExplorerTabIdentityPolicySnapshot second)
    {
        if (_cancelled || _binding == null
            || first == null || second == null
            || !IsDomainValid(first)
            || !IsDomainValid(second)
            || first.LastInputTick != BaselineLastInputTick
            || second.LastInputTick != BaselineLastInputTick
            || !SnapshotsEqual(first, second)
            || !MatchesBoundCreatedSelection(first, _binding))
        {
            Cancel();
            return false;
        }

        return true;
    }

    public void CancelOwnership()
    {
        _cancelled = true;
    }

    private ExplorerTabIdentityPolicyDecision Cancel()
    {
        _cancelled = true;
        return ExplorerTabIdentityPolicyDecision.Unsafe;
    }

    private bool SelectionCanStillBeOurs(
        ExplorerTabIdentityPolicySnapshot snapshot,
        nint addedNative,
        string addedRuntimeId)
    {
        var nativeAllowed = snapshot.ActiveNativeTabHandle == OriginalNativeTabHandle
                            || (addedNative != 0
                                && snapshot.ActiveNativeTabHandle == addedNative);
        var runtimeAllowed = string.Equals(
                                 snapshot.SelectedRuntimeId,
                                 OriginalRuntimeId,
                                 StringComparison.Ordinal)
                             || (!string.IsNullOrEmpty(addedRuntimeId)
                                 && string.Equals(
                                     snapshot.SelectedRuntimeId,
                                     addedRuntimeId,
                                     StringComparison.Ordinal));
        return nativeAllowed && runtimeAllowed;
    }

    private bool MatchesBoundCreatedSelection(
        ExplorerTabIdentityPolicySnapshot snapshot,
        ExplorerTabIdentityPolicyBinding binding)
    {
        return snapshot.TargetWindowIsForeground
               && snapshot.LastInputTick == BaselineLastInputTick
               && snapshot.NativeTabHandles.SequenceEqual(binding.BoundNativeOrder)
               && snapshot.RuntimeIds.SequenceEqual(
                   binding.BoundRuntimeOrder,
                   StringComparer.Ordinal)
               && snapshot.ActiveNativeTabHandle == binding.CreatedNativeTabHandle
               && string.Equals(
                   snapshot.SelectedRuntimeId,
                   binding.CreatedRuntimeId,
                   StringComparison.Ordinal);
    }

    private TopologyState AnalyzeNative(
        IReadOnlyList<nint> current,
        out nint added)
    {
        added = 0;
        if (current.Count != new HashSet<nint>(current).Count
            || !_initialNativeSet.All(initial => current.Contains(initial)))
            return TopologyState.Unsafe;

        if (current.Count == _initialNativeOrder.Length)
            return current.All(_initialNativeSet.Contains)
                ? TopologyState.Initial
                : TopologyState.Unsafe;
        if (current.Count != _initialNativeOrder.Length + 1)
            return TopologyState.Unsafe;

        var additions = current.Where(handle => !_initialNativeSet.Contains(handle)).ToArray();
        if (additions.Length != 1) return TopologyState.Unsafe;
        added = additions[0];
        return TopologyState.Added;
    }

    private TopologyState AnalyzeRuntimeIds(
        IReadOnlyList<string> current,
        out string added)
    {
        added = string.Empty;
        if (current.Any(string.IsNullOrWhiteSpace)
            || current.Count != new HashSet<string>(current, StringComparer.Ordinal).Count)
            return TopologyState.Unsafe;

        var retained = current.Where(_initialRuntimeSet.Contains).ToArray();
        if (!retained.SequenceEqual(_initialRuntimeOrder, StringComparer.Ordinal))
            return TopologyState.Unsafe;

        if (current.Count == _initialRuntimeOrder.Length)
            return current.All(_initialRuntimeSet.Contains)
                ? TopologyState.Initial
                : TopologyState.Unsafe;
        if (current.Count != _initialRuntimeOrder.Length + 1)
            return TopologyState.Unsafe;

        var additions = current.Where(id => !_initialRuntimeSet.Contains(id)).ToArray();
        if (additions.Length != 1) return TopologyState.Unsafe;
        added = additions[0];
        return TopologyState.Added;
    }

    private static bool IsDomainValid(ExplorerTabIdentityPolicySnapshot snapshot)
    {
        if (!snapshot.TargetWindowIsForeground
            || snapshot.NativeTabHandles.Count == 0
            || snapshot.RuntimeIds.Count == 0
            || snapshot.NativeTabHandles.Any(handle => handle == 0)
            || snapshot.RuntimeIds.Any(string.IsNullOrWhiteSpace)
            || snapshot.NativeTabHandles.Count
               != new HashSet<nint>(snapshot.NativeTabHandles).Count
            || snapshot.RuntimeIds.Count
               != new HashSet<string>(snapshot.RuntimeIds, StringComparer.Ordinal).Count)
            return false;

        return snapshot.NativeTabHandles.Count(
                   handle => handle == snapshot.ActiveNativeTabHandle) == 1
               && snapshot.RuntimeIds.Count(id => string.Equals(
                   id,
                   snapshot.SelectedRuntimeId,
                   StringComparison.Ordinal)) == 1;
    }

    private static bool SnapshotsEqual(
        ExplorerTabIdentityPolicySnapshot first,
        ExplorerTabIdentityPolicySnapshot second)
    {
        return first.TargetWindowIsForeground == second.TargetWindowIsForeground
               && first.LastInputTick == second.LastInputTick
               && first.ActiveNativeTabHandle == second.ActiveNativeTabHandle
               && string.Equals(
                   first.SelectedRuntimeId,
                   second.SelectedRuntimeId,
                   StringComparison.Ordinal)
               && first.NativeTabHandles.SequenceEqual(second.NativeTabHandles)
               && first.RuntimeIds.SequenceEqual(
                   second.RuntimeIds,
                   StringComparer.Ordinal);
    }

    private enum TopologyState
    {
        Initial,
        Added,
        Unsafe
    }
}
