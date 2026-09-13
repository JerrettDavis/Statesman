namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared partition-catalog suite, run against the Entity Framework Core provider over the shared
/// single-connection fixture. Every assertion in this suite is sequential, so the default concurrency
/// (a single connection) is sufficient.
/// </summary>
public sealed class EntityFrameworkPartitionCatalogConformanceTests : PartitionCatalogConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(TimeProvider.System);
}
