namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared record-metadata suite, run against the tiered provider over in-memory hot and cold
/// stores. The import fact skips: Tiered vetoes <see cref="IStateLedgerReplica"/> outright, because the
/// hot replica is its private cache-repair channel.
/// </summary>
public sealed class TieredRecordMetadataConformanceTests : RecordMetadataConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
