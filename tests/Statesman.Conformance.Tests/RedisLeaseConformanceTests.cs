namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared lease conformance suite, run against a live Redis when <c>STATESMAN_TEST_REDIS</c> is
/// set, and skipped cleanly when it is not.
/// </summary>
public sealed class RedisLeaseConformanceTests : LeaseConformanceTests
{
    // Thirty seconds, because nothing in this suite waits a hold TTL out: every acquisition that must
    // still be held when its assertion lands takes this one, so no amount of scheduler stall on a
    // loaded runner can expire it first. The same figure
    // tests/Statesman.Redis.Tests/RedisLeaseProviderTests.cs:84 already uses for its successor.
    protected override TimeSpan HoldTtl => TimeSpan.FromSeconds(30);

    // A real 200 ms TTL, because Redis expiry is the server's own clock: the store's TimeProvider is
    // not consulted at all, so there is no virtual form of this suite's expiry assertions. Only the
    // acquisitions ExpireAsync is going to wait out take it.
    protected override TimeSpan ExpiringTtl => TimeSpan.FromMilliseconds(200);

    protected override string SkipReason => ConformanceProviders.RedisSkipReason;

    protected override Task ExpireAsync() => Task.Delay(TimeSpan.FromMilliseconds(400));

    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(TimeProvider.System);
}
