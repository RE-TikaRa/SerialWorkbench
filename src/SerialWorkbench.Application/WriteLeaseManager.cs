using System.Diagnostics.CodeAnalysis;

namespace SerialWorkbench.Application;

public sealed class WriteLeaseManager
{
    private readonly Dictionary<Guid, LeaseState> leases = [];
    private readonly object gate = new();

    public int ActiveCount
    {
        get
        {
            lock (gate)
            {
                return leases.Count;
            }
        }
    }

    public WriteLease Acquire(Guid connectionId, string owner)
    {
        return TryAcquire(connectionId, owner, out var lease, out var existingOwner)
            ? lease
            : throw new InvalidOperationException($"Connection {connectionId} already has a write lease owned by {existingOwner}.");
    }

    public bool TryAcquire(Guid connectionId, string owner, [NotNullWhen(true)] out WriteLease? lease, out string? existingOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        lock (gate)
        {
            if (leases.TryGetValue(connectionId, out var existing))
            {
                lease = null;
                existingOwner = existing.Owner;
                return false;
            }

            var token = Guid.NewGuid();
            leases.Add(connectionId, new LeaseState(token, owner));
            lease = new WriteLease(this, connectionId, token, owner);
            existingOwner = null;
            return true;
        }
    }

    public string? GetOwner(Guid connectionId)
    {
        lock (gate)
        {
            return leases.GetValueOrDefault(connectionId)?.Owner;
        }
    }

    private void Release(Guid connectionId, Guid token)
    {
        lock (gate)
        {
            if (leases.TryGetValue(connectionId, out var existing) && existing.Token == token)
            {
                leases.Remove(connectionId);
            }
        }
    }

    private sealed record LeaseState(Guid Token, string Owner);

    public sealed class WriteLease : IAsyncDisposable, IDisposable
    {
        private readonly Guid connectionId;
        private readonly WriteLeaseManager manager;
        private readonly Guid token;
        private int released;

        internal WriteLease(WriteLeaseManager manager, Guid connectionId, Guid token, string owner)
        {
            this.manager = manager;
            this.connectionId = connectionId;
            this.token = token;
            ConnectionId = connectionId;
            Owner = owner;
        }

        public Guid ConnectionId { get; }

        public string Owner { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                manager.Release(connectionId, token);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
