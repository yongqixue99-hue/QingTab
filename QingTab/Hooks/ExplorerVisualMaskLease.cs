using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using QingTab.WinAPI;

namespace QingTab.Hooks;

public enum ExplorerVisualMaskAppearance
{
    Snapshot,
    LoadingPlaceholder
}

public static class ExplorerVisualMaskPresentation
{
    public static string CreateLoadingMessage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "正在打开…";

        var display = path!.Trim();
        if (display.Length >= 2
            && char.IsLetter(display[0])
            && display[1] == ':'
            && IsDriveRootSuffix(display, startIndex: 2))
            return "正在打开 " + char.ToUpperInvariant(display[0]) + " 盘…";

        display = display.TrimEnd('\\', '/');
        if (display.Length == 0) return "正在打开…";

        var separator = Math.Max(display.LastIndexOf('\\'), display.LastIndexOf('/'));
        if (separator >= 0 && separator + 1 < display.Length)
            display = display.Substring(separator + 1);
        if (display.Length > 48)
            display = display.Substring(0, 47) + "…";
        return "正在打开 " + display + "…";
    }

    private static bool IsDriveRootSuffix(string value, int startIndex)
    {
        for (var index = startIndex; index < value.Length; index++)
        {
            if (value[index] != '\\' && value[index] != '/') return false;
        }
        return true;
    }
}

