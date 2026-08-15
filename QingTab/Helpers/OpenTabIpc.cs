using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QingTab.Helpers;

public enum OpenTabIpcResponse : byte
{
    Rejected = 0,
    Accepted = 1,
    Duplicate = 2
}

/// <summary>
/// Small per-user named-pipe bridge used by short-lived Shell command
/// processes to hand a folder path to the resident tray process.
/// </summary>
public static class OpenTabIpc
{
    private const int MaximumRequestLength = 32_767;
    private static readonly string CurrentPipeName = InstanceObjectNames.Current.PipeName;

    public static bool TrySend(string path, int timeoutMilliseconds, out string error)
    {
        return TrySend(
                   CurrentPipeName,
                   path,
                   timeoutMilliseconds,
                   out var response,
                   out error)
               && response != OpenTabIpcResponse.Rejected;
    }

    public static bool TrySend(
        string path,
        int timeoutMilliseconds,
        out OpenTabIpcResponse response,
        out string error)
    {
        return TrySend(CurrentPipeName, path, timeoutMilliseconds, out response, out error);
    }

    public static bool TrySend(
        string pipeName,
        string path,
        int timeoutMilliseconds,
        out OpenTabIpcResponse response,
        out string error)
    {
        response = OpenTabIpcResponse.Rejected;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            error = "命名管道名称为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "文件夹路径为空。";
            return false;
        }

        if (path.Length > MaximumRequestLength)
        {
            error = "文件夹路径过长。";
            return false;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            client.Connect(timeoutMilliseconds);

            using var writer = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true);
            writer.Write(path);
            writer.Flush();

            var remainingMilliseconds = Math.Max(
                0,
                timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
            var responseBuffer = new byte[1];
            var readTask = client.ReadAsync(responseBuffer, 0, responseBuffer.Length);
            if (remainingMilliseconds == 0 || !readTask.Wait(remainingMilliseconds))
            {
                client.Dispose();
                ObserveTimedOutRead(readTask);
                error = "等待驻留实例确认超时。";
                return false;
            }

            if (readTask.GetAwaiter().GetResult() != 1)
            {
                error = "驻留实例没有返回确认。";
                return false;
            }

            response = (OpenTabIpcResponse)responseBuffer[0];
            if (response != OpenTabIpcResponse.Accepted
                && response != OpenTabIpcResponse.Duplicate
                && response != OpenTabIpcResponse.Rejected)
            {
                response = OpenTabIpcResponse.Rejected;
                error = "驻留实例返回了未知响应。";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ObserveTimedOutRead(Task<int> readTask)
    {
        _ = readTask.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public sealed class Server : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<string, OpenTabIpcResponse> _requestReceived;
        private readonly object _serverLock = new();
        private NamedPipeServerStream? _activeServer;
        private Task? _listenerTask;
        private int _disposed;

        public Server(Func<string, OpenTabIpcResponse> requestReceived)
            : this(CurrentPipeName, requestReceived)
        {
        }

        public Server(string pipeName, Func<string, OpenTabIpcResponse> requestReceived)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("命名管道名称不能为空。", nameof(pipeName));

            _pipeName = pipeName;
            _requestReceived = requestReceived ?? throw new ArgumentNullException(nameof(requestReceived));
        }

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Server));

            if (_listenerTask != null) return;
            _listenerTask = Task.Run(ListenLoop);
        }

        private void ListenLoop()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = CreateCurrentUserServer(_pipeName);

                    lock (_serverLock)
                        _activeServer = server;

                    server.WaitForConnection();
                    if (Volatile.Read(ref _disposed) != 0) return;

                    using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
                    var path = reader.ReadString();
                    var response = OpenTabIpcResponse.Rejected;
                    if (!string.IsNullOrWhiteSpace(path) && path.Length <= MaximumRequestLength)
                        response = _requestReceived(path);

                    using var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true);
                    writer.Write((byte)response);
                    writer.Flush();
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (IOException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ErrorLog.Write(ex, "ipc-listener-failed");
                    Thread.Sleep(100);
                }
                finally
                {
                    lock (_serverLock)
                    {
                        if (ReferenceEquals(_activeServer, server))
                            _activeServer = null;
                    }

                    server?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            lock (_serverLock)
            {
                try
                {
                    _activeServer?.Dispose();
                }
                catch
                {
                    // The listener may already be unwinding.
                }
                finally
                {
                    _activeServer = null;
                }
            }
        }
    }

    private static NamedPipeServerStream CreateCurrentUserServer(string pipeName)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException("无法读取当前 Windows 用户标识。");
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetOwner(currentSid);
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            currentSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);
    }
}
