using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using QingTab.WinAPI;

namespace QingTab.Hooks;

/// <summary>
/// Reads only the Explorer tab-strip geometry on a bounded MTA dispatcher.
/// UIA proxies never cross into the WinForms UI STA; callers receive a value.
/// </summary>
internal static class ExplorerVisualMaskBoundsCapture
{
    private const string TabListAutomationId = "TabListView";
    private const string ExplorerXamlBridgeClass =
        "Microsoft.UI.Content.DesktopChildSiteBridge";

    private static readonly BoundedUiaDispatcher Dispatcher =
        new BoundedUiaDispatcher();
    // Accessed only by Dispatcher. Keeping the TabListView container avoids a
    // full XAML-descendant search on every folder open; the element itself is
    // revalidated against both HWND ownership and Explorer PID before use.
    private static readonly Dictionary<nint, TabListCacheEntry> TabListCache =
        new Dictionary<nint, TabListCacheEntry>();

    public static async Task<ExplorerVisualMaskBounds?> TryCaptureAsync(
        nint explorer,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var dispatch = await Dispatcher.TryInvokeAsync(
                guard => TryCaptureOnDispatcher(explorer, guard),
                timeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(true);
        return dispatch.Completed ? dispatch.Value : null;
    }

    private static ExplorerVisualMaskBounds? TryCaptureOnDispatcher(
        nint explorer,
        UiaOperationGuard guard)
    {
        if (!guard.CanContinue
            || !WinApi.IsWindow(explorer)
            || WinApi.GetWindowThreadProcessId(explorer, out var processId) == 0
            || processId == 0)
            return null;

        PruneStaleEntries();
        if (TabListCache.TryGetValue(explorer, out var cached))
        {
            if (cached.ProcessId == processId
                && TryReadBounds(explorer, processId, cached.Element, guard, out var cachedBounds))
                return cachedBounds;
            TabListCache.Remove(explorer);
        }

        var xamlBridge = WinApi.FindWindowEx(
            explorer,
            0,
            ExplorerXamlBridgeClass,
            null);
        var root = AutomationElement.FromHandle(
            xamlBridge != 0 ? xamlBridge : explorer);
        if (!guard.CanContinue) return null;

        var tabList = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.AutomationIdProperty,
                TabListAutomationId));
        if (tabList == null || !guard.CanContinue) return null;

        if (!TryReadBounds(explorer, processId, tabList, guard, out var bounds))
            return null;

        TabListCache[explorer] = new TabListCacheEntry(processId, tabList);
        return bounds;
    }

    private static void PruneStaleEntries()
    {
        if (TabListCache.Count == 0) return;

        var staleHandles = new List<nint>();
        foreach (var pair in TabListCache)
        {
            if (!WinApi.IsWindow(pair.Key)
                || WinApi.GetWindowThreadProcessId(pair.Key, out var processId) == 0
                || processId != pair.Value.ProcessId)
                staleHandles.Add(pair.Key);
        }

        foreach (var handle in staleHandles)
            TabListCache.Remove(handle);
    }

    private static bool TryReadBounds(
        nint explorer,
        uint processId,
        AutomationElement tabList,
        UiaOperationGuard guard,
        out ExplorerVisualMaskBounds bounds)
    {
        bounds = default;
        try
        {
            if (!guard.CanContinue
                || !WinApi.IsWindow(explorer)
                || WinApi.GetWindowThreadProcessId(explorer, out var currentProcessId) == 0
                || currentProcessId != processId
                || tabList.Current.ProcessId != (int)processId
                || !string.Equals(
                    tabList.Current.AutomationId,
                    TabListAutomationId,
                    StringComparison.Ordinal))
                return false;

            var tabBounds = tabList.Current.BoundingRectangle;
            return !tabBounds.IsEmpty
                   && guard.CanContinue
                   && ExplorerVisualMaskLease.TryCalculateBounds(
                       explorer,
                       tabBounds.Bottom,
                       out bounds);
        }
        catch
        {
            return false;
        }
    }

    private sealed class TabListCacheEntry
    {
        public TabListCacheEntry(uint processId, AutomationElement element)
        {
            ProcessId = processId;
            Element = element;
        }

        public uint ProcessId { get; }
        public AutomationElement Element { get; }
    }
}
