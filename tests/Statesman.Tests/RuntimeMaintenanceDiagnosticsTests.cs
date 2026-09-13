using System.Diagnostics.Metrics;
using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// Exercises the ROADMAP 0.3 Phase 16 diagnostics surface directly: <see cref="IStatesmanDiagnostics"/>
/// discovered through <see cref="StatesmanDiagnosticsExtensions.TryGetDiagnostics"/>, the per-source
/// rate limit ahead of retention, and the retention bound across multiple rate-limit windows.
/// </summary>
/// <remarks>
/// Isolation: <c>RuntimeMaintenanceFailureBoundTests</c> also makes <c>PruneAsync</c> throw and reads
/// the same unlabelled meter counters (<c>Counter&lt;long&gt;.Add(1)</c> carries no tags), so this
/// class shares <see cref="MaintenanceFailureMeterCollection"/> with it. That collection's
/// <c>DisableParallelization</c> keeps the two classes from ever running at the same time as each
/// other while leaving the rest of the assembly free to parallelize.
/// </remarks>
[Collection(nameof(MaintenanceFailureMeterCollection))]
public sealed class RuntimeMaintenanceDiagnosticsTests
{
    private static readonly StateKey<int> Counter = StateKey.Define<int>("maintenance-diagnostics/counter");

