namespace Statesman.Tiered.Tests;

public sealed class TieredDistributedCaptureTests
{
    [Fact]
    public async Task CaptureAsync_delegates_to_the_cold_store()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new FakeDistributedCaptureStore();
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        var address = new StateAddress("app", "capture/item", StatePartition.Default);
        var record = new StateRecord
        {
            Address = address,
            Revision = 1,
            GlobalPosition = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Operation = StateOperation.Set,
            Status = StateStatus.Ready,
            ValueType = typeof(string).FullName!,
            SchemaVersion = 1,
            Payload = System.Text.Encoding.UTF8.GetBytes("one"),
            Source = "test",
        };
        cold.Records[address] = record;

        IReadOnlyDictionary<StateAddress, StateRecord?> captured = await tiered.CaptureAsync(
            new[] { address }, StateCaptureConsistency.SnapshotDistributed);

        Assert.Same(record, Assert.Single(captured).Value);
        Assert.Equal(StateCaptureConsistency.SnapshotDistributed, cold.LastRequired);
    }

    [Fact]
    public async Task CaptureAsync_throws_when_the_cold_store_does_not_support_the_capability()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await tiered.CaptureAsync(
                new[] { new StateAddress("app", "capture/item", StatePartition.Default) },
                StateCaptureConsistency.ReadCommittedDistributed));
    }

    private sealed class FakeDistributedCaptureStore : IStateLedgerStore, IDistributedCapture
    {
        public Dictionary<StateAddress, StateRecord?> Records { get; } = new();

        public StateCaptureConsistency? LastRequired { get; private set; }

        public string Name => "fake-cold";

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Records.GetValueOrDefault(address));

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
            throw new NotSupportedException("FakeDistributedCaptureStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyDictionary<StateAddress, StateRecord?>> CaptureAsync(
            IEnumerable<StateAddress> addresses,
            StateCaptureConsistency required,
            CancellationToken cancellationToken = default)
        {
            LastRequired = required;
            IReadOnlyDictionary<StateAddress, StateRecord?> result = addresses
                .ToDictionary(address => address, address => Records.GetValueOrDefault(address));
            return ValueTask.FromResult(result);
        }
    }
}
