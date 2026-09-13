namespace Statesman.Conformance.Tests;

/// <summary>
/// The conditional-write contract every <see cref="IStateLedgerStore"/> shares, run once per provider
/// by a subclass. Before ROADMAP 0.3 Phase 16 the repository had exactly two provider-level
/// conditional-write assertions — a contested Redis append and an Entity Framework Core
/// lost-acknowledgement path — and none at all for the in-memory, filesystem and tiered providers,
/// even though optimistic revisions are the library's central promise (ADR 0003).
/// </summary>
/// <remarks>
/// This is a <b>pinning</b> suite: a pre-flight probe over all five providers measured every assertion
/// below as already true, byte-identically, so there is no RED at baseline to point at. What proves it
/// discriminates is <see cref="BrokenStoreConformanceTests"/>, which runs
/// <see cref="AssertExclusiveCreationAsync"/> against a store that ignores its write condition and
/// requires it to fail.
/// </remarks>
public abstract class LedgerWriteConformanceTests
{
    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that exactly one of many concurrent <see cref="StateWriteCondition.Absent"/> appends to
    /// one address wins, and that every loser reports a conflict carrying the winner's record. Public
    /// and static so the negative test in this project can run it against a deliberately broken store
    /// and require it to fail — a conformance suite that cannot fail is not a suite.
    /// </summary>
    /// <param name="store">The store under test.</param>
    /// <param name="racers">How many concurrent appends to issue.</param>
    public static async Task AssertExclusiveCreationAsync(IStateLedgerStore store, int racers = 8)
    {
        ArgumentNullException.ThrowIfNull(store);
        var address = new StateAddress("app", $"conformance/contested-{Guid.NewGuid():N}", StatePartition.Default);

        using var barrier = new Barrier(racers);
        StateAppendResult[] results = await Task.WhenAll(Enumerable.Range(0, racers).Select(index => Task.Run(async () =>
        {
            // The barrier is what makes this a race rather than a sequence: every task is released
            // within the same scheduling window, so the store's own exclusion is what decides, not
            // task start order.
            barrier.SignalAndWait();
            return await store.AppendAsync(address, StateWriteCondition.Absent, Commit($"racer-{index}"));
        })));

        StateAppendResult winner = Assert.Single(results, result => result.Succeeded);
        Assert.Equal(1L, winner.Record!.Revision);

        StateAppendResult[] losers = [.. results.Where(result => !result.Succeeded)];
        Assert.Equal(racers - 1, losers.Length);
        foreach (StateAppendResult loser in losers)
        {
            // Null, not an exception: losing a conditional write is a documented outcome, not a
            // failure. And Current must carry the record that won, so a caller can retry from it.
            Assert.Null(loser.Record);
            Assert.NotNull(loser.Current);
            Assert.Equal(1L, loser.Current!.Revision);
        }
    }

    [Fact]
    public async Task An_absent_condition_on_an_occupied_address_conflicts_carrying_the_current_record()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/write-absent", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

        StateAppendResult second = await store.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("v2"));

        Assert.False(second.Succeeded);
        Assert.Null(second.Record);
        Assert.NotNull(second.Current);
        Assert.Equal(1L, second.Current!.Revision);
    }

    [Fact]
    public async Task A_revision_condition_that_does_not_match_the_head_conflicts_carrying_the_current_record()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/write-revision", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

        StateAppendResult ahead = await store.Store.AppendAsync(
            address, StateWriteCondition.AtRevision(7), Commit("from-the-future"));

        Assert.False(ahead.Succeeded);
        Assert.Null(ahead.Record);
        Assert.NotNull(ahead.Current);
        Assert.Equal(1L, ahead.Current!.Revision);
    }

    [Fact]
    public async Task A_conflict_changes_neither_the_head_nor_the_history()
    {
        // The assertion that discriminates a store which validates AFTER writing. The two conflicts
        // above would both still report Conflict on such a store; only the head and the history say
        // whether anything landed.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/write-unchanged", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

        await store.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("rejected-a"));
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(7), Commit("rejected-b"));

        StateRecord? head = await store.Store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(1L, head!.Revision);
        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([1L], revisions);
    }

    [Fact]
    public async Task A_matching_revision_condition_appends_exactly_the_next_revision()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/write-next", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

        StateAppendResult second = await store.Store.AppendAsync(
            address, StateWriteCondition.AtRevision(1), Commit("v2"));

        Assert.True(second.Succeeded);
        Assert.Equal(2L, second.Record!.Revision);
        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([1L, 2L], revisions);
    }

    [Fact]
    public async Task An_unconditional_append_succeeds_on_an_absent_and_on_an_occupied_address()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var fresh = new StateAddress("app", "conformance/write-any-fresh", StatePartition.Default);
        var occupied = new StateAddress("app", "conformance/write-any-occupied", StatePartition.Default);
        await store!.Store.AppendAsync(occupied, StateWriteCondition.Absent, Commit("v1"));

        StateAppendResult onFresh = await store.Store.AppendAsync(fresh, StateWriteCondition.Any, Commit("v1"));
        StateAppendResult onOccupied = await store.Store.AppendAsync(occupied, StateWriteCondition.Any, Commit("v2"));

        Assert.True(onFresh.Succeeded);
        Assert.Equal(1L, onFresh.Record!.Revision);
        Assert.True(onOccupied.Succeeded);
        Assert.Equal(2L, onOccupied.Record!.Revision);
    }

    [Fact]
    public async Task Exactly_one_of_many_racing_absent_appends_wins()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);

        await AssertExclusiveCreationAsync(store!.Store);
    }

    private static async Task<long[]> RevisionsAsync(IStateLedgerStore store, StateAddress address)
    {
        var revisions = new List<long>();
        await foreach (StateRecord record in store.ReadHistoryAsync(
            address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            revisions.Add(record.Revision);
        }

        return [.. revisions];
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
