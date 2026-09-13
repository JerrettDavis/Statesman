namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared partition-catalog suite, run against the tiered provider over in-memory hot and cold
/// stores. Tiered forwards <c>ListPartitionsAsync</c> to its cold tier, so the first three facts run
/// normally; the import-sourced fact skips because Tiered vetoes <see cref="IStateLedgerReplica"/>
/// outright, even though its in-memory hot tier implements it privately.
/// </summary>
public sealed class TieredPartitionCatalogConformanceTests : PartitionCatalogConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
