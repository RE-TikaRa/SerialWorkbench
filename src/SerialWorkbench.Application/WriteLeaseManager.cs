namespace SerialWorkbench.Application;

public sealed class WriteLeaseManager
{
    private readonly Dictionary<Guid, LeaseState> leases = [];
    private readonly object gate = new();

    public WriteLease Acquire(Guid connectionId, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        lock (gate)
        {
            if (leases.TryGetValue(connectionId, out var existing))
            {
                throw new InvalidOperationException($"Connection {connectionId} already has a write lease owned by {existing.Owner}.");
            }

            var token = Guid.NewGuid();
            leases.Add(connectionId, new LeaseState(token, owner));
            return new WriteLease(this, connectionId, token, owner);
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
