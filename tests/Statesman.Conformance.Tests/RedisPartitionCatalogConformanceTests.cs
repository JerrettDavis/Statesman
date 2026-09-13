namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared partition-catalog suite, run against a live Redis when <c>STATESMAN_TEST_REDIS</c> is
/// set, and skipped cleanly when it is not.
/// </summary>
public sealed class RedisPartitionCatalogConformanceTests : PartitionCatalogConformanceTests
{
    protected override string SkipReason => ConformanceProviders.RedisSkipReason;

    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(TimeProvider.System);
}
