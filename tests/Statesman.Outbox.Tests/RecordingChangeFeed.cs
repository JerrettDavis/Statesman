namespace Statesman.Outbox.Tests;

/// <summary>
/// A store whose change feed records the <see cref="StateChangeReadOptions.Take"/> it was handed on
/// each read, so a test can assert what the dispatcher asks the provider for rather than only what it
/// publishes.
/// </summary>
internal sealed class RecordingFeedLedgerStore
    : IStateLedgerStore, IStateChangeFeed, IStateLeaseProvider, IStateLedgerReplica
{
    private readonly InMemoryStateLedgerStore _inner;
    private readonly List<int?> _requestedTakes = [];

    public RecordingFeedLedgerStore(string name = "recording") => _inner = new InMemoryStateLedgerStore(name);

    public FakeLeaseProvider Leases { get; } = new();

    /// <summary>One entry per <c>ReadAsync</c> call, in order.</summary>
    public IReadOnlyList<int?> RequestedTakes
    {
        get
        {
            lock (_requestedTakes)
            {
                return [.. _requestedTakes];
            }
        }
    }

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

    public IAsyncEnumerable<StateChangeEnvelope> ReadAsync(StateChangeCursor? from, StateChangeReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_requestedTakes)
        {
            _requestedTakes.Add(options.Take);
        }

        return _inner.ReadAsync(from, options, cancellationToken);
    }

    public ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Leases.AcquireAsync(leaseId, ttl, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
