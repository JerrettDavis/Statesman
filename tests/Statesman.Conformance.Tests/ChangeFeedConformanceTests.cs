using Statesman.TestHelpers;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The behaviour every <see cref="IStateChangeFeed"/> implementation must share, run once per
/// provider by a subclass. ROADMAP 0.3 Phase 1 deferred a shared conformance suite "to when a third
/// provider exists"; five do, and the Phase 8 losslessness guarantee is the first one that has to
/// hold identically across all of them.
/// </summary>
public abstract class ChangeFeedConformanceTests
{
    /// <summary>
    /// Which <see cref="TimeProvider.GetUtcNow"/> call sits between allocating a record's global
    /// position and publishing it to the feed. One-based, and provider-specific: the filesystem
    /// provider reads the clock inside <c>NextGlobalPosition</c> before reading it again for
    /// <c>OccurredAt</c>, so its window is the second call, not the first.
    /// </summary>
    protected abstract int PauseCallIndex { get; }

    /// <summary>
    /// Creates a store with <b>default</b> options over the given clock, or returns
    /// <see langword="null"/> when the provider's infrastructure is not available here.
    /// </summary>
    /// <param name="clock">The clock the store must use.</param>
    protected abstract ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock);

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    [Fact]
    public async Task A_drain_with_no_cursor_yields_everything()
    {
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/all", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("two"));

        List<StateChangeEnvelope> changes = await DrainAsync(store.Feed, from: null);

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task Resuming_from_a_cursor_yields_only_later_records()
    {
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);
        var addressA = new StateAddress("app", "conformance/a", StatePartition.Default);
        var addressB = new StateAddress("app", "conformance/b", StatePartition.Default);
        StateAppendResult first = await store!.Store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult second = await store.Store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult third = await store.Store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));

        List<StateChangeEnvelope> changes = await DrainAsync(store.Feed, new StateChangeCursor(first.Record!.GlobalPosition));

        Assert.Equal(2, changes.Count);
        Assert.Equal(second.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
        Assert.Equal(third.Record!.GlobalPosition, changes[1].Record.GlobalPosition);
    }

    [Fact]
    public async Task Positions_are_strictly_increasing_across_a_drain()
    {
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);
        for (int index = 0; index < 5; index++)
        {
            var address = new StateAddress("app", $"conformance/order-{index}", StatePartition.Default);
            await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit($"v{index}"));
        }

        List<StateChangeEnvelope> changes = await DrainAsync(store!.Feed, from: null);

        Assert.Equal(5, changes.Count);
        for (int index = 1; index < changes.Count; index++)
        {
            Assert.True(
                changes[index - 1].Record.GlobalPosition < changes[index].Record.GlobalPosition,
                $"position {changes[index].Record.GlobalPosition} did not follow {changes[index - 1].Record.GlobalPosition}");
        }
    }

    [Fact]
    public async Task A_consumer_that_drains_during_an_in_flight_append_still_receives_that_record()
    {
        // The load-bearing test of ROADMAP 0.3 Phase 8, and the only assertion of the guarantee
        // that is true on every provider. Writer A is paused between allocating its position and
        // publishing its record; writer B runs; a consumer drains, keeps the cursor it got, and
        // resumes from it. Both records must appear across the two batches.
        //
        // Deliberately NOT "the first drain is empty". That is true on the in-process providers
        // and on Entity Framework Core, but false on Redis, where the paused writer has not
        // contacted the server at all and the concurrent writer legitimately takes the lower
        // position -- nothing is withheld and nothing is skipped. Each provider's own test project
        // asserts the stricter form where it holds.
        //
        // Nothing is awaited before the clock is released: on the filesystem provider the paused
        // writer holds the same global gate that BOTH the second writer and the reader need, so
        // awaiting either would deadlock. Each gets a bounded observation window instead. On
        // today's pre-fix code every window completes at once; after the fix some expire, which is
        // the correct outcome.
        var clock = new PausingTimeProvider(PauseCallIndex);
        await using ConformanceStore? store = await CreateAsync(clock);
        Assert.SkipUnless(store is not null, SkipReason);
        var addressA = new StateAddress("app", "conformance/inflight-a", StatePartition.Default);
        var addressB = new StateAddress("app", "conformance/inflight-b", StatePartition.Default);

        Task<StateAppendResult> writerA = Task.Run(() =>
            store!.Store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a")).AsTask());
        Task<StateAppendResult> writerB;
        Task<List<StateChangeEnvelope>> drain;
        try
        {
            await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));

            writerB = Task.Run(() =>
                store!.Store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b")).AsTask());
            await Task.WhenAny(writerB, Task.Delay(TimeSpan.FromSeconds(2)));

            drain = Task.Run(() => DrainAsync(store!.Feed, from: null));
            await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            clock.Release();
        }

        await writerA;
        await writerB;
        List<StateChangeEnvelope> firstBatch = await drain;

        StateChangeCursor? cursor = firstBatch.Count == 0 ? null : firstBatch[^1].Cursor;
        List<StateChangeEnvelope> secondBatch = await DrainAsync(store!.Feed, cursor);

        HashSet<(string Address, long Revision)> seen =
        [
            .. firstBatch.Concat(secondBatch)
                .Select(envelope => (envelope.Record.Address.Canonical, envelope.Record.Revision)),
        ];
        Assert.Contains((addressA.Canonical, 1L), seen);
        Assert.Contains((addressB.Canonical, 1L), seen);
    }

    [Fact]
    public async Task A_cursor_advancing_consumer_receives_every_concurrently_written_record_exactly_once()
    {
        // The test that would have caught the original bug, and the only one that exercises real
        // interleaving. Many writers across many addresses; one consumer draining in a loop and
        // advancing its cursor after each batch, exactly as an outbox does, WHILE the writers are
        // still in flight -- the drain starts before the writers are awaited and keeps sampling
        // whether they have finished, stopping only on an empty batch observed after they have. A
        // drain that waited for every writer to finish before starting could never catch a lower
        // position published after the drain began, which is exactly the bug this suite exists to
        // catch. Every record must arrive, and no {address, revision} pair may arrive twice within
        // one drain.
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);

        const int addressCount = 4;
        const int writesPerAddress = 5;

        List<Task> writers = Enumerable.Range(0, addressCount).Select(async index =>
        {
            var address = new StateAddress("app", $"conformance/stress-{index}", StatePartition.Default);
            for (int revision = 0; revision < writesPerAddress; revision++)
            {
                StateWriteCondition condition = revision == 0
                    ? StateWriteCondition.Absent
                    : StateWriteCondition.AtRevision(revision);
                await store!.Store.AppendAsync(address, condition, Commit($"v{revision}"));
            }
        }).ToList<Task>();

        Task all = Task.WhenAll(writers);

        var received = new List<(string Address, long Revision)>();
        StateChangeCursor? cursor = null;
        while (true)
        {
            bool writersDone = all.IsCompleted; // sampled before the drain, not after
            List<StateChangeEnvelope> batch = await DrainAsync(store!.Feed, cursor);
            if (batch.Count > 0)
            {
                received.AddRange(batch.Select(envelope =>
                    (envelope.Record.Address.Canonical, envelope.Record.Revision)));
                cursor = batch[^1].Cursor;
            }
            else if (writersDone)
            {
                break;
            }
        }

        await all;

        Assert.Equal(addressCount * writesPerAddress, received.Count);
        Assert.Equal(received.Count, received.Distinct().Count());
    }

    [Fact]
    public async Task A_take_of_one_yields_exactly_one_record_and_a_resumable_cursor()
    {
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/take-one", StatePartition.Default);
        for (int revision = 0; revision < 5; revision++)
        {
            StateWriteCondition condition = revision == 0
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision);
            await store!.Store.AppendAsync(address, condition, Commit($"v{revision}"));
        }

        List<StateChangeEnvelope> first = await DrainAsync(
            store!.Feed, from: null, new StateChangeReadOptions { Take = 1 });

        StateChangeEnvelope only = Assert.Single(first);

        // The cursor from a capped page must resume exactly like any other cursor: the remaining four
        // records, in order, with no gap and no repeat of the one already seen.
        List<StateChangeEnvelope> rest = await DrainAsync(store.Feed, only.Cursor);
        Assert.Equal(4, rest.Count);
        Assert.Equal([2L, 3L, 4L, 5L], rest.Select(envelope => envelope.Record.Revision).ToArray());
    }

    [Fact]
    public async Task A_take_larger_than_the_backlog_and_a_null_take_both_yield_everything()
    {
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/take-all", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("two"));
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("three"));

        Assert.Equal(3, (await DrainAsync(store.Feed, from: null, new StateChangeReadOptions { Take = 100 })).Count);
        Assert.Equal(3, (await DrainAsync(store.Feed, from: null, new StateChangeReadOptions { Take = null })).Count);
        Assert.Equal(3, (await DrainAsync(store.Feed, from: null, StateChangeReadOptions.Default)).Count);
    }

    [Fact]
    public async Task Successive_capped_reads_cover_the_whole_backlog_with_no_gap_and_no_duplicate()
    {
        // Paging is only useful if it composes. Fifty records at Take = 7 is seven full pages and a
        // short one, and the union must be exactly the fifty distinct positions in ascending order.
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/paging", StatePartition.Default);
        for (int revision = 0; revision < 50; revision++)
        {
            StateWriteCondition condition = revision == 0
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision);
            await store!.Store.AppendAsync(address, condition, Commit($"v{revision}"));
        }

        var options = new StateChangeReadOptions { Take = 7 };
        var positions = new List<long>();
        StateChangeCursor? cursor = null;
        int pages = 0;
        while (true)
        {
            List<StateChangeEnvelope> page = await DrainAsync(store!.Feed, cursor, options);
            if (page.Count == 0)
            {
                break;
            }

            Assert.True(page.Count <= 7, $"a page yielded {page.Count} records against Take = 7.");
            positions.AddRange(page.Select(envelope => envelope.Record.GlobalPosition));
            cursor = page[^1].Cursor;
            pages++;
        }

        Assert.Equal(50, positions.Count);
        Assert.Equal(positions.Count, positions.Distinct().Count());
        Assert.Equal(positions.OrderBy(position => position).ToArray(), positions.ToArray());

        // Eight non-empty pages, not one: a provider that ignored Take would deliver everything in
        // the first page and this reads 1.
        Assert.Equal(8, pages);
    }

    [Fact]
    public void A_take_of_zero_or_negative_is_rejected()
    {
        // Validate() rather than a read, because four of the five providers are iterators and would
        // defer the throw to the first MoveNextAsync while the tiered store throws eagerly -- the
        // contract is what Validate does, not where each provider happens to call it.
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateChangeReadOptions { Take = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateChangeReadOptions { Take = -1 }.Validate());
        new StateChangeReadOptions { Take = null }.Validate();
        StateChangeReadOptions.Default.Validate();
        Assert.Null(StateChangeReadOptions.Default.Take);
    }

    [Fact]
    public async Task Retention_removes_a_pruned_revision_from_the_feed()
    {
        // The Phase 11 rule: a record leaves the change feed exactly when its history record leaves
        // the store. Retention that removes a revision removes that revision's feed entry, on every
        // provider.
        //
        // Before Phase 11 this FAILED on the in-memory provider, on tiered-over-in-memory, and on
        // Redis -- whose PruneAsync trimmed the per-address history and never the feed structure, so
        // MaxRevisions and MaxBytes, two options that exist to bound memory, did not bound it -- and
        // PASSED on the filesystem provider (whose pruned history file is gone, so its change-log
        // line dangles and is skipped) and on Entity Framework Core (whose feed IS its history
        // table). That five-way discrimination is what proves this test tests the rule and not the
        // suite; it is recorded per provider in the phase ledger.
        //
        // Address B is the control: prune is per-address while the feed is cross-address, so an
        // implementation that trimmed the feed by position RANGE rather than by member would take
        // B's records too and the B assertion below would fail.
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);
        var addressA = new StateAddress("app", "conformance/retention-a", StatePartition.Default);
        var addressB = new StateAddress("app", "conformance/retention-b", StatePartition.Default);

        StateAppendResult a1 = await store!.Store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        await store.Store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        await store.Store.AppendAsync(addressA, StateWriteCondition.AtRevision(2), Commit("a3"));
        await store.Store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        await store.Store.AppendAsync(addressB, StateWriteCondition.AtRevision(1), Commit("b2"));

        Assert.Equal(5, (await DrainAsync(store.Feed, from: null)).Count);

        await store.Store.PruneAsync(addressA, new StateRetentionPolicy { MaxRevisions = 1 });

        List<StateChangeEnvelope> afterPrune = await DrainAsync(store.Feed, from: null);

        Assert.Equal(3, afterPrune.Count);
        StateChangeEnvelope survivor = Assert.Single(
            afterPrune, envelope => envelope.Record.Address.Canonical == addressA.Canonical);
        Assert.Equal(3L, survivor.Record.Revision);
        Assert.Equal(
            [1L, 2L],
            afterPrune
                .Where(envelope => envelope.Record.Address.Canonical == addressB.Canonical)
                .Select(envelope => envelope.Record.Revision)
                .ToArray());

        for (int index = 1; index < afterPrune.Count; index++)
        {
            Assert.True(
                afterPrune[index - 1].Record.GlobalPosition < afterPrune[index].Record.GlobalPosition,
                $"position {afterPrune[index].Record.GlobalPosition} did not follow {afterPrune[index - 1].Record.GlobalPosition}");
        }

        // A cursor at a REMOVED position still resumes at the next surviving record. StateChangeCursor
        // is compared with > (Exclude.Start on Redis), never looked up, so a position that no longer
        // exists is a valid resume point: no skip, no throw. This is the assertion that would catch a
        // provider "fixing" retention by invalidating cursors.
        List<StateChangeEnvelope> fromRemoved = await DrainAsync(
            store.Feed, new StateChangeCursor(a1.Record!.GlobalPosition));
        Assert.Equal(
            afterPrune.Select(envelope => envelope.Record.GlobalPosition).ToArray(),
            fromRemoved.Select(envelope => envelope.Record.GlobalPosition).ToArray());

        // Break-the-mechanism half. Take bounds records YIELDED, not entries parsed. An
        // implementation that removed feed entries but let a page consisting of removed positions
        // come back empty would terminate this loop early and collect fewer than three records --
        // which is exactly how a paging outbox consumer stalls its cursor forever.
        var paged = new List<long>();
        StateChangeCursor? cursor = null;
        var one = new StateChangeReadOptions { Take = 1 };
        while (true)
        {
            List<StateChangeEnvelope> page = await DrainAsync(store.Feed, cursor, one);
            if (page.Count == 0)
            {
                break;
            }

            Assert.Single(page);
            paged.Add(page[0].Record.GlobalPosition);
            cursor = page[0].Cursor;
        }

        Assert.Equal(afterPrune.Select(envelope => envelope.Record.GlobalPosition).ToArray(), paged.ToArray());
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
