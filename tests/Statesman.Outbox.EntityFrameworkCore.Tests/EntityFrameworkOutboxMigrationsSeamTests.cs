using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Statesman.TestHelpers;

namespace Statesman.Outbox.EntityFrameworkCore.Tests;

/// <summary>
/// Pins, for the outbox cursor context, the mechanism
/// <c>EntityFrameworkMigrationsSeamTests</c> pins for the ledger: a migration generated against
/// <see cref="StatesmanOutboxCursorDbContext"/> reaching a consumer's subclass. Without
/// <see cref="StatesmanOutboxMigrationsAssembly"/>, <c>Migrate()</c> on a subclass returns normally
/// and creates nothing but the history table.
/// </summary>
public sealed class EntityFrameworkOutboxMigrationsSeamTests
{
    /// <summary>The migration this file discovers. Public because Entity Framework Core activates it by its public constructor.</summary>
    [DbContext(typeof(StatesmanOutboxCursorDbContext))]
    [Migration(ProbeMigrationId)]
    public sealed class OutboxProbeMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.CreateTable(
                name: "StatesmanOutboxCursors",
                columns: table => new
                {
                    OutboxId = table.Column<string>(maxLength: 256, nullable: false),
                    Position = table.Column<long>(nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_StatesmanOutboxCursors", x => x.OutboxId));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(name: "StatesmanOutboxCursors");
        }
    }

    internal const string ProbeMigrationId = "20260101000000_OutboxProbe";

    [Fact]
    public async Task A_shipped_migration_reaches_a_consumers_subclass()
    {
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamCursorContext(database.Options<SeamCursorContext>(
            builder => builder.UseStatesmanOutboxMigrations(typeof(OutboxProbeMigration).Assembly)));

        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.NotEmpty(assembly.Migrations);

        await context.Database.MigrateAsync();

        Assert.Contains(ProbeMigrationId, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        // Quoted: PostgreSQL folds an unquoted identifier to lower case, which would not match the
        // mixed-case table name the migration actually created.
        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"StatesmanOutboxCursors\" WHERE 1 = 0");
    }

    [Fact]
    public async Task A_shipped_migration_still_reaches_the_base_context()
    {
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new StatesmanOutboxCursorDbContext(
            database.Options<StatesmanOutboxCursorDbContext>(
                builder => builder.UseStatesmanOutboxMigrations(typeof(OutboxProbeMigration).Assembly)));

        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.NotEmpty(assembly.Migrations);

        await context.Database.MigrateAsync();

        Assert.Contains(ProbeMigrationId, await context.Database.GetAppliedMigrationsAsync());
    }

    private sealed class SeamCursorContext : StatesmanOutboxCursorDbContext
    {
        public SeamCursorContext(DbContextOptions<SeamCursorContext> options)
            : base(options)
        {
        }
    }
}
