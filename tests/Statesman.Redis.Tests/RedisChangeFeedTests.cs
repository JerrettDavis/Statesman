using StackExchange.Redis;
using Statesman.TestHelpers;

namespace Statesman.Redis.Tests;

public sealed class RedisChangeFeedTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public void RedisStateLedgerStore_reports_change_feed_capability()
    {
        Assert.True(typeof(RedisStateLedgerStore).GetInterfaces().Contains(typeof(IStateChangeFeed)));
    }

    [Fact]
    public async Task ReadAsync_yields_records_after_the_given_cursor_across_streams_in_position_order()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"feed-test-{Guid.NewGuid():N}", connection);
        var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/b", StatePartition.Default);
        StateAppendResult first = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult second = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult third = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(new StateChangeCursor(first.Record!.GlobalPosition), StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
        Assert.Equal(second.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
        Assert.Equal(third.Record!.GlobalPosition, changes[1].Record.GlobalPosition);
    }

    [Fact]
    public async Task ReadAsync_yields_everything_from_the_start_when_no_cursor_is_given()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"feed-test-{Guid.NewGuid():N}", connection);
        var address = new StateAddress("app", "feed/item", StatePartition.Default);
        await store.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
        await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("two"));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task PruneAsync_removes_the_pruned_revisions_from_the_changes_sorted_set()
    {
        // The structural half of Phase 11's rule, which no read-path assertion can prove: the feed's
        // own sorted set must SHRINK, not merely stop yielding. Before Phase 11 PruneAsync touched
        // only the per-address history sorted set, so MaxRevisions bounded the history and the
        // :changes set grew forever -- and this assertion reads 4.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"feed-test-{Guid.NewGuid():N}";
        await using var store = new RedisStateLedgerStore(name, connection);
        var address = new StateAddress("app", "feed/retention", StatePartition.Default);
        for (int revision = 0; revision < 4; revision++)
        {
            StateWriteCondition condition = revision == 0
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision);
            Assert.True((await store.AppendAsync(address, condition, Commit($"v{revision}"))).Succeeded);
        }

        // The key shape RedisStateLedgerStore.ChangeFeedKey() builds: {KeyPrefix}:{Name}:changes,
        // with RedisStateLedgerStoreOptions.KeyPrefix defaulting to "statesman". Asserted directly
        // rather than through ReadAsync, because "the structure shrank" is the whole point.
        IDatabase database = connection.GetDatabase();
        var changesKey = (RedisKey)$"statesman:{name}:changes";
        Assert.Equal(4, await database.SortedSetLengthAsync(changesKey));

        await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

        Assert.Equal(1, await database.SortedSetLengthAsync(changesKey));

        // ...and the one member left is the surviving revision, not an arbitrary one: a range-based
        // removal that happened to leave one behind would fail here.
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        StateChangeEnvelope only = Assert.Single(changes);
        Assert.Equal(4L, only.Record.Revision);

        List<StateRecord> history = [];
        await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions()))
        {
            history.Add(record);
        }

        Assert.Equal(4L, Assert.Single(history).Revision);
    }

    [Fact]
    public async Task ReadAsync_never_skips_a_record_whose_append_was_in_flight_during_a_drain()
    {
        // The Phase 8 guarantee, stated operationally. Before commit-time allocation, writer A's
        // StringIncrementAsync burned a position a full round trip before its transaction
        // committed, so writer B could allocate higher, commit first, and let a consumer persist a
        // cursor above A's position -- permanently losing A.
        //
        // Note what "commit-time allocation" means on Redis, because it is not what it means on
        // the in-process providers: after the fix the paused writer has not contacted the server
        // at all, so the concurrent writer legitimately takes the LOWER position and the first
        // drain is correctly non-empty. Asserting an empty drain here would be wrong. What must
        // hold -- and what fails today -- is that resuming from the cursor still delivers A.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var clock = new PausingTimeProvider(pauseOnCall: 1);
        await using var store = new RedisStateLedgerStore($"feed-test-{Guid.NewGuid():N}", connection, options: null, clock);
        var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/b", StatePartition.Default);

        Task<StateAppendResult> writerA = Task.Run(() =>
            store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a")).AsTask());
        Task<StateAppendResult> writerB;
        List<StateChangeEnvelope> firstBatch;
        try
        {
            await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));
            writerB = Task.Run(() =>
                store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b")).AsTask());
            await Task.WhenAny(writerB, Task.Delay(TimeSpan.FromSeconds(2)));
            firstBatch = await DrainAsync(store, from: null);
        }
        finally
        {
            clock.Release();
        }

        await writerA;
        await writerB;

        StateChangeCursor? cursor = firstBatch.Count == 0 ? null : firstBatch[^1].Cursor;
        List<StateChangeEnvelope> secondBatch = await DrainAsync(store, cursor);

        HashSet<(string Address, long Revision)> seen =
        [
            .. firstBatch.Concat(secondBatch)
                .Select(envelope => (envelope.Record.Address.Canonical, envelope.Record.Revision)),
        ];
        Assert.Contains((addressA.Canonical, 1L), seen);
        Assert.Contains((addressB.Canonical, 1L), seen);
    }

    [Fact]
    public async Task A_rejected_append_burns_no_global_position()
    {
        // Today INCR runs before the revision guard is evaluated, so an append whose transaction
        // condition fails consumes a position permanently and leaves a hole in the change feed
        // that will never be filled. After the fix INCR runs inside the script, after the guard
        // passes, so a rejected append consumes nothing and Redis positions are dense.
        //
        // The race is made deterministic with the pausing clock rather than left to chance:
        // writer A is parked between its head read and its write, writer B commits the same
        // address underneath it, and A is then released to be rejected.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var clock = new PausingTimeProvider(pauseOnCall: 1);
        await using var store = new RedisStateLedgerStore($"feed-test-{Guid.NewGuid():N}", connection, options: null, clock);
        var address = new StateAddress("app", "feed/contested", StatePartition.Default);

        Task<StateAppendResult> rejected = Task.Run(() =>
            store.AppendAsync(address, StateWriteCondition.Absent, Commit("loser")).AsTask());
        StateAppendResult winner;
        try
        {
            await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));
            winner = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("winner"));
        }
        finally
        {
            clock.Release();
        }

        StateAppendResult loser = await rejected;
        Assert.True(winner.Succeeded);
        Assert.False(loser.Succeeded);

        StateAppendResult next = await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("second"));

        Assert.True(next.Succeeded);
        Assert.Equal(1, winner.Record!.GlobalPosition);
        Assert.Equal(2, next.Record!.GlobalPosition);
    }

    private static async Task<List<StateChangeEnvelope>> DrainAsync(
        IStateChangeFeed feed,
        StateChangeCursor? from,
        StateChangeReadOptions? options = null)
    {
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from, options ?? StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        return changes;
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
