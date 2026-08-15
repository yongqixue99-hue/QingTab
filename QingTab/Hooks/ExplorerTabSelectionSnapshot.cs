using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using QingTab.WinAPI;

namespace QingTab.Hooks;

/// <summary>
/// Captures the user-visible Explorer tab selection before a request. Runtime
/// IDs are used only to validate identity and index; selection itself stays on
/// Explorer's existing WM_COMMAND path so no keyboard input is synthesized.
/// </summary>
internal sealed class ExplorerTabSelectionSnapshot
{
    private const int SelectTabByIndexCommand = 0xA221;
    private const string TabListAutomationId = "TabListView";
    private const string ExplorerXamlBridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

    private static readonly object TabSearchRootCacheLock = new object();
    private static readonly Dictionary<nint, AutomationElement> TabSearchRootCache =
        new Dictionary<nint, AutomationElement>();

    private readonly nint _window;
    private readonly string[] _initialRuntimeIds;
    private readonly HashSet<string> _initialRuntimeIdSet;
    private readonly string _originalRuntimeId;
    private readonly int _originalIndex;
    private readonly AutomationElement _originalTabElement;
    private AutomationElement _tabSearchRoot;
    private TreeScope _tabSearchScope;
    private string? _newRuntimeId;

    private ExplorerTabSelectionSnapshot(
        nint window,
        string[] initialRuntimeIds,
        string originalRuntimeId,
        int originalIndex,
        AutomationElement originalTabElement,
        AutomationElement tabSearchRoot,
        TreeScope tabSearchScope)
    {
        _window = window;
        _initialRuntimeIds = initialRuntimeIds;
        _initialRuntimeIdSet = new HashSet<string>(
            initialRuntimeIds,
            StringComparer.Ordinal);
        _originalRuntimeId = originalRuntimeId;
        _originalIndex = originalIndex;
        _originalTabElement = originalTabElement;
        _tabSearchRoot = tabSearchRoot;
        _tabSearchScope = tabSearchScope;
    }

    public int InitialCount => _initialRuntimeIds.Length;

    internal string[] CopyInitialRuntimeIds()
    {
        return (string[])_initialRuntimeIds.Clone();
    }

    internal string OriginalRuntimeId => _originalRuntimeId;

    internal int OriginalIndex => _originalIndex;

    public ExplorerTabActivationLease CreateActivationLease(nint originalTabHandle)
    {
        return new ExplorerTabActivationLease(
            _window,
            originalTabHandle,
            _initialRuntimeIds,
            _originalRuntimeId);
    }

    public bool TryGetVisualMaskBounds(out ExplorerVisualMaskBounds bounds)
    {
        bounds = default;
        try
        {
            if (!string.Equals(
                    _tabSearchRoot.Current.AutomationId,
                    TabListAutomationId,
                    StringComparison.Ordinal))
                return false;

            var tabBounds = _tabSearchRoot.Current.BoundingRectangle;
            return !tabBounds.IsEmpty
                   && ExplorerVisualMaskLease.TryCalculateBounds(
                       _window,
                       tabBounds.Bottom,
                       out bounds);
        }
        catch
        {
            return false;
        }
    }

    public static ExplorerTabSelectionSnapshot? TryCapture(nint window)
    {
        if (!TryFindTabSearchRoot(window, out var searchRoot, out var searchScope)
            || !TryReadTabs(searchRoot, searchScope, out var tabs))
        {
            InvalidateCachedTabSearchRoot(window);
            if (!TryFindTabSearchRoot(window, out searchRoot, out searchScope)
                || !TryReadTabs(searchRoot, searchScope, out tabs))
                return null;
        }
        if (tabs.Count == 0) return null;

        var selectedIndex = tabs.FindIndex(tab => tab.IsSelected);
        if (selectedIndex < 0) return null;

        return new ExplorerTabSelectionSnapshot(
            window,
            tabs.Select(tab => tab.RuntimeId).ToArray(),
            tabs[selectedIndex].RuntimeId,
            selectedIndex,
            tabs[selectedIndex].Element,
            searchRoot,
            searchScope);
    }

