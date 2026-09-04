using System.Runtime.CompilerServices;

namespace Statesman.Tiered.Tests;

public sealed class TieredReplicationLagTests
{
    [Fact]
    public async Task EstimateLagAsync_reports_caught_up_for_two_empty_tiers()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        StateReplicationLag lag = await tiered.EstimateLagAsync();

        Assert.Equal(0, lag.AuthoritativePosition);
        Assert.Equal(0, lag.ReplicaPosition);
        Assert.Equal(0, lag.PartitionsBehind);
        Assert.True(lag.IsCaughtUp);
    }

    [Fact]
    public async Task EstimateLagAsync_reports_caught_up_after_a_write_through_the_tiered_store()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var address = new StateAddress("app", "lag/item", StatePartition.Default);

        StateAppendResult appended = await tiered.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));

        StateReplicationLag lag = await tiered.EstimateLagAsync();

        Assert.Equal(appended.Record!.GlobalPosition, lag.AuthoritativePosition);
        Assert.Equal(appended.Record.GlobalPosition, lag.ReplicaPosition);
        Assert.Equal(0, lag.PartitionsBehind);
        Assert.True(lag.IsCaughtUp);
    }

    [Fact]
    public async Task EstimateLagAsync_counts_a_partition_the_replica_has_never_seen()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var seen = new StateAddress("app", "lag/seen", StatePartition.Default);
        var unseen = new StateAddress("app", "lag/unseen", StatePartition.Default);

        StateAppendResult first = await tiered.AppendAsync(seen, StateWriteCondition.Absent, Commit("one"));
        // Bypass the tiered store entirely: another process writing straight to the authority.
        StateAppendResult direct = await cold.AppendAsync(unseen, StateWriteCondition.Absent, Commit("two"));

        StateReplicationLag lag = await tiered.EstimateLagAsync();

        Assert.Equal(direct.Record!.GlobalPosition, lag.AuthoritativePosition);
        Assert.Equal(first.Record!.GlobalPosition, lag.ReplicaPosition);
        Assert.Equal(direct.Record.GlobalPosition - first.Record.GlobalPosition, lag.PositionGap);
        Assert.Equal(1, lag.PartitionsBehind);
        Assert.False(lag.IsCaughtUp);
    }

    [Fact]
    public async Task EstimateLagAsync_counts_a_partition_the_replica_holds_at_an_older_position()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var address = new StateAddress("app", "lag/item", StatePartition.Default);

        StateAppendResult first = await tiered.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
        StateAppendResult second = await cold.AppendAsync(
            address, StateWriteCondition.AtRevision(first.Record!.Revision), Commit("two"));

        StateReplicationLag lag = await tiered.EstimateLagAsync();

        Assert.Equal(second.Record!.GlobalPosition, lag.AuthoritativePosition);
        Assert.Equal(first.Record.GlobalPosition, lag.ReplicaPosition);
        Assert.Equal(1, lag.PartitionsBehind);
        Assert.False(lag.IsCaughtUp);
    }

    [Fact]
    public async Task EstimateLagAsync_reports_caught_up_again_after_a_validating_read_repairs_the_replica()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var address = new StateAddress("app", "lag/item", StatePartition.Default);

        await cold.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
        StateReplicationLag before = await tiered.EstimateLagAsync();

        // Default ReadMode is ValidateCold, which imports the authoritative record into hot.
        StateRecord? read = await tiered.ReadLatestAsync(address);
        StateReplicationLag after = await tiered.EstimateLagAsync();

        Assert.NotNull(read);
        Assert.Equal(1, before.PartitionsBehind);
        Assert.False(before.IsCaughtUp);
        Assert.Equal(0, after.PartitionsBehind);
        Assert.True(after.IsCaughtUp);
    }

    [Fact]
    public async Task EstimateLagAsync_reports_lag_when_the_hot_import_fails()
    {
        var hot = new FaultyReplicaStore(new InMemoryStateLedgerStore("hot"));
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var address = new StateAddress("app", "lag/item", StatePartition.Default);

        StateAppendResult appended = await tiered.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));

        StateReplicationLag lag = await tiered.EstimateLagAsync();

        Assert.True(appended.Succeeded);
        Assert.NotNull(tiered.LastCacheError);
        Assert.Equal(appended.Record!.GlobalPosition, lag.AuthoritativePosition);
        Assert.Equal(0, lag.ReplicaPosition);
        Assert.Equal(appended.Record.GlobalPosition, lag.PositionGap);
        Assert.Equal(1, lag.PartitionsBehind);
        Assert.False(lag.IsCaughtUp);
    }

    [Fact]
    public async Task EstimateLagAsync_enumerates_the_hot_catalog_before_the_cold_catalog()
    {
        List<string> order = [];
        var hot = new OrderRecordingStore("hot", order);
        var cold = new OrderRecordingStore("cold", order);
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        StateReplicationLag lag = await tiered.EstimateLagAsync();

        Assert.Equal(new[] { "hot", "cold" }, order);
        Assert.True(lag.IsCaughtUp);
    }

    [Fact]
    public async Task EstimateLagAsync_throws_when_the_hot_store_does_not_support_partition_catalog()
    {
        var hot = new CatalogLessReplicaStore();
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await tiered.EstimateLagAsync());

        Assert.Contains("hot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EstimateLagAsync_throws_when_the_cold_store_does_not_support_partition_catalog()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new CatalogLessReplicaStore();
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await tiered.EstimateLagAsync());

        Assert.Contains("cold", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tiered_store_advertises_the_capability_directly()
    {
        var tiered = new TieredStateLedgerStore(
            "tiered", new InMemoryStateLedgerStore("hot"), new InMemoryStateLedgerStore("cold"));

        Assert.True(tiered.TryGetCapability(out IReplicationLagSource? source));
        Assert.Same(tiered, source);
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    /// <summary>
    /// A hot tier whose ImportAsync always fails, so the tiered store records a cache error and the
    /// replica never receives the authoritative record. Everything else delegates to a real store so
    /// its partition catalog is genuine (and empty).
    /// </summary>
    private sealed class FaultyReplicaStore(InMemoryStateLedgerStore inner)
        : IStateLedgerStore, IStateLedgerReplica, IPartitionCatalog
    {
        public string Name => inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            inner.PruneAsync(address, policy, cancellationToken);

        public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated hot replica failure.");

        public IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListPartitionsAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// An empty store that records its name into a shared list when its catalog is enumerated, so a
    /// test can assert which tier the tiered store consulted first.
    /// </summary>
    private sealed class OrderRecordingStore(string name, List<string> order)
        : IStateLedgerStore, IStateLedgerReplica, IPartitionCatalog
    {
        public string Name => name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StateRecord?>(null);

        public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("OrderRecordingStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            order.Add(name);
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Satisfies the tiered store's constructor (hot must be an IStateLedgerReplica) but implements
    /// no IPartitionCatalog, so it is unusable as either tier for lag estimation.
    /// </summary>
    private sealed class CatalogLessReplicaStore : IStateLedgerStore, IStateLedgerReplica
    {
        public string Name => "catalog-less";

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StateRecord?>(null);

        public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("CatalogLessReplicaStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
