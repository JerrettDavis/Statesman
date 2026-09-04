namespace Statesman.Tiered.Tests;

public sealed class TieredPartitionCatalogTests
{
    [Fact]
    public async Task ListPartitionsAsync_delegates_to_the_cold_store()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var address = new StateAddress("app", "catalog/item", StatePartition.Default);

        StateAppendResult appended = await tiered.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));

        List<StatePartitionDescriptor> partitions = [];
        await foreach (StatePartitionDescriptor descriptor in tiered.ListPartitionsAsync())
        {
            partitions.Add(descriptor);
        }

        StatePartitionDescriptor descriptor2 = Assert.Single(partitions);
        Assert.Equal(address, descriptor2.Address);
        Assert.Equal(appended.Record!.GlobalPosition, descriptor2.LastPosition.Position);
    }

    [Fact]
    public async Task ListPartitionsAsync_throws_when_the_cold_store_does_not_support_the_capability()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new NonCatalogStore();
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (StatePartitionDescriptor _ in tiered.ListPartitionsAsync())
            {
            }
        });
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

    private sealed class NonCatalogStore : IStateLedgerStore
    {
        public string Name => "non-catalog";

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StateRecord?>(null);

        public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("NonCatalogStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
