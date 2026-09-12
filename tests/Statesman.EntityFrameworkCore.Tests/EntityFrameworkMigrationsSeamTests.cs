using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Statesman.TestHelpers;

namespace Statesman.EntityFrameworkCore.Tests;

/// <summary>
/// Pins the one mechanism the whole of ROADMAP 0.3 Phase 14 rests on: a migration generated against
/// <see cref="StatesmanLedgerDbContext"/> reaching a consumer's subclass.
/// </summary>
/// <remarks>
/// Entity Framework Core discovers migrations by EXACT context-type equality --
/// <c>DbContextAttribute.ContextType == contextType</c>, a reference comparison against the running
/// context's own type. Without <see cref="StatesmanMigrationsAssembly"/>, a subclass finds nothing,
/// and <c>Database.Migrate()</c> RETURNS NORMALLY having created only the history table: no
/// exception, no log the consumer reads, and the failure is discovered at the first query. That is
/// the worst available failure mode for a schema tool, and it is what this file exists to prevent
/// regressing to.
/// </remarks>
public sealed class EntityFrameworkMigrationsSeamTests
{
    /// <summary>The migration this file discovers. Public because Entity Framework Core activates it by its public constructor.</summary>
    [DbContext(typeof(StatesmanLedgerDbContext))]
    [Migration(ProbeMigrationId)]
    public sealed class LedgerProbeMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.CreateTable(
                name: "StatesmanHeads",
                columns: table => new
                {
                    Root = table.Column<string>(maxLength: 128, nullable: false),
                    Path = table.Column<string>(maxLength: 512, nullable: false),
                    Partition = table.Column<string>(maxLength: 256, nullable: false),
                    Revision = table.Column<long>(nullable: false),
                },
                constraints: table => table.PrimaryKey(
                    "PK_StatesmanHeads", x => new { x.Root, x.Path, x.Partition }));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(name: "StatesmanHeads");
        }
    }

    internal const string ProbeMigrationId = "20260101000000_LedgerProbe";

    [Fact]
    public async Task A_shipped_migration_reaches_a_consumers_subclass()
    {
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamContext(database.Options<SeamContext>(
            builder => builder.UseStatesmanLedgerMigrations(typeof(LedgerProbeMigration).Assembly)));

        // Assert BEFORE Migrate(), because Migrate() on a subclass that discovered nothing SUCCEEDS.
        // Without this assertion the test would pass on the broken implementation for the wrong
        // reason: an empty migration set has no pending migrations either.
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.NotEmpty(assembly.Migrations);

        await context.Database.MigrateAsync();

        Assert.Contains(ProbeMigrationId, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        // Throws if the table is absent on any of the three engines, and touches no rows if present.
        // Quoted: PostgreSQL folds an unquoted identifier to lower case, which would not match the
        // mixed-case table name the migration actually created.
        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"StatesmanHeads\" WHERE 1 = 0");
    }

    [Fact]
    public async Task A_shipped_migration_still_reaches_the_base_context()
    {
        // The replacement service relaxes the filter; it must not break the arrangement that already
        // worked. A consumer who takes the base context directly is the documented zero-code
        // alternative to subclassing, so this is a supported path, not a curiosity.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new StatesmanLedgerDbContext(database.Options<StatesmanLedgerDbContext>(
            builder => builder.UseStatesmanLedgerMigrations(typeof(LedgerProbeMigration).Assembly)));

        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.NotEmpty(assembly.Migrations);

        await context.Database.MigrateAsync();

        Assert.Contains(ProbeMigrationId, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task The_shipped_history_table_is_used_instead_of_the_default()
    {
        // __EFMigrationsHistory is shared per database, so a consumer running their own migrations
        // beside a shipped set needs the two sets in separate tables or they interleave.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamContext(database.Options<SeamContext>(
            builder => builder.UseStatesmanLedgerMigrations(typeof(LedgerProbeMigration).Assembly)));

        await context.Database.MigrateAsync();

        // Quoted for the same reason as above: PostgreSQL case-folds an unquoted identifier.
        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"{StatesmanLedgerMigrations.HistoryTableName}\" WHERE 1 = 0");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE 1 = 0"));
    }

    private sealed class SeamContext : StatesmanLedgerDbContext
    {
        public SeamContext(DbContextOptions<SeamContext> options)
            : base(options)
        {
        }
    }
}
