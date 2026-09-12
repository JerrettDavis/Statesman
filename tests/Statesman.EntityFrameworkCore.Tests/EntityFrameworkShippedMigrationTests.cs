using Microsoft.EntityFrameworkCore;
using Statesman.Persistence.EntityFrameworkCore.PostgreSql;
using Statesman.Persistence.EntityFrameworkCore.Sqlite;
using Statesman.Persistence.EntityFrameworkCore.SqlServer;
using Statesman.TestHelpers;

namespace Statesman.EntityFrameworkCore.Tests;

/// <summary>
/// Creates the schema from the shipped migration rather than from <c>EnsureCreated</c>, then drives
/// the real store through it.
/// </summary>
/// <remarks>
/// This is the test a model-drift check cannot be. Drift compares a model to a snapshot and never
/// touches a database; this runs the generated DDL on a real engine and then writes through it, which
/// is the only thing that shows the DDL is valid and that the store's queries match the tables it
/// produced.
/// </remarks>
public sealed class EntityFrameworkShippedMigrationTests
{
    [Fact]
    public async Task The_shipped_migration_creates_a_schema_the_store_can_round_trip()
    {
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<MigratedLedgerContext> factory =
            await database.CreateFactoryWithMigrationsAsync<MigratedLedgerContext>(
                options => new MigratedLedgerContext(options),
                SelectShippedMigrations());

        var store = new EntityFrameworkStateLedgerStore<MigratedLedgerContext>("migrated", factory);
        var address = new StateAddress("migrated", "accounts/account", StatePartition.Default);

        StateAppendResult first = await store.AppendAsync(
            address, StateWriteCondition.Absent, Commit("one"));
        StateAppendResult second = await store.AppendAsync(
            address, StateWriteCondition.AtRevision(1), Commit("two"));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        StateRecord? latest = await store.ReadLatestAsync(address);
        Assert.Equal(2, latest!.Revision);

        // The unique index on GlobalPosition is part of the generated DDL, so a round trip that never
        // reads the change feed would not exercise it.
        var positions = new List<long>();
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            positions.Add(envelope.Record.GlobalPosition);
        }

        Assert.Equal(2, positions.Count);
        Assert.Equal(positions.Distinct().Count(), positions.Count);
    }

    // The one place this file knows which engine it is on. Everything else about the test is
    // engine-agnostic, which is what lets the two server jobs run the shipped DDL against a live
    // engine with no workflow change: both jobs already run this project by explicit path.
    private static Action<DbContextOptionsBuilder<MigratedLedgerContext>> SelectShippedMigrations() =>
        EntityFrameworkTestDatabase.SelectedEngine switch
        {
            EntityFrameworkTestEngine.SqlServer =>
                static builder => builder.UseStatesmanLedgerSqlServerMigrations(),
            EntityFrameworkTestEngine.PostgreSql =>
                static builder => builder.UseStatesmanLedgerPostgreSqlMigrations(),
            _ => static builder => builder.UseStatesmanLedgerSqliteMigrations(),
        };

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "migration-test",
    };

    private sealed class MigratedLedgerContext : StatesmanLedgerDbContext
    {
        public MigratedLedgerContext(DbContextOptions<MigratedLedgerContext> options)
            : base(options)
        {
        }
    }
}