    /// <summary>
    /// Starts a cache lineage on the caller's apartment. The dual-identity
    /// broker calls this only from its dedicated MTA and performs every later
    /// operation on that same dispatcher, so UIA proxies never leak onto the
    /// application UI thread.
    /// </summary>
    internal static ExplorerTabSelectionSnapshot? TryCaptureIsolated(nint window)
    {
        InvalidateCachedTabSearchRoot(window);
        return TryCapture(window);
    }

    /// <summary>
    /// Requests the original ordinal after Explorer activates the newly-created
    /// tab. The UIA snapshot captured that ordinal before creation; later checks
    /// still use runtime IDs so this private command never decides identity.
    /// </summary>
    public bool TryObserve(
        nint activeNativeTabHandle,
        bool targetWindowIsForeground,
        out TabStripObservation observation)
    {
        observation = null!;
        if (!TryReadTabs(out var tabs)) return false;

        var selected = tabs.Where(tab => tab.IsSelected).ToArray();
        if (selected.Length != 1) return false;

        observation = new TabStripObservation(
            activeNativeTabHandle,
            tabs.Select(tab => tab.RuntimeId),
            selected[0].RuntimeId,
            targetWindowIsForeground);
        return true;
    }

    /// <summary>
    /// Restores the tab captured immediately before QingTab posts Ctrl+T. This
    /// path intentionally avoids a synchronous UIA read while Explorer is
    /// painting its default page; the exact original native HWND is verified
    /// before the visual guard is released, and UIA identity is bound after
    /// that restore is no longer user-visible.
    /// </summary>
    public bool TrySelectCapturedOriginalFast()
    {
        if (!WinApi.IsWindow(_window)) return false;
        if (!ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                _originalIndex + 1))
            return TrySelect(_originalTabElement);

