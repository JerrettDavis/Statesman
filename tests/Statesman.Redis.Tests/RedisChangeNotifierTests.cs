using StackExchange.Redis;

namespace Statesman.Redis.Tests;

public sealed class RedisChangeNotifierTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public void RedisStateLedgerStore_reports_the_change_notifier_capability()
    {
        Assert.True(typeof(RedisStateLedgerStore).GetInterfaces().Contains(typeof(IStateChangeNotifier)));
    }

    [Fact]
    public async Task An_append_signals_a_live_subscriber()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"notify-test-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(name, connection);
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> pending = hints.MoveNextAsync();

            // Unlike the in-memory provider, this subscription is a network round trip, so it is
            // NOT established when MoveNextAsync returns. Waiting for the server to report it is
            // what stops this test from being a coin flip.
            await WaitForSubscriptionAsync(connection, name);

            Assert.True((await store.AppendAsync(Address("a"), StateWriteCondition.Absent, Commit("one"))).Succeeded);

            Assert.True(await pending.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_import_signals_a_live_subscriber()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"notify-test-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(name, connection);
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> pending = hints.MoveNextAsync();
            await WaitForSubscriptionAsync(connection, name);

            await store.ImportAsync(ImportedRecord(position: 41, revision: 1));

            Assert.True(await pending.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_hint_for_an_append_never_arrives_before_the_feed_can_yield_that_record()
    {
        // This is the live probe, kept as a permanent test. The contract PERMITS a hint to arrive
        // before the feed can yield the record; the client-side publish makes it never happen on
        // Redis, because the record is already on the change-feed sorted set when PUBLISH is
        // issued. The assertion shape is still the weak one Phase 8 ruled for: poll, accept an
        // empty result, poll again, require the record to appear. Never assert "the drain is
        // empty" -- that is false on Redis.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"notify-test-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(name, connection);
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> pending = hints.MoveNextAsync();
            await WaitForSubscriptionAsync(connection, name);

            StateAppendResult appended =
                await store.AppendAsync(Address("a"), StateWriteCondition.Absent, Commit("one"));
            Assert.True(appended.Succeeded);
            Assert.True(await pending.AsTask().WaitAsync(Timeout));

            StateChangeCursor? cursor = null;
            bool seen = false;
            int polls = 0;
            for (; polls < 50 && !seen; polls++)
            {
                await foreach (StateChangeEnvelope envelope in store.ReadAsync(cursor, StateChangeReadOptions.Default, cts.Token))
                {
                    cursor = envelope.Cursor;
                    if (envelope.Record.GlobalPosition == appended.Record!.GlobalPosition)
                    {
                        seen = true;
                    }
                }

                if (!seen)
                {
                    await Task.Delay(20, cts.Token);
                }
            }

            Assert.True(seen, "the feed never yielded the record its hint was raised for");
            Assert.Equal(1, polls);
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposing_the_subscription_unsubscribes_from_the_channel()
    {
        // A ChannelMessageQueue that is never unsubscribed keeps a server-side subscription for the
        // multiplexer's whole lifetime, which in a long-lived host is a real leak.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"notify-test-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(name, connection);
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        ValueTask<bool> pending = hints.MoveNextAsync();
        await WaitForSubscriptionAsync(connection, name);
        Assert.True((await store.AppendAsync(Address("a"), StateWriteCondition.Absent, Commit("one"))).Succeeded);

        // Let the pending MoveNextAsync complete before disposing: disposing an enumerator while a
        // move is in flight is undefined behaviour, not a teardown test.
        Assert.True(await pending.AsTask().WaitAsync(Timeout));
        await hints.DisposeAsync();

        RedisChannel channel = NotificationChannel(name);
        ISubscriber subscriber = connection.GetSubscriber();
        for (int attempt = 0; attempt < 250 && subscriber.SubscribedEndpoint(channel) is not null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Null(subscriber.SubscribedEndpoint(channel));
    }

    [Fact]
    public async Task Disposing_the_store_ends_a_live_subscription_even_when_it_does_not_own_the_connection()
    {
        // IStateChangeNotifier.SubscribeAsync's doc comment promises the sequence ends when the
        // subscription is cancelled OR the store is disposed -- unconditionally, not only when the
        // store happens to own its connection. RedisStateLedgerStoreOptions.OwnsConnection defaults
        // to false, which is the shape every other test in this file uses, so that is the
        // configuration this test exercises: the store below never calls Options with
        // OwnsConnection = true.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"notify-test-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(name, connection); // OwnsConnection defaults to false.
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        ValueTask<bool> pending = hints.MoveNextAsync();
        await WaitForSubscriptionAsync(connection, name);

        await store.DisposeAsync();

        // `cts` carries a ten-second safety net so a regression cannot hang this test forever, but
        // it is not what is expected to end the subscription -- the store's own DisposeAsync is.
        // A `false` result (the sequence ended cleanly, with no items and no exception) is what
        // "ends when the store is disposed" means for a subscriber that received no hints before
        // disposal; an OperationCanceledException here would mean the store leaked its own
        // disposal-driven cancellation to the caller instead of ending quietly.
        bool moved = await pending.AsTask().WaitAsync(Timeout);
        Assert.False(moved, "the subscription should end (MoveNextAsync returns false) when the store is disposed, not hang or throw");

        await hints.DisposeAsync();

        RedisChannel channel = NotificationChannel(name);
        ISubscriber subscriber = connection.GetSubscriber();
        for (int attempt = 0; attempt < 250 && subscriber.SubscribedEndpoint(channel) is not null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Null(subscriber.SubscribedEndpoint(channel));

        // The store never owned this connection, so disposing the store must not have touched it:
        // the connection must still work for operations that have nothing to do with the store.
        Assert.True(connection.IsConnected);
        TimeSpan latency = await connection.GetDatabase().PingAsync();
        Assert.True(latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task A_gap_in_the_subscription_loses_hints_but_never_records()
    {
        // Redis pub/sub is at-most-once with no backlog: everything published while nobody is
        // subscribed is gone for good. Asserting that hints were lost would be unnecessary and
        // flaky; asserting the FEED is intact across the gap is the whole point.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"notify-test-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(name, connection);
        using var cts = new CancellationTokenSource(Timeout);

        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> pending = hints.MoveNextAsync();
        await WaitForSubscriptionAsync(connection, name);
        StateAppendResult first = await store.AppendAsync(Address("a"), StateWriteCondition.Absent, Commit("v1"));
        Assert.True(first.Succeeded);
        Assert.True(await pending.AsTask().WaitAsync(Timeout));
        await hints.DisposeAsync();

        var beforeGap = new StateChangeCursor(first.Record!.GlobalPosition);
        List<long> written = [];
        for (long revision = 2; revision <= 4; revision++)
        {
            StateAppendResult next = await store.AppendAsync(
                Address("a"), StateWriteCondition.AtRevision(revision - 1), Commit($"v{revision}"));
            Assert.True(next.Succeeded);
            written.Add(next.Record!.GlobalPosition);
        }

        IAsyncEnumerator<StateChangeNotification> resumed =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Task<bool> resumedMove = resumed.MoveNextAsync().AsTask();
        try
        {
            await WaitForSubscriptionAsync(connection, name);

            List<long> replayed = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(beforeGap, StateChangeReadOptions.Default, cts.Token))
            {
                replayed.Add(envelope.Record.GlobalPosition);
            }

            Assert.Equal(written, replayed);
        }
        finally
        {
            // Let the in-flight move finish before disposing: disposing an enumerator with a move
            // outstanding is undefined behaviour. Cancelling is what ends it, since nothing is
            // going to publish another hint on this channel.
            await cts.CancelAsync();
            try
            {
                await resumedMove;
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await resumed.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task A_consumer_that_ignores_hints_still_reads_every_record()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"notify-test-{Guid.NewGuid():N}", connection);
        for (long revision = 1; revision <= 25; revision++)
        {
            StateWriteCondition condition = revision == 1
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision - 1);
            Assert.True((await store.AppendAsync(Address("a"), condition, Commit($"v{revision}"))).Succeeded);
        }

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(25, changes.Count);
    }

    private static RedisChannel NotificationChannel(string storeName) =>
        RedisChannel.Literal($"statesman:{storeName}:notifications");

    private static async Task WaitForSubscriptionAsync(IConnectionMultiplexer connection, string storeName)
    {
        RedisChannel channel = NotificationChannel(storeName);
        ISubscriber subscriber = connection.GetSubscriber();
        for (int attempt = 0; attempt < 250; attempt++)
        {
            if (subscriber.SubscribedEndpoint(channel) is not null)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException(
            $"The subscription to '{channel}' was never established within five seconds, so this test " +
            "would have raced the append it is about to make and could pass or fail for the wrong reason.");
    }

    private static StateAddress Address(string leaf) =>
        new StateAddress("app", $"notify/{leaf}", StatePartition.Default);

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private static StateRecord ImportedRecord(long position, long revision) => new()
    {
        Address = Address("imported"),
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes("imported"),
        Source = "test",
    };
}
