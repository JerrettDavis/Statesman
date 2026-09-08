namespace Statesman.Outbox.Tests;

/// <summary>An in-process lease provider whose acquire and renew outcomes the test controls.</summary>
internal sealed class FakeLeaseProvider : IStateLeaseProvider
{
    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private int _acquireCalls;

    /// <summary>How many times an acquire has been attempted. Interlocked because the hosted worker calls this from a pool thread while a test reads it.</summary>
    public int AcquireCalls => Volatile.Read(ref _acquireCalls);

    public bool RefuseAcquire { get; set; }

    public bool RefuseRenew { get; set; }

    public int RenewCalls { get; private set; }

    public string? LastRequestedLeaseId { get; private set; }

    public int HeldCount
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    public ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acquireCalls);
        LastRequestedLeaseId = leaseId;
        if (RefuseAcquire)
        {
            return ValueTask.FromResult<IStateLease?>(null);
        }

        lock (_gate)
        {
            if (!_held.Add(leaseId))
            {
                return ValueTask.FromResult<IStateLease?>(null);
            }
        }

        return ValueTask.FromResult<IStateLease?>(new FakeLease(this, leaseId));
    }

    private bool Renew()
    {
        RenewCalls++;
        return !RefuseRenew;
    }

    private void Release(string leaseId)
    {
        lock (_gate)
        {
            _held.Remove(leaseId);
        }
    }

    private sealed class FakeLease : IStateLease
    {
        private readonly FakeLeaseProvider _provider;
        private readonly string _leaseId;

        public FakeLease(FakeLeaseProvider provider, string leaseId)
        {
            _provider = provider;
            _leaseId = leaseId;
        }

        public ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_provider.Renew());

        public ValueTask DisposeAsync()
        {
            _provider.Release(_leaseId);
            return ValueTask.CompletedTask;
        }
    }
}
