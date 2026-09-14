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
/// proves it discriminates is <c>BrokenRetentionConformanceTests</c>, which runs each public
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
}
