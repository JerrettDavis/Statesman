using Statesman.TestHelpers;

namespace Statesman.Tests;

public sealed class InMemoryChangeFeedTests
{
    [Fact]
    public async Task ReadAsync_yields_everything_from_the_start_when_no_cursor_is_given()
    {
        var store = new InMemoryStateLedgerStore();
        var address = new StateAddress("app", "feed/item", StatePartition.Default);
        await store.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
        await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("two"));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task ReadAsync_yields_records_after_the_given_cursor_across_streams_in_position_order()
    {
        var store = new InMemoryStateLedgerStore();
        var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/b", StatePartition.Default);
        StateAppendResult first = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult second = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult third = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(new StateChangeCursor(first.Record!.GlobalPosition)))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
        Assert.Equal(second.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
        Assert.Equal(third.Record!.GlobalPosition, changes[1].Record.GlobalPosition);
    }

    [Fact]
    public async Task ReadAsync_reflects_records_written_through_ImportAsync()
    {
        var store = new InMemoryStateLedgerStore();
        var address = new StateAddress("app", "feed/item", StatePartition.Default);
        var record = new StateRecord
        {
            Address = address,
            Revision = 1,
            GlobalPosition = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Operation = StateOperation.Imported,
            Status = StateStatus.Ready,
            ValueType = typeof(string).FullName!,
            SchemaVersion = 1,
            Payload = "value"u8.ToArray(),
            Source = "test",
        };

        await store.ImportAsync(record);

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        Assert.Single(changes);
        Assert.Equal(1, changes[0].Record.GlobalPosition);
    }

    [Fact]
    public async Task ReadAsync_yields_a_repeated_ImportAsync_call_only_once()
    {
        var store = new InMemoryStateLedgerStore();
        var address = new StateAddress("app", "feed/item", StatePartition.Default);
        var record = new StateRecord
        {
            Address = address,
            Revision = 1,
            GlobalPosition = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Operation = StateOperation.Imported,
            Status = StateStatus.Ready,
            ValueType = typeof(string).FullName!,
            SchemaVersion = 1,
            Payload = "value"u8.ToArray(),
            Source = "test",
        };

        await store.ImportAsync(record);
        await store.ImportAsync(record);
        await store.ImportAsync(record);

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        Assert.Single(changes);
    }

    [Fact]
    public async Task ReadAsync_never_skips_a_record_whose_append_was_in_flight_during_a_drain()
    {
        // The Phase 8 guarantee, stated operationally: a consumer that drains the feed while
        // another write is in flight, persists the cursor it got, and resumes from that cursor,
        // still receives the in-flight record. Before commit-time allocation, writer A allocated
        // position 1 and paused; writer B allocated 2 and published; the consumer persisted cursor
        // 2; and A's record at position 1 was never yielded again.
        var clock = new PausingTimeProvider(pauseOnCall: 1);
        await using var store = new InMemoryStateLedgerStore("memory", clock);
        var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/b", StatePartition.Default);

        Task<StateAppendResult> writerA = Task.Run(() =>
            store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a")).AsTask());
        Task<StateAppendResult> writerB;
        List<StateChangeEnvelope> firstBatch;
        try
        {
            await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));

            // Never await B or the drain before releasing: after the fix B blocks on the same
            // lock the paused writer holds.
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
    public async Task ReadAsync_yields_nothing_while_an_append_holds_the_feed_lock()
    {
        // In-memory specific, and stronger than the shared guarantee: because allocation and the
        // enqueue happen under one lock, a drain that runs while an append is in flight sees a
        // prefix of the published sequence -- here, the empty prefix. This assertion is
        // deliberately NOT part of the cross-provider suite: on Redis the paused writer has not
        // contacted the server at all, so the concurrent writer legitimately takes the lower
        // position and the drain is correctly non-empty.
        var clock = new PausingTimeProvider(pauseOnCall: 1);
        await using var store = new InMemoryStateLedgerStore("memory", clock);
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

        Assert.Empty(firstBatch);
        List<StateChangeEnvelope> everything = await DrainAsync(store, from: null);
        Assert.Equal(2, everything.Count);
        Assert.True(everything[0].Record.GlobalPosition < everything[1].Record.GlobalPosition);
    }

    private static async Task<List<StateChangeEnvelope>> DrainAsync(
        IStateChangeFeed feed,
        StateChangeCursor? from)
    {
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from))
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
