using System;

namespace QingTab.Hooks;

/// <summary>
/// Serializes the lifetime decision shared by Explorer operations and their
/// Shell COM connection. Retiring a generation immediately rejects new work,
/// while shared COM cleanup is authorized exactly once after the last request
/// from that generation has completed.
/// </summary>
public sealed class ExplorerOperationLifetime
{
    private readonly object _sync = new();
    private long _generation = 1;
    private int _activeOperations;
    private bool _accepting;
    private bool _retirementCleanupDelivered;

    public ExplorerOperationLifetime(bool initiallyAccepting)
    {
        _accepting = initiallyAccepting;
    }

    public bool TryBegin(out ExplorerOperationTicket? ticket)
    {
        lock (_sync)
        {
            if (!_accepting)
            {
                ticket = null;
                return false;
            }

            _activeOperations++;
            ticket = new ExplorerOperationTicket(this, _generation);
            return true;
        }
    }

    public bool IsCurrent(ExplorerOperationTicket? ticket)
    {
        if (ticket == null || !ReferenceEquals(ticket.Owner, this)) return false;

        lock (_sync)
        {
            return _accepting
                   && !ticket.IsCompleted
                   && ticket.Generation == _generation;
        }
    }

    /// <summary>
    /// Stops admitting work. True authorizes the caller to release the shared
    /// Shell connection now; false means an in-flight request still owns it.
    /// </summary>
    public bool Retire()
    {
        lock (_sync)
        {
            if (_accepting)
            {
                _accepting = false;
                _generation++;
                _retirementCleanupDelivered = false;
            }

            return TryClaimRetirementCleanup();
        }
    }

    public void Activate()
    {
        lock (_sync)
        {
            if (_accepting) return;

            _accepting = true;
            _generation++;
            _retirementCleanupDelivered = false;
        }
    }

    /// <summary>
    /// Completes one request. True authorizes deferred shared COM cleanup.
    /// Repeated or foreign tickets are ignored and return false.
    /// </summary>
    public bool Complete(ExplorerOperationTicket? ticket)
    {
        if (ticket == null || !ReferenceEquals(ticket.Owner, this)) return false;

        lock (_sync)
        {
            if (ticket.IsCompleted) return false;

            ticket.IsCompleted = true;
            _activeOperations = Math.Max(0, _activeOperations - 1);
            return TryClaimRetirementCleanup();
        }
    }

    private bool TryClaimRetirementCleanup()
    {
        if (_accepting || _activeOperations != 0 || _retirementCleanupDelivered)
            return false;

        _retirementCleanupDelivered = true;
        return true;
    }
}

public sealed class ExplorerOperationTicket
{
    internal ExplorerOperationTicket(ExplorerOperationLifetime owner, long generation)
    {
        Owner = owner;
        Generation = generation;
    }

    internal ExplorerOperationLifetime Owner { get; }
    internal long Generation { get; }
    internal bool IsCompleted { get; set; }
}
