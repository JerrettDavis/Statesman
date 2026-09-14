namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared retention suite, run against a live Redis when <c>STATESMAN_TEST_REDIS</c> is set, and
/// skipped cleanly when it is not. Redis retention reads the injected clock rather than the server's
/// own, unlike Redis lease expiry, so every age assertion here is exact.
/// </summary>
public sealed class RedisRetentionConformanceTests : RetentionConformanceTests
{
    protected override string SkipReason => ConformanceProviders.RedisSkipReason;

    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(Clock);
}
