using System.Reflection;
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

    [Fact]
    public void Discovery_survives_one_unloadable_type_in_the_migrations_assembly()
    {
        // Fix (Task 1, review round 1): StatesmanMigrationsAssembly must degrade to the loadable types
        // when Assembly.DefinedTypes throws ReflectionTypeLoadException, the same way the stock
        // MigrationsAssembly it replaces does through Assembly.GetConstructibleTypes(). Without the
        // fallback, one type this process cannot load anywhere in the migrations assembly loses
        // discovery for every migration in it -- a worse failure than the one this whole seam exists to
        // fix.
        using var database = EntityFrameworkTestDatabase.Create();
        var fakeAssembly = new PartiallyLoadableAssembly(typeof(LedgerProbeMigration));
        using var context = new SeamContext(database.Options<SeamContext>(
            builder => builder.UseStatesmanLedgerMigrations(fakeAssembly)));

        var assembly = context.GetService<IMigrationsAssembly>();

        Assert.Contains(ProbeMigrationId, assembly.Migrations.Keys);
    }

    /// <summary>
    /// An assembly whose <see cref="DefinedTypes"/> reproduces a partial load failure: one type failed
    /// to load, but <see cref="LedgerProbeMigration"/> -- passed in as the one type that DID load --
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

    [Fact]
    public async Task A_database_created_by_EnsureCreated_is_baselined_rather_than_rebuilt()
    {
        // EnsureCreated writes no migration-history row, so a consumer adopting a shipped migration
        // gets "table already exists" on the first update. What they need is a baseline HISTORY ROW,
        // not a baseline migration -- nothing extra is generated. Spec Phase 14 item 3.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamContext(database.Options<SeamContext>(
            builder => builder.UseStatesmanLedgerMigrations(typeof(LedgerProbeMigration).Assembly)));

        await context.Database.EnsureCreatedAsync();
        await context.StatesmanSequences.AddAsync(
            new StatesmanLedgerSequence { Name = "global-position", Value = 42 });
        await context.SaveChangesAsync();

        // Without the baseline this is the failure a consumer hits in production.
        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.MigrateAsync());

        Assert.True(await StatesmanLedgerMigrations.BaselineAsync(context));

        // After it: the migration is recorded as applied, Migrate() is a clean no-op, and the data
        // written before the baseline is still there -- which is the whole point of baselining rather
        // than dropping and re-migrating.
        Assert.Contains(ProbeMigrationId, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        StatesmanLedgerSequence? sequence = await context.StatesmanSequences.SingleOrDefaultAsync();
        Assert.Equal(42L, sequence!.Value);

        // Idempotent: an application that calls it unconditionally at startup is supported, not a bug.
        Assert.False(await StatesmanLedgerMigrations.BaselineAsync(context));
    }

    [Fact]
    public async Task Baselining_a_context_with_no_shipped_migration_throws()
    {
        // The one case that must NOT be a quiet no-op. No migration discovered means the options are
        // misconfigured -- the wrong assembly, or UseStatesmanLedgerMigrations never called -- and
        // writing an empty history table would bake the misconfiguration in and report success.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamContext(database.Options<SeamContext>());

        await context.Database.EnsureCreatedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StatesmanLedgerMigrations.BaselineAsync(context));
    }

    private sealed class SeamContext : StatesmanLedgerDbContext
    {
        public SeamContext(DbContextOptions<SeamContext> options)
            : base(options)
        {
        }
    }
}
