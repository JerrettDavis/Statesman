namespace Statesman.Conformance.Tests;

/// <summary>
/// The import contract every <see cref="IStateLedgerReplica"/> implementation shares, run once per
/// provider by a subclass. Capability presence is answered by
/// <see cref="StateCapabilityExtensions.TryGetCapability{TCapability}"/> at the test site, never by a
/// per-provider boolean: the tiered provider over an in-memory cold tier vetoes this capability
/// outright, so all four facts skip there with an honest "No" rather than a silent pass.
/// </summary>
public abstract class LedgerReplicaConformanceTests
{
    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that an imported record's head comes back with every field the import carried,
    /// unchanged. Public and static so the negative test in this project can run it against a replica
    /// that re-allocates its own position instead of preserving the record's, and require it to fail.
    /// </summary>
    /// <param name="store">The store the replica belongs to.</param>
    /// <param name="replica">The replica capability under test.</param>
    public static async Task AssertImportIsExactAsync(IStateLedgerStore store, IStateLedgerReplica replica)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(replica);
        var address = new StateAddress("app", $"conformance/replica-exact-{Guid.NewGuid():N}", StatePartition.Default);
        StateRecord imported = Record(address, revision: 1, position: 100);

        await replica.ImportAsync(imported);

        StateRecord? head = await store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(imported.Revision, head!.Revision);
        Assert.Equal(imported.GlobalPosition, head.GlobalPosition);
        Assert.Equal(imported.Operation, head.Operation);
        Assert.Equal(imported.Payload, head.Payload);
        Assert.Equal(imported.Source, head.Source);
    }

    [Fact]
    public async Task ImportAsync_stores_the_record_exactly_including_its_global_position()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        await AssertImportIsExactAsync(store.Store, replica!);
    }

    [Fact]
    public async Task ImportAsync_is_idempotent_for_the_same_address_and_revision()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        var address = new StateAddress("app", "conformance/replica-idempotent", StatePartition.Default);
        StateRecord record = Record(address, revision: 1, position: 100);

        await replica!.ImportAsync(record);
        await replica.ImportAsync(record);

        long[] revisions = await RevisionsAsync(store.Store, address);
        Assert.Equal([1L], revisions);
    }

    [Fact]
    public async Task ImportAsync_does_not_regress_the_head_when_an_older_revision_arrives_later()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        var address = new StateAddress("app", "conformance/replica-no-regress", StatePartition.Default);
        await replica!.ImportAsync(Record(address, revision: 3, position: 300));
        await replica.ImportAsync(Record(address, revision: 1, position: 100));

        StateRecord? head = await store.Store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(3L, head!.Revision);
    }

    [Fact]
    public async Task An_append_after_an_import_never_reuses_an_imported_position()
    {
        // 1_000_000 rather than something near RedisStateLedgerStore.MaxImportablePosition (2^52):
        // Redis refuses anything above that with NotSupportedException, which is documented provider
        // behaviour and not what this fact is about.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        var importedAddress = new StateAddress("app", "conformance/replica-high-water", StatePartition.Default);
        var appendedAddress = new StateAddress("app", "conformance/replica-appended", StatePartition.Default);
        await replica!.ImportAsync(Record(importedAddress, revision: 1, position: 1_000_000));

        StateAppendResult appended = await store.Store.AppendAsync(
            appendedAddress, StateWriteCondition.Absent, Commit("after-import"));

        Assert.True(appended.Succeeded);
        Assert.True(appended.Record!.GlobalPosition > 1_000_000L);
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
