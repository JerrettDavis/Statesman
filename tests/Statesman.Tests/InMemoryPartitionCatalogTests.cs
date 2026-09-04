namespace Statesman.Tests;

public sealed class InMemoryPartitionCatalogTests
{
    [Fact]
    public async Task ListPartitionsAsync_yields_one_descriptor_per_distinct_address_with_its_latest_position()
    {
        var store = new InMemoryStateLedgerStore();
        var addressA = new StateAddress("app", "catalog/a", StatePartition.Default);
        var addressB = new StateAddress("app", "catalog/b", StatePartition.Default);
        await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult second = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        StateAppendResult third = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

        List<StatePartitionDescriptor> partitions = [];
        await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
        {
            partitions.Add(descriptor);
        }

        Assert.Equal(2, partitions.Count);
        StatePartitionDescriptor descriptorA = Assert.Single(partitions, value => value.Address.Equals(addressA));
        Assert.Equal(second.Record!.GlobalPosition, descriptorA.LastPosition.Position);
        StatePartitionDescriptor descriptorB = Assert.Single(partitions, value => value.Address.Equals(addressB));
        Assert.Equal(third.Record!.GlobalPosition, descriptorB.LastPosition.Position);
    }

    [Fact]
    public async Task ListPartitionsAsync_returns_nothing_when_the_store_has_never_been_written_to()
    {
        var store = new InMemoryStateLedgerStore();

        List<StatePartitionDescriptor> partitions = [];
        await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
        {
            partitions.Add(descriptor);
        }

        Assert.Empty(partitions);
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
