using System.Diagnostics.Metrics;
using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// Pins the bound on the runtime's maintenance-failure retention. Before ROADMAP 0.3 Phase 15 the
/// queue was an unbounded <c>ConcurrentQueue&lt;Exception&gt;</c> fed once per successful append by
/// <c>StateHandle</c>'s post-append prune path, with no dequeue and no consumer anywhere in the
/// repository — so a store whose prune kept failing retained one exception per append for the
/// lifetime of the runtime.
/// </summary>
/// <remarks>
/// <para>
/// The retained collection is <c>internal</c> and this repository has zero
/// <c>[InternalsVisibleTo]</c>, so the bound is observed through the two counters on
/// <see cref="StatesmanTelemetry.Meter"/>, which is public. That is the public seam; it is not the
/// read-and-clear diagnostics surface, which stays out of scope.
/// </para>
/// <para>
/// Isolation assumption: no sibling test in this assembly makes <c>PruneAsync</c> throw, so the meter
/// counters this test reads cannot be perturbed by another test running in parallel. A future test
/// that also makes pruning fail must join a collection with this one, or filter its own measurements
/// by store name.
/// </para>
/// </remarks>
public sealed class RuntimeMaintenanceFailureBoundTests
{
    private const int Appends = 100;

    // Mirrors the private const of the same name in src/Statesman/Runtime/StatesmanRuntime.cs. Kept
    // here rather than as a shared public type per the Phase 15 controller ruling: a public read of
    // the bound is not worth a new public surface when the two counters already make it observable.
    private const int MaxRetainedMaintenanceFailures = 64;

    private static readonly StateKey<int> Counter = StateKey.Define<int>("maintenance-bound/counter");

    [Fact]
    public async Task Reported_maintenance_failures_are_counted_and_the_retention_is_bounded()
    {
        long reported = 0;
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
            if (instrument.Name == "statesman.maintenance.failures")
            {
                Interlocked.Add(ref reported, measurement);
            }
            else if (instrument.Name == "statesman.maintenance.failures.dropped")
            {
                Interlocked.Add(ref dropped, measurement);
            }
        });
        listener.Start();

        var store = new PruneFailingStore(new InMemoryStateLedgerStore("pruning"));
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration(),
            stores: new IStateLedgerStore[] { store });

        IState<int> state = harness.Runtime.State(Counter);
        for (int index = 1; index <= Appends; index++)
        {
            await state.SetAsync(index);
        }

        listener.RecordObservableInstruments();

        // Every append's prune threw, and every throw was reported.
        Assert.Equal(Appends, store.PruneFailures);
        Assert.Equal(Appends, Interlocked.Read(ref reported));

        // And the retention is bounded: everything past the bound was dropped and said so.
        Assert.Equal(
            Appends - MaxRetainedMaintenanceFailures,
            Interlocked.Read(ref dropped));
    }

    private static StatesmanDeclaration BuildDeclaration() =>
        global::Statesman.Statesman.Declare("maintenance-bound")
            .State(Counter, state => state
                .StoreWith("pruning")
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
            Interlocked.Increment(ref _pruneFailures);
            throw new InvalidOperationException(
                $"prune failure {Volatile.Read(ref _pruneFailures)}");
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
