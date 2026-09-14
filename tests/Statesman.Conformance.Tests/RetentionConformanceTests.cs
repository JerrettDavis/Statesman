using Statesman.Testing;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The retention contract every <see cref="IStateLedgerStore"/> shares, run once per provider by a
/// subclass. ROADMAP 0.2 bullet 1 named retention among the contracts a shared suite must cover, and
/// Phases 16, 17 and 18 each parked it on the premise that pruning uniformity was a per-provider
/// question about eviction order and byte accounting. A pre-flight probe over eighteen scenarios and
/// all five built-in providers measured that premise false: the four base providers run the same six
/// filters in the same order, and every provider agreed on the surviving revision set in every
/// scenario, including the head, the change feed and the partition catalog.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>pinning</b> suite, exactly as <see cref="LedgerWriteConformanceTests"/> and
/// <see cref="CancellationConformanceTests"/> are: every assertion below was measured already true on
/// all five providers before the suite existed, so there is no RED at baseline to point at. What
/// proves it discriminates is <see cref="BrokenRetentionConformanceTests"/>, which runs each public
/// <c>Assert</c> entry point below against a store that gets exactly one part of retention wrong and
/// requires it to fail, and against a correct store and requires it to pass.
/// </para>
/// <para>
/// Prune <b>cancellation</b> is deliberately not re-asserted here. It is already shared, on all five
/// providers, by
/// <see cref="CancellationConformanceTests.An_already_cancelled_token_is_honoured_by_every_store_operation"/>,
/// and a second copy would drift from it rather than strengthen it.
/// </para>
/// <para>
/// Every fact runs on a <see cref="ManualTimeProvider"/>, which is what makes <c>MaxAge</c> exact:
/// every provider stamps <see cref="StateRecord.OccurredAt"/> and computes its age cutoff from the
/// injected clock, including Redis, whose server clock governs only lease expiry and never retention.
/// </para>
/// </remarks>
public abstract class RetentionConformanceTests
{
    /// <summary>
    /// The clock every store in this suite is constructed with, and the only thing that moves time.
    /// xUnit builds one instance per fact, so each fact gets its own clock at the same start instant.
    /// </summary>
    protected ManualTimeProvider Clock { get; } = new();

