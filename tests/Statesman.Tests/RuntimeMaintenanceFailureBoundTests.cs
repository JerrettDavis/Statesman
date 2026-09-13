using System.Diagnostics.Metrics;
using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// Groups every test that makes <c>PruneAsync</c> throw and reads the unlabelled maintenance-failure
/// meter counters, so xUnit never runs two of them at the same time. Tests outside this collection are
/// unaffected and still parallelize normally.
/// </summary>
[CollectionDefinition(nameof(MaintenanceFailureMeterCollection), DisableParallelization = true)]
public sealed class MaintenanceFailureMeterCollection;

/// <summary>
/// Pins the bound on the runtime's maintenance-failure retention. Before ROADMAP 0.3 Phase 15 the
/// queue was an unbounded <c>ConcurrentQueue&lt;Exception&gt;</c> fed once per successful append by
/// <c>StateHandle</c>'s post-append prune path, with no dequeue and no consumer anywhere in the
/// repository — so a store whose prune kept failing retained one exception per append for the
/// lifetime of the runtime.
/// </summary>
/// <remarks>
/// <para>
/// ROADMAP 0.3 Phase 16 added a per-source rate limit ahead of the retention bound: a single store
/// whose prune keeps failing is capped at <see cref="StatesmanDiagnostics.MaintenanceFailureRate"/>
/// retained failures per <see cref="StatesmanDiagnostics.MaintenanceFailureRateWindow"/>, so a run of
/// one hundred appends against one store within one window no longer reaches the retention bound at
/// all — it is throttled long before that. This test therefore no longer exercises
/// <see cref="StatesmanDiagnostics.MaxRetainedMaintenanceFailures"/> directly;
/// <c>RuntimeMaintenanceDiagnosticsTests</c> drives multiple windows to reach it.
/// </para>
/// <para>
/// The retained collection is <c>internal</c> and this repository has zero
/// <c>[InternalsVisibleTo]</c>, so this test observes the bound and the rate limit through the three
/// counters on <see cref="StatesmanTelemetry.Meter"/>, which is public. That is the public seam this
/// test exercises; the read-and-clear diagnostics surface (<see cref="IStatesmanDiagnostics"/>) is
/// exercised directly in <c>RuntimeMaintenanceDiagnosticsTests</c>.
/// </para>
/// <para>
/// Isolation: the meter counters this test reads are unlabelled (<c>Counter&lt;long&gt;.Add(1)</c>
/// carries no tags), so any sibling test that also makes <c>PruneAsync</c> throw while this one is
/// running would perturb these reads regardless of which store name either test uses.
/// <c>RuntimeMaintenanceDiagnosticsTests</c> does exactly that, so both test classes share
/// <see cref="MaintenanceFailureMeterCollection"/>, whose <c>DisableParallelization</c> keeps them
/// from ever running at the same time as each other while leaving the rest of the assembly free to
/// parallelize. A future test that also makes pruning fail must join that same collection.
/// </para>
/// </remarks>
[Collection(nameof(MaintenanceFailureMeterCollection))]
public sealed class RuntimeMaintenanceFailureBoundTests
{
    private const int Appends = 100;

    private static readonly StateKey<int> Counter = StateKey.Define<int>("maintenance-bound/counter");

    [Fact]
    public async Task Reported_maintenance_failures_are_counted_and_the_retention_is_bounded()
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
            if (instrument.Name == "statesman.maintenance.failures")
            {
                Interlocked.Add(ref reported, measurement);
            }
            else if (instrument.Name == "statesman.maintenance.failures.suppressed")
            {
                Interlocked.Add(ref suppressed, measurement);
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

        // The per-source rate limit admits StatesmanDiagnostics.MaintenanceFailureRate per window from
        // one store within one window, and suppresses the rest before retention is ever consulted --
        // so nothing reaches the retention bound and nothing is dropped.
        Assert.Equal(
            Appends - StatesmanDiagnostics.MaintenanceFailureRate,
            Interlocked.Read(ref suppressed));
        Assert.Equal(0, Interlocked.Read(ref dropped));
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