public readonly struct ExplorerVisualMaskBounds
{
    private ExplorerVisualMaskBounds(int left, int top, int width, int height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsValid => Width > 0 && Height > 0;

    public static bool TryCreateBelowTabStrip(
        int clientLeft,
        int clientTop,
        int clientRight,
        int clientBottom,
        double tabListBottom,
        out ExplorerVisualMaskBounds bounds)
    {
        bounds = default;
        if (clientRight <= clientLeft
            || clientBottom <= clientTop
            || double.IsNaN(tabListBottom)
            || double.IsInfinity(tabListBottom))
            return false;

        var roundedTop = Math.Ceiling(tabListBottom);
        if (roundedTop < int.MinValue || roundedTop > int.MaxValue)
            return false;

        var top = Math.Max(clientTop, (int)roundedTop);
        if (top >= clientBottom) return false;

        var width = (long)clientRight - clientLeft;
        var height = (long)clientBottom - top;
        if (width <= 0 || width > int.MaxValue || height <= 0 || height > int.MaxValue)
            return false;

        bounds = new ExplorerVisualMaskBounds(clientLeft, top, (int)width, (int)height);
        return true;
    }

    public bool MatchesOwnerClient(
        int clientLeft,
        int clientTop,
        int clientRight,
        int clientBottom)
    {
        return IsValid
               && clientRight > clientLeft
               && clientBottom > clientTop
               && Left == clientLeft
               && Right == clientRight
               && Bottom == clientBottom
               && Top >= clientTop
               && Top < clientBottom;
    }
}

public enum ExplorerVisualMaskStopReason
{
    None,
    Released,
    OwnerIdentityChanged,
    OwnerNotForeground,
    GeometryChanged,
    HardTimeout,
    UserInputDetected
}

/// <summary>
/// Deterministic lifecycle policy for a transient mask. Time is supplied by
/// the caller so deadline and renewal behavior can be tested without a window.
/// Exactly one renewal acknowledgement is allowed, but renewal never moves the
/// absolute deadline. A stalled caller therefore cannot keep a mask alive by
/// repeatedly pulsing the renew event.
/// </summary>
public sealed class ExplorerVisualMaskStopPolicy
{
    public const int MaximumHardTimeoutMilliseconds = 4_500;

    private bool _renewed;

    public ExplorerVisualMaskStopPolicy(int hardTimeoutMilliseconds)
    {
        if (!IsValidHardTimeout(hardTimeoutMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(hardTimeoutMilliseconds));

        DeadlineMilliseconds = hardTimeoutMilliseconds;
    }

    public long DeadlineMilliseconds { get; }

    public static bool IsValidHardTimeout(int hardTimeoutMilliseconds)
    {
        return hardTimeoutMilliseconds > 0
               && hardTimeoutMilliseconds <= MaximumHardTimeoutMilliseconds;
    }

    public bool TryRenew(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        if (_renewed || elapsedMilliseconds >= DeadlineMilliseconds)
            return false;

        _renewed = true;
        return true;
    }

    public ExplorerVisualMaskStopReason Evaluate(
        long elapsedMilliseconds,
        bool releaseRequested,
        bool ownerIdentityIsValid,
        bool ownerIsForeground,
        bool geometryMatches)
    {
        return Evaluate(
            elapsedMilliseconds,
            releaseRequested,
            ownerIdentityIsValid,
            ownerIsForeground,
            geometryMatches,
            userInputUnchanged: true);
    }

    public ExplorerVisualMaskStopReason Evaluate(
        long elapsedMilliseconds,
        bool releaseRequested,
        bool ownerIdentityIsValid,
        bool ownerIsForeground,
        bool geometryMatches,
        bool userInputUnchanged)
    {
        if (elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        if (releaseRequested) return ExplorerVisualMaskStopReason.Released;
        if (!userInputUnchanged) return ExplorerVisualMaskStopReason.UserInputDetected;
        if (!ownerIdentityIsValid) return ExplorerVisualMaskStopReason.OwnerIdentityChanged;
        if (!ownerIsForeground) return ExplorerVisualMaskStopReason.OwnerNotForeground;
        if (!geometryMatches) return ExplorerVisualMaskStopReason.GeometryChanged;
        if (elapsedMilliseconds >= DeadlineMilliseconds)
            return ExplorerVisualMaskStopReason.HardTimeout;
        return ExplorerVisualMaskStopReason.None;
    }
}

/// <summary>
/// Thread-safe ownership state for a mask lease. Abandonment only hands
/// lifecycle responsibility to the hard timeout; it does not consume the
/// explicit release request that a later Dispose must still be able to make.
/// </summary>
public sealed class ExplorerVisualMaskLeaseState
{
    private int _abandoned;
    private int _releaseRequested;

    public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;
    public bool ReleaseRequested => Volatile.Read(ref _releaseRequested) != 0;
    public bool CanRenew => !IsAbandoned && !ReleaseRequested;

    public bool TryAbandonToTimeout()
    {
        if (ReleaseRequested) return false;
        return Interlocked.CompareExchange(ref _abandoned, 1, 0) == 0;
    }

    public bool TryRequestRelease()
    {
        return Interlocked.Exchange(ref _releaseRequested, 1) == 0;
    }
}

/// <summary>
/// Owns one opaque screenshot mask on a dedicated background STA thread. The
/// same thread creates, pumps, and destroys the layered window and all GDI
/// resources. Explorer itself is only observed; this type never sends it a
/// message or changes its redraw/show/DWM state.
/// </summary>
public sealed class ExplorerVisualMaskLease : IDisposable
{
    private const int ReadyWaitMaximumMilliseconds = 500;
    private const int ReleaseJoinMilliseconds = 150;
    private const string ExplorerWindowClass = "CabinetWClass";
    private const string ExplorerImageName = "explorer.exe";

    private readonly nint _explorer;
    private readonly uint _explorerProcessId;
    private readonly OwnerClientBounds _ownerClientBounds;
    private readonly ExplorerVisualMaskBounds _maskBounds;
    private readonly ExplorerVisualMaskAppearance _appearance;
    private readonly string _loadingMessage;
    private readonly int _hardTimeoutMilliseconds;
    private readonly uint _lastInputTick;
    private readonly ExplorerVisualMaskLeaseState _leaseState = new();
    private readonly EventWaitHandle _ready = new(false, EventResetMode.ManualReset);
    private readonly EventWaitHandle _release = new(false, EventResetMode.ManualReset);
    private readonly EventWaitHandle _renew = new(false, EventResetMode.AutoReset);
    private readonly EventWaitHandle _renewAcknowledged = new(false, EventResetMode.AutoReset);
    private readonly TaskCompletionSource<bool> _completed = new();
    private readonly object _renewGate = new();
    private readonly Thread _thread;

    private Exception? _startupFailure;
    private int _presented;
    private int _renewResult;
    private int _disposed;
    private int _handlesDisposed;
    private int _cleanupQueued;

    private ExplorerVisualMaskLease(
        nint explorer,
        uint explorerProcessId,
        OwnerClientBounds ownerClientBounds,
        ExplorerVisualMaskBounds maskBounds,
        ExplorerVisualMaskAppearance appearance,
        string loadingMessage,
        uint lastInputTick,
        int hardTimeoutMilliseconds)
    {
        _explorer = explorer;
        _explorerProcessId = explorerProcessId;
        _ownerClientBounds = ownerClientBounds;
        _maskBounds = maskBounds;
        _appearance = appearance;
        _loadingMessage = loadingMessage;
        _lastInputTick = lastInputTick;
        _hardTimeoutMilliseconds = hardTimeoutMilliseconds;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "QingTab Explorer visual mask"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public static bool TryCalculateBounds(
        nint explorer,
        double tabListBottom,
        out ExplorerVisualMaskBounds bounds)
    {
        bounds = default;
        return TryGetOwnerClientBounds(explorer, out var client)
               && ExplorerVisualMaskBounds.TryCreateBelowTabStrip(
                   client.Left,
                   client.Top,
                   client.Right,
                   client.Bottom,
                   tabListBottom,
                   out bounds);
    }

    public static bool TryStart(
        nint explorer,
        ExplorerVisualMaskBounds bounds,
        int hardTimeoutMs,
        ExplorerVisualMaskAppearance appearance,
        string loadingMessage,
        out ExplorerVisualMaskLease? lease)
    {
        lease = null;
        if (!bounds.IsValid
            || !ExplorerVisualMaskStopPolicy.IsValidHardTimeout(hardTimeoutMs))
            return false;

        ExplorerVisualMaskLease? candidate = null;
        try
        {
            if (!TryCaptureOwner(
                    explorer,
                    bounds,
                    out var processId,
                    out var ownerClientBounds,
                    out var lastInputTick))
                return false;

            candidate = new ExplorerVisualMaskLease(
                explorer,
                processId,
                ownerClientBounds,
                bounds,
                appearance,
                loadingMessage ?? string.Empty,
                lastInputTick,
                hardTimeoutMs);
            candidate._thread.Start();
            var readyWait = Math.Min(hardTimeoutMs, ReadyWaitMaximumMilliseconds);
            if (!candidate._ready.WaitOne(readyWait)
                || candidate._startupFailure != null
                || Volatile.Read(ref candidate._presented) == 0
                || !candidate._thread.IsAlive)
            {
                candidate.CancelFailedStart();
                return false;
            }

            lease = candidate;
            return true;
        }
        catch
        {
            candidate?.CancelFailedStart();
            return false;
        }
    }

    public bool TryRenew()
    {
        if (Volatile.Read(ref _disposed) != 0
            || !_leaseState.CanRenew
            || !_thread.IsAlive)
            return false;

        lock (_renewGate)
        {
            if (Volatile.Read(ref _disposed) != 0
                || !_leaseState.CanRenew
                || !_thread.IsAlive)
                return false;

            while (_renewAcknowledged.WaitOne(0)) { }
            Volatile.Write(ref _renewResult, 0);
            try
            {
                _renew.Set();
                var acknowledgementWait = Math.Min(_hardTimeoutMilliseconds, 100);
                if (!_renewAcknowledged.WaitOne(acknowledgementWait))
                    return false;
                return Volatile.Read(ref _renewResult) != 0 && _thread.IsAlive;
            }
            catch
            {
                return false;
            }
        }
    }

    public void AbandonToTimeout()
    {
        if (Volatile.Read(ref _disposed) != 0
            || !_leaseState.TryAbandonToTimeout())
            return;
        QueueHandleCleanup();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _leaseState.TryRequestRelease();
        lock (_renewGate)
        {
            try { _release.Set(); } catch { }
        }

        if (Thread.CurrentThread == _thread)
        {
            QueueHandleCleanup();
            return;
        }

        try
        {
            if (_thread.Join(ReleaseJoinMilliseconds))
                DisposeHandles();
            else
                QueueHandleCleanup();
        }
        catch
        {
            QueueHandleCleanup();
        }
    }

    private void Run()
    {
        ExplorerVisualMaskWindow? maskWindow = null;
        var stopPolicy = new ExplorerVisualMaskStopPolicy(_hardTimeoutMilliseconds);
        var startupClock = Stopwatch.StartNew();
        try
        {
            if (ShouldStop(startupClock, stopPolicy)) return;

            maskWindow = ExplorerVisualMaskWindow.Create(
                _explorer,
                _maskBounds,
                _appearance,
                _loadingMessage);
            if (ShouldStop(startupClock, stopPolicy)) return;

            maskWindow.PrepareFrame();
            if (ShouldStop(startupClock, stopPolicy)) return;

            // Start the non-renewable absolute lifetime before ShowWindow. This
            // deliberately counts any time spent inside ShowWindow, so the
            // visible lifetime can never be longer than the configured limit.
            var presentationClock = Stopwatch.StartNew();
            maskWindow.ShowPreparedFrame();
            if (ShouldStop(presentationClock, stopPolicy)) return;

            Volatile.Write(ref _presented, 1);
            _ready.Set();

            while (true)
            {
                if (_renew.WaitOne(0))
                {
                    var renewed = _leaseState.CanRenew
                                  && stopPolicy.TryRenew(presentationClock.ElapsedMilliseconds);
                    Volatile.Write(ref _renewResult, renewed ? 1 : 0);
                    _renewAcknowledged.Set();
                }

                if (ShouldStop(presentationClock, stopPolicy))
                    break;

                maskWindow.PumpMessages(maximumMessages: 32);
                Thread.Sleep(2);
            }
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
        }
        finally
        {
            maskWindow?.Dispose();
            Volatile.Write(ref _renewResult, 0);
            try { _renewAcknowledged.Set(); } catch { }
            try { _ready.Set(); } catch { }
            _completed.TrySetResult(true);
        }
    }

    private bool ShouldStop(
        Stopwatch clock,
        ExplorerVisualMaskStopPolicy stopPolicy)
    {
        if (_release.WaitOne(0)) return true;

        return stopPolicy.Evaluate(
                   clock.ElapsedMilliseconds,
                   releaseRequested: _leaseState.ReleaseRequested,
                   ownerIdentityIsValid: OwnerIdentityIsValid(),
                   ownerIsForeground: OwnerIsForeground(),
                   geometryMatches: OwnerGeometryMatches(),
                   userInputUnchanged: UserInputIsUnchanged())
               != ExplorerVisualMaskStopReason.None;
    }

    private bool UserInputIsUnchanged()
    {
        return VisualMaskNative.TryGetLastInputTick(out var currentInputTick)
               && currentInputTick == _lastInputTick;
    }

    private bool OwnerIdentityIsValid()
    {
        if (!WinApi.IsWindow(_explorer)
            || !WinApi.IsWindowVisible(_explorer)
            || WinApi.IsIconic(_explorer)
            || !VisualMaskNative.IsWindowUncloaked(_explorer)
            || WinApi.GetAncestor(_explorer, WinApi.GA_ROOT) != _explorer
            || !WinApi.IsWindowHasClassName(
                _explorer,
                ExplorerWindowClass,
                StringComparison.Ordinal))
            return false;

        return WinApi.GetWindowThreadProcessId(_explorer, out var currentProcessId) != 0
               && currentProcessId == _explorerProcessId;
    }

    private bool OwnerIsForeground()
    {
        return WinApi.GetForegroundWindow() == _explorer;
    }

    private bool OwnerGeometryMatches()
    {
        return TryGetOwnerClientBounds(_explorer, out var current)
               && current.Equals(_ownerClientBounds)
               && _maskBounds.MatchesOwnerClient(
                   current.Left,
                   current.Top,
                   current.Right,
                   current.Bottom);
    }

    private static bool TryCaptureOwner(
        nint explorer,
        ExplorerVisualMaskBounds maskBounds,
        out uint processId,
        out OwnerClientBounds ownerClientBounds,
        out uint lastInputTick)
    {
        processId = 0;
        ownerClientBounds = default;
        lastInputTick = 0;
        if (explorer == 0
            || !WinApi.IsWindow(explorer)
            || !WinApi.IsWindowVisible(explorer)
            || WinApi.IsIconic(explorer)
            || !VisualMaskNative.IsWindowUncloaked(explorer)
            || WinApi.GetForegroundWindow() != explorer
            || WinApi.GetAncestor(explorer, WinApi.GA_ROOT) != explorer
            || !WinApi.IsWindowHasClassName(
                explorer,
                ExplorerWindowClass,
                StringComparison.Ordinal)
            || WinApi.GetWindowThreadProcessId(explorer, out processId) == 0
            || processId == 0
            || processId > int.MaxValue)
            return false;

        var processPath = WinApi.GetProcessPath((int)processId);
        if (string.IsNullOrWhiteSpace(processPath)
            || !string.Equals(
                Path.GetFileName(processPath),
                ExplorerImageName,
                StringComparison.OrdinalIgnoreCase))
            return false;

        return TryGetOwnerClientBounds(explorer, out ownerClientBounds)
               && maskBounds.MatchesOwnerClient(
                   ownerClientBounds.Left,
                   ownerClientBounds.Top,
                   ownerClientBounds.Right,
                   ownerClientBounds.Bottom)
               && VisualMaskNative.TryGetLastInputTick(out lastInputTick);
    }

    private static bool TryGetOwnerClientBounds(nint owner, out OwnerClientBounds bounds)
    {
        bounds = default;
        if (!VisualMaskNative.GetClientRect(owner, out var client)) return false;

        var topLeft = new VisualMaskNative.Point { X = client.Left, Y = client.Top };
        var bottomRight = new VisualMaskNative.Point { X = client.Right, Y = client.Bottom };
        if (!VisualMaskNative.ClientToScreen(owner, ref topLeft)
            || !VisualMaskNative.ClientToScreen(owner, ref bottomRight)
            || bottomRight.X <= topLeft.X
            || bottomRight.Y <= topLeft.Y)
            return false;

        bounds = new OwnerClientBounds(
            topLeft.X,
            topLeft.Y,
            bottomRight.X,
            bottomRight.Y);
        return true;
    }

    private void CancelFailedStart()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _leaseState.TryRequestRelease();
        try { _release.Set(); } catch { }
        try
        {
            if ((_thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0)
                _completed.TrySetResult(true);
        }
        catch { }
        QueueHandleCleanup();
    }

    private void QueueHandleCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupQueued, 1) != 0) return;
        _completed.Task.ContinueWith(
            _ => DisposeHandles(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeHandles()
    {
        lock (_renewGate)
        {
            if (Interlocked.Exchange(ref _handlesDisposed, 1) != 0) return;
            _ready.Dispose();
            _release.Dispose();
            _renew.Dispose();
            _renewAcknowledged.Dispose();
        }
    }

    private readonly struct OwnerClientBounds : IEquatable<OwnerClientBounds>
    {
        internal OwnerClientBounds(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        internal int Left { get; }
        internal int Top { get; }
        internal int Right { get; }
        internal int Bottom { get; }

        public bool Equals(OwnerClientBounds other)
        {
            return Left == other.Left
                   && Top == other.Top
                   && Right == other.Right
                   && Bottom == other.Bottom;
        }

        public override bool Equals(object? value)
        {
            return value is OwnerClientBounds other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Left;
                hash = (hash * 397) ^ Top;
                hash = (hash * 397) ^ Right;
                hash = (hash * 397) ^ Bottom;
                return hash;
            }
        }
    }
}

internal sealed class ExplorerVisualMaskWindow : IDisposable
{
    private static readonly VisualMaskNative.WindowProcedure WindowProcedure = ProcessWindowMessage;

    private readonly string _className;
    private readonly nint _instance;
    private nint _screenDeviceContext;
    private nint _memoryDeviceContext;
    private nint _bitmap;
    private nint _oldBitmap;
    private readonly ExplorerVisualMaskBounds _bounds;
    private nint _window;
    private bool _disposed;

    private ExplorerVisualMaskWindow(
        string className,
        nint instance,
        nint screenDeviceContext,
        nint memoryDeviceContext,
        nint bitmap,
        nint oldBitmap,
        ExplorerVisualMaskBounds bounds,
        nint window)
    {
        _className = className;
        _instance = instance;
        _screenDeviceContext = screenDeviceContext;
        _memoryDeviceContext = memoryDeviceContext;
        _bitmap = bitmap;
        _oldBitmap = oldBitmap;
        _bounds = bounds;
        _window = window;
    }

    internal static ExplorerVisualMaskWindow Create(
        nint explorer,
        ExplorerVisualMaskBounds bounds,
        ExplorerVisualMaskAppearance appearance,
        string loadingMessage)
    {
        if (!bounds.IsValid) throw new ArgumentOutOfRangeException(nameof(bounds));

        var className = "QingTab.VisualMask." + Guid.NewGuid().ToString("N");
        var instance = VisualMaskNative.GetModuleHandle(null);
        var windowClass = new VisualMaskNative.WindowClass
        {
            Size = (uint)Marshal.SizeOf(typeof(VisualMaskNative.WindowClass)),
            WindowProcedure = WindowProcedure,
            Instance = instance,
            ClassName = className
        };
        if (VisualMaskNative.RegisterClassEx(ref windowClass) == 0)
            throw new InvalidOperationException("Could not register the visual-mask window class.");

        nint screenDeviceContext = 0;
        nint memoryDeviceContext = 0;
        nint bitmap = 0;
        nint oldBitmap = 0;
        nint window = 0;
        try
        {
            screenDeviceContext = VisualMaskNative.GetDC(0);
            if (screenDeviceContext == 0)
                throw new InvalidOperationException("Could not acquire the screen device context.");

            memoryDeviceContext = VisualMaskNative.CreateCompatibleDC(screenDeviceContext);
            bitmap = VisualMaskNative.CreateCompatibleBitmap(
                screenDeviceContext,
                bounds.Width,
                bounds.Height);
            if (memoryDeviceContext == 0 || bitmap == 0)
                throw new InvalidOperationException("Could not allocate the visual-mask bitmap.");

            oldBitmap = VisualMaskNative.SelectObject(memoryDeviceContext, bitmap);
            if (oldBitmap == 0 || oldBitmap == (nint)(-1))
                throw new InvalidOperationException("Could not select the visual-mask bitmap.");

            if (appearance == ExplorerVisualMaskAppearance.LoadingPlaceholder)
            {
                RenderLoadingPlaceholder(
                    memoryDeviceContext,
                    screenDeviceContext,
                    bounds,
                    loadingMessage);
            }
            else if (!VisualMaskNative.BitBlt(
                         memoryDeviceContext,
                         0,
                         0,
                         bounds.Width,
                         bounds.Height,
                         screenDeviceContext,
                         bounds.Left,
                         bounds.Top,
                         VisualMaskNative.Srccopy | VisualMaskNative.CaptureBlt))
            {
                throw new InvalidOperationException("Could not capture the Explorer visual.");
            }

            window = VisualMaskNative.CreateWindowEx(
                VisualMaskNative.WsExLayered
                | VisualMaskNative.WsExTransparent
                | VisualMaskNative.WsExNoActivate
                | VisualMaskNative.WsExToolWindow,
                className,
                string.Empty,
                VisualMaskNative.WsPopup,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                explorer,
                0,
                instance,
                0);
            if (window == 0)
                throw new InvalidOperationException("Could not create the visual-mask window.");

            return new ExplorerVisualMaskWindow(
                className,
                instance,
                screenDeviceContext,
                memoryDeviceContext,
                bitmap,
                oldBitmap,
                bounds,
                window);
        }
        catch
        {
            if (window != 0) VisualMaskNative.DestroyWindow(window);
            if (oldBitmap != 0
                && oldBitmap != (nint)(-1)
                && memoryDeviceContext != 0)
                VisualMaskNative.SelectObject(memoryDeviceContext, oldBitmap);
            if (bitmap != 0) VisualMaskNative.DeleteObject(bitmap);
            if (memoryDeviceContext != 0) VisualMaskNative.DeleteDC(memoryDeviceContext);
            if (screenDeviceContext != 0) VisualMaskNative.ReleaseDC(0, screenDeviceContext);
            VisualMaskNative.UnregisterClass(className, instance);
            throw;
        }
    }

    internal void PrepareFrame()
    {
        var destination = new VisualMaskNative.Point { X = _bounds.Left, Y = _bounds.Top };
        var source = new VisualMaskNative.Point();
        var size = new VisualMaskNative.Size { Width = _bounds.Width, Height = _bounds.Height };
        if (!VisualMaskNative.UpdateLayeredWindow(
                _window,
                _screenDeviceContext,
                ref destination,
                ref size,
                _memoryDeviceContext,
                ref source,
                0,
                0,
                VisualMaskNative.UlwOpaque))
            throw new InvalidOperationException("Could not present the visual-mask window.");

        // UpdateLayeredWindow maintains the submitted appearance. Keeping the
        // full-screen DDB and its DCs for the remainder of the lease would only
        // add transient memory pressure on high-resolution displays.
        ReleaseCaptureResources();
    }

    internal void ShowPreparedFrame()
    {
        VisualMaskNative.ShowWindow(_window, WinApi.SW_SHOWNOACTIVATE);
    }

    internal void PumpMessages(int maximumMessages)
    {
        for (var index = 0; index < maximumMessages && VisualMaskNative.PeekMessage(
                   out var message,
                   0,
                   0,
                   0,
                   VisualMaskNative.PmRemove); index++)
        {
            VisualMaskNative.TranslateMessage(ref message);
            VisualMaskNative.DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_window != 0)
        {
            VisualMaskNative.DestroyWindow(_window);
            _window = 0;
        }
        ReleaseCaptureResources();
        VisualMaskNative.UnregisterClass(_className, _instance);
    }

    private void ReleaseCaptureResources()
    {
        if (_oldBitmap != 0 && _memoryDeviceContext != 0)
            VisualMaskNative.SelectObject(_memoryDeviceContext, _oldBitmap);
        _oldBitmap = 0;

        if (_bitmap != 0) VisualMaskNative.DeleteObject(_bitmap);
        _bitmap = 0;

        if (_memoryDeviceContext != 0) VisualMaskNative.DeleteDC(_memoryDeviceContext);
        _memoryDeviceContext = 0;

        if (_screenDeviceContext != 0) VisualMaskNative.ReleaseDC(0, _screenDeviceContext);
        _screenDeviceContext = 0;
    }

    private static nint ProcessWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        const uint wmNcHitTest = 0x0084;
        const uint wmMouseActivate = 0x0021;
        const int htTransparent = -1;
        const int maNoActivate = 3;
        if (message == wmNcHitTest) return (nint)htTransparent;
        if (message == wmMouseActivate) return (nint)maNoActivate;
        return VisualMaskNative.DefWindowProc(window, message, wParam, lParam);
    }

    private static void RenderLoadingPlaceholder(
        nint destinationDeviceContext,
        nint screenDeviceContext,
        ExplorerVisualMaskBounds bounds,
        string loadingMessage)
    {
        var dark = IsDarkExplorerSurface(screenDeviceContext, bounds);
        var background = dark
            ? Color.FromArgb(32, 32, 32)
            : Color.FromArgb(250, 250, 250);
        var foreground = dark
            ? Color.FromArgb(235, 235, 235)
            : Color.FromArgb(45, 45, 45);
        var accent = dark
            ? Color.FromArgb(96, 205, 255)
            : Color.FromArgb(0, 103, 192);

        using var graphics = Graphics.FromHdc(destinationDeviceContext);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.Clear(background);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (bounds.Width < 140 || bounds.Height < 70) return;

        using var font = new Font(
            SystemFonts.MessageBoxFont.FontFamily,
            11f,
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var textBrush = new SolidBrush(foreground);
        using var accentBrush = new SolidBrush(accent);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        var scale = Math.Max(1f, graphics.DpiX / 96f);
        var dotDiameter = 8f * scale;
        var gap = 12f * scale;
        var maximumTextWidth = Math.Max(80f, Math.Min(440f * scale, bounds.Width - 72f * scale));
        var measured = graphics.MeasureString(
            loadingMessage,
            font,
            new SizeF(maximumTextWidth, 40f * scale),
            format);
        var groupWidth = dotDiameter + gap + Math.Min(maximumTextWidth, measured.Width);
        var left = Math.Max(24f * scale, (bounds.Width - groupWidth) / 2f);
        var centerY = Math.Max(36f * scale, bounds.Height * 0.44f);

        graphics.FillEllipse(
            accentBrush,
            left,
            centerY - dotDiameter / 2f,
            dotDiameter,
            dotDiameter);
        graphics.DrawString(
            loadingMessage,
            font,
            textBrush,
            new RectangleF(
                left + dotDiameter + gap,
                centerY - 22f * scale,
                maximumTextWidth,
                44f * scale),
            format);
    }

    private static bool IsDarkExplorerSurface(
        nint screenDeviceContext,
        ExplorerVisualMaskBounds bounds)
    {
        var red = new int[9];
        var green = new int[9];
        var blue = new int[9];
        var count = 0;
        var horizontal = new[] { 58, 73, 88 };
        var vertical = new[] { 22, 50, 78 };
        foreach (var xPercent in horizontal)
        foreach (var yPercent in vertical)
        {
            var color = VisualMaskNative.GetPixel(
                screenDeviceContext,
                bounds.Left + bounds.Width * xPercent / 100,
                bounds.Top + bounds.Height * yPercent / 100);
            if (color == uint.MaxValue) continue;
            red[count] = (int)(color & 0xFF);
            green[count] = (int)((color >> 8) & 0xFF);
            blue[count] = (int)((color >> 16) & 0xFF);
            count++;
        }

        if (count == 0) return false;
        Array.Sort(red, 0, count);
        Array.Sort(green, 0, count);
        Array.Sort(blue, 0, count);
        var middle = count / 2;
        var luminance = red[middle] * 0.2126
                        + green[middle] * 0.7152
                        + blue[middle] * 0.0722;
        return luminance < 128;
    }
}

internal static class VisualMaskNative
{
    private const uint DwmwaCloaked = 14;

    [DllImport("gdi32.dll")]
    internal static extern uint GetPixel(nint deviceContext, int x, int y);

    internal const uint WsExTransparent = 0x00000020;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint WsExLayered = 0x00080000;
    internal const uint WsExNoActivate = 0x08000000;
    internal const uint WsPopup = 0x80000000;
    internal const uint Srccopy = 0x00CC0020;
    internal const uint CaptureBlt = 0x40000000;
    internal const uint UlwOpaque = 0x00000004;
    internal const uint PmRemove = 0x0001;

    internal delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal WindowProcedure WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size
    {
        internal int Width;
        internal int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Value;
        internal nint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Cursor;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size;
        internal uint TickCount;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    internal static bool TryGetLastInputTick(out uint tickCount)
    {
        var input = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf(typeof(LastInputInfo))
        };
        if (!GetLastInputInfo(ref input))
        {
            tickCount = 0;
            return false;
        }

        tickCount = input.TickCount;
        return true;
    }

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentOrOwner,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(
        nint destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint operation);

    [DllImport("user32.dll")]
    internal static extern bool UpdateLayeredWindow(
        nint window,
        nint destinationDeviceContext,
        ref Point destination,
        ref Size size,
        nint sourceDeviceContext,
        ref Point source,
        uint colorKey,
        nint blend,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out int value,
        int valueSize);

    internal static bool IsWindowUncloaked(nint window)
    {
        return DwmGetWindowAttribute(
                   window,
                   DwmwaCloaked,
                   out var cloaked,
                   sizeof(int)) == 0
               && cloaked == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern bool PeekMessage(
        out Message message,
        nint window,
        uint minimum,
        uint maximum,
        uint remove);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref Message message);
}
