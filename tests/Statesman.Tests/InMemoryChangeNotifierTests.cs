namespace Statesman.Tests;

public sealed class InMemoryChangeNotifierTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void InMemoryStateLedgerStore_reports_the_change_notifier_capability()
    {
        Assert.True(typeof(InMemoryStateLedgerStore).GetInterfaces().Contains(typeof(IStateChangeNotifier)));
    }

    [Fact]
    public async Task An_append_signals_a_live_subscriber()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            // An async iterator's body runs synchronously up to its first suspension, and the
            // subscriber is registered before any await, so the subscription exists by the time
            // this call RETURNS even though the ValueTask it returns is still pending. That is
            // what makes the append below unable to race the subscription -- writing first and
            // subscribing second would be the vacuous version of this test.
            ValueTask<bool> pending = hints.MoveNextAsync();

            StateAppendResult appended = await store.AppendAsync(
                Address("a"), StateWriteCondition.Absent, Commit("one"));
            Assert.True(appended.Succeeded);

            Assert.True(await pending.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_import_that_introduces_a_new_position_signals_a_live_subscriber()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> pending = hints.MoveNextAsync();

            await store.ImportAsync(ImportedRecord(position: 41, revision: 1));

            Assert.True(await pending.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_subscriber_that_stops_reading_never_blocks_or_loses_a_writer()
    {
        // The capacity-1 DropWrite channel is what makes this true: a hint written into a full
        // channel is discarded and TryWrite still returns true, so a subscriber that stops calling
        // MoveNextAsync cannot apply backpressure to any writer.
        await using var store = new InMemoryStateLedgerStore("memory");
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> idle =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> first = idle.MoveNextAsync();
            Assert.True((await store.AppendAsync(Address("a"), StateWriteCondition.Absent, Commit("v1"))).Succeeded);
            Assert.True(await first.AsTask().WaitAsync(Timeout));

            // From here this subscriber never reads again. Its channel fills on the next hint and
            // stays full for the remaining 99 appends.
            for (long revision = 2; revision <= 100; revision++)
            {
                StateAppendResult result = await store
                    .AppendAsync(Address("a"), StateWriteCondition.AtRevision(revision - 1), Commit($"v{revision}"))
                    .AsTask()
                    .WaitAsync(Timeout);
                Assert.True(result.Succeeded);
            }

            List<StateChangeEnvelope> changes = await DrainAsync(store);
            Assert.Equal(100, changes.Count);
        }
        finally
        {
            await idle.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_burst_of_appends_produces_at_least_one_hint_and_at_most_one_per_append()
    {
        // A lower bound only. Coalescing means the exact count is deliberately unspecified, and a
        // test asserting one hint per append would pin the opposite of the design.
        var store = new InMemoryStateLedgerStore("memory");
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> first = hints.MoveNextAsync();
        int observed = 0;

        Task reader = Task.Run(async () =>
        {
            if (!await first)
            {
                return;
            }

            Interlocked.Increment(ref observed);
            while (await hints.MoveNextAsync())
            {
                Interlocked.Increment(ref observed);
            }
        });

        for (long revision = 1; revision <= 100; revision++)
        {
            StateWriteCondition condition = revision == 1
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision - 1);
            Assert.True((await store.AppendAsync(Address("a"), condition, Commit($"v{revision}"))).Succeeded);
        }

        // Completing every subscriber is how the reader loop ends cleanly rather than by
        // cancellation, which would leave the count racing a faulted task.
        await store.DisposeAsync();
        await reader.WaitAsync(Timeout);
        await hints.DisposeAsync();

        Assert.InRange(observed, 1, 100);
    }

    [Fact]
    public async Task Disposing_the_store_ends_every_live_subscription()
    {
        var store = new InMemoryStateLedgerStore("memory");
        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> pending = hints.MoveNextAsync();

        await store.DisposeAsync();

        // False, not a hang: DisposeAsync completes every subscriber's channel writer.
        Assert.False(await pending.AsTask().WaitAsync(Timeout));
        await hints.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_one_enumerator_unsubscribes_it_and_leaves_later_subscribers_working()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        using var cts = new CancellationTokenSource(Timeout);

        IAsyncEnumerator<StateChangeNotification> first =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> firstPending = first.MoveNextAsync();
        Assert.True((await store.AppendAsync(Address("a"), StateWriteCondition.Absent, Commit("one"))).Succeeded);
        Assert.True(await firstPending.AsTask().WaitAsync(Timeout));
        await first.DisposeAsync();

        IAsyncEnumerator<StateChangeNotification> second =
            store.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> secondPending = second.MoveNextAsync();
            Assert.True((await store.AppendAsync(Address("a"), StateWriteCondition.AtRevision(1), Commit("two"))).Succeeded);
            Assert.True(await secondPending.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_consumer_that_ignores_hints_still_reads_every_record()
    {
        // The test that stops a future refactor from making the feed depend on the hint.
        await using var store = new InMemoryStateLedgerStore("memory");
        for (long revision = 1; revision <= 25; revision++)
        {
            StateWriteCondition condition = revision == 1
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision - 1);
            Assert.True((await store.AppendAsync(Address("a"), condition, Commit($"v{revision}"))).Succeeded);
        }

        List<StateChangeEnvelope> changes = await DrainAsync(store);
        Assert.Equal(25, changes.Count);
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

    private static async Task<List<StateChangeEnvelope>> DrainAsync(InMemoryStateLedgerStore store)
    {
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        return changes;
    }
}
