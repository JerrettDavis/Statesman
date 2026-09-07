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
        // advancing its cursor after each batch, exactly as an outbox does. Every record must
        // arrive, and no {address, revision} pair may arrive twice within one drain.
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.SkipUnless(store is not null, SkipReason);

        const int addressCount = 4;
        const int writesPerAddress = 5;

        var writers = Enumerable.Range(0, addressCount).Select(async index =>
        {
            var address = new StateAddress("app", $"conformance/stress-{index}", StatePartition.Default);
            for (int revision = 0; revision < writesPerAddress; revision++)
            {
                StateWriteCondition condition = revision == 0
                    ? StateWriteCondition.Absent
                    : StateWriteCondition.AtRevision(revision);
                await store!.Store.AppendAsync(address, condition, Commit($"v{revision}"));
            }
        });

        await Task.WhenAll(writers);

        var received = new List<(string Address, long Revision)>();
        StateChangeCursor? cursor = null;
        while (true)
        {
            List<StateChangeEnvelope> batch = await DrainAsync(store!.Feed, cursor);
            if (batch.Count == 0)
            {
                break;
            }

            received.AddRange(batch.Select(envelope =>
                (envelope.Record.Address.Canonical, envelope.Record.Revision)));
            cursor = batch[^1].Cursor;
        }

        Assert.Equal(addressCount * writesPerAddress, received.Count);
        Assert.Equal(received.Count, received.Distinct().Count());
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
