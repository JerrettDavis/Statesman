using StackExchange.Redis;
using Statesman.TestHelpers;

namespace Statesman.Redis.Tests;

/// <summary>
/// The dispose race that <see cref="RedisChangeNotifierTests"/>'s
/// <c>Disposing_the_store_ends_a_live_subscription_even_when_it_does_not_own_the_connection</c> can
/// only hit by luck: these two facts make a Redis command genuinely in flight when the multiplexer
/// closes, using <see cref="StallProxy"/> instead of a repetition count.
/// </summary>
public sealed class RedisChangeNotifierDisposeRaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public async Task Disposing_the_store_ends_a_subscription_whose_SUBSCRIBE_is_still_in_flight()
    {
        // The store owns this connection, so DisposeAsync cancels the disposal token and then closes
        // the multiplexer immediately. StackExchange.Redis's SubscribeAsync takes no cancellation
        // token, so the SUBSCRIBE round trip is still outstanding when the socket is torn down, and
        // it throws from the await rather than from the read loop. The contract is unchanged:
        // MoveNextAsync returns false, with no exception the caller never asked for.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using StallProxy proxy = StallProxy.Start(ConnectionString!);
        ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{proxy.Port},abortConnect=false");

        var store = new RedisStateLedgerStore(
            $"notify-stall-{Guid.NewGuid():N}",
            connection,
            RedisTestLayout.Options(ownsConnection: true));

        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // The handshake has completed by now; from here on every command the client writes is
        // swallowed and never answered.
        proxy.Stall = true;

        ValueTask<bool> pending = hints.MoveNextAsync();
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await store.DisposeAsync();

        bool moved = await pending.AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(
            moved,
            "the subscription should end cleanly (MoveNextAsync returns false) when the store is "
                + "disposed while its SUBSCRIBE is still in flight, not throw and not hang");

        await hints.DisposeAsync();
    }

    [Fact]
    public async Task A_genuine_connection_failure_with_no_disposal_still_reaches_the_caller()
    {
        // The anti-lever for the fact above. The guard is narrowed by
        // `linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested`, not by the
        // exception type alone, so with the store's own disposal never invoked a real socket failure
        // must propagate unchanged. OwnsConnection is left at its default false and DisposeAsync is
        // never called; the proxy is destroyed while the SUBSCRIBE is outstanding.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        StallProxy proxy = StallProxy.Start(ConnectionString!);
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{proxy.Port},abortConnect=false");

        var store = new RedisStateLedgerStore($"notify-stall-{Guid.NewGuid():N}", connection, RedisTestLayout.Options());

        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        proxy.Stall = true;
        ValueTask<bool> pending = hints.MoveNextAsync();
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await proxy.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => pending.AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken));

        await hints.DisposeAsync();
    }
}
