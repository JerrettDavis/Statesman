namespace Statesman.Conformance.Tests;

/// <summary>The shared partition-catalog suite, run against the in-memory provider.</summary>
public sealed class InMemoryPartitionCatalogConformanceTests : PartitionCatalogConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
