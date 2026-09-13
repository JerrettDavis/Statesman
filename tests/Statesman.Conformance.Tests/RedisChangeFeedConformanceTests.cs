namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-feed conformance suite, run against a live Redis when
/// <c>STATESMAN_TEST_REDIS</c> is set, and skipped cleanly when it is not.
/// </summary>
public sealed class RedisChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    // AppendAsync's only clock read is OccurredAt, before the append script runs.
    protected override int PauseCallIndex => 1;

    protected override string SkipReason => ConformanceProviders.RedisSkipReason;

    protected override ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock) =>
        ConformanceProviders.RedisAsync(clock);
}
