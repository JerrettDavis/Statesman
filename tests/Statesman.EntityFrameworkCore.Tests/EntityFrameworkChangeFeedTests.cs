using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Statesman.TestHelpers;

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
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestFeedContext> factory =
            await database.CreateFactoryAsync<TestFeedContext>(options => new TestFeedContext(options));

        var store = new EntityFrameworkStateLedgerStore<TestFeedContext>("database", factory);
        var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/b", StatePartition.Default);
        StateAppendResult first = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult second = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult third = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(new StateChangeCursor(first.Record!.GlobalPosition), StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
        Assert.Equal(second.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
        Assert.Equal(third.Record!.GlobalPosition, changes[1].Record.GlobalPosition);
    }

    [Fact]
    public async Task GlobalPosition_is_allocated_from_a_concurrency_tokened_row_not_a_database_sequence()
    {
        // This is a regression guard, not a behaviour test: it can never have failed, and that is
        // the point. The Entity Framework Core change feed is lossless only because the position
        // comes from the StatesmanLedgerSequence table row, whose Value is a concurrency token,
        // read and incremented inside the same serializable transaction as the record insert. For
        // a writer to allocate N+1 it must observe a COMMITTED N, or its generated
        // UPDATE ... WHERE Name = @n AND Value = N-1 matches zero rows and EF Core raises
        // DbUpdateConcurrencyException. That one row is a global serialization point, and it --
        // not the transaction boundary by itself -- is what makes the feed safe here.
        //
        // Switching to a database SEQUENCE, an IDENTITY column, or HiLo would allocate outside
        // transaction scope, which is exactly why those mechanisms exist, and would silently
        // reintroduce the tail loss ROADMAP 0.3 Phase 8 removed -- with every other test still
        // green. Hence this test.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new TestFeedContext(database.Options<TestFeedContext>());
        IModel model = context.Model;

        Assert.Empty(model.GetSequences());

        IEntityType sequence = Assert.IsAssignableFrom<IEntityType>(model.FindEntityType(typeof(StatesmanLedgerSequence)));
        IProperty sequenceValue = Assert.IsAssignableFrom<IProperty>(sequence.FindProperty(nameof(StatesmanLedgerSequence.Value)));
        Assert.True(sequenceValue.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.Never, sequenceValue.ValueGenerated);

        IEntityType record = Assert.IsAssignableFrom<IEntityType>(model.FindEntityType(typeof(StatesmanLedgerRecord)));
        IProperty position = Assert.IsAssignableFrom<IProperty>(record.FindProperty(nameof(StatesmanLedgerRecord.GlobalPosition)));
        Assert.Equal(ValueGenerated.Never, position.ValueGenerated);
        Assert.Contains(record.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Count == 1 &&
            index.Properties[0].Name == position.Name);
    }

    [Fact]
    public async Task A_paused_writer_holds_the_sequence_row_so_a_higher_position_cannot_commit_first()
    {
        // The behavioural half of the guard above: with writer A parked inside its open
        // serializable transaction, writer B cannot commit a higher position, and the feed shows
        // nothing at all. On SQLite that is enforced by BEGIN IMMEDIATE's database-wide write
        // lock; on SQL Server or PostgreSQL it is enforced by the sequence row's own lock and
        // concurrency token. Either way, positions land in commit order.
        //
        // A database that can host two concurrent transactions is required: the other tests in this
        // file share one open connection across every context, and one Microsoft.Data.Sqlite
        // connection object cannot host two transactions at once.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(
            EntityFrameworkTestConcurrency.ConcurrentTransactions);
        TestDbContextFactory<TestFeedContext> factory =
            await database.CreateFactoryAsync<TestFeedContext>(options => new TestFeedContext(options));

        var clock = new PausingTimeProvider(pauseOnCall: 1);
        var store = new EntityFrameworkStateLedgerStore<TestFeedContext>("database", factory, clock);
        var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
        var addressB = new StateAddress("app", "feed/b", StatePartition.Default);

        Task<StateAppendResult> writerA = Task.Run(() =>
            store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a")).AsTask());
        Task<StateAppendResult> writerB;
        List<StateChangeEnvelope> whilePaused;
        try
        {
            await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));
            writerB = Task.Run(() =>
                store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b")).AsTask());
            await Task.WhenAny(writerB, Task.Delay(TimeSpan.FromSeconds(2)));

            whilePaused = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
            {
                whilePaused.Add(envelope);
            }
        }
        finally
        {
            clock.Release();
        }

        StateAppendResult resultA = await writerA;
        StateAppendResult resultB = await writerB;

        Assert.Empty(whilePaused);
        Assert.Equal(1, resultA.Record!.GlobalPosition);
        Assert.Equal(2, resultB.Record!.GlobalPosition);
    }

    [Fact]
    public async Task A_capped_read_asks_the_database_for_the_limit_rather_than_filtering_in_memory()
    {
        var sql = new List<string>();
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestFeedContext> factory = await database.CreateFactoryAsync<TestFeedContext>(
            options => new TestFeedContext(options),
            builder => builder.LogTo(sql.Add, [DbLoggerCategory.Database.Command.Name]));

        var store = new EntityFrameworkStateLedgerStore<TestFeedContext>("database", factory);
        var address = new StateAddress("app", "feed/limit", StatePartition.Default);
        for (int revision = 0; revision < 100; revision++)
        {
            StateWriteCondition condition = revision == 0
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision);
            await store.AppendAsync(address, condition, Commit($"v{revision}"));
        }

        sql.Clear();
        var read = new List<StateChangeEnvelope>();
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, new StateChangeReadOptions { Take = 5 }))
        {
            read.Add(envelope);
        }

        Assert.Equal(5, read.Count);

        // The point of this test is not that five records came back, but that the SERVER was asked
        // for five. A client-side .Take(5) over an unbounded query satisfies the count assertion and
        // fails this one. SQLite and PostgreSQL render Queryable.Take as LIMIT; SQL Server renders
        // it as TOP(...) when there is no Skip, and as OFFSET ... FETCH NEXT when there is.
        Assert.Contains(
            sql,
            entry => database.TakeSqlFragments.Any(
                fragment => entry.Contains(fragment, StringComparison.Ordinal)));
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
}