    [Fact]
    public async Task The_diagnostics_surface_is_discoverable_and_reports_what_the_counters_report()
    {
        long reported = 0;
        long suppressed = 0;
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, StatesmanTelemetry.Meter)
                && instrument.Name.StartsWith("statesman.maintenance.failures", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            switch (instrument.Name)
            {
                case "statesman.maintenance.failures":
                    Interlocked.Add(ref reported, measurement);
                    break;
                case "statesman.maintenance.failures.suppressed":
                    Interlocked.Add(ref suppressed, measurement);
                    break;
                case "statesman.maintenance.failures.dropped":
                    Interlocked.Add(ref dropped, measurement);
                    break;
            }
        });
        listener.Start();

        var clock = new ManualTimeProvider();
        var store = new PruneFailingStore(new InMemoryStateLedgerStore("discoverable", clock));
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration("maintenance-diagnostics-discoverable", "discoverable"),
            stores: new IStateLedgerStore[] { store },
            time: clock);

        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));
        Assert.NotNull(diagnostics);

        IState<int> state = harness.Runtime.State(Counter);
        const int appends = StatesmanDiagnostics.MaintenanceFailureRate + 4;
        for (int index = 1; index <= appends; index++)
        {
            await state.SetAsync(index);
        }

        listener.RecordObservableInstruments();

        MaintenanceFailureDiagnostics snapshot = diagnostics!.ReadMaintenanceFailures();

        // Two independent paths to the same numbers is what makes either trustworthy.
        Assert.Equal(Interlocked.Read(ref reported), snapshot.Reported);
        Assert.Equal(Interlocked.Read(ref suppressed), snapshot.Suppressed);
        Assert.Equal(Interlocked.Read(ref dropped), snapshot.Dropped);
        Assert.Equal(appends, snapshot.Reported);
        Assert.Equal(StatesmanDiagnostics.MaintenanceFailureRate, snapshot.Retained.Count);
    }

    [Fact]
    public async Task One_source_is_limited_to_the_shipped_rate_per_window()
    {
        var clock = new ManualTimeProvider();
        var store = new PruneFailingStore(new InMemoryStateLedgerStore("rate-limited", clock));
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration("maintenance-diagnostics-rate-limited", "rate-limited"),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        IState<int> state = harness.Runtime.State(Counter);
        const int appends = StatesmanDiagnostics.MaintenanceFailureRate + 4;
        for (int index = 1; index <= appends; index++)
        {
            await state.SetAsync(index);
        }

        MaintenanceFailureDiagnostics snapshot = diagnostics!.ReadMaintenanceFailures();
        Assert.Equal(StatesmanDiagnostics.MaintenanceFailureRate, snapshot.Retained.Count);
        Assert.Equal(4, snapshot.Suppressed);
    }

    [Fact]
    public async Task Advancing_the_clock_past_the_window_admits_the_next_burst()
    {
        var clock = new ManualTimeProvider();
        var store = new PruneFailingStore(new InMemoryStateLedgerStore("window-advance", clock));
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration("maintenance-diagnostics-window-advance", "window-advance"),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        IState<int> state = harness.Runtime.State(Counter);

        // Exhaust the first window's rate exactly -- nothing suppressed yet.
        for (int index = 1; index <= StatesmanDiagnostics.MaintenanceFailureRate; index++)
        {
            await state.SetAsync(index);
        }

        MaintenanceFailureDiagnostics beforeAdvance = diagnostics!.ReadMaintenanceFailures();
        Assert.Equal(StatesmanDiagnostics.MaintenanceFailureRate, beforeAdvance.Retained.Count);
        Assert.Equal(0, beforeAdvance.Suppressed);

        // Advance the clock, never shorten the window: there is no option to shorten and there must
        // not be.
        clock.Advance(StatesmanDiagnostics.MaintenanceFailureRateWindow);

        await state.SetAsync(StatesmanDiagnostics.MaintenanceFailureRate + 1);

        MaintenanceFailureDiagnostics afterAdvance = diagnostics.ReadMaintenanceFailures();

        // The next burst's first failure is admitted rather than suppressed, so the retained count
        // grows and the suppressed count does not.
        Assert.Equal(StatesmanDiagnostics.MaintenanceFailureRate + 1, afterAdvance.Retained.Count);
        Assert.Equal(0, afterAdvance.Suppressed);
    }

    /// <summary>
    /// Pins that the window resets as a whole rather than refilling continuously. A continuous refill
    /// (one token every <c>MaintenanceFailureRateWindow / MaintenanceFailureRate</c>) would already
    /// admit a fraction of a fresh burst after only half the window has elapsed; a whole-window reset
    /// admits nothing until the entire window has elapsed. Neither
    /// <see cref="Advancing_the_clock_past_the_window_admits_the_next_burst"/> nor
    /// <see cref="The_retention_bound_holds_at_exactly_the_newest_sixty_four_across_windows"/> only ever
    /// advances the clock by a whole window, so a continuous-refill mutant happens to compute the same
    /// admitted/suppressed counts as the shipped whole-reset semantics at every point either of those
    /// two facts observes — this fact is the one that tells them apart, with a half-window advance.
    /// </summary>
    [Fact]
    public async Task Advancing_the_clock_partway_through_the_window_admits_nothing_more()
    {
        var clock = new ManualTimeProvider();
        var store = new PruneFailingStore(new InMemoryStateLedgerStore("window-partial-advance", clock));
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration("maintenance-diagnostics-window-partial-advance", "window-partial-advance"),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        IState<int> state = harness.Runtime.State(Counter);

        // Exhaust the first window's rate exactly -- nothing suppressed yet.
        for (int index = 1; index <= StatesmanDiagnostics.MaintenanceFailureRate; index++)
        {
            await state.SetAsync(index);
        }

        // Advance only halfway through the window. A continuous refill would free up half the tokens;
        // a whole-window reset frees none.
        clock.Advance(TimeSpan.FromTicks(StatesmanDiagnostics.MaintenanceFailureRateWindow.Ticks / 2));

        await state.SetAsync(StatesmanDiagnostics.MaintenanceFailureRate + 1);

        MaintenanceFailureDiagnostics snapshot = diagnostics!.ReadMaintenanceFailures();

        Assert.Equal(StatesmanDiagnostics.MaintenanceFailureRate, snapshot.Retained.Count);
        Assert.Equal(1, snapshot.Suppressed);
    }

    [Fact]
    public async Task The_retention_bound_holds_at_exactly_the_newest_sixty_four_across_windows()
    {
        var clock = new ManualTimeProvider();
        var store = new PruneFailingStore(new InMemoryStateLedgerStore("retention-bound", clock));
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration("maintenance-diagnostics-retention-bound", "retention-bound"),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        IState<int> state = harness.Runtime.State(Counter);

        // Five bursts of exactly the per-window rate, each in its own window: eighty admitted, none
        // suppressed, since every burst stays at or under the rate for its own window.
        const int windows = 5;
        int value = 0;
        for (int window = 0; window < windows; window++)
        {
            for (int index = 0; index < StatesmanDiagnostics.MaintenanceFailureRate; index++)
            {
                await state.SetAsync(++value);
            }

            if (window < windows - 1)
            {
                clock.Advance(StatesmanDiagnostics.MaintenanceFailureRateWindow);
            }
        }

        int totalAdmitted = windows * StatesmanDiagnostics.MaintenanceFailureRate;
        int totalDropped = totalAdmitted - StatesmanDiagnostics.MaxRetainedMaintenanceFailures;

        MaintenanceFailureDiagnostics snapshot = diagnostics!.ReadMaintenanceFailures();

        Assert.Equal(0, snapshot.Suppressed);
        Assert.Equal(StatesmanDiagnostics.MaxRetainedMaintenanceFailures, snapshot.Retained.Count);
        Assert.Equal(totalDropped, snapshot.Dropped);

        // The retained set's oldest entry is the seventeenth admitted failure: the first sixteen
        // (the first window) were evicted by the retention bound.
        Assert.Equal(
            totalDropped + 1,
            store.PruneFailureIndexOf(snapshot.Retained[0].Exception.Message));
    }

    [Fact]
    public void The_diagnostics_interface_is_deliberately_not_a_store_capability()
    {
        // CapabilityMatrixTests' completeness half reflects over every IStateCapability implementer in
        // Statesman.Abstractions and demands a row in docs/architecture/capabilities.md plus a tuple in
        // that test for each one. A runtime diagnostics surface has no provider column, so making it a
        // capability would put a row in a store matrix for something no store implements.
        Assert.False(typeof(IStateCapability).IsAssignableFrom(typeof(IStatesmanDiagnostics)));
    }

    private static StatesmanDeclaration BuildDeclaration(string id, string storeName) =>
        global::Statesman.Statesman.Declare(id)
            .State(Counter, state => state
                .StoreWith(storeName)
                .Initial(0))
            .Build();

    /// <summary>Forwards everything to an inner store except <c>PruneAsync</c>, which always throws.</summary>
    private sealed class PruneFailingStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner;
        private int _pruneFailures;

        public PruneFailingStore(InMemoryStateLedgerStore inner) => _inner = inner;

        public int PruneFailures => Volatile.Read(ref _pruneFailures);

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default)
        {
            int failureIndex = Interlocked.Increment(ref _pruneFailures);
            throw new InvalidOperationException($"prune failure {failureIndex}");
        }

        /// <summary>Extracts the failure index this store embedded in an exception message it raised.</summary>
        public int PruneFailureIndexOf(string exceptionMessage) =>
            int.Parse(exceptionMessage["prune failure ".Length..]);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
