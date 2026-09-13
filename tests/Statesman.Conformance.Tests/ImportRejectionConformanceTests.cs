namespace Statesman.Conformance.Tests;

/// <summary>
/// What every import target does with a damaged record: refuse it, and change nothing. This is the
/// half of ROADMAP 0.2's "corruption behaviour" that all four import targets genuinely share — each
/// calls <see cref="StateRecord.Validate"/> before touching its storage. The other half, what a reader
/// observes after a damaged record already on disk, is only meaningful on the filesystem provider,
/// whose append-only change log is the one textual structure a crash can leave half-written; those
/// tests stay in that provider's own project, and docs/providers/index.md says why.
/// </summary>
public abstract class ImportRejectionConformanceTests
{
    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that an import target refuses a record that fails validation, and that the refusal
    /// leaves the address absent. Public and static so the negative test in this project can run it
    /// against a replica that imports whatever it is handed and require it to fail.
    /// </summary>
    /// <param name="store">The store the replica belongs to.</param>
    /// <param name="replica">The replica capability under test.</param>
    public static async Task AssertInvalidImportIsRefusedAsync(
        IStateLedgerStore store,
        IStateLedgerReplica replica)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(replica);
        var address = new StateAddress("app", $"conformance/refused-{Guid.NewGuid():N}", StatePartition.Default);

        // Revision zero: StateRecord.Validate's first numeric guard, and a value no legitimate export
        // can carry. A truncated or corrupted export line is how one arrives.
        StateRecord damaged = Record(address, revision: 1, position: 100) with { Revision = 0 };

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await replica.ImportAsync(damaged));

        // The half that discriminates a store which validates after writing.
        Assert.Null(await store.ReadLatestAsync(address));
    }

    [Fact]
    public async Task ImportAsync_refuses_a_record_that_fails_validation_and_writes_nothing()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        await AssertInvalidImportIsRefusedAsync(store.Store, replica!);
    }

    [Fact]
    public async Task ImportAsync_refuses_a_null_record()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await replica!.ImportAsync(null!));
    }

    [Fact]
    public async Task A_refused_import_leaves_the_change_feed_and_the_partition_catalog_untouched()
    {
        // The assertion that catches a provider which writes its feed entry or its catalog entry
        // before validating — a store can leave the head correct and the feed wrong, which is exactly
        // the class of defect Phase 13 and Phase 15 each found once.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        var good = new StateAddress("app", "conformance/refuse-good", StatePartition.Default);
        var bad = new StateAddress("app", "conformance/refuse-bad", StatePartition.Default);
        await replica!.ImportAsync(Record(good, revision: 1, position: 100));
        if (store.Maintain is not null)
        {
            await store.Maintain();
        }

        int feedBefore = await CountAsync(store.Feed);
        int catalogBefore = await CountPartitionsAsync(store.Store);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await replica.ImportAsync(Record(bad, revision: 1, position: 200) with { Source = " " }));
        if (store.Maintain is not null)
        {
            await store.Maintain();
        }

        Assert.Equal(feedBefore, await CountAsync(store.Feed));
        Assert.Equal(catalogBefore, await CountPartitionsAsync(store.Store));
    }

    private static async Task<int> CountAsync(IStateChangeFeed feed)
    {
        int count = 0;
        await foreach (StateChangeEnvelope _ in feed.ReadAsync(null, StateChangeReadOptions.Default))
        {
            count++;
        }

        return count;
    }

    private static async Task<int> CountPartitionsAsync(IStateLedgerStore store)
    {
        if (!store.TryGetCapability(out IPartitionCatalog? catalog))
        {
            return 0;
        }

        int count = 0;
        await foreach (StatePartitionDescriptor _ in catalog!.ListPartitionsAsync())
        {
            count++;
        }

        return count;
    }

    private static StateRecord Record(StateAddress address, long revision, long position) => new()
    {
        Address = address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes($"{address.Canonical}@{position}"),
        Source = "test",
    };
}
