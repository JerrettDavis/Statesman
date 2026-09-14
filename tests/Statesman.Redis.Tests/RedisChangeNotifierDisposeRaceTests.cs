using StackExchange.Redis;

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
            new RedisStateLedgerStoreOptions { OwnsConnection = true });

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

        var store = new RedisStateLedgerStore($"notify-stall-{Guid.NewGuid():N}", connection);

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

    [Fact]
    public async Task A_multiplexer_disposed_by_its_actual_owner_must_not_end_the_read_loop_with_a_silent_false()
    {
        // Fix round 1. The Critical review finding: the read loop's widened catch
        // (OperationCanceledException plus RedisException, ObjectDisposedException, IOException,
        // SocketException) used to have only the "caller didn't cancel" operand, which entailed
        // "disposal did" for OperationCanceledException alone but not for the other four -- those
        // four can come from a connection failure with no cancellation of either token at all, most
        // concretely when OwnsConnection is false and the connection's actual owner (here, the test)
        // disposes it out from under a live subscription, which is exactly what this fact does.
        //
        // Measured before writing this assertion (see task-7-report.md, Fix round 1): with
        // OwnsConnection left at its default false, SUBSCRIBE completed (the proxy is never
        // stalled), one MoveNextAsync pending on the read loop, and the store never disposed,
        // disposing the multiplexer directly neither throws from MoveNextAsync nor completes it with
        // `false` within an 8 s window -- it just stays pending, because StackExchange.Redis's own
        // machinery does not tear down or complete a live ChannelMessageQueue on a plain Dispose.
        // Destroying the underlying proxy connection instead produced the identical shape. Neither
        // scenario reaches an exception at the read loop, so this fact cannot assert propagation; it
        // asserts the one thing both measured shapes support and the fix must guarantee regardless:
        // MoveNextAsync must never complete with a successful `false` here. Staying pending, or
        // completing with a thrown exception, both pass -- only a silent clean end does not.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using StallProxy proxy = StallProxy.Start(ConnectionString!);
        ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{proxy.Port},abortConnect=false");

        var store = new RedisStateLedgerStore($"notify-owner-dispose-{Guid.NewGuid():N}", connection);

        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Do NOT stall: SUBSCRIBE completes normally, so this single MoveNextAsync call both
        // performs the SUBSCRIBE round trip and then pends on the read loop's await for the next
        // pub/sub message.
        Task<bool> pending = hints.MoveNextAsync().AsTask();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // The multiplexer's actual owner -- the test, since OwnsConnection defaults to false --
        // disposes it directly, never through store.DisposeAsync(), so _disposalCts and linked are
        // never cancelled.
        await connection.DisposeAsync();

        Task delay = Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Task completed = await Task.WhenAny(pending, delay);
        if (completed == pending && pending.IsCompletedSuccessfully)
        {
            bool moved = await pending;
            Assert.False(
                moved,
                "a multiplexer disposed by its actual owner, with no store disposal, must not end "
                    + "the read loop with a silent `false`; an exception or an indefinite pend are "
                    + "both acceptable, a clean completion is not");
        }

        // A completion by fault (a genuine exception propagated) or a still-pending task at the
        // bound are both the outcome this fact requires; only the successful-`false` branch above
        // needed an assertion. Dispose the enumerator only when it actually finished -- forcing
        // disposal on a MoveNextAsync that is still outstanding is not part of what this fact
        // measures and is not guaranteed to complete either.
        if (completed == pending)
        {
            await hints.DisposeAsync();
        }
    }
}
