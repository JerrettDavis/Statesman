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

    [Fact]
    public async Task ListPartitionsAsync_honours_an_already_cancelled_token_before_issuing_HGETALL()
    {
        // Phase 3 parked this. The HashGetAllAsync ran with no cancellation check above it -- the
        // check sat inside the yield loop, AFTER the whole hash had been fetched -- so a caller who
        // cancelled before the call still paid for a full HGETALL against a possibly-large key.
        //
        // No live Redis. AbortOnConnectFail = false makes ConnectAsync succeed against an unreachable
        // endpoint and defers the failure to the first command, which is exactly the discriminator:
        // before the fix this throws RedisConnectionException naming `command=HGETALL`; after it, the
        // token is honoured first and nothing is issued at all.
        var configuration = new ConfigurationOptions
        {
            EndPoints = { { "127.0.0.1", 6399 } },
            AbortOnConnectFail = false,
            ConnectTimeout = 200,
            ConnectRetry = 0,
            SyncTimeout = 300,
        };

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(configuration);
        var store = new RedisStateLedgerStore($"catalog-token-{Guid.NewGuid():N}", connection);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (StatePartitionDescriptor _ in store.ListPartitionsAsync(cancellation.Token))
            {
            }
        });
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
