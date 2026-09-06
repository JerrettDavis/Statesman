namespace Statesman.Outbox.Tests;

/// <summary>An in-memory store that also implements <see cref="IStateLeaseProvider"/>, which the in-memory provider deliberately does not.</summary>
internal sealed class LeasedLedgerStore : IStateLedgerStore, IStateChangeFeed, IStateLeaseProvider, IStateLedgerReplica
{
    private readonly InMemoryStateLedgerStore _inner;

    public LeasedLedgerStore(string name = "leased") => _inner = new InMemoryStateLedgerStore(name);

    public FakeLeaseProvider Leases { get; } = new();

    public string Name => _inner.Name;

    public ValueTask<StateRecord?> ReadLatestAsync(StateAddress address, CancellationToken cancellationToken = default) =>
        _inner.ReadLatestAsync(address, cancellationToken);

    public IAsyncEnumerable<StateRecord> ReadHistoryAsync(StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
        _inner.ReadHistoryAsync(address, options, cancellationToken);

    public ValueTask<StateAppendResult> AppendAsync(StateAddress address, StateWriteCondition condition, StateCommit commit, CancellationToken cancellationToken = default) =>
        _inner.AppendAsync(address, condition, commit, cancellationToken);

    public ValueTask PruneAsync(StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
        _inner.PruneAsync(address, policy, cancellationToken);

    public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default) =>
        _inner.ImportAsync(record, cancellationToken);

    public IAsyncEnumerable<StateChangeEnvelope> ReadAsync(StateChangeCursor? from, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(from, cancellationToken);

    public ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Leases.AcquireAsync(leaseId, ttl, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
