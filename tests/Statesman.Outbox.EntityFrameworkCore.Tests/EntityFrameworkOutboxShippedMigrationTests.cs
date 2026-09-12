using Microsoft.EntityFrameworkCore;
using Statesman.Outbox.EntityFrameworkCore.PostgreSql;
using Statesman.Outbox.EntityFrameworkCore.Sqlite;
using Statesman.Outbox.EntityFrameworkCore.SqlServer;
using Statesman.TestHelpers;

namespace Statesman.Outbox.EntityFrameworkCore.Tests;

/// <summary>
/// Creates the cursor table from the shipped migration rather than from <c>EnsureCreated</c>, then
/// drives the real cursor store through it -- including the monotonic write, which is a conditional
/// <c>UPDATE</c> against the column the migration generated.
/// </summary>
public sealed class EntityFrameworkOutboxShippedMigrationTests
{
    [Fact]
    public async Task The_shipped_migration_creates_a_cursor_table_the_store_can_round_trip()
    {
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<MigratedCursorContext> factory =
            await database.CreateFactoryWithMigrationsAsync<MigratedCursorContext>(
                options => new MigratedCursorContext(options),
                SelectShippedMigrations());

        var store = new EntityFrameworkOutboxCursorStore<MigratedCursorContext>(factory);
        string outboxId = $"outbox-{Guid.NewGuid():N}";

        Assert.Null(await store.ReadAsync(outboxId));
        await store.WriteAsync(outboxId, new StateChangeCursor(64));
        Assert.Equal(64, (await store.ReadAsync(outboxId))!.Value.Position);

        // The monotonic predicate rides on the generated column, so a migration that got the column
        // type wrong would show up here rather than in the first write.
        await store.WriteAsync(outboxId, new StateChangeCursor(1));
        Assert.Equal(64, (await store.ReadAsync(outboxId))!.Value.Position);
    }

    private static Action<DbContextOptionsBuilder<MigratedCursorContext>> SelectShippedMigrations() =>
        EntityFrameworkTestDatabase.SelectedEngine switch
        {
            EntityFrameworkTestEngine.SqlServer =>
                static builder => builder.UseStatesmanOutboxSqlServerMigrations(),
            EntityFrameworkTestEngine.PostgreSql =>
                static builder => builder.UseStatesmanOutboxPostgreSqlMigrations(),
            _ => static builder => builder.UseStatesmanOutboxSqliteMigrations(),
        };

    private sealed class MigratedCursorContext : StatesmanOutboxCursorDbContext
    {
        public MigratedCursorContext(DbContextOptions<MigratedCursorContext> options)
            : base(options)
        {
        }
    }
}
