using Statesman.Testing;

namespace Statesman.Tests;

public sealed class RuntimeEdgeCaseTests
{
    private static readonly StateKey<CounterState> Counter = StateKey.Define<CounterState>("counter");

    [Fact]
    public async Task Strict_fresh_reads_reject_stale_non_faulted_values()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("strict-fresh")
            .State(Counter, state => state
                .Freshness(freshness => freshness.FreshFor(TimeSpan.FromMinutes(1))))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);

        await state.SetAsync(new CounterState(7));
        harness.Time.Advance(TimeSpan.FromMinutes(2));

        StateUnavailableException exception = await Assert.ThrowsAsync<StateUnavailableException>(async () =>
            await state.GetAsync(StateReadOptions.Fresh));

        Assert.Equal(StateStatus.Stale, exception.Status);
    }

    [Fact]
    public async Task Fresh_reads_can_explicitly_serve_a_faulted_last_known_value()
    {
        var source = new FlakyCounterSource();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("faulted-last-known")
            .State(Counter, state => state
                .Freshness(freshness => freshness.FreshFor(TimeSpan.FromMinutes(1)))
                .Load(load => load.From<FlakyCounterSource>(
                    "counter-api",
                    (service, _, cancellationToken) => service.LoadAsync(cancellationToken))))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(source));
        IState<CounterState> state = harness.Runtime.State(Counter);

        IStateSnapshot<CounterState> initial = await state.RefreshAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        source.Fail = true;

        IStateSnapshot<CounterState> lastKnown = await state.GetAsync(new StateReadOptions
        {
            Mode = StateReadMode.Fresh,
            AllowLastKnownOnFault = true,
        });

        Assert.Equal(StateStatus.Faulted, lastKnown.Status);
        Assert.True(lastKnown.HasValue);
        Assert.Equal(initial.RequiredValue, lastKnown.RequiredValue);
        await Assert.ThrowsAsync<StateUnavailableException>(async () =>
            await state.GetAsync(new StateReadOptions
            {
                Mode = StateReadMode.Fresh,
                AllowLastKnownOnFault = false,
            }));
    }

    [Fact]
    public async Task Fault_recording_preserves_explicit_revision_conflicts()
    {
        var source = new FlakyCounterSource { Fail = true };
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("fault-concurrency")
            .State(Counter, state => state.Load(load => load.From<FlakyCounterSource>(
                "counter-api",
                (service, _, cancellationToken) => service.LoadAsync(cancellationToken))))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(source));
        IState<CounterState> state = harness.Runtime.State(Counter);
        await state.SetAsync(new CounterState(3));

        StateConcurrencyException exception = await Assert.ThrowsAsync<StateConcurrencyException>(async () =>
            await state.RefreshAsync(new StateWriteOptions { ExpectedRevision = 99 }));

        Assert.Equal(99L, exception.Expected);
        Assert.Equal(1L, exception.Actual);
    }

    [Fact]
    public async Task When_stale_refresh_retries_a_faulted_last_known_value()
    {
        var source = new CountingFlakyCounterSource();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("fault-retry")
            .State(Counter, state => state
                .Freshness(freshness => freshness.FreshFor(TimeSpan.FromMinutes(1)))
                .Refresh(refresh => refresh.WhenStale())
                .Load(load => load.From<CountingFlakyCounterSource>(
                    "counter-api",
                    (service, _, cancellationToken) => service.LoadAsync(cancellationToken))))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(source));
        IState<CounterState> state = harness.Runtime.State(Counter);

        await state.RefreshAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        source.Fail = true;
        IStateSnapshot<CounterState> faulted = await state.GetAsync();

        source.Fail = false;
        IStateSnapshot<CounterState> recovered = await state.GetAsync();

        Assert.Equal(StateStatus.Faulted, faulted.Status);
        Assert.Equal(StateStatus.Ready, recovered.Status);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task Retained_state_handles_reject_use_after_root_disposal()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("disposed")
            .State(Counter, state => state.Initial(new CounterState(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);
        await state.SetAsync(new CounterState(1));

        await harness.Runtime.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => _ = state.Current);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await state.SetAsync(new CounterState(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await state.RefreshAsync());
    }

    [Fact]
    public async Task Initial_only_state_records_its_first_materialization_as_seeded()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("seeded")
            .State(Counter, state => state
                .Initial(new CounterState(11))
                .Freshness(freshness => freshness
                    .FreshFor(TimeSpan.FromMinutes(5))
                    .ServeStaleFor(TimeSpan.FromMinutes(10))))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);

        IStateSnapshot<CounterState> snapshot = await state.RefreshAsync();

        Assert.Equal(StateOperation.Seeded, snapshot.Operation);
        Assert.NotNull(snapshot.FreshUntil);
        Assert.NotNull(snapshot.ServeUntil);
        Assert.Equal(state.Address, snapshot.Address);
        Assert.Equal(state.Manifest.Path, state.Key.Path);
    }


    [Fact]
    public async Task Concurrent_refreshes_share_one_in_flight_load()
    {
        var source = new BlockingCounterSource();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("single-flight")
            .State(Counter, state => state.Load(load => load.From<BlockingCounterSource>(
                "counter-api",
                (service, _, cancellationToken) => service.LoadAsync(cancellationToken))))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(source));
        IState<CounterState> state = harness.Runtime.State(Counter);

        Task<IStateSnapshot<CounterState>> first = state.RefreshAsync().AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<IStateSnapshot<CounterState>> second = state.RefreshAsync().AsTask();
        source.Release.TrySetResult();

        IStateSnapshot<CounterState>[] snapshots = await Task.WhenAll(first, second);

        Assert.Equal(1, source.Calls);
        Assert.All(snapshots, snapshot => Assert.Equal(1, snapshot.Revision));
    }

    [Fact]
    public async Task Invalid_history_and_observation_bounds_fail_fast()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("bounds")
            .State(Counter, state => state.Initial(new CounterState(0)))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        IState<CounterState> state = harness.Runtime.State(Counter);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (IStateSnapshot<CounterState> _ in state.HistoryAsync(new StateHistoryOptions { Take = 0 }))
            {
            }
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using IAsyncEnumerator<StateChange<CounterState>> observer = state
                .ObserveAsync(new StateObservationOptions { BufferCapacity = 0 })
                .GetAsyncEnumerator();
            await observer.MoveNextAsync();
        });
    }

    [ManagedState]
    private sealed record CounterState(int Count);

    private sealed class BlockingCounterSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<CounterState> LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new CounterState(73);
        }
    }

    private sealed class CountingFlakyCounterSource
    {
        private int _calls;

        public bool Fail { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<CounterState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            if (Fail)
            {
                throw new HttpRequestException("The counter endpoint is unavailable.");
            }

            return ValueTask.FromResult(new CounterState(42));
        }
    }

    private sealed class FlakyCounterSource
    {
        public bool Fail { get; set; }

        public ValueTask<CounterState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail)
            {
                throw new HttpRequestException("The counter endpoint is unavailable.");
            }

            return ValueTask.FromResult(new CounterState(42));
        }
    }
}
