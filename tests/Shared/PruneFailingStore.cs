namespace Statesman.TestHelpers;

/// <summary>
/// Forwards every ledger operation to an inner in-memory store except <see cref="PruneAsync"/>, which
/// always throws. The one seam the runtime's maintenance-failure path needs: <c>StateHandle</c> prunes
/// after every accepted append, and a prune that throws is reported as a maintenance failure rather
/// than turning the accepted transition into a fault.
/// </summary>
/// <remarks>
/// Shared rather than copied. Before ROADMAP 0.3 Phase 17 this existed three times, in two shapes —
/// twice in <c>Statesman.Tests</c> and once in <c>Statesman.Hosting.Tests</c>, whose copy carried a doc
/// comment claiming the two assemblies had no shared test helper. They do: this file. The failure index
/// here comes from <see cref="Interlocked.Increment(ref int)"/>'s own return value rather than from a
/// separate read afterwards, so two concurrent prunes can never be told they are the same failure.
/// </remarks>
public sealed class PruneFailingStore : IStateLedgerStore
{
    private readonly InMemoryStateLedgerStore _inner;
    private int _pruneFailures;

    /// <summary>Wraps an inner store.</summary>
    /// <param name="inner">The store every operation except pruning is forwarded to.</param>
    public PruneFailingStore(InMemoryStateLedgerStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>How many times <see cref="PruneAsync"/> has thrown.</summary>
    public int PruneFailures => Volatile.Read(ref _pruneFailures);

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public ValueTask<StateRecord?> ReadLatestAsync(
        StateAddress address, CancellationToken cancellationToken = default) =>
        _inner.ReadLatestAsync(address, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
        StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
        _inner.ReadHistoryAsync(address, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateAppendResult> AppendAsync(
        StateAddress address,
        StateWriteCondition condition,
        StateCommit commit,
        CancellationToken cancellationToken = default) =>
        _inner.AppendAsync(address, condition, commit, cancellationToken);

    /// <inheritdoc />
    public ValueTask PruneAsync(
        StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default)
    {
        int failureIndex = Interlocked.Increment(ref _pruneFailures);
        throw new InvalidOperationException($"prune failure {failureIndex}");
    }

    /// <summary>Extracts the failure index this store embedded in an exception message it raised.</summary>
    /// <param name="exceptionMessage">A message produced by <see cref="PruneAsync"/>.</param>
    /// <returns>The one-based index of that failure.</returns>
    public static int PruneFailureIndexOf(string exceptionMessage)
    {
        ArgumentNullException.ThrowIfNull(exceptionMessage);
        return int.Parse(exceptionMessage["prune failure ".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
