using System.Reflection;
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

    [Fact]
    public void Discovery_survives_one_unloadable_type_in_the_migrations_assembly()
    {
        // Fix (Task 1, review round 1): StatesmanOutboxMigrationsAssembly must degrade to the loadable
        // types when Assembly.DefinedTypes throws ReflectionTypeLoadException, the same way the stock
        // MigrationsAssembly it replaces does through Assembly.GetConstructibleTypes(). Without the
        // fallback, one type this process cannot load anywhere in the migrations assembly loses
        // discovery for every migration in it -- a worse failure than the one this whole seam exists to
        // fix.
        using var database = EntityFrameworkTestDatabase.Create();
        var fakeAssembly = new PartiallyLoadableAssembly(typeof(OutboxProbeMigration));
        using var context = new SeamCursorContext(database.Options<SeamCursorContext>(
            builder => builder.UseStatesmanOutboxMigrations(fakeAssembly)));

        var assembly = context.GetService<IMigrationsAssembly>();

        Assert.Contains(ProbeMigrationId, assembly.Migrations.Keys);
    }

    /// <summary>
    /// An assembly whose <see cref="DefinedTypes"/> reproduces a partial load failure: one type failed
    /// to load, but <see cref="OutboxProbeMigration"/> -- passed in as the one type that DID load --
    /// still did.
    /// </summary>
    private sealed class PartiallyLoadableAssembly : Assembly
    {
        private readonly Type _loadableType;

        public PartiallyLoadableAssembly(Type loadableType) => _loadableType = loadableType;

        /// <inheritdoc />
        public override IEnumerable<TypeInfo> DefinedTypes =>
            throw new ReflectionTypeLoadException(
                [_loadableType, null],
                [null, new TypeLoadException("simulated unloadable type")]);
    }

    private sealed class SeamCursorContext : StatesmanOutboxCursorDbContext
    {
        public SeamCursorContext(DbContextOptions<SeamCursorContext> options)
            : base(options)
        {
        }
    }
}
