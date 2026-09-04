using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Statesman.EntityFrameworkCore.Tests;

public sealed class EntityFrameworkChangeFeedTests
{
    [Fact]
    public void EntityFrameworkStateLedgerStore_reports_change_feed_capability()
    {
        Assert.True(typeof(EntityFrameworkStateLedgerStore<>).GetInterfaces().Contains(typeof(IStateChangeFeed)));
    }

    [Fact]
    public async Task ReadAsync_yields_records_after_the_given_cursor_across_streams_in_position_order()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestFeedContext>().UseSqlite(connection).Options;
        var factory = new TestFeedContextFactory(options);
        await using (TestFeedContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TestFeedContext>("database", factory);
        var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/b", StatePartition.Default);
        StateAppendResult first = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult second = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult third = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(new StateChangeCursor(first.Record!.GlobalPosition)))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
        Assert.Equal(second.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
        Assert.Equal(third.Record!.GlobalPosition, changes[1].Record.GlobalPosition);
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

    private sealed class TestFeedContext : StatesmanLedgerDbContext
    {
        public TestFeedContext(DbContextOptions<TestFeedContext> options)
            : base(options)
        {
        }
    }

    private sealed class TestFeedContextFactory : IDbContextFactory<TestFeedContext>
    {
        private readonly DbContextOptions<TestFeedContext> _options;

        public TestFeedContextFactory(DbContextOptions<TestFeedContext> options)
        {
            _options = options;
        }

        public TestFeedContext CreateDbContext() => new(_options);

        public Task<TestFeedContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
