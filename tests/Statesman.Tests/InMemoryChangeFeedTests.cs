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
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
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
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(new StateChangeCursor(first.Record!.GlobalPosition), StateChangeReadOptions.Default))
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
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
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
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Single(changes);
    }

    [Fact]
    public async Task ImportAsync_keeps_both_feed_entries_when_two_addresses_collide_on_position()
    {
        // Regression (Phase 11 fix round 3): a restore can legitimately import a record for a
        // DIFFERENT address at a position this feed already holds -- the documented restore
        // interleave (docs/providers/index.md's restore section: "the in-memory and filesystem
        // providers interleave the two histories in their change feeds without error"). An
        // earlier FeedEntryComparer that ordered on Position alone treated the colliding import as
        // a duplicate of the existing entry and silently dropped it while keeping its history --
        // a record in history with no feed entry, violating Phase 11's own rule in the direction
        // opposite the one it exists to fix. FeedEntryComparer now breaks the tie by address and
        // then revision, so both entries survive.
        var store = new InMemoryStateLedgerStore();
        var addressA = new StateAddress("app", "feed/collide-a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/collide-b", StatePartition.Default);

        StateAppendResult first = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        Assert.Equal(1, first.Record!.GlobalPosition);

        // Built from the real appended record so every required field is populated, but for a
        // different address at the SAME GlobalPosition -- exactly the colliding-lineage shape a
        // restore produces.
        StateRecord colliding = first.Record with { Address = addressB, Revision = 1 };
        await store.ImportAsync(colliding);

        List<StateChangeEnvelope> changes = await DrainAsync(store, from: null);
        Assert.Equal(2, changes.Count);

        // Give address A a second revision so a MaxRevisions = 1 prune on it alone has something
        // to drop, then prune ONLY address A. Address B's entry at the shared position must
        // survive -- proving the prune's removal key is address-aware, not position-only, which
        // is exactly what a position-only comparer would have gotten wrong.
        await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        await store.PruneAsync(addressA, new StateRetentionPolicy { MaxRevisions = 1 });

        List<StateChangeEnvelope> afterPrune = await DrainAsync(store, from: null);
        StateChangeEnvelope survivorB = Assert.Single(
            afterPrune, envelope => envelope.Record.Address.Canonical == addressB.Canonical);
        Assert.Equal(1L, survivorB.Record.Revision);
        Assert.Equal(1, survivorB.Record.GlobalPosition);
    }

    [Fact]
    public async Task ImportAsync_removes_the_stale_feed_entry_when_a_revision_moves_position()
    {
        // The residue docs/providers/index.md documents and this provider's own comment admits:
        // re-importing an existing revision at a DIFFERENT position replaced the history record and
        // left the old feed entry behind, so one record yielded at two positions with only one
        // history twin. Before this fix the drain below reads 3 entries, two of them revision 1 of
        // address A.
        var store = new InMemoryStateLedgerStore();
        var addressA = new StateAddress("app", "feed/move-a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/move-b", StatePartition.Default);

        StateAppendResult seed = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateRecord template = seed.Record!;

        await store.ImportAsync(template with { Address = addressA, Revision = 1, GlobalPosition = 100 });

        // The control. A prune or a repair that worked by position RANGE rather than by member would
        // take this with it, and the assertion below would fail.
        await store.ImportAsync(template with { Address = addressB, Revision = 1, GlobalPosition = 150 });

        await store.ImportAsync(template with { Address = addressA, Revision = 1, GlobalPosition = 200 });

        List<StateChangeEnvelope> changes = await DrainAsync(store, from: null);

        StateChangeEnvelope onlyA = Assert.Single(
            changes, envelope => envelope.Record.Address.Canonical == addressA.Canonical);
        Assert.Equal(200L, onlyA.Record.GlobalPosition);
        Assert.Equal(200L, onlyA.Cursor.Position);
        Assert.Single(changes, envelope => envelope.Record.Address.Canonical == addressB.Canonical);
    }

    [Fact]
    public async Task ImportAsync_does_not_evict_a_different_addresss_entry_when_a_revision_moves_onto_its_position()
    {
        // The other direction, and the one that makes the removal key's shape load-bearing: address
        // A moves ONTO the position address B already occupies. A position-keyed removal would take
        // B's entry -- a record leaving the change feed while its history record stays, which is the
        // violation Phase 11 exists to prevent. The removal key carries the dropped record itself, so
        // FeedEntryComparer matches on canonical address and revision and cannot reach B.
        var store = new InMemoryStateLedgerStore();
        var addressA = new StateAddress("app", "feed/onto-a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/onto-b", StatePartition.Default);

        StateAppendResult seed = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateRecord template = seed.Record!;

        await store.ImportAsync(template with { Address = addressA, Revision = 1, GlobalPosition = 100 });
        await store.ImportAsync(template with { Address = addressB, Revision = 1, GlobalPosition = 200 });
        await store.ImportAsync(template with { Address = addressA, Revision = 1, GlobalPosition = 200 });

        List<StateChangeEnvelope> changes = await DrainAsync(store, from: null);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, envelope => Assert.Equal(200L, envelope.Record.GlobalPosition));
        Assert.Single(changes, envelope => envelope.Record.Address.Canonical == addressA.Canonical);
        Assert.Single(changes, envelope => envelope.Record.Address.Canonical == addressB.Canonical);
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
