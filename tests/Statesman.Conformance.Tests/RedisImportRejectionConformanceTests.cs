namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared import-rejection suite, run against a live Redis when <c>STATESMAN_TEST_REDIS</c> is set,
/// and skipped cleanly when it is not.
/// </summary>
public sealed class RedisImportRejectionConformanceTests : ImportRejectionConformanceTests
{
    protected override string SkipReason => ConformanceProviders.RedisSkipReason;

    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(TimeProvider.System);
}
