namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-feed conformance suite, run against the tiered provider over an in-memory hot
/// replica and an in-memory cold authority. <see cref="TieredStateLedgerStore.AppendAsync"/> and
/// <see cref="TieredStateLedgerStore.ReadAsync"/> both delegate directly to the cold store, so the
/// clock call that sits in the window between allocating a position and publishing it is the cold
/// store's own -- the same call the in-memory conformance suite pauses on.
/// </summary>
public sealed class TieredChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    // AppendAsync delegates straight to the cold store; that store here is
    // InMemoryStateLedgerStore, whose only clock read is OccurredAt, immediately after the
    // position is allocated.
    protected override int PauseCallIndex => 1;

    protected override ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock)
    {
        var cold = new InMemoryStateLedgerStore("tiered-conformance-cold", clock);
        var hot = new InMemoryStateLedgerStore("tiered-conformance-hot");
        var store = new TieredStateLedgerStore("tiered-conformance", hot, cold, ownsStores: true);
        return ValueTask.FromResult<ConformanceStore?>(new ConformanceStore
        {
            Store = store,
            Feed = store,
        });
    }
}
