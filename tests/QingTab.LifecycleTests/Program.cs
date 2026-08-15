using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingTab.Helpers;
using QingTab.Hooks;

namespace QingTab.LifecycleTests;

internal static class Program
{
    private static readonly List<string> Failures = new();
    private static int Checks;

    private static int Main()
    {
        TestExitIsIdempotent();
        TestSessionIsolation();
        TestRepeatedWatcherDispose();
        TestExplorerRestartLifetime();

        if (Failures.Count == 0)
        {
            Console.WriteLine($"PASS: {Checks} QingTab lifecycle checks");
            return 0;
        }

        foreach (var failure in Failures)
            Console.Error.WriteLine(failure);
        return 1;
    }

    private static void TestExitIsIdempotent()
    {
        var integrationOwned = 1;
        var actualRegistryMutations = 0;
        var preparations = new ApplicationExitPreparation[64];

        Parallel.For(0, preparations.Length, index =>
        {
            preparations[index] = ApplicationExitPolicy.Prepare(
                delegate(out string error)
                {
                    if (Interlocked.Exchange(ref integrationOwned, 0) == 1)
                        Interlocked.Increment(ref actualRegistryMutations);
                    error = string.Empty;
                    return true;
                });
        });

        CheckTrue("concurrent repeated exit requests all observe a safe restored state",
            preparations.All(result => result.CanExit && result.WindowsFolderOpenRestored));
        Check("the owned Folder-open registration is mutated only once", 1, actualRegistryMutations);

        var failed = ApplicationExitPolicy.Prepare(
            delegate(out string error)
            {
                error = "restore failed";
                return false;
            });
        CheckTrue("a failed restore blocks exit",
            !failed.CanExit && !failed.WindowsFolderOpenRestored && failed.Error == "restore failed");

        var faulted = ApplicationExitPolicy.Prepare(
            delegate(out string error)
            {
                error = string.Empty;
                throw new InvalidOperationException("injected restore failure");
            });
        CheckTrue("an exception during restore blocks exit without escaping",
            !faulted.CanExit
            && !faulted.WindowsFolderOpenRestored
            && faulted.Error.Contains("injected restore failure"));
    }

    private static void TestSessionIsolation()
    {
        var uniqueSid = "S-1-5-21-" + Math.Abs(Guid.NewGuid().GetHashCode());
        var firstSession = InstanceObjectNames.Create(uniqueSid, sessionId: 41);
        var secondSession = InstanceObjectNames.Create(uniqueSid, sessionId: 42);

        CheckTrue("logout/session change receives isolated mutex names",
            firstSession.MutexName != secondSession.MutexName);
        CheckTrue("logout/session change receives isolated exit events",
            firstSession.ExitEventName != secondSession.ExitEventName);
        CheckTrue("logout/session change receives isolated ready events",
            firstSession.ReadyEventName != secondSession.ReadyEventName);
        CheckTrue("logout/session change receives isolated IPC pipes",
            firstSession.PipeName != secondSession.PipeName);

        using var firstExit = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            firstSession.ExitEventName);
        using var secondExit = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            secondSession.ExitEventName);
        firstExit.Set();
        CheckTrue("an old-session exit signal is received by that session", firstExit.WaitOne(0));
        CheckTrue("an old-session exit signal cannot terminate the next login session", !secondExit.WaitOne(0));
    }

    private static void TestRepeatedWatcherDispose()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            var watcher = new ExplorerWatcher(enabled: false);

            watcher.Dispose();
            watcher.Dispose();
            watcher.SetEnabled(true);

            var disposedResult = watcher.OpenPathInNewTabAsync(@"C:\").GetAwaiter().GetResult();
            CheckTrue("repeated watcher disposal is harmless",
                disposedResult.Kind == OpenTabResultKind.Disposed);

            var disposeTasks = Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => watcher.Dispose()))
                .ToArray();
            Task.WaitAll(disposeTasks);
            CheckTrue("parallel late disposal requests do not throw", disposeTasks.All(task => task.IsCompleted));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void TestExplorerRestartLifetime()
    {
        var lifetime = new ExplorerOperationLifetime(initiallyAccepting: true);
        var tickets = new List<ExplorerOperationTicket>();
        for (var index = 0; index < 32; index++)
        {
            if (!lifetime.TryBegin(out var ticket) || ticket == null)
                throw new InvalidOperationException("Could not create Explorer lifetime fixture.");
            tickets.Add(ticket);
        }

        CheckTrue("Explorer restart retires the generation before releasing shared state",
            !lifetime.Retire());
        CheckTrue("Explorer restart rejects work while the old generation drains",
            !lifetime.TryBegin(out _));
        CheckTrue("all pre-restart tickets become stale",
            tickets.All(ticket => !lifetime.IsCurrent(ticket)));

        var cleanupClaims = 0;
        Parallel.ForEach(tickets, ticket =>
        {
            if (lifetime.Complete(ticket))
                Interlocked.Increment(ref cleanupClaims);
        });
        Check("exactly one final completion releases the retired Explorer connection", 1, cleanupClaims);

        Parallel.ForEach(tickets, ticket =>
        {
            if (lifetime.Complete(ticket))
                Interlocked.Increment(ref cleanupClaims);
        });
        Check("repeated completion cannot release the same Explorer connection twice", 1, cleanupClaims);

        lifetime.Activate();
        CheckTrue("a reconnect admits a fresh Explorer generation",
            lifetime.TryBegin(out var freshTicket)
            && freshTicket != null
            && lifetime.IsCurrent(freshTicket));
        CheckTrue("a reconnect never revives an old Explorer ticket",
            tickets.All(ticket => !lifetime.IsCurrent(ticket)));
        CheckTrue("completing active reconnect work does not retire its shared connection",
            !lifetime.Complete(freshTicket));

        var foreignLifetime = new ExplorerOperationLifetime(initiallyAccepting: true);
        foreignLifetime.TryBegin(out var foreignTicket);
        CheckTrue("a ticket from another watcher cannot release this watcher's connection",
            !lifetime.Complete(foreignTicket));

        var restartCyclesPassed = true;
        for (var cycle = 0; cycle < 256; cycle++)
        {
            if (!lifetime.Retire() || lifetime.Retire())
            {
                restartCyclesPassed = false;
                break;
            }
            lifetime.Activate();
            if (!lifetime.TryBegin(out var cycleTicket)
                || cycleTicket == null
                || !lifetime.IsCurrent(cycleTicket)
                || lifetime.Complete(cycleTicket))
            {
                restartCyclesPassed = false;
                break;
            }
        }
        CheckTrue("256 Explorer restart cycles remain idempotent", restartCyclesPassed);
        CheckTrue("the final idle restart releases shared state exactly once",
            lifetime.Retire() && !lifetime.Retire());
    }

    private static void Check(string name, int expected, int actual)
    {
        Checks++;
        if (expected == actual) return;
        Failures.Add($"FAIL: {name}; expected <{expected}> but got <{actual}>");
    }

    private static void CheckTrue(string name, bool condition)
    {
        Checks++;
        if (condition) return;
        Failures.Add($"FAIL: {name}; expected true but got false");
    }
}
