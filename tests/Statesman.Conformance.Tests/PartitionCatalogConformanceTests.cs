namespace Statesman.Conformance.Tests;

/// <summary>
/// The partition-listing contract every <see cref="IPartitionCatalog"/> implementation shares, run once
/// per provider by a subclass. All five providers implement this capability directly, so capability
/// presence is answered by <see cref="StateCapabilityExtensions.TryGetCapability{TCapability}"/> at the
/// test site rather than a per-provider boolean; only the import-sourced fact narrows further, because
/// not every provider is also an import target.
/// </summary>
public abstract class PartitionCatalogConformanceTests
{
    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that a catalog collapses many revisions of one address into exactly one descriptor per
    /// distinct address. Public and static so the negative test in this project can run it against a
    /// catalog that reports one descriptor per revision instead, and require it to fail.
    /// </summary>
    /// <param name="store">The store to write through.</param>
    /// <param name="catalog">The catalog capability under test.</param>
    public static async Task AssertOneDescriptorPerAddressAsync(IStateLedgerStore store, IPartitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        var addressA = new StateAddress("app", $"conformance/catalog-a-{Guid.NewGuid():N}", StatePartition.Default);
        var addressB = new StateAddress("app", $"conformance/catalog-b-{Guid.NewGuid():N}", StatePartition.Default);

        await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        await store.AppendAsync(addressA, StateWriteCondition.AtRevision(2), Commit("a3"));
        await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        await store.AppendAsync(addressB, StateWriteCondition.AtRevision(1), Commit("b2"));

        List<StatePartitionDescriptor> descriptors = await ListAsync(catalog);

        Assert.Single(descriptors, descriptor => descriptor.Address.Canonical == addressA.Canonical);
        Assert.Single(descriptors, descriptor => descriptor.Address.Canonical == addressB.Canonical);
    }

    [Fact]
    public async Task ListPartitionsAsync_yields_one_descriptor_per_distinct_address()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IPartitionCatalog? catalog),
            "This provider does not implement a partition catalog.");

        await AssertOneDescriptorPerAddressAsync(store.Store, catalog!);
    }

    [Fact]
    public async Task ListPartitionsAsync_reports_each_partition_at_its_newest_position()
    {
        // Not an absolute value: the filesystem provider allocates positions from UTC ticks, so only
        // the relationship to the append result holds on every provider.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IPartitionCatalog? catalog),
            "This provider does not implement a partition catalog.");

        var addressA = new StateAddress("app", "conformance/catalog-newest-a", StatePartition.Default);
        var addressB = new StateAddress("app", "conformance/catalog-newest-b", StatePartition.Default);
        await store.Store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult lastA = await store.Store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        StateAppendResult lastB = await store.Store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

        List<StatePartitionDescriptor> descriptors = await ListAsync(catalog!);

        StatePartitionDescriptor descriptorA = Assert.Single(
            descriptors, candidate => candidate.Address.Canonical == addressA.Canonical);
        Assert.Equal(lastA.Record!.GlobalPosition, descriptorA.LastPosition.Position);
        StatePartitionDescriptor descriptorB = Assert.Single(
            descriptors, candidate => candidate.Address.Canonical == addressB.Canonical);
        Assert.Equal(lastB.Record!.GlobalPosition, descriptorB.LastPosition.Position);
    }

    [Fact]
    public async Task ListPartitionsAsync_yields_nothing_for_a_store_never_written_to()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IPartitionCatalog? catalog),
            "This provider does not implement a partition catalog.");

        List<StatePartitionDescriptor> descriptors = await ListAsync(catalog!);

        Assert.Empty(descriptors);
    }

    [Fact]
    public async Task ListPartitionsAsync_lists_a_partition_created_by_ImportAsync()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IPartitionCatalog? catalog),
            "This provider does not implement a partition catalog.");
        Assert.SkipUnless(
            store.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");

        var address = new StateAddress("app", "conformance/catalog-imported", StatePartition.Default);
        await replica!.ImportAsync(Record(address, revision: 1, position: 100));
        if (store.Maintain is not null)
        {
            await store.Maintain();
        }

        List<StatePartitionDescriptor> descriptors = await ListAsync(catalog!);

        StatePartitionDescriptor descriptor = Assert.Single(
            descriptors, candidate => candidate.Address.Canonical == address.Canonical);
        Assert.Equal(100L, descriptor.LastPosition.Position);
    }

    private static async Task<List<StatePartitionDescriptor>> ListAsync(IPartitionCatalog catalog)
    {
        var descriptors = new List<StatePartitionDescriptor>();
        await foreach (StatePartitionDescriptor descriptor in catalog.ListPartitionsAsync())
        {
            descriptors.Add(descriptor);
        }

        return descriptors;
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
