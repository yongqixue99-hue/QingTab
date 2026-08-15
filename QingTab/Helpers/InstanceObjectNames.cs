using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace QingTab.Helpers;

public sealed class InstanceObjectNames
{
    private InstanceObjectNames(string scope)
    {
        MutexName = @"Local\QingTab.SingleInstance." + scope;
        ExitEventName = @"Local\QingTab.ExitRequested." + scope;
        ReadyEventName = @"Local\QingTab.Ready." + scope;
        PipeName = "QingTab.OpenTab." + scope;
    }

    public string MutexName { get; }
    public string ExitEventName { get; }
    public string ReadyEventName { get; }
    public string PipeName { get; }

    public static InstanceObjectNames Current
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value ?? Environment.UserName;
            using var process = Process.GetCurrentProcess();
            return Create(user, process.SessionId);
        }
    }

    public static InstanceObjectNames Create(string userIdentity, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(userIdentity))
            throw new ArgumentException("用户标识不能为空。", nameof(userIdentity));
        if (sessionId < 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId));

        var safeIdentity = new StringBuilder(userIdentity.Length);
        foreach (var character in userIdentity)
            safeIdentity.Append(char.IsLetterOrDigit(character) ? character : '_');

        return new InstanceObjectNames(safeIdentity + "." + sessionId);
    }
}
