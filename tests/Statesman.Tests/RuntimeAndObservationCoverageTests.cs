using Statesman.Testing;

namespace Statesman.Tests;

public sealed class RuntimeAndObservationCoverageTests
{
    private static readonly StateKey<ObservedState> Observed = StateKey.Define<ObservedState>("users/observed");
    private static readonly StateKey<ObservedState> Global = StateKey.Define<ObservedState>("global");
    private static readonly StateKey<int> Orders = StateKey.Define<int>("orders/count");

    [Fact]
    public async Task Container_views_route_operations_and_enforce_scope()
    {
        var source = new TrackingSource();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("container-runtime")
            .Container("users", users => users
                .Isolated()
                .State(Observed, state => state
                    .Partitioned()
                    .Initial(new ObservedState("seed", 0))
                    .Refresh(refresh => refresh.OnSignal("reload"))
                    .Load(load => load.From<TrackingSource>(
                        "profile",
                        (service, context, cancellationToken) => service.LoadAsync(context.Address.Canonical, cancellationToken)))))
            .Container("orders", orders => orders
                .State(Orders, state => state.Initial(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(source));
        IStateContainer users = harness.Runtime.Container("users");
        StateReference observed = new(Observed.Path, new StatePartition("jd"));
        StateReference orders = new(Orders.Path, StatePartition.Default);

        Assert.Equal("container-runtime", users.RootId);
        Assert.Equal(StateContainerIsolation.Isolated, users.Isolation);

        IState<ObservedState> typed = users.State(Observed, "jd");
        await typed.SetAsync(new ObservedState("manual", 1));
        IStateSnapshot fetched = await users.GetAsync(observed, StateReadOptions.Cached);
        Assert.Equal(StateStatus.Ready, fetched.Status);

        await using IAsyncEnumerator<StateChange> observer = users
            .ObserveAsync(new StateObservationOptions { IncludeCurrent = false })
            .GetAsyncEnumerator();
        Task<bool> observedMove = observer.MoveNextAsync().AsTask();

        IStateSnapshot invalidated = await users.InvalidateAsync(observed, "stale");
        Assert.Equal(StateStatus.Invalidated, invalidated.Status);
        Assert.True(await observedMove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(StateOperation.Invalidated, observer.Current.Operation);

        IStateSnapshot cleared = await users.ClearAsync(observed, "reset");
        Assert.Equal(StateStatus.Cleared, cleared.Status);

        await users.SignalAsync(new StateSignal(
            "reload",
            new StatePartition("jd"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cause"] = "container",
            }));

        IStateSnapshot<ObservedState> reloaded = await harness.Runtime.State(Observed, "jd").GetAsync(StateReadOptions.Cached);
        StateSnapshotSet capture = await users.CaptureAsync(new[] { observed });
        List<IStateSnapshot> history = await ReadHistoryAsync(users.HistoryAsync(observed));

        Assert.Equal(1, source.Calls["container-runtime::users/observed::jd"]);
        Assert.Equal("container", reloaded.Metadata["cause"]);
        Assert.Single(capture.Snapshots);
        Assert.Contains(history, snapshot => snapshot.Operation == StateOperation.Invalidated);
        Assert.Contains(history, snapshot => snapshot.Operation == StateOperation.Cleared);
        Assert.Contains(history, snapshot => snapshot.Operation == StateOperation.Refreshed);
        Assert.Throws<StateDeclarationException>(() => users.State(Orders));
        await Assert.ThrowsAsync<StateDeclarationException>(async () => await users.GetAsync(orders));
        await Assert.ThrowsAsync<StateDeclarationException>(async () => await users.SetAsync(orders, 1));
        await Assert.ThrowsAsync<StateDeclarationException>(async () => await users.InvalidateAsync(orders));
        await Assert.ThrowsAsync<StateDeclarationException>(async () => await users.ClearAsync(orders));
        await Assert.ThrowsAsync<StateDeclarationException>(async () => await users.CaptureAsync(new[] { orders }));
        await Assert.ThrowsAsync<StateDeclarationException>(async () => await ReadHistoryAsync(users.HistoryAsync(orders)));
    }

    [Fact]
    public async Task Runtime_initialization_signals_and_observation_extensions_cover_expected_paths()
    {
        var source = new TrackingSource();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("runtime-coverage")
            .Container("users", users => users
                .State(Observed, state => state
                    .Partitioned()
                    .Initial(new ObservedState("seed", 0))
                    .Refresh(refresh => refresh
                        .WarmOnStart("jd", "ada")
                        .OnSignal("reload"))
                    .Load(load => load.From<TrackingSource>(
                        "users",
                        (service, context, cancellationToken) => service.LoadAsync(context.Address.Canonical, cancellationToken)))))
            .State(Global, state => state
                .Initial(new ObservedState("global", 0))
                .Refresh(refresh => refresh
                    .WarmOnStart()
                    .OnSignal("reload"))
                .Load(load => load.From<TrackingSource>(
                    "global",
                    (service, context, cancellationToken) => service.LoadAsync(context.Address.Canonical, cancellationToken))))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(source));

        await harness.Runtime.InitializeAsync();
        await harness.Runtime.InitializeAsync();

        Assert.True(harness.Runtime.IsInitialized);
        Assert.Equal(1, source.Calls["runtime-coverage::users/observed::ada"]);
        Assert.Equal(1, source.Calls["runtime-coverage::users/observed::jd"]);
        Assert.Equal(1, source.Calls["runtime-coverage::global::default"]);

        IState<ObservedState> state = harness.Runtime.State(Observed, "jd");
        var operations = new List<StateOperation>();
        var setSeen = new TaskCompletionSource<StateOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitionSeen = new TaskCompletionSource<StateOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidatedSeen = new TaskCompletionSource<StateOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearedSeen = new TaskCompletionSource<StateOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultSeen = new TaskCompletionSource<StateOperation>(TaskCreationOptions.RunContinuationsAsynchronously);

        using StateSubscription onChange = state.OnChange((change, _) =>
        {
            operations.Add(change.Operation);
            return ValueTask.CompletedTask;
        }, new StateObservationOptions { IncludeCurrent = false });
        await using StateSubscription onSet = state.OnSet((change, _) =>
        {
            setSeen.TrySetResult(change.Operation);
            return ValueTask.CompletedTask;
        }, new StateObservationOptions { IncludeCurrent = false });
        await using StateSubscription onTransition = state.OnTransition((change, _) =>
        {
            transitionSeen.TrySetResult(change.Operation);
            return ValueTask.CompletedTask;
        }, new StateObservationOptions { IncludeCurrent = false });
        await using StateSubscription onInvalidated = state.OnInvalidated((change, _) =>
        {
            invalidatedSeen.TrySetResult(change.Operation);
            return ValueTask.CompletedTask;
        }, new StateObservationOptions { IncludeCurrent = false });
        await using StateSubscription onCleared = state.OnCleared((change, _) =>
        {
            clearedSeen.TrySetResult(change.Operation);
            return ValueTask.CompletedTask;
        }, new StateObservationOptions { IncludeCurrent = false });
        await using StateSubscription onFaulted = state.OnFaulted((change, _) =>
        {
            faultSeen.TrySetResult(change.Operation);
            return ValueTask.CompletedTask;
        }, new StateObservationOptions { IncludeCurrent = false });

        await state.SetAsync(new ObservedState("manual", 1));
        await state.UpdateAsync(current => current! with { Revision = current.Revision + 1 });
        await state.InvalidateAsync("stale");

        await using IAsyncEnumerator<StateChange<ObservedState>> filteredObserver = state.ObserveAsync(
                StateOperation.Cleared,
                new StateObservationOptions { IncludeCurrent = false })
            .GetAsyncEnumerator();
        Task<bool> filteredMove = filteredObserver.MoveNextAsync().AsTask();
        await state.ClearAsync("clear");
        Assert.True(await filteredMove.WaitAsync(TimeSpan.FromSeconds(2)));
        StateChange<ObservedState> filtered = filteredObserver.Current;

        source.FailFor.Add("runtime-coverage::users/observed::jd");
        IStateSnapshot<ObservedState> faulted = await state.RefreshAsync();
        await Task.WhenAll(
            setSeen.Task,
            transitionSeen.Task,
            invalidatedSeen.Task,
            clearedSeen.Task,
            faultSeen.Task).WaitAsync(TimeSpan.FromSeconds(2));

        source.FailFor.Clear();
        await harness.Runtime.Container("users").SignalAsync(new StateSignal("reload", new StatePartition("ada")));
        await harness.Runtime.SignalAsync(new StateSignal("reload"));

        StateOperation setOperation = await setSeen.Task;
        StateOperation transitionOperation = await transitionSeen.Task;
        StateOperation invalidatedOperation = await invalidatedSeen.Task;
        StateOperation clearedOperation = await clearedSeen.Task;
        StateOperation faultOperation = await faultSeen.Task;

        Assert.Equal(StateStatus.Faulted, faulted.Status);
        Assert.Equal(StateOperation.Set, setOperation);
        Assert.Equal(StateOperation.Transitioned, transitionOperation);
        Assert.Equal(StateOperation.Invalidated, invalidatedOperation);
        Assert.Equal(StateOperation.Cleared, clearedOperation);
        Assert.Equal(StateOperation.Faulted, faultOperation);
        Assert.Equal(StateOperation.Cleared, filtered.Operation);
        Assert.Contains(StateOperation.Set, operations);
        Assert.Contains(StateOperation.Transitioned, operations);
        Assert.Contains(StateOperation.Invalidated, operations);
        Assert.Contains(StateOperation.Cleared, operations);
        Assert.Contains(StateOperation.Faulted, operations);
        Assert.Equal(3, source.Calls["runtime-coverage::users/observed::ada"]);
        Assert.Equal(3, source.Calls["runtime-coverage::users/observed::jd"]);
        Assert.Equal(2, source.Calls["runtime-coverage::global::default"]);

        onChange.Dispose();
    }

    private static async Task<List<IStateSnapshot>> ReadHistoryAsync(IAsyncEnumerable<IStateSnapshot> history)
    {
        var snapshots = new List<IStateSnapshot>();
        await foreach (IStateSnapshot snapshot in history)
        {
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    [ManagedState]
    private sealed record ObservedState(string Name, int Revision);

    private sealed class TrackingSource
    {
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

        public ValueTask<ObservedState> LoadAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls[key] = Calls.TryGetValue(key, out int count) ? count + 1 : 1;
            if (FailFor.Contains(key))
            {
                throw new InvalidOperationException($"Failed to load '{key}'.");
            }

            return ValueTask.FromResult(new ObservedState(key, Calls[key]));
        }
    }
}
