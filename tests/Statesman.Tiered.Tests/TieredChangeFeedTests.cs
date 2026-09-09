namespace Statesman.Tiered.Tests;

public sealed class TieredChangeFeedTests
{
    [Fact]
    public async Task ReadAsync_delegates_to_the_cold_store()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var address = new StateAddress("app", "feed/item", StatePartition.Default);

        StateAppendResult appended = await tiered.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in tiered.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Single(changes);
        Assert.Equal(appended.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
    }

    [Fact]
    public async Task ReadAsync_throws_when_the_cold_store_does_not_support_the_capability()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new NonFeedStore();
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (StateChangeEnvelope _ in tiered.ReadAsync(from: null, StateChangeReadOptions.Default))
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

    private sealed class NonFeedStore : IStateLedgerStore
    {
        public string Name => "non-feed";

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
            throw new NotSupportedException("NonFeedStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
