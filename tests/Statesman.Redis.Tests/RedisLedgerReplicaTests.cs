using System.Text;
using StackExchange.Redis;

namespace Statesman.Redis.Tests;

public sealed class RedisLedgerReplicaTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public void RedisStateLedgerStore_reports_ledger_replica_capability()
    {
        Assert.True(typeof(RedisStateLedgerStore).GetInterfaces().Contains(typeof(IStateLedgerReplica)));
    }

    [Fact]
    public async Task ImportAsync_stores_the_exact_record_and_advances_head_history_feed_catalog_and_position()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"replica-test-{Guid.NewGuid():N}", connection);
        var address = new StateAddress("app", "replica/item", StatePartition.Default);
        StateRecord imported = Record(address, revision: 1, position: 7, "one");

        await store.ImportAsync(imported);

        StateRecord? head = await store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(1, head!.Revision);
        Assert.Equal(7, head.GlobalPosition);
        Assert.Equal("one", Encoding.UTF8.GetString(head.Payload!));
        Assert.Equal(imported.OccurredAt, head.OccurredAt);

        List<StateRecord> history = [];
        await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            history.Add(record);
        }

        StateRecord only = Assert.Single(history);
        Assert.Equal(7, only.GlobalPosition);

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        StateChangeEnvelope change = Assert.Single(changes);
        Assert.Equal(7, change.Cursor.Position);

        List<StatePartitionDescriptor> partitions = [];
        await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
        {
            partitions.Add(descriptor);
        }

        StatePartitionDescriptor partition = Assert.Single(partitions);
        Assert.Equal(address, partition.Address);
        Assert.Equal(7, partition.LastPosition.Position);

        StateAppendResult appended = await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("two"));
        Assert.True(appended.Succeeded);
        Assert.Equal(2, appended.Record!.Revision);
        Assert.True(appended.Record.GlobalPosition > 7,
            $"an append after importing position 7 must allocate above it, but got {appended.Record.GlobalPosition}");
    }

    [Fact]
    public async Task ImportAsync_is_idempotent_and_replaces_a_divergent_revision_instead_of_duplicating_it()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"replica-test-{Guid.NewGuid():N}", connection);
        var address = new StateAddress("app", "replica/item", StatePartition.Default);
        StateRecord original = Record(address, revision: 1, position: 3, "one");

        await store.ImportAsync(original);
        await store.ImportAsync(original);
        await store.ImportAsync(original with { Payload = Encoding.UTF8.GetBytes("repaired") });

        StateRecord? head = await store.ReadLatestAsync(address);
        Assert.Equal("repaired", Encoding.UTF8.GetString(head!.Payload!));

        List<StateRecord> history = [];
        await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            history.Add(record);
        }

        StateRecord only = Assert.Single(history);
        Assert.Equal("repaired", Encoding.UTF8.GetString(only.Payload!));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        StateChangeEnvelope change = Assert.Single(changes);
        Assert.Equal("repaired", Encoding.UTF8.GetString(change.Record.Payload!));
    }

    [Fact]
    public async Task ImportAsync_does_not_regress_the_head_or_catalog_when_an_older_revision_arrives_later()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"replica-test-{Guid.NewGuid():N}", connection);
        var address = new StateAddress("app", "replica/item", StatePartition.Default);

        await store.ImportAsync(Record(address, revision: 2, position: 9, "two"));
        await store.ImportAsync(Record(address, revision: 1, position: 8, "one"));

        StateRecord? head = await store.ReadLatestAsync(address);
        Assert.Equal(2, head!.Revision);
        Assert.Equal(9, head.GlobalPosition);

        List<StateRecord> history = [];
        await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            history.Add(record);
        }

        Assert.Equal(new[] { 1L, 2L }, history.Select(record => record.Revision).ToArray());

        List<StatePartitionDescriptor> partitions = [];
        await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
        {
            partitions.Add(descriptor);
        }

        Assert.Equal(9, Assert.Single(partitions).LastPosition.Position);
    }

    private static StateRecord Record(StateAddress address, long revision, long position, string value) => new()
    {
        Address = address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero).AddSeconds(position),
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
