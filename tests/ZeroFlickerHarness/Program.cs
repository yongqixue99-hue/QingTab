using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint WineventOutOfContext = 0;
    private const int ObjidWindow = 0;

    private static readonly List<EventRecord> Events = new List<EventRecord>();
    private static readonly object EventsLock = new object();
    private static readonly Stopwatch Clock = new Stopwatch();
    private static WinEventDelegate? _callback;

    [STAThread]
    private static int Main(string[] args)
    {
        var timeoutMs = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 6000;
        // Windows PowerShell 5 drops an empty native-process argument. Use an
        // explicit sentinel so the third argument cannot slide into the verb slot.
        var verb = args.Length > 1 && !string.Equals(args[1], "__default__", StringComparison.Ordinal)
            ? args[1]
            : string.Empty;
        var overrideExecutable = args.Length > 2 ? args[2] : string.Empty;
        var overrideMode = args.Length > 3 ? args[3] : "open-empty-delegate";
        var requestedTarget = args.Length > 4 ? args[4] : string.Empty;
        var maximumAcceptableLatencyMs = args.Length > 5
                                         && int.TryParse(args[5], out var parsedLatency)
            ? parsedLatency
            : int.MaxValue;
        var directOpenTabMode = !string.IsNullOrWhiteSpace(overrideExecutable)
                                && string.Equals(overrideMode, "direct-open-tab", StringComparison.OrdinalIgnoreCase);
        var fixtureRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingTab",
            "ZeroFlickerHarness");
        var ownsFixture = string.IsNullOrWhiteSpace(requestedTarget);
        var fixture = ownsFixture
            ? Path.Combine(fixtureRoot, "fixture-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(requestedTarget);
        if (ownsFixture)
            Directory.CreateDirectory(fixture);
        else if (!Directory.Exists(fixture))
        {
            WriteResult(new Result
            {
                Verdict = "BLOCKED",
                Detail = "指定的性能测试目标不存在。",
                Fixture = fixture
            });
            return 4;
        }

        var beforeWindows = FindAllWindows("CabinetWClass").ToHashSet();
        var beforeTabs = GetAllExplorerTabs().ToHashSet();
        var beforeLocations = SnapshotTabLocations();
        if (beforeWindows.Count == 0)
        {
            WriteResult(new Result
            {
                Verdict = "BLOCKED",
                Detail = "测试前必须至少打开一扇文件资源管理器窗口。",
                Fixture = fixture
            });
            if (ownsFixture)
                TryDeleteFixture(fixture, fixtureRoot);
            return 2;
        }

        var selectionObserver = new ExplorerTabSelectionObserver(beforeWindows);

        using var folderOpenOverride = string.IsNullOrWhiteSpace(overrideExecutable)
                                       || directOpenTabMode
            ? null
            : new TemporaryFolderOpenOverride(overrideExecutable, overrideMode);

        _callback = OnWinEvent;
        var hook = SetWinEventHook(
            EventObjectCreate,
            EventObjectHide,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);
        if (hook == IntPtr.Zero)
        {
            WriteResult(new Result
            {
                Verdict = "BLOCKED",
                Detail = "SetWinEventHook 初始化失败。",
                Fixture = fixture
            });
            if (ownsFixture)
                TryDeleteFixture(fixture, fixtureRoot);
            return 3;
        }

        object? matchingShellItem = null;
        object? navigatedExistingItem = null;
        object? newTopLevelItem = null;
        object? unselectedTargetItem = null;
        var unselectedTargetTopLevel = IntPtr.Zero;
        var unselectedTargetTab = IntPtr.Zero;
        var openedAsTab = false;
        var matchingTopLevel = IntPtr.Zero;
        var matchingTab = IntPtr.Zero;
        long? matchLatencyMs = null;
        object? observerShell = null;
        object? observerWindows = null;
        var preserveCurrentForeground = string.Equals(
            Environment.GetEnvironmentVariable("QINGTAB_HARNESS_PRESERVE_FOREGROUND"),
            "1",
            StringComparison.Ordinal);
        if (directOpenTabMode && !preserveCurrentForeground)
            PrepareExplorerForeground(beforeWindows);
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application")
                            ?? throw new InvalidOperationException("Shell.Application is unavailable.");
            observerShell = Activator.CreateInstance(shellType)
                            ?? throw new InvalidOperationException("Cannot create the Shell observer.");
            observerWindows = (object?)((dynamic)observerShell).Windows()
                              ?? throw new InvalidOperationException("ShellWindows is unavailable.");

            Clock.Restart();
            selectionObserver.Start();
            var startInfo = directOpenTabMode
                ? new ProcessStartInfo
                {
                    FileName = overrideExecutable,
                    Arguments = "--open-tab " + QuoteArgument(fixture) + " --no-registration-repair",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new ProcessStartInfo
                {
                    FileName = fixture,
                    UseShellExecute = true,
                    Verb = verb
                };
            Process.Start(startInfo);

            while (Clock.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();

                var candidateItem = FindShellItemAtPath(
                    observerWindows,
                    fixture,
                    beforeTabs,
                    beforeLocations,
                    out matchingTopLevel,
                    out matchingTab);
                if (candidateItem != null)
                {
                    openedAsTab = beforeWindows.Contains(matchingTopLevel)
                                  && matchingTab != IntPtr.Zero
                                  && !beforeTabs.Contains(matchingTab);
                    if (openedAsTab)
                    {
                        if (!selectionObserver.IsNewTabSelected(matchingTopLevel))
                        {
                            ReleaseCom(candidateItem);
                            matchingTopLevel = IntPtr.Zero;
                            matchingTab = IntPtr.Zero;
                            openedAsTab = false;
                            Thread.Sleep(5);
                            continue;
                        }

                        matchingShellItem = candidateItem;
                        matchLatencyMs = Clock.ElapsedMilliseconds;
                        break;
                    }

                    if (beforeWindows.Contains(matchingTopLevel)
                        && matchingTab != IntPtr.Zero
                        && beforeTabs.Contains(matchingTab))
                    {
                        navigatedExistingItem = candidateItem;
                        matchLatencyMs = Clock.ElapsedMilliseconds;
                        break;
                    }

                    if (!beforeWindows.Contains(matchingTopLevel) && newTopLevelItem == null)
                        newTopLevelItem = candidateItem;
                    else
                        ReleaseCom(candidateItem);
                }

                Thread.Sleep(5);
            }

            if (matchingShellItem == null
                && navigatedExistingItem == null
                && newTopLevelItem == null)
            {
                unselectedTargetItem = FindShellItemAtPath(
                    observerWindows,
                    fixture,
                    beforeTabs,
                    beforeLocations,
                    out unselectedTargetTopLevel,
                    out unselectedTargetTab);
            }

            Thread.Sleep(120);
            Application.DoEvents();
        }
        finally
        {
            selectionObserver.Dispose();
            UnhookWinEvent(hook);
            ReleaseCom(observerWindows);
            ReleaseCom(observerShell);
        }

        List<EventRecord> snapshot;
        lock (EventsLock)
            snapshot = Events.ToList();

        var newTopLevelEvents = snapshot
            .Where(item => item.Handle != IntPtr.Zero && !beforeWindows.Contains(item.Handle))
            .ToList();
        var newTopLevelObserved = newTopLevelEvents.Any();
        var newTopLevelShown = newTopLevelEvents.Any(item => item.EventType == EventObjectShow);

        var defaultPageSelectedMilliseconds = matchLatencyMs.HasValue
            ? selectionObserver.GetInitialNewTabSelectionDuration(
                matchingTopLevel,
                matchLatencyMs.Value)
            : null;

        var unselectedTargetOpenedAsTab = unselectedTargetItem != null
                                          && beforeWindows.Contains(unselectedTargetTopLevel)
                                          && unselectedTargetTab != IntPtr.Zero
                                          && !beforeTabs.Contains(unselectedTargetTab);
        TryCloseCreatedTab(
            unselectedTargetItem,
            unselectedTargetOpenedAsTab,
            unselectedTargetTopLevel,
            unselectedTargetTab,
            beforeWindows,
            beforeTabs);

        TryCloseCreatedTab(
            matchingShellItem,
            openedAsTab,
            matchingTopLevel,
            matchingTab,
            beforeWindows,
            beforeTabs);
        TryRestoreShellItem(navigatedExistingItem, matchingTab, beforeLocations);
        TryCloseShellItem(newTopLevelItem);
        if (ownsFixture)
            TryDeleteFixture(fixture, fixtureRoot);

        var latencyWithinTarget = matchLatencyMs.HasValue
                                  && matchLatencyMs.Value <= maximumAcceptableLatencyMs;
        var intermediateDefaultPageObserved = defaultPageSelectedMilliseconds >= 50;
        var structurallyGreen = matchingShellItem != null && openedAsTab && !newTopLevelObserved;
        var green = structurallyGreen && latencyWithinTarget && !intermediateDefaultPageObserved;
        var result = new Result
        {
            Verdict = green ? "GREEN" : "RED",
            Detail = green
                ? "直接增加标签，没有创建新的 Explorer 顶层窗口，也没有持续显示默认中间页。"
                : intermediateDefaultPageObserved
                    ? $"新标签的默认页被选中约 {defaultPageSelectedMilliseconds} ms。"
                : structurallyGreen && !latencyWithinTarget
                    ? $"结构零闪烁，但 {matchLatencyMs} ms 超过 {maximumAcceptableLatencyMs} ms 性能目标。"
                : newTopLevelObserved
                        ? "打开过程中出现了新的 Explorer 顶层窗口，因此存在闪烁路径。"
                        : matchingShellItem == null
                            ? "超时：没有找到测试文件夹对应的新标签。"
                            : "测试文件夹没有进入测试前已有的 Explorer 窗口。",
            Fixture = fixture,
            OpenedAsTab = openedAsTab,
            NewTopLevelWindowObserved = newTopLevelObserved,
            NewTopLevelShowObserved = newTopLevelShown,
            MatchLatencyMs = matchLatencyMs,
            LatencyWithinTarget = latencyWithinTarget,
            IntermediateDefaultPageObserved = intermediateDefaultPageObserved,
            DefaultPageSelectedMilliseconds = defaultPageSelectedMilliseconds,
            Events = newTopLevelEvents
        };
        WriteResult(result);
        return green ? 0 : 1;
    }

    private static void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (objectId != ObjidWindow || childId != 0 || window == IntPtr.Zero)
            return;

        if (!string.Equals(GetClassName(window), "CabinetWClass", StringComparison.OrdinalIgnoreCase))
            return;

        lock (EventsLock)
        {
            Events.Add(new EventRecord
            {
                EventType = eventType,
                Handle = window,
                Milliseconds = Clock.ElapsedMilliseconds
            });
        }
    }

    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashCount * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashCount);
                result.Append(character);
            }
            backslashCount = 0;
        }

        result.Append('\\', backslashCount * 2);
        result.Append('"');
        return result.ToString();
    }

    private static object? FindShellItemAtPath(
        object windows,
        string expectedPath,
        HashSet<IntPtr> tabsPresentBeforeRequest,
        IReadOnlyDictionary<IntPtr, string> locationsBeforeRequest,
        out IntPtr topLevelWindow,
        out IntPtr tabWindow)
    {
        var normalizedExpectedPath = expectedPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        topLevelWindow = IntPtr.Zero;
        tabWindow = IntPtr.Zero;
        object? existingMatch = null;
        var existingTopLevel = IntPtr.Zero;
        var existingTab = IntPtr.Zero;
        try
        {
            var count = (int)((dynamic)windows).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = (object)((dynamic)windows).Item(index);
                    var location = NormalizeLocation((string?)((dynamic)item).LocationURL);
                    if (!string.Equals(location, normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        ReleaseCom(item);
                        continue;
                    }

                    var candidateTopLevel = new IntPtr(Convert.ToInt64(((dynamic)item).HWND));
                    var candidateTab = GetTabHandle(item);
                    if (candidateTab != IntPtr.Zero && !tabsPresentBeforeRequest.Contains(candidateTab))
                    {
                        ReleaseCom(existingMatch);
                        topLevelWindow = candidateTopLevel;
                        tabWindow = candidateTab;
                        return item;
                    }

                    var tabAlreadyHadTarget = locationsBeforeRequest.TryGetValue(
                                                  candidateTab,
                                                  out var originalLocation)
                                              && string.Equals(
                                                  originalLocation,
                                                  normalizedExpectedPath,
                                                  StringComparison.OrdinalIgnoreCase);
                    if (existingMatch == null && !tabAlreadyHadTarget)
                    {
                        existingMatch = item;
                        existingTopLevel = candidateTopLevel;
                        existingTab = candidateTab;
                    }
                    else
                    {
                        ReleaseCom(item);
                    }
                }
                catch
                {
                    ReleaseCom(item);
                }
            }

            topLevelWindow = existingTopLevel;
            tabWindow = existingTab;
            return existingMatch;
        }
        catch
        {
            ReleaseCom(existingMatch);
            return null;
        }
    }

    private static Dictionary<IntPtr, string> SnapshotTabLocations()
    {
        var result = new Dictionary<IntPtr, string>();
        object? shell = null;
        object? windows = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return result;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return result;

            windows = ((dynamic)shell).Windows();
            var count = (int)((dynamic)windows).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = (object)((dynamic)windows).Item(index);
                    var tab = GetTabHandle(item);
                    var location = NormalizeLocation((string?)((dynamic)item).LocationURL);
                    if (tab != IntPtr.Zero && !string.IsNullOrWhiteSpace(location))
                        result[tab] = location!;
                }
                catch
                {
                    // A tab may disappear while ShellWindows is enumerated.
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
        }
        catch
        {
            // A missing snapshot only disables current-tab restoration.
        }
        finally
        {
            ReleaseCom(windows);
            ReleaseCom(shell);
        }

        return result;
    }

    private static IntPtr GetTabHandle(object item)
    {
        if (!(item is IServiceProvider serviceProvider)) return IntPtr.Zero;
        var shellBrowserGuid = typeof(IShellBrowser).GUID;
        serviceProvider.QueryService(ref shellBrowserGuid, ref shellBrowserGuid, out var shellBrowser);
        if (shellBrowser == null) return IntPtr.Zero;
        try
        {
            shellBrowser.GetWindow(out var handle);
            return handle;
        }
        finally
        {
            Marshal.ReleaseComObject(shellBrowser);
        }
    }

    private static IEnumerable<IntPtr> GetAllExplorerTabs()
    {
        foreach (var topLevel in FindAllWindows("CabinetWClass"))
        foreach (var tab in FindAllWindows("ShellTabWindowClass", topLevel))
            yield return tab;
    }

    private static void PrepareExplorerForeground(IEnumerable<IntPtr> windows)
    {
        var target = windows
            .OrderByDescending(window => FindAllWindows("ShellTabWindowClass", window).Count())
            .FirstOrDefault();
        if (target == IntPtr.Zero) return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            keybd_event(0x86, 0, 0, IntPtr.Zero);
            keybd_event(0x86, 0, 0x0002, IntPtr.Zero);
            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0
                           && foregroundThread != currentThread
                           && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(target);
                SetForegroundWindow(target);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(currentThread, foregroundThread, false);
            }
            Thread.Sleep(40);
            if (GetForegroundWindow() == target)
                return;
        }
    }

    private static string? NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        try
        {
            return new Uri(location).LocalPath.TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return location!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static void TryCloseShellItem(object? item)
    {
        if (item == null) return;
        try
        {
            ((dynamic)item).Quit();
        }
        catch
        {
            // The test item may already have been closed by Explorer.
        }
        finally
        {
            ReleaseCom(item);
        }
    }

    private static void TryCloseCreatedTab(
        object? item,
        bool openedAsTab,
        IntPtr expectedTopLevel,
        IntPtr expectedTab,
        IReadOnlyCollection<IntPtr> windowsPresentBeforeRequest,
        IReadOnlyCollection<IntPtr> tabsPresentBeforeRequest)
    {
        if (item == null) return;
        try
        {
            if (!openedAsTab
                || expectedTopLevel == IntPtr.Zero
                || expectedTab == IntPtr.Zero
                || !windowsPresentBeforeRequest.Contains(expectedTopLevel)
                || tabsPresentBeforeRequest.Contains(expectedTab)
                || FindAllWindows("ShellTabWindowClass", expectedTopLevel).Count() <= 1)
                return;

            var actualTopLevel = new IntPtr(Convert.ToInt64(((dynamic)item).HWND));
            var actualTab = GetTabHandle(item);
            if (actualTopLevel == expectedTopLevel && actualTab == expectedTab)
                ((dynamic)item).Quit();
        }
        catch
        {
            // Never risk closing an existing tab or the last Explorer window.
        }
        finally
        {
            ReleaseCom(item);
        }
    }

    private static void TryRestoreShellItem(
        object? item,
        IntPtr tabHandle,
        IReadOnlyDictionary<IntPtr, string> originalLocations)
    {
        if (item == null) return;
        try
        {
            if (tabHandle != IntPtr.Zero && originalLocations.TryGetValue(tabHandle, out var original))
                ((dynamic)item).Navigate2(original);
        }
        catch
        {
            // Best effort: never close a tab that existed before the test.
        }
        finally
        {
            ReleaseCom(item);
        }
    }

    private static void TryDeleteFixture(string fixture, string fixtureRoot)
    {
        try
        {
            var resolvedFixture = Path.GetFullPath(fixture);
            var resolvedRoot = Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
            if (!resolvedFixture.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                return;
            if (Directory.Exists(resolvedFixture) && !Directory.EnumerateFileSystemEntries(resolvedFixture).Any())
                Directory.Delete(resolvedFixture, false);
        }
        catch
        {
            // Cleanup does not affect the verdict.
        }
    }

    private static IEnumerable<IntPtr> FindAllWindows(string className, IntPtr parent = default)
    {
        var current = IntPtr.Zero;
        while (true)
        {
            current = FindWindowEx(parent, current, className, null);
            if (current == IntPtr.Zero) yield break;
            yield return current;
        }
    }

    private static string GetClassName(IntPtr window)
    {
        var builder = new StringBuilder(128);
        RealGetWindowClass(window, builder, (uint)builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
            return string.Empty;

        var builder = new StringBuilder(length + 1);
        GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }

    private static void WriteResult(Result result)
    {
        Console.WriteLine("Verdict=" + result.Verdict);
        Console.WriteLine("Detail=" + result.Detail);
        Console.WriteLine("Fixture=" + result.Fixture);
        Console.WriteLine("OpenedAsTab=" + result.OpenedAsTab);
        Console.WriteLine("NewTopLevelWindowObserved=" + result.NewTopLevelWindowObserved);
        Console.WriteLine("NewTopLevelShowObserved=" + result.NewTopLevelShowObserved);
        Console.WriteLine("MatchLatencyMs=" + (result.MatchLatencyMs?.ToString() ?? "null"));
        Console.WriteLine("LatencyWithinTarget=" + result.LatencyWithinTarget);
        Console.WriteLine("IntermediateDefaultPageObserved=" + result.IntermediateDefaultPageObserved);
        Console.WriteLine(
            "DefaultPageSelectedMilliseconds=" +
            (result.DefaultPageSelectedMilliseconds?.ToString() ?? "null"));
        foreach (var item in result.Events ?? new List<EventRecord>())
            Console.WriteLine($"Event={item.EventType:X4},Hwnd=0x{item.Handle.ToInt64():X},Ms={item.Milliseconds}");
    }

    private sealed class Result
    {
        public string Verdict { get; set; } = "BLOCKED";
        public string Detail { get; set; } = string.Empty;
        public string Fixture { get; set; } = string.Empty;
        public bool OpenedAsTab { get; set; }
        public bool NewTopLevelWindowObserved { get; set; }
        public bool NewTopLevelShowObserved { get; set; }
        public long? MatchLatencyMs { get; set; }
        public bool LatencyWithinTarget { get; set; }
        public bool IntermediateDefaultPageObserved { get; set; }
        public long? DefaultPageSelectedMilliseconds { get; set; }
        public List<EventRecord>? Events { get; set; }
    }

    private sealed class ExplorerTabSelectionObserver : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<IntPtr, WindowSelectionState> _states;
        private readonly Thread _thread;
        private volatile bool _stopping;
        private int _started;

        public ExplorerTabSelectionObserver(IEnumerable<IntPtr> windows)
        {
            _states = windows.ToDictionary(
                window => window,
                window => new WindowSelectionState(
                    FindAllWindows("ShellTabWindowClass", window).ToHashSet()));
            _thread = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = "QingTab zero-flicker native observer",
                Priority = ThreadPriority.AboveNormal
            };
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                _thread.Start();
        }

        private void SampleLoop()
        {
            while (!_stopping)
            {
                Sample(Clock.ElapsedMilliseconds);
                Thread.Sleep(1);
            }
        }

        private void Sample(long elapsedMilliseconds)
        {
            lock (_gate)
            {
                foreach (var pair in _states)
                {
                    var state = pair.Value;
                    var currentTabs = FindAllWindows("ShellTabWindowClass", pair.Key).ToArray();
                    if (currentTabs.Length <= state.InitialTabs.Count)
                        continue;

                    var activeTab = WinApiFindActiveTab(pair.Key);
                    state.NewTabIsSelected = activeTab != IntPtr.Zero
                                             && !state.InitialTabs.Contains(activeTab);

                    var intermediateSelected = state.NewTabIsSelected;
                    if (intermediateSelected && !state.FirstSelectedAt.HasValue)
                        state.FirstSelectedAt = elapsedMilliseconds;
                    else if (!intermediateSelected
                             && state.FirstSelectedAt.HasValue
                             && !state.FirstDeselectedAt.HasValue)
                        state.FirstDeselectedAt = elapsedMilliseconds;
                }
            }
        }

        public long? GetInitialNewTabSelectionDuration(IntPtr window, long targetVisibleAt)
        {
            lock (_gate)
            {
                if (!_states.TryGetValue(window, out var state) || !state.FirstSelectedAt.HasValue)
                    return null;

                return Math.Max(
                    0,
                    (state.FirstDeselectedAt ?? targetVisibleAt) - state.FirstSelectedAt.Value);
            }
        }

        public bool IsNewTabSelected(IntPtr window)
        {
            lock (_gate)
                return _states.TryGetValue(window, out var state) && state.NewTabIsSelected;
        }

        public void Dispose()
        {
            _stopping = true;
            if (Volatile.Read(ref _started) != 0 && _thread.IsAlive)
                _thread.Join(500);
        }

        private static IntPtr WinApiFindActiveTab(IntPtr window)
        {
            return FindWindowEx(window, IntPtr.Zero, "ShellTabWindowClass", null);
        }

        private sealed class WindowSelectionState
        {
            public WindowSelectionState(HashSet<IntPtr> initialTabs)
            {
                InitialTabs = initialTabs;
            }

            public HashSet<IntPtr> InitialTabs { get; }
            public bool NewTabIsSelected { get; set; }
            public long? FirstSelectedAt { get; set; }
            public long? FirstDeselectedAt { get; set; }
        }
    }

    private sealed class EventRecord
    {
        public uint EventType { get; set; }
        public IntPtr Handle { get; set; }
        public long Milliseconds { get; set; }
    }

    private sealed class TemporaryFolderOpenOverride : IDisposable
    {
        private const string FolderClassPath = @"Software\Classes\Folder";
        private const string OpenCommandPath = FolderClassPath + @"\shell\open\command";
        private const string ProbeVerbName = "QingTabProbe";
        private const string ProbeVerbPath = FolderClassPath + @"\shell\" + ProbeVerbName;
        private const string ProbeCommandPath = ProbeVerbPath + @"\command";
        private readonly string _command;
        private readonly string _mode;
        private bool _disposed;

        public TemporaryFolderOpenOverride(string executable, string mode)
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(FolderClassPath, false))
            {
                if (existing != null)
                    throw new InvalidOperationException("Safety stop: HKCU\\Software\\Classes\\Folder already exists.");
            }

            _command = $"\"{executable}\" \"%1\"";
            _mode = mode;
            if (string.Equals(_mode, "custom-default-verb", StringComparison.OrdinalIgnoreCase))
            {
                using (var shell = Registry.CurrentUser.CreateSubKey(FolderClassPath + @"\shell", true))
                    shell?.SetValue(string.Empty, ProbeVerbName, RegistryValueKind.String);
                using (var commandKey = Registry.CurrentUser.CreateSubKey(ProbeCommandPath, true))
                    commandKey?.SetValue(string.Empty, _command, RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(OpenCommandPath, true);
                if (key == null) throw new InvalidOperationException("Unable to create the temporary Folder open override.");
                key.SetValue(string.Empty, _command, RegistryValueKind.String);
                if (!string.Equals(_mode, "open-command-only", StringComparison.OrdinalIgnoreCase))
                    key.SetValue("DelegateExecute", string.Empty, RegistryValueKind.String);
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(250);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            using (var folder = Registry.CurrentUser.OpenSubKey(FolderClassPath, false))
            using (var shell = Registry.CurrentUser.OpenSubKey(FolderClassPath + @"\shell", false))
            {
                var treeIsExpected = folder != null
                                     && folder.GetSubKeyNames().SequenceEqual(new[] { "shell" }, StringComparer.OrdinalIgnoreCase)
                                     && folder.GetValueNames().Length == 0
                                     && shell != null;

                if (treeIsExpected && string.Equals(_mode, "custom-default-verb", StringComparison.OrdinalIgnoreCase))
                {
                    using var verbKey = Registry.CurrentUser.OpenSubKey(ProbeVerbPath, false);
                    using var command = Registry.CurrentUser.OpenSubKey(ProbeCommandPath, false);
                    treeIsExpected = shell!.GetSubKeyNames().SequenceEqual(new[] { ProbeVerbName }, StringComparer.OrdinalIgnoreCase)
                                     && shell.GetValueNames().SequenceEqual(new[] { string.Empty }, StringComparer.OrdinalIgnoreCase)
                                     && string.Equals(shell.GetValue(string.Empty) as string, ProbeVerbName, StringComparison.Ordinal)
                                     && verbKey != null
                                     && verbKey.GetSubKeyNames().SequenceEqual(new[] { "command" }, StringComparer.OrdinalIgnoreCase)
                                     && verbKey.GetValueNames().Length == 0
                                     && command != null
                                     && command.GetSubKeyNames().Length == 0
                                     && command.GetValueNames().SequenceEqual(new[] { string.Empty }, StringComparer.OrdinalIgnoreCase)
                                     && string.Equals(command.GetValue(string.Empty) as string, _command, StringComparison.Ordinal);
                }
                else if (treeIsExpected)
                {
                    using var open = Registry.CurrentUser.OpenSubKey(FolderClassPath + @"\shell\open", false);
                    using var command = Registry.CurrentUser.OpenSubKey(OpenCommandPath, false);
                    var expectedValues = string.Equals(_mode, "open-command-only", StringComparison.OrdinalIgnoreCase)
                        ? new[] { string.Empty }
                        : new[] { string.Empty, "DelegateExecute" };
                    treeIsExpected = shell!.GetSubKeyNames().SequenceEqual(new[] { "open" }, StringComparer.OrdinalIgnoreCase)
                                     && shell.GetValueNames().Length == 0
                                     && open != null
                                     && open.GetSubKeyNames().SequenceEqual(new[] { "command" }, StringComparer.OrdinalIgnoreCase)
                                     && open.GetValueNames().Length == 0
                                     && command != null
                                     && command.GetSubKeyNames().Length == 0
                                     && command.GetValueNames().OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                                         .SequenceEqual(expectedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                                     && string.Equals(command.GetValue(string.Empty) as string, _command, StringComparison.Ordinal)
                                     && (expectedValues.Length == 1
                                         || string.Equals(command.GetValue("DelegateExecute") as string, string.Empty, StringComparison.Ordinal));
                }

                if (!treeIsExpected)
                    throw new InvalidOperationException("Safety stop: the temporary Folder class changed during the test and was left in place.");
            }

            Registry.CurrentUser.DeleteSubKeyTree(FolderClassPath, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
    [ComImport]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(
            ref Guid service,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellBrowser? shellBrowser);
    }

    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [ComImport]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr handle);
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scanCode, uint flags, IntPtr extraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string className,
        string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RealGetWindowClass(IntPtr window, StringBuilder className, uint maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
