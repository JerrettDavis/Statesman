using Statesman.Testing;

namespace Statesman.Tests;

public sealed class DistributedCaptureRoutingTests
{
    private static readonly StateKey<CounterState> Counter = StateKey.Define<CounterState>("counter");
    private static readonly StateKey<CounterState> Other = StateKey.Define<CounterState>("other");

    [Fact]
    public async Task CaptureAsync_with_a_distributed_level_builds_snapshots_from_the_stores_records()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("distributed-capture")
            .State(Counter, state => state.StoreWith("captures").Initial(new CounterState(0)))
            .Build();
        var store = new FakeCaptureStore("captures");
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration, stores: new IStateLedgerStore[] { store });
        var address = new StateAddress("distributed-capture", Counter.Path, StatePartition.Default);
        store.Records[address] = new StateRecord
        {
            Address = address,
            Revision = 3,
            GlobalPosition = 9,
            OccurredAt = DateTimeOffset.UtcNow,
            Operation = StateOperation.Set,
            Status = StateStatus.Ready,
            ValueType = typeof(CounterState).FullName!,
            SchemaVersion = 1,
            Payload = new JsonStateSerializer().Serialize(new CounterState(42)),
            Source = "test",
        };

        StateSnapshotSet capture = await harness.Runtime.CaptureAsync(
            new[] { Counter.At(StatePartition.Default) },
            StateCaptureConsistency.SnapshotDistributed);

        Assert.Equal(StateCaptureConsistency.SnapshotDistributed, store.LastRequired);
        Assert.Equal(new[] { address }, store.LastRequestedAddresses);
        var snapshot = (IStateSnapshot<CounterState>)Assert.Single(capture.Snapshots).Value;
        Assert.Equal(42, snapshot.RequiredValue.Count);
        Assert.Equal(9, snapshot.GlobalPosition);
    }

    [Fact]
    public async Task CaptureAsync_groups_requested_state_by_its_resolved_store()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("distributed-capture-multi")
            .State(Counter, state => state.StoreWith("storeA").Initial(new CounterState(0)))
            .State(Other, state => state.StoreWith("storeB").Initial(new CounterState(0)))
            .Build();
        var storeA = new FakeCaptureStore("storeA");
        var storeB = new FakeCaptureStore("storeB");
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration, stores: new IStateLedgerStore[] { storeA, storeB });
        var addressA = new StateAddress("distributed-capture-multi", Counter.Path, StatePartition.Default);
        var addressB = new StateAddress("distributed-capture-multi", Other.Path, StatePartition.Default);

        await harness.Runtime.CaptureAsync(
            new[] { Counter.At(StatePartition.Default), Other.At(StatePartition.Default) },
            StateCaptureConsistency.ReadCommittedDistributed);

        Assert.Equal(new[] { addressA }, storeA.LastRequestedAddresses);
        Assert.Equal(new[] { addressB }, storeB.LastRequestedAddresses);
    }

    [Fact]
    public async Task CaptureAsync_rejects_a_SnapshotDistributed_capture_spanning_more_than_one_store()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("distributed-capture-torn")
            .State(Counter, state => state.StoreWith("storeA").Initial(new CounterState(0)))
            .State(Other, state => state.StoreWith("storeB").Initial(new CounterState(0)))
            .Build();
        var storeA = new FakeCaptureStore("storeA");
        var storeB = new FakeCaptureStore("storeB");
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration, stores: new IStateLedgerStore[] { storeA, storeB });

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await harness.Runtime.CaptureAsync(
                new[] { Counter.At(StatePartition.Default), Other.At(StatePartition.Default) },
                StateCaptureConsistency.SnapshotDistributed));

        Assert.Empty(storeA.LastRequestedAddresses);
        Assert.Empty(storeB.LastRequestedAddresses);
    }

    [Fact]
    public async Task CaptureAsync_with_a_distributed_level_reports_an_unwritten_address_as_absent()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("distributed-capture-absent")
            .State(Counter, state => state.StoreWith("captures").Initial(new CounterState(7)))
            .Build();
        var store = new FakeCaptureStore("captures");
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration, stores: new IStateLedgerStore[] { store });

        StateSnapshotSet capture = await harness.Runtime.CaptureAsync(
            new[] { Counter.At(StatePartition.Default) },
            StateCaptureConsistency.ReadCommittedDistributed);

        IStateSnapshot snapshot = Assert.Single(capture.Snapshots).Value;
        Assert.Equal(StateStatus.Absent, snapshot.Status);
        Assert.False(snapshot.HasValue);
    }

    [Fact]
    public async Task CaptureAsync_throws_when_the_resolved_store_does_not_implement_IDistributedCapture()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("distributed-capture-unsupported")
            .State(Counter, state => state.StoreWith("no-capture").Initial(new CounterState(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration, stores: new IStateLedgerStore[] { new NoCaptureStore() });

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await harness.Runtime.CaptureAsync(
                new[] { Counter.At(StatePartition.Default) },
                StateCaptureConsistency.SnapshotDistributed));
    }

    private sealed record CounterState(int Count);

    private sealed class FakeCaptureStore : IStateLedgerStore, IDistributedCapture
    {
        public FakeCaptureStore(string name) => Name = name;

        public string Name { get; }

        public Dictionary<StateAddress, StateRecord?> Records { get; } = new();

        public List<StateAddress> LastRequestedAddresses { get; } = new();

        public StateCaptureConsistency? LastRequired { get; private set; }

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
            throw new NotSupportedException("FakeCaptureStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyDictionary<StateAddress, StateRecord?>> CaptureAsync(
            IEnumerable<StateAddress> addresses,
            StateCaptureConsistency required,
            CancellationToken cancellationToken = default)
        {
            StateAddress[] targets = addresses.ToArray();
            LastRequestedAddresses.AddRange(targets);
            LastRequired = required;
            IReadOnlyDictionary<StateAddress, StateRecord?> result =
                targets.ToDictionary(address => address, address => Records.GetValueOrDefault(address));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoCaptureStore : IStateLedgerStore
    {
        public string Name => "no-capture";

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
            throw new NotSupportedException("NoCaptureStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
