namespace Statesman.Tooling.Tests;

/// <summary>A store that implements nothing beyond <see cref="IStateLedgerStore"/> — no capabilities at all.</summary>
internal sealed class MinimalLedgerStore : IStateLedgerStore
{
    public string Name => "minimal";

    public ValueTask<StateRecord?> ReadLatestAsync(StateAddress address, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("MinimalLedgerStore does not read.");

    public IAsyncEnumerable<StateRecord> ReadHistoryAsync(StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("MinimalLedgerStore does not read.");

    public ValueTask<StateAppendResult> AppendAsync(StateAddress address, StateWriteCondition condition, StateCommit commit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("MinimalLedgerStore does not write.");

    public ValueTask PruneAsync(StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("MinimalLedgerStore does not prune.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