    /// <summary>
    /// Creates a store with <b>default</b> options over <see cref="Clock"/>, or returns
    /// <see langword="null"/> when this provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that <see cref="StateRetentionPolicy.MaxRevisions"/> keeps exactly the newest N
    /// revisions of an address and nothing older. Public and static so the negative tests in this
    /// project can run it against deliberately broken stores.
    /// </summary>
    /// <param name="store">The store under test.</param>
    public static async Task AssertMaxRevisionsKeepsTheNewestAsync(IStateLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var address = new StateAddress("app", $"conformance/retention-newest-{Guid.NewGuid():N}", StatePartition.Default);
        await SeedAsync(store, address, 10, 10, 10, 10, 10, 10);

        await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 3 });

        long[] revisions = await RevisionsAsync(store, address);
        Assert.Equal([4L, 5L, 6L], revisions);
    }

    /// <summary>
    /// Asserts that <see cref="StateRetentionPolicy.MaxAge"/> keeps exactly the revisions whose
    /// <see cref="StateRecord.OccurredAt"/> is at or after the cutoff. Public and static for the
    /// negative tests.
    /// </summary>
    /// <param name="store">The store under test.</param>
    /// <param name="clock">The clock the store was constructed with.</param>
    public static async Task AssertMaxAgeKeepsOnlyTheRecentAsync(IStateLedgerStore store, ManualTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        var address = new StateAddress("app", $"conformance/retention-age-{Guid.NewGuid():N}", StatePartition.Default);
        await SeedOverTimeAsync(store, clock, address, 6, TimeSpan.FromHours(1));

        await store.PruneAsync(address, new StateRetentionPolicy { MaxAge = TimeSpan.FromMinutes(150) });

        long[] revisions = await RevisionsAsync(store, address);
        Assert.Equal([4L, 5L, 6L], revisions);
    }

    /// <summary>
    /// Asserts that <see cref="StateRetentionPolicy.MaxBytes"/> admits revisions newest-first under a
    /// payload-byte budget. Public and static for the negative tests.
    /// </summary>
    /// <param name="store">The store under test.</param>
    public static async Task AssertMaxBytesAdmitsNewestFirstAsync(IStateLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var address = new StateAddress("app", $"conformance/retention-bytes-{Guid.NewGuid():N}", StatePartition.Default);
        await SeedAsync(store, address, 10, 10, 10, 10, 10, 10);

        await store.PruneAsync(address, new StateRetentionPolicy { MaxBytes = 25 });

        long[] revisions = await RevisionsAsync(store, address);
        Assert.Equal([5L, 6L], revisions);
    }

    /// <summary>
    /// Asserts that a byte budget smaller than one record still keeps the latest revision, and that
    /// the head still answers it. Public and static for the negative tests.
    /// </summary>
    /// <param name="store">The store under test.</param>
    public static async Task AssertPruneKeepsTheHeadAsync(IStateLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var address = new StateAddress("app", $"conformance/retention-head-{Guid.NewGuid():N}", StatePartition.Default);
        await SeedAsync(store, address, 10, 10, 10, 10, 10, 10);

        await store.PruneAsync(address, new StateRetentionPolicy { MaxBytes = 1 });

        long[] revisions = await RevisionsAsync(store, address);
        Assert.Equal([6L], revisions);
        StateRecord? head = await store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(6L, head!.Revision);
    }

    /// <summary>
    /// Asserts the Phase 11 rule for retention: a record leaves the change feed exactly when the prune
    /// removes it from the store, and a second address is untouched. Public and static for the
    /// negative tests.
    /// </summary>
    /// <param name="store">The store under test.</param>
    /// <param name="feed">The same store's change feed.</param>
    /// <param name="maintain">The provider's feed-repair maintenance step, or null when it has none.</param>
    public static async Task AssertPrunedRevisionsLeaveTheFeedAsync(
        IStateLedgerStore store,
        IStateChangeFeed feed,
        Func<ValueTask>? maintain = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(feed);
        string suffix = Guid.NewGuid().ToString("N");
        var pruned = new StateAddress("app", $"conformance/retention-feed-a-{suffix}", StatePartition.Default);
        var control = new StateAddress("app", $"conformance/retention-feed-b-{suffix}", StatePartition.Default);
        await SeedAsync(store, pruned, 10, 10, 10);
        await SeedAsync(store, control, 10, 10);

        await store.PruneAsync(pruned, new StateRetentionPolicy { MaxRevisions = 1 });
        if (maintain is not null)
        {
            await maintain();
        }

        List<StateChangeEnvelope> changes = await DrainAsync(feed);
        long[] survivors = [.. changes
            .Where(envelope => envelope.Record.Address.Canonical == pruned.Canonical)
            .Select(envelope => envelope.Record.Revision)];
        Assert.Equal([3L], survivors);
        long[] untouched = [.. changes
            .Where(envelope => envelope.Record.Address.Canonical == control.Canonical)
            .Select(envelope => envelope.Record.Revision)];
        Assert.Equal([1L, 2L], untouched);
    }

    [Fact]
    public async Task MaxRevisions_keeps_exactly_the_newest_revisions_and_pruning_again_changes_nothing()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var pruned = new StateAddress("app", "conformance/retention-newest", StatePartition.Default);
        var control = new StateAddress("app", "conformance/retention-newest-control", StatePartition.Default);
        await SeedAsync(store!.Store, pruned, 10, 10, 10, 10, 10, 10);
        await SeedAsync(store.Store, control, 10, 10, 10, 10, 10, 10);

        await store.Store.PruneAsync(pruned, new StateRetentionPolicy { MaxRevisions = 3 });

        long[] survivors = await RevisionsAsync(store.Store, pruned);
        Assert.Equal([4L, 5L, 6L], survivors);

        // Prune is per address: the control keeps all six, so an implementation that trimmed by
        // global position range rather than per address would be caught here.
        long[] untouched = await RevisionsAsync(store.Store, control);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], untouched);

        // Idempotence. Applying the same policy twice more removes nothing further, because the
        // filters are computed over what is stored now rather than over what was ever stored.
        await store.Store.PruneAsync(pruned, new StateRetentionPolicy { MaxRevisions = 3 });
        await store.Store.PruneAsync(pruned, new StateRetentionPolicy { MaxRevisions = 3 });
        long[] afterTwoMore = await RevisionsAsync(store.Store, pruned);
        Assert.Equal([4L, 5L, 6L], afterTwoMore);

        // Read options page what survived, unchanged by the prune: retention decides which records
        // exist, StateHistoryOptions decides how a reader walks them.
        long[] newestFirst = await RevisionsAsync(
            store.Store, pruned, new StateHistoryOptions { Take = null, NewestFirst = true });
        Assert.Equal([6L, 5L, 4L], newestFirst);
        long[] newestTwo = await RevisionsAsync(
            store.Store, pruned, new StateHistoryOptions { Take = 2, NewestFirst = true });
        Assert.Equal([6L, 5L], newestTwo);
    }

    [Fact]
    public async Task MaxRevisions_of_one_leaves_only_the_latest_revision()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/retention-one", StatePartition.Default);
        await SeedAsync(store!.Store, address, 10, 10, 10, 10, 10, 10);

        await store.Store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([6L], revisions);
        StateRecord? head = await store.Store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(6L, head!.Revision);
    }

    [Fact]
    public async Task MaxAge_keeps_only_the_revisions_younger_than_the_cutoff()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/retention-age", StatePartition.Default);
        await SeedOverTimeAsync(store!.Store, Clock, address, 6, TimeSpan.FromHours(1));

        // Revision 6 occurred at the clock's current instant, revision 1 five hours before it, so a
        // 150 minute cutoff lands between revisions 3 and 4.
        await store.Store.PruneAsync(address, new StateRetentionPolicy { MaxAge = TimeSpan.FromMinutes(150) });

        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([4L, 5L, 6L], revisions);
    }

    [Fact]
    public async Task The_MaxAge_cutoff_is_inclusive_at_the_boundary()
    {
        // The corner no provider documents: the filter is OccurredAt >= now - age, so a record whose
        // occurrence is exactly the cutoff survives. Pinned because "older than three hours" and "at
        // least three hours old" differ by exactly this record, and four providers agree on it.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/retention-boundary", StatePartition.Default);
        await SeedOverTimeAsync(store!.Store, Clock, address, 6, TimeSpan.FromHours(1));

        // Revision 3 occurred exactly three hours before the clock's current instant.
        await store.Store.PruneAsync(address, new StateRetentionPolicy { MaxAge = TimeSpan.FromHours(3) });

        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([3L, 4L, 5L, 6L], revisions);
    }

    [Fact]
    public async Task MaxAge_never_evicts_the_latest_revision_however_stale_it_is()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var young = new StateAddress("app", "conformance/retention-age-young", StatePartition.Default);
        var stale = new StateAddress("app", "conformance/retention-age-stale", StatePartition.Default);
        await SeedOverTimeAsync(store!.Store, Clock, young, 6, TimeSpan.FromHours(1));
        await SeedOverTimeAsync(store.Store, Clock, stale, 6, TimeSpan.FromHours(1));

        // A cutoff one minute wide: on the young address only the newest record is inside it.
        await store.Store.PruneAsync(young, new StateRetentionPolicy { MaxAge = TimeSpan.FromMinutes(1) });
        long[] youngest = await RevisionsAsync(store.Store, young);
        Assert.Equal([6L], youngest);

        // And with the clock thirty days past every record, the head itself is outside the cutoff.
        // It survives anyway: the age filter exempts the latest revision explicitly, which is what
        // makes retention safe to run on a stream nobody has written to for a month.
        Clock.Advance(TimeSpan.FromDays(30));
        await store.Store.PruneAsync(stale, new StateRetentionPolicy { MaxAge = TimeSpan.FromMinutes(1) });
        long[] staleSurvivors = await RevisionsAsync(store.Store, stale);
        Assert.Equal([6L], staleSurvivors);
        StateRecord? head = await store.Store.ReadLatestAsync(stale);
        Assert.NotNull(head);
        Assert.Equal(6L, head!.Revision);
    }

    [Fact]
    public async Task MaxBytes_admits_newest_first_and_keeps_the_head_under_any_budget()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var budgeted = new StateAddress("app", "conformance/retention-bytes", StatePartition.Default);
        var tiny = new StateAddress("app", "conformance/retention-bytes-tiny", StatePartition.Default);
        await SeedAsync(store!.Store, budgeted, 10, 10, 10, 10, 10, 10);
        await SeedAsync(store.Store, tiny, 10, 10, 10, 10, 10, 10);

        // Twenty five bytes admits two ten byte records and not a third.
        await store.Store.PruneAsync(budgeted, new StateRetentionPolicy { MaxBytes = 25 });
        long[] two = await RevisionsAsync(store.Store, budgeted);
        Assert.Equal([5L, 6L], two);

        // A budget below one record still admits the newest one unconditionally. This is the
        // documented promise that the latest revision is always retained even when it alone exceeds
        // the byte budget.
        await store.Store.PruneAsync(tiny, new StateRetentionPolicy { MaxBytes = 1 });
        long[] one = await RevisionsAsync(store.Store, tiny);
        Assert.Equal([6L], one);
        StateRecord? head = await store.Store.ReadLatestAsync(tiny);
        Assert.NotNull(head);
        Assert.Equal(6L, head!.Revision);
    }

    [Fact]
    public async Task MaxBytes_leaves_a_non_contiguous_survivor_set_rather_than_stopping_at_the_first_overflow()
    {
        // The corner nothing documents, and it is deliberate in all four base implementations: the
        // byte walk skips past a record that would overflow the budget and carries on considering
        // older, smaller ones, rather than stopping at the first overflow. The survivor set is
        // therefore not always a contiguous suffix of the revisions. It is pinned as intended
        // behaviour because it retains strictly more revisions under the same budget, which is what
        // the option is for. ROADMAP 0.3 pre-Phase-19 addendum decision 90.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/retention-gap", StatePartition.Default);
        await SeedAsync(store!.Store, address, 5, 5, 5, 100, 10, 10);

        // Newest first under a thirty byte budget: 6 (10), 5 (10), 4 would overflow and is skipped,
        // 3 (5), 2 (5), 1 would overflow and is skipped.
        await store.Store.PruneAsync(address, new StateRetentionPolicy { MaxBytes = 30 });

        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([2L, 3L, 5L, 6L], revisions);
    }

    [Fact]
    public async Task MaxBytes_counts_serialized_payload_bytes_and_nothing_else()
    {
        // The second undocumented corner: the budget is measured against StateRecord.Payload's length
        // alone. No file size, no key size, no JSON envelope, no metadata. A tombstone carries no
        // payload and therefore costs zero, so three of them fit inside any budget at all. A provider
        // that charged its own on-disk or on-wire overhead would evict them here.
        // ROADMAP 0.3 pre-Phase-19 addendum decision 91.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/retention-payload-only", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit(10));
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(1), Tombstone());
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(2), Tombstone());
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(3), Tombstone());
        await store.Store.AppendAsync(address, StateWriteCondition.AtRevision(4), Commit(10));

        // Twenty payload bytes in total, against a budget of twenty five.
        await store.Store.PruneAsync(address, new StateRetentionPolicy { MaxBytes = 25 });

        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], revisions);
    }

    [Fact]
    public async Task MaxRevisions_narrows_first_and_MaxBytes_then_applies_to_what_survived()
    {
        // Several limits at once narrow sequentially rather than intersecting independently computed
        // sets: age, then tombstones, then MaxRevisions, then MaxBytes over whatever is left. The
        // measurable consequence is that MaxRevisions counts what survived the earlier filters rather
        // than the raw revision count.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/retention-combined", StatePartition.Default);
        await SeedAsync(store!.Store, address, 10, 10, 10, 10, 10, 10);

        // MaxRevisions narrows six to [4, 5, 6]; MaxBytes then admits 6 and 5 and skips 4.
        await store.Store.PruneAsync(
            address, new StateRetentionPolicy { MaxRevisions = 3, MaxBytes = 25 });

        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([5L, 6L], revisions);
    }

    [Fact]
    public async Task A_tombstone_is_dropped_only_when_it_is_not_the_latest_revision()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var mixed = new StateAddress("app", "conformance/retention-tombstone-mixed", StatePartition.Default);
        var allTombstones = new StateAddress("app", "conformance/retention-tombstone-all", StatePartition.Default);

        // Set, clear, set, clear. The clear at revision 2 is droppable; the clear at revision 4 is
        // the latest and is exempt.
        await store!.Store.AppendAsync(mixed, StateWriteCondition.Absent, Commit(10));
        await store.Store.AppendAsync(mixed, StateWriteCondition.AtRevision(1), Tombstone());
        await store.Store.AppendAsync(mixed, StateWriteCondition.AtRevision(2), Commit(10));
        await store.Store.AppendAsync(mixed, StateWriteCondition.AtRevision(3), Tombstone());

        await store.Store.PruneAsync(mixed, new StateRetentionPolicy { KeepTombstones = false });

        long[] survivors = await RevisionsAsync(store.Store, mixed);
        Assert.Equal([1L, 3L, 4L], survivors);
        StateRecord? head = await store.Store.ReadLatestAsync(mixed);
        Assert.NotNull(head);
        Assert.Equal(4L, head!.Revision);
        Assert.Equal(StateOperation.Cleared, head.Operation);

        // And when every revision is a tombstone, the latest one is still what survives.
        await store.Store.AppendAsync(allTombstones, StateWriteCondition.Absent, Tombstone());
        await store.Store.AppendAsync(allTombstones, StateWriteCondition.AtRevision(1), Tombstone());
        await store.Store.AppendAsync(allTombstones, StateWriteCondition.AtRevision(2), Tombstone());
        await store.Store.AppendAsync(allTombstones, StateWriteCondition.AtRevision(3), Tombstone());

        await store.Store.PruneAsync(allTombstones, new StateRetentionPolicy { KeepTombstones = false });

        long[] lastOne = await RevisionsAsync(store.Store, allTombstones);
        Assert.Equal([4L], lastOne);
    }

    [Fact]
    public async Task KeepAll_an_unwritten_address_and_a_lone_revision_are_all_no_ops()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var everything = new StateAddress("app", "conformance/retention-keep-all", StatePartition.Default);
        var lonely = new StateAddress("app", "conformance/retention-lone", StatePartition.Default);
        var neverWritten = new StateAddress("app", "conformance/retention-unknown", StatePartition.Default);
        await SeedAsync(store!.Store, everything, 10, 10, 10, 10, 10, 10);
        await SeedAsync(store.Store, lonely, 10);

        // The default policy bounds nothing, however far the clock has moved.
        Clock.Advance(TimeSpan.FromDays(365));
        await store.Store.PruneAsync(everything, StateRetentionPolicy.KeepAll);
        long[] all = await RevisionsAsync(store.Store, everything);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], all);

        // One revision is returned before any filter runs, so every bound at once still keeps it.
        await store.Store.PruneAsync(
            lonely,
            new StateRetentionPolicy { MaxRevisions = 1, MaxAge = TimeSpan.FromMinutes(1), MaxBytes = 1 });
        long[] lone = await RevisionsAsync(store.Store, lonely);
        Assert.Equal([1L], lone);

        // An address nobody ever wrote is a silent no-op rather than a throw, which is what lets a
        // caller prune on a schedule without first proving the address exists.
        await store.Store.PruneAsync(neverWritten, new StateRetentionPolicy { MaxRevisions = 1 });
        long[] nothing = await RevisionsAsync(store.Store, neverWritten);
        Assert.Empty(nothing);
        Assert.Null(await store.Store.ReadLatestAsync(neverWritten));
    }

    [Fact]
    public async Task Every_limit_removes_the_pruned_revisions_from_the_change_feed()
    {
        // The Phase 11 rule, asserted for all three bounds rather than only MaxRevisions: a record
        // leaves the change feed exactly when the prune removes it from the store. The control
        // address is what discriminates a feed repair that worked by position range rather than by
        // member.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var byCount = new StateAddress("app", "conformance/retention-feed-count", StatePartition.Default);
        var byAge = new StateAddress("app", "conformance/retention-feed-age", StatePartition.Default);
        var byBytes = new StateAddress("app", "conformance/retention-feed-bytes", StatePartition.Default);
        var control = new StateAddress("app", "conformance/retention-feed-control", StatePartition.Default);
        await SeedOverTimeAsync(store!.Store, Clock, byAge, 3, TimeSpan.FromHours(1));
        await SeedAsync(store.Store, byCount, 10, 10, 10);
        await SeedAsync(store.Store, byBytes, 10, 10, 10);
        await SeedAsync(store.Store, control, 10, 10);

        await store.Store.PruneAsync(byCount, new StateRetentionPolicy { MaxRevisions = 1 });
        await store.Store.PruneAsync(byAge, new StateRetentionPolicy { MaxAge = TimeSpan.FromMinutes(1) });
        await store.Store.PruneAsync(byBytes, new StateRetentionPolicy { MaxBytes = 1 });
        if (store.Maintain is not null)
        {
            // The filesystem provider's change log is append-only, so its feed repair is a
            // maintenance step rather than something the prune itself does.
            await store.Maintain();
        }

        List<StateChangeEnvelope> changes = await DrainAsync(store.Feed);
        Assert.Equal([3L], FeedRevisions(changes, byCount));
        Assert.Equal([3L], FeedRevisions(changes, byAge));
        Assert.Equal([3L], FeedRevisions(changes, byBytes));
        Assert.Equal([1L, 2L], FeedRevisions(changes, control));

        // Positions stay strictly increasing across what is left, so a consumer paging the feed after
        // a prune never sees it go backwards.
        long[] positions = [.. changes.Select(envelope => envelope.Record.GlobalPosition)];
        for (int index = 1; index < positions.Length; index++)
        {
            Assert.True(
                positions[index - 1] < positions[index],
                $"position {positions[index]} did not follow {positions[index - 1]}");
        }
    }

    [Fact]
    public async Task The_head_still_answers_after_every_limit_has_pruned_its_predecessors()
    {
        // Four independent mechanisms keep the head: the early return for a single record, the
        // explicit latest-revision exemption in the age and tombstone filters, MaxRevisions keeping
        // the tail of a revision-ascending list, and the byte walk admitting its first entry
        // unconditionally. This asserts the observable consequence on all three bounds at once.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var byCount = new StateAddress("app", "conformance/retention-head-count", StatePartition.Default);
        var byAge = new StateAddress("app", "conformance/retention-head-age", StatePartition.Default);
        var byBytes = new StateAddress("app", "conformance/retention-head-bytes", StatePartition.Default);
        await SeedOverTimeAsync(store!.Store, Clock, byAge, 6, TimeSpan.FromHours(1));
        await SeedAsync(store.Store, byCount, 10, 10, 10, 10, 10, 10);
        await SeedAsync(store.Store, byBytes, 10, 10, 10, 10, 10, 10);

        StateRecord? beforeCount = await store.Store.ReadLatestAsync(byCount);
        await store.Store.PruneAsync(byCount, new StateRetentionPolicy { MaxRevisions = 1 });
        await store.Store.PruneAsync(byAge, new StateRetentionPolicy { MaxAge = TimeSpan.FromMinutes(1) });
        await store.Store.PruneAsync(byBytes, new StateRetentionPolicy { MaxBytes = 1 });

        StateRecord? afterCount = await store.Store.ReadLatestAsync(byCount);
        Assert.NotNull(afterCount);
        Assert.Equal(6L, afterCount!.Revision);
        Assert.Equal(beforeCount!.GlobalPosition, afterCount.GlobalPosition);
        StateRecord? afterAge = await store.Store.ReadLatestAsync(byAge);
        Assert.NotNull(afterAge);
        Assert.Equal(6L, afterAge!.Revision);
        StateRecord? afterBytes = await store.Store.ReadLatestAsync(byBytes);
        Assert.NotNull(afterBytes);
        Assert.Equal(6L, afterBytes!.Revision);
    }

    [Fact]
    public async Task A_pruned_address_stays_in_the_partition_catalog()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IPartitionCatalog? catalog),
            "This provider does not advertise a partition catalog.");
        var address = new StateAddress("app", "conformance/retention-catalog", StatePartition.Default);
        await SeedAsync(store.Store, address, 10, 10, 10, 10, 10, 10);

        await store.Store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

        var listed = new List<string>();
        await foreach (StatePartitionDescriptor descriptor in catalog!.ListPartitionsAsync())
        {
            listed.Add(descriptor.Address.Canonical);
        }

        Assert.Contains(address.Canonical, listed);
    }

    private static long[] FeedRevisions(List<StateChangeEnvelope> changes, StateAddress address) =>
        [.. changes
            .Where(envelope => envelope.Record.Address.Canonical == address.Canonical)
            .Select(envelope => envelope.Record.Revision)];

    private static async Task<List<StateChangeEnvelope>> DrainAsync(IStateChangeFeed feed)
    {
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        return changes;
    }

    private static async Task SeedAsync(IStateLedgerStore store, StateAddress address, params int[] payloadSizes)
    {
        for (int index = 0; index < payloadSizes.Length; index++)
        {
            StateWriteCondition condition = index == 0
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(index);
            StateAppendResult result = await store.AppendAsync(address, condition, Commit(payloadSizes[index]));
            Assert.True(result.Succeeded, $"seeding revision {index + 1} of {address.Canonical} failed");
        }
    }

    private static async Task SeedOverTimeAsync(
        IStateLedgerStore store,
        ManualTimeProvider clock,
        StateAddress address,
        int revisions,
        TimeSpan step)
    {
        for (int index = 0; index < revisions; index++)
        {
            if (index > 0)
            {
                clock.Advance(step);
            }

            StateWriteCondition condition = index == 0
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(index);
            StateAppendResult result = await store.AppendAsync(address, condition, Commit(10));
            Assert.True(result.Succeeded, $"seeding revision {index + 1} of {address.Canonical} failed");
        }
    }

    private static async Task<long[]> RevisionsAsync(
        IStateLedgerStore store,
        StateAddress address,
        StateHistoryOptions? options = null)
    {
        var revisions = new List<long>();
        await foreach (StateRecord record in store.ReadHistoryAsync(
            address, options ?? new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            revisions.Add(record.Revision);
        }

        return [.. revisions];
    }

    private static StateCommit Commit(int payloadBytes) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = new byte[payloadBytes],
        Source = "test",
    };

    private static StateCommit Tombstone() => new()
    {
        Operation = StateOperation.Cleared,
        Status = StateStatus.Cleared,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = null,
        Source = "test",
    };
}
