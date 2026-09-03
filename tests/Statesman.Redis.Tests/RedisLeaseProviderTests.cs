using StackExchange.Redis;

namespace Statesman.Redis.Tests;

public sealed class RedisLeaseProviderTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public void RedisStateLedgerStore_reports_lease_capability()
    {
        Assert.True(typeof(RedisStateLedgerStore).GetInterfaces().Contains(typeof(IStateLeaseProvider)));
    }

    [Fact]
    public async Task AcquireAsync_grants_exclusive_ownership_until_release()
    {
        Assert.SkipUnless(ConnectionString is not null,
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"lease-test-{Guid.NewGuid():N}", connection);

        IStateLease? first = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(first);

        IStateLease? second = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.Null(second);

        await first!.DisposeAsync();

        IStateLease? third = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(third);
        await third!.DisposeAsync();
    }

    [Fact]
    public async Task RenewAsync_extends_the_lease_while_it_is_still_held()
    {
        Assert.SkipUnless(ConnectionString is not null,
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"lease-test-{Guid.NewGuid():N}", connection);

        IStateLease? lease = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        Assert.True(await lease!.RenewAsync(TimeSpan.FromSeconds(60)));

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RenewAsync_returns_false_after_the_lease_was_released()
    {
        Assert.SkipUnless(ConnectionString is not null,
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"lease-test-{Guid.NewGuid():N}", connection);

        IStateLease? lease = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        await lease!.DisposeAsync();

        Assert.False(await lease.RenewAsync(TimeSpan.FromSeconds(60)));
    }
}
