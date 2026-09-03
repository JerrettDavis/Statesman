using Statesman.Testing;

namespace Statesman.Tests;

public sealed class RuntimeLifecycleTests
{
    private static readonly StateKey<CounterState> Counter = StateKey.Define<CounterState>("counter");

    [Fact]
    public async Task Reactive_writes_create_an_observable_ledger()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("lifecycle")
            .State(Counter, state => state.Initial(new CounterState(0)).Retain(retention => retention.Forever()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);

        IStateSnapshot<CounterState> set = await state.SetAsync(new CounterState(1));
        IStateSnapshot<CounterState> updated = await state.UpdateAsync(value => value! with { Count = value.Count + 1 });
        IStateSnapshot<CounterState> invalidated = await state.InvalidateAsync("upstream changed");
        IStateSnapshot<CounterState> cleared = await state.ClearAsync("signed out");
        List<IStateSnapshot<CounterState>> history = await ReadHistoryAsync(state);

        Assert.Equal(1, set.Revision);
        Assert.Equal(2, updated.RequiredValue.Count);
        Assert.Equal(StateStatus.Invalidated, invalidated.Status);
        Assert.True(invalidated.HasValue);
        Assert.Equal(StateStatus.Cleared, cleared.Status);
        Assert.False(cleared.HasValue);
        Assert.Equal(new StateOperation?[]
        {
            StateOperation.Cleared,
            StateOperation.Invalidated,
            StateOperation.Transitioned,
            StateOperation.Set,
        }, history.Select(snapshot => snapshot.Operation).ToArray());
    }

    [Fact]
    public async Task Typed_interactions_enforce_requirements_and_reduce_atomically()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("interactions")
            .State(Counter, state => state
                .Initial(new CounterState(0))
                .Invariant("Count cannot be negative.", value => value.Count >= 0)
                .Interact<Add>("add", interaction => interaction
                    .Require((_, command) => command.Amount > 0, "Amount must be positive.")
                    .Reduce((current, command) => current with { Count = current.Count + command.Amount })))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);

        await state.RefreshAsync();
        IStateSnapshot<CounterState> result = await state.DispatchAsync(new Add(3));
        StateInteractionRejectedException rejected = await Assert.ThrowsAsync<StateInteractionRejectedException>(async () =>
            await state.DispatchAsync(new Add(0)));

        Assert.Equal(3, result.RequiredValue.Count);
        Assert.Contains("positive", rejected.Message);
    }

    [Fact]
    public async Task Partitions_are_independent_and_capture_is_coherent()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("partitions")
            .State(Counter, state => state.Partitioned().Initial(new CounterState(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> first = harness.Runtime.State(Counter, "first");
        IState<CounterState> second = harness.Runtime.State(Counter, "second");

        await first.SetAsync(new CounterState(10));
        await second.SetAsync(new CounterState(20));
        StateSnapshotSet capture = await harness.Runtime.CaptureAsync(new[]
        {
            Counter.At("first"),
            Counter.At("second"),
        });

        Assert.Equal(2, capture.Snapshots.Count);
        Assert.Equal(10, ((IStateSnapshot<CounterState>)capture.Snapshots[first.Current.Address]).RequiredValue.Count);
        Assert.Equal(20, ((IStateSnapshot<CounterState>)capture.Snapshots[second.Current.Address]).RequiredValue.Count);
    }

    [Fact]
    public async Task Observation_stream_emits_the_current_value_and_subsequent_changes()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("observe")
            .State(Counter, state => state.Initial(new CounterState(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);
        await state.RefreshAsync();

        await using IAsyncEnumerator<StateChange<CounterState>> observer = state.ObserveAsync().GetAsyncEnumerator();
        Assert.True(await observer.MoveNextAsync());
        Assert.Equal(0, observer.Current.Current.RequiredValue.Count);

        await state.SetAsync(new CounterState(7));
        Assert.True(await observer.MoveNextAsync());
        Assert.Equal(StateOperation.Set, observer.Current.Operation);
        Assert.Equal(7, observer.Current.Current.RequiredValue.Count);
    }


    [Fact]
    public async Task Operation_subscriptions_expose_set_invalidate_clear_and_fault_events()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("operation-subscriptions")
            .State(Counter, state => state.Initial(new CounterState(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);
        var observed = new TaskCompletionSource<StateChange<CounterState>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using StateSubscription subscription = state.On(
            StateOperation.Set,
            (change, _) =>
            {
                observed.TrySetResult(change);
                return ValueTask.CompletedTask;
            });

        await state.InvalidateAsync();
        await state.SetAsync(new CounterState(19));
        StateChange<CounterState> change = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StateOperation.Set, change.Operation);
        Assert.Equal(19, change.Current.RequiredValue.Count);
    }

    [Fact]
    public async Task Retention_is_applied_after_commits()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("retention")
            .State(Counter, state => state.Retain(retention => retention.Last(2)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);

        await state.SetAsync(new CounterState(1));
        await state.SetAsync(new CounterState(2));
        await state.SetAsync(new CounterState(3));
        List<IStateSnapshot<CounterState>> history = await ReadHistoryAsync(state);

        Assert.Equal(2, history.Count);
        Assert.Equal(new long[] { 3, 2 }, history.Select(snapshot => snapshot.Revision).ToArray());
    }

    private static async Task<List<IStateSnapshot<CounterState>>> ReadHistoryAsync(IState<CounterState> state)
    {
        var result = new List<IStateSnapshot<CounterState>>();
        await foreach (IStateSnapshot<CounterState> snapshot in state.HistoryAsync())
        {
            result.Add(snapshot);
        }

        return result;
    }

    [ManagedState]
    private sealed record CounterState(int Count);

    private sealed record Add(int Amount);
}
