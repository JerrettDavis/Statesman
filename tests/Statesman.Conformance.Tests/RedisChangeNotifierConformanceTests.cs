namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-notifier suite, run against a live Redis when <c>STATESMAN_TEST_REDIS</c> is set,
/// and skipped cleanly when it is not. Redis notifications are a fire-and-forget <c>PUBLISH</c> with no
/// backlog, which is exactly what the shared assertion's retry-until-delivered loop accounts for.
/// </summary>
public sealed class RedisChangeNotifierConformanceTests : ChangeNotifierConformanceTests
{
    protected override string SkipReason => ConformanceProviders.RedisSkipReason;

    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(TimeProvider.System);
}
