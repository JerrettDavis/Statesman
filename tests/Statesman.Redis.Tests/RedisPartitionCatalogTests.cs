using StackExchange.Redis;

namespace Statesman.Redis.Tests;

public sealed class RedisPartitionCatalogTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public void RedisStateLedgerStore_reports_partition_catalog_capability()
    {
        Assert.True(typeof(RedisStateLedgerStore).GetInterfaces().Contains(typeof(IPartitionCatalog)));
    }

    [Fact]
    public async Task ListPartitionsAsync_yields_one_descriptor_per_distinct_address_with_its_latest_position()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"catalog-test-{Guid.NewGuid():N}", connection);
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
