namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared lease conformance suite, run against a live Redis when <c>STATESMAN_TEST_REDIS</c> is
/// set, and skipped cleanly when it is not.
/// </summary>
public sealed class RedisLeaseConformanceTests : LeaseConformanceTests
{
    // A real 200 ms TTL, because Redis expiry is the server's own clock: the store's TimeProvider is
    // not consulted at all, so there is no virtual form of this suite's expiry assertions. The same
    // figures tests/Statesman.Redis.Tests/RedisLeaseProviderTests.cs already uses.
    protected override TimeSpan LeaseTtl => TimeSpan.FromMilliseconds(200);

    protected override string SkipReason => ConformanceProviders.RedisSkipReason;

    protected override Task ExpireAsync() => Task.Delay(TimeSpan.FromMilliseconds(400));

    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(TimeProvider.System);
}
