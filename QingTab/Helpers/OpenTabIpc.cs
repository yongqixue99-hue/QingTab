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
    private const int MaximumClientSilenceMilliseconds = 500;
    private static readonly Encoding PipeEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly int MaximumRequestByteLength =
        PipeEncoding.GetMaxByteCount(MaximumRequestLength);
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
            _listenerTask = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = CreateCurrentUserServer(_pipeName);

                    lock (_serverLock)
                        _activeServer = server;

                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    if (Volatile.Read(ref _disposed) != 0) return;

                    var readTask = ReadStringAsync(server, CancellationToken.None);
                    using var readTimeout = new CancellationTokenSource();
                    var timeoutTask = Task.Delay(
                        MaximumClientSilenceMilliseconds,
                        readTimeout.Token);
                    var completedTask = await Task.WhenAny(readTask, timeoutTask)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(completedTask, readTask))
                    {
                        // PipeStream cancellation is not guaranteed to interrupt
                        // an already-pending Windows named-pipe read on every
                        // supported .NET Framework build. Closing this one server
                        // instance is the deterministic cancellation boundary.
                        server.Dispose();
                        ObserveAbandonedServerTask(readTask);
                        continue;
                    }

                    readTimeout.Cancel();
                    var path = await readTask.ConfigureAwait(false);
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
                catch (OperationCanceledException) when (Volatile.Read(ref _disposed) == 0)
                {
                    // A connected same-user process did not send a complete
                    // request. Drop only that pipe instance so later folder
                    // opens can immediately connect to a fresh listener.
                }
                catch (IOException) when (Volatile.Read(ref _disposed) == 0)
                {
                    // A short-lived Shell client can disappear between connect,
                    // request and acknowledgement. This is not a resident error.
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

        private static async Task<string> ReadStringAsync(
            PipeStream stream,
            CancellationToken cancellationToken)
        {
            var length = 0;
            var shift = 0;
            var oneByte = new byte[1];
            for (var index = 0; index < 5; index++)
            {
                await ReadExactlyAsync(
                        stream,
                        oneByte,
                        0,
                        1,
                        cancellationToken)
                    .ConfigureAwait(false);
                var current = oneByte[0];
                length |= (current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    if (length < 0 || length > MaximumRequestByteLength)
                        throw new IOException("IPC request payload is too large.");

                    var bytes = new byte[length];
                    await ReadExactlyAsync(
                            stream,
                            bytes,
                            0,
                            bytes.Length,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return PipeEncoding.GetString(bytes);
                }

                shift += 7;
            }

            throw new IOException("IPC request length prefix is invalid.");
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var completed = 0;
            while (completed < count)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        offset + completed,
                        count - completed,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("IPC client disconnected before completing its request.");
                completed += read;
            }
        }

        private static void ObserveAbandonedServerTask(Task task)
        {
            _ = task.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