        return WinApi.PostMessage(
            _window,
            WinApi.WM_COMMAND,
            SelectTabByIndexCommand,
            _originalIndex + 1);
    }

    public bool TrySelectOriginalIfObservationIsCurrent(
        TabStripObservation expectedObservation)
    {
        if (expectedObservation == null || !WinApi.IsWindow(_window)) return false;
        if (!TryReadTabs(out var tabs)) return false;

        var currentRuntimeIds = tabs.Select(tab => tab.RuntimeId).ToArray();
        if (!currentRuntimeIds.SequenceEqual(
                expectedObservation.RuntimeIds,
                StringComparer.Ordinal))
            return false;

        var selected = tabs.Where(tab => tab.IsSelected).ToArray();
        if (selected.Length != 1
            || !string.Equals(
                selected[0].RuntimeId,
                expectedObservation.SelectedRuntimeId,
                StringComparison.Ordinal))
            return false;

        var currentOriginalIndex = tabs.FindIndex(tab => string.Equals(
            tab.RuntimeId,
            _originalRuntimeId,
            StringComparison.Ordinal));
        if (currentOriginalIndex < 0) return false;

        if (!ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                currentOriginalIndex + 1))
            return TrySelect(tabs[currentOriginalIndex].Element);

        return WinApi.PostMessage(
            _window,
            WinApi.WM_COMMAND,
            SelectTabByIndexCommand,
            currentOriginalIndex + 1);
    }

    /// <summary>
    /// Revalidates the UIA order and selection, then runs one final native and
    /// deadline guard immediately before posting the identity-independent
    /// ordinal command. Explorer's private command has only been verified for
    /// positive ordinals through the current tab count; probes cover 10, 11,
    /// 12, 20 and 36 rather than extrapolating the public Ctrl+number limit.
    /// A timed-out dispatcher therefore cannot post a delayed restore.
    /// </summary>
    internal bool TryPostSelectOriginalIfObservationIsCurrent(
        TabStripObservation expectedObservation,
        Func<bool> finalGuard)
    {
        if (expectedObservation == null
            || finalGuard == null
            || !WinApi.IsWindow(_window)
            || !TryReadTabs(out var tabs))
            return false;

        var currentRuntimeIds = tabs.Select(tab => tab.RuntimeId).ToArray();
        if (!currentRuntimeIds.SequenceEqual(
                expectedObservation.RuntimeIds,
                StringComparer.Ordinal))
            return false;

        var selected = tabs.Where(tab => tab.IsSelected).ToArray();
        if (selected.Length != 1
            || !string.Equals(
                selected[0].RuntimeId,
                expectedObservation.SelectedRuntimeId,
                StringComparison.Ordinal))
            return false;

        var currentOriginalIndex = tabs.FindIndex(tab => string.Equals(
            tab.RuntimeId,
            _originalRuntimeId,
            StringComparison.Ordinal));
        if (currentOriginalIndex < 0
            || !ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                currentOriginalIndex + 1)
            || !finalGuard())
            return false;

        return WinApi.PostMessage(
            _window,
            WinApi.WM_COMMAND,
            SelectTabByIndexCommand,
            currentOriginalIndex + 1);
    }

    public bool IsOriginalSelected()
    {
        return TryReadTabs(out var tabs)
               && tabs.Any(tab => tab.IsSelected
                                  && string.Equals(
                                      tab.RuntimeId,
                                      _originalRuntimeId,
                                      StringComparison.Ordinal));
    }

    public bool TryRememberNewTab()
    {
        if (!TryReadTabs(out var tabs)) return false;

        var candidates = tabs
            .Where(tab => !_initialRuntimeIdSet.Contains(tab.RuntimeId))
            .Select(tab => tab.RuntimeId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length != 1) return false;

        _newRuntimeId = candidates[0];
        return true;
    }

    public bool IsNewTabSelected()
    {
        if (_newRuntimeId == null || !TryReadTabs(out var tabs)) return false;
        return tabs.Any(tab => tab.IsSelected
                               && string.Equals(
                                   tab.RuntimeId,
                                   _newRuntimeId,
                                   StringComparison.Ordinal));
    }

    /// <summary>
    /// Selects the request tab only if the original set of tabs is unchanged,
    /// exactly one new tab exists, and the user is still looking at the tab
    /// that was active when the request began.
    /// </summary>
    public bool TrySelectNewTabIfUserIntentIsUnchanged()
    {
        return TrySelectNewTabIfUserIntentIsUnchanged(finalGuard: null);
    }

    public bool TrySelectNewTabIfUserIntentIsUnchanged(Func<bool>? finalGuard)
    {
        if (!TryReadTabs(out var tabs)) return false;
        if (tabs.Count != InitialCount + 1) return false;

        if (_newRuntimeId == null)
        {
            var candidates = tabs
                .Where(tab => !_initialRuntimeIdSet.Contains(tab.RuntimeId))
                .Select(tab => tab.RuntimeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length != 1) return false;
            _newRuntimeId = candidates[0];
        }

        foreach (var initialRuntimeId in _initialRuntimeIds)
        {
            if (!tabs.Any(tab => string.Equals(
                    tab.RuntimeId,
                    initialRuntimeId,
                    StringComparison.Ordinal)))
                return false;
        }

        if (!tabs.Any(tab => tab.IsSelected
                             && string.Equals(
                                 tab.RuntimeId,
                                 _originalRuntimeId,
                                 StringComparison.Ordinal)))
            return false;

        var newIndex = tabs.FindIndex(tab => string.Equals(
            tab.RuntimeId,
            _newRuntimeId,
            StringComparison.Ordinal));
        if (newIndex < 0) return false;
        if (finalGuard != null && !finalGuard()) return false;

        if (!ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                newIndex + 1))
            return TrySelect(tabs[newIndex].Element);

        return WinApi.PostMessage(
            _window,
            WinApi.WM_COMMAND,
            SelectTabByIndexCommand,
            newIndex + 1);
    }

    /// <summary>
    /// Ctrl+T appends the owned tab to the current TabView. For ordinals one
    /// through the captured tab count Explorer exposes its range-checked
    /// private selection command, so a final native/input guard can post that
    /// ordinal without a blocking UIA tree walk.
    /// </summary>
    public bool TrySelectAppendedNewTabFast(Func<bool> finalGuard)
    {
        if (finalGuard == null
            || !ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                InitialCount)
            || !WinApi.IsWindow(_window)
            || !finalGuard())
            return false;

        return WinApi.PostMessage(
            _window,
            WinApi.WM_COMMAND,
            SelectTabByIndexCommand,
            InitialCount + 1);
    }

    private bool TryReadTabs(out List<TabState> tabs)
    {
        if (TryReadTabs(_tabSearchRoot, _tabSearchScope, out tabs))
            return true;

        InvalidateCachedTabSearchRoot(_window);
        if (!TryFindTabSearchRoot(_window, out var searchRoot, out var searchScope)
            || !TryReadTabs(searchRoot, searchScope, out tabs))
            return false;

        _tabSearchRoot = searchRoot;
        _tabSearchScope = searchScope;
        return true;
    }

    private static bool TryFindTabSearchRoot(
        nint window,
        out AutomationElement searchRoot,
        out TreeScope searchScope)
    {
        searchRoot = null!;
        searchScope = TreeScope.Descendants;
        try
        {
            lock (TabSearchRootCacheLock)
            {
                if (TabSearchRootCache.TryGetValue(window, out var cachedRoot))
                {
                    searchRoot = cachedRoot;
                    searchScope = TreeScope.Children;
                    return true;
                }
            }

            var xamlBridge = WinApi.FindWindowEx(
                window,
                0,
                ExplorerXamlBridgeClass,
                null);
            var windowRoot = AutomationElement.FromHandle(
                xamlBridge != 0 ? xamlBridge : window);
            var tabList = windowRoot.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    TabListAutomationId));
            searchRoot = tabList ?? windowRoot;
            searchScope = tabList == null ? TreeScope.Descendants : TreeScope.Children;
            if (tabList != null)
            {
                lock (TabSearchRootCacheLock)
                    TabSearchRootCache[window] = tabList;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void InvalidateCachedTabSearchRoot(nint window)
    {
        lock (TabSearchRootCacheLock)
            TabSearchRootCache.Remove(window);
    }

    private static bool TryReadTabs(
        AutomationElement searchRoot,
        TreeScope searchScope,
        out List<TabState> tabs)
    {
        tabs = new List<TabState>();
        try
        {
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.TabItem);
            var items = searchRoot.FindAll(searchScope, condition);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var runtimeId = string.Join(
                    ".",
                    item.GetRuntimeId().Select(value => value.ToString()));
                if (string.IsNullOrWhiteSpace(runtimeId)) return false;

                var selectionPattern = (SelectionItemPattern)item.GetCurrentPattern(
                    SelectionItemPattern.Pattern);
                tabs.Add(new TabState(item, runtimeId, selectionPattern.Current.IsSelected));
            }

            return tabs.Count > 0;
        }
        catch
        {
            tabs.Clear();
            return false;
        }
    }

    private static bool TrySelect(AutomationElement tab)
    {
        try
        {
            var selectionPattern = (SelectionItemPattern)tab.GetCurrentPattern(
                SelectionItemPattern.Pattern);
            selectionPattern.Select();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TabState
    {
        public TabState(AutomationElement element, string runtimeId, bool isSelected)
        {
            Element = element;
            RuntimeId = runtimeId;
            IsSelected = isSelected;
        }

        public AutomationElement Element { get; }
        public string RuntimeId { get; }
        public bool IsSelected { get; }
    }
}
