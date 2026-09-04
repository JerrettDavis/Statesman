using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Statesman.EntityFrameworkCore.Tests;

public sealed class EntityFrameworkPartitionCatalogTests
{
    [Fact]
    public void EntityFrameworkStateLedgerStore_reports_partition_catalog_capability()
    {
        Assert.True(typeof(EntityFrameworkStateLedgerStore<>).GetInterfaces().Contains(typeof(IPartitionCatalog)));
    }

    [Fact]
    public async Task ListPartitionsAsync_yields_one_descriptor_per_distinct_address_with_its_latest_position()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestCatalogContext>().UseSqlite(connection).Options;
        var factory = new TestCatalogContextFactory(options);
        await using (TestCatalogContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TestCatalogContext>("database", factory);
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

    private sealed class TestCatalogContext : StatesmanLedgerDbContext
    {
        public TestCatalogContext(DbContextOptions<TestCatalogContext> options)
            : base(options)
        {
        }
    }

    private sealed class TestCatalogContextFactory : IDbContextFactory<TestCatalogContext>
    {
        private readonly DbContextOptions<TestCatalogContext> _options;

        public TestCatalogContextFactory(DbContextOptions<TestCatalogContext> options)
        {
            _options = options;
        }

        public TestCatalogContext CreateDbContext() => new(_options);

        public Task<TestCatalogContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
