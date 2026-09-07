using StackExchange.Redis;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-feed conformance suite, run against a live Redis when
/// <c>STATESMAN_TEST_REDIS</c> is set, and skipped cleanly when it is not.
/// </summary>
public sealed class RedisChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    // AppendAsync's only clock read is OccurredAt, before the append script runs.
    protected override int PauseCallIndex => 1;

    protected override string SkipReason =>
        "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.";

    protected override async ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return null;
        }

        ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        var store = new RedisStateLedgerStore(
            $"conformance-{Guid.NewGuid():N}",
            connection,
            new RedisStateLedgerStoreOptions { OwnsConnection = true },
            clock);

        return new ConformanceStore
        {
            Store = store,
            Feed = store,
        };
    }
}
