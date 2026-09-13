using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task The_shipped_history_table_is_used_instead_of_the_default()
    {
        // Mirrors the ledger seam test of the same name (final review, Minor 4): __EFMigrationsHistory
        // is shared per database, so a consumer running their own migrations beside a shipped outbox
        // set needs the two sets kept in separate tables or they interleave.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamCursorContext(database.Options<SeamCursorContext>(
            builder => builder.UseStatesmanOutboxMigrations(typeof(OutboxProbeMigration).Assembly)));

        await context.Database.MigrateAsync();

        // Quoted: PostgreSQL folds an unquoted identifier to lower case.
        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"{StatesmanOutboxMigrations.HistoryTableName}\" WHERE 1 = 0");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE 1 = 0"));
    }

    [Fact]
    public async Task Baselining_a_context_with_no_shipped_migration_throws()
    {
        // Mirrors the ledger seam test of the same name (final review, Minor 4): no migration
        // discovered means the options are misconfigured, and writing an empty history table would
        // bake the misconfiguration in and report success.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamCursorContext(database.Options<SeamCursorContext>());

        await context.Database.EnsureCreatedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StatesmanOutboxMigrations.BaselineAsync(context));
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

    [Fact]
    public async Task A_database_created_by_EnsureCreated_is_baselined_rather_than_rebuilt()
    {
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new SeamCursorContext(database.Options<SeamCursorContext>(
            builder => builder.UseStatesmanOutboxMigrations(typeof(OutboxProbeMigration).Assembly)));

        await context.Database.EnsureCreatedAsync();
        await context.StatesmanOutboxCursors.AddAsync(
            new StatesmanOutboxCursorEntity { OutboxId = "orders", Position = 42 });
        await context.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.MigrateAsync());

        Assert.True(await StatesmanOutboxMigrations.BaselineAsync(context));

        Assert.Contains(ProbeMigrationId, await context.Database.GetAppliedMigrationsAsync());
        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();
        StatesmanOutboxCursorEntity? cursor = await context.StatesmanOutboxCursors.SingleOrDefaultAsync();
        Assert.Equal(42L, cursor!.Position);
        Assert.False(await StatesmanOutboxMigrations.BaselineAsync(context));
    }

    // --- ROADMAP 0.3 Phase 15, Task 2 -------------------------------------------------------

    /// <summary>
    /// A context nothing else in this assembly constructs, so the three probe migrations below are
    /// invisible to every other seam test: DeclaredForThisContext only matches a running context
    /// that IS an OutboxMinorsProbeContext, and SeamCursorContext and StatesmanOutboxCursorDbContext
    /// are not.
    /// </summary>
    private sealed class OutboxMinorsProbeContext : StatesmanOutboxCursorDbContext
    {
        public OutboxMinorsProbeContext(DbContextOptions<OutboxMinorsProbeContext> options)
            : base(options)
        {
        }
    }

    /// <summary>A context outside the StatesmanOutboxCursorDbContext hierarchy entirely.</summary>
    private sealed class UnrelatedOutboxContext : DbContext
    {
        public UnrelatedOutboxContext(DbContextOptions<UnrelatedOutboxContext> options)
            : base(options)
        {
        }
    }

    internal const string TwoLevelAttributeMigrationId = "20260102000000_OutboxTwoLevelAttribute";

    /// <summary>
    /// Carries [DbContext] and is abstract, so ConstructibleTypes() filters it out; its only job is
    /// to put a SECOND DbContextAttribute in the derived migration's inheritance chain.
    /// </summary>
    [DbContext(typeof(OutboxMinorsProbeContext))]
    public abstract class OutboxAttributedBaseMigration : Migration
    {
    }

    /// <summary>Public because Entity Framework Core activates a migration by its public constructor.</summary>
    [DbContext(typeof(OutboxMinorsProbeContext))]
    [Migration(TwoLevelAttributeMigrationId)]
    public sealed class OutboxTwoLevelAttributeMigration : OutboxAttributedBaseMigration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            ArgumentNullException.ThrowIfNull(migrationBuilder);
    }

    /// <summary>
    /// Targets the probe context and derives from Migration, but carries no [Migration] attribute.
    /// Entity Framework Core's own MigrationsAssembly logs MigrationAttributeMissingWarning for
    /// exactly this shape; the replacement used to skip it in silence.
    /// </summary>
    [DbContext(typeof(OutboxMinorsProbeContext))]
    public sealed class OutboxMigrationWithoutAttribute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            ArgumentNullException.ThrowIfNull(migrationBuilder);
    }

    [Fact]
    public void Discovery_tolerates_a_DbContext_attribute_at_two_levels_of_a_migration_hierarchy()
    {
        // Phase 14 deferred Minor (b). DbContextAttribute is AllowMultiple=True and Inherited=True
        // (measured), so the single-attribute GetCustomAttribute<T>() overload -- which defaults to
        // inherit: true -- finds two and throws AmbiguousMatchException. Entity Framework Core's own
        // GetDbContextType walks the hierarchy one level at a time with inherit: false and cannot
        // throw, so before this fix the replacement was STRICTLY LESS tolerant on this one input than
        // the base class it replaces.
        using var database = EntityFrameworkTestDatabase.Create();
        using var context = new OutboxMinorsProbeContext(database.Options<OutboxMinorsProbeContext>(
            builder => builder.UseStatesmanOutboxMigrations(typeof(OutboxTwoLevelAttributeMigration).Assembly)));

        var assembly = context.GetService<IMigrationsAssembly>();

        Assert.Contains(TwoLevelAttributeMigrationId, assembly.Migrations.Keys);
    }

    [Fact]
    public void An_unrelated_context_discovers_no_migration_from_this_assembly()
    {
        // Phase 14 deferred Minor (d). The isolation half of DeclaredForThisContext was asserted only
        // in a source comment. BREAK-THE-MECHANISM LEVER, and it must be this one: replace
        // `declared.IsAssignableFrom(_contextType)` with `true` in StatesmanOutboxMigrationsAssembly
        // and this test goes red. Phase 14's levers -- removing ReplaceService, reverting
        // IsAssignableFrom to == -- do NOT discriminate it.
        using var database = EntityFrameworkTestDatabase.Create();
        using var context = new UnrelatedOutboxContext(database.Options<UnrelatedOutboxContext>(
            builder => builder.UseStatesmanOutboxMigrations(typeof(OutboxProbeMigration).Assembly)));

        var assembly = context.GetService<IMigrationsAssembly>();

        Assert.Empty(assembly.Migrations);
    }

    [Fact]
    public void A_context_targeted_migration_without_a_Migration_attribute_is_logged_not_silently_skipped()
    {
        // Phase 14 deferred Minors (c) and (e), which pair: a diagnostic nothing in the harness can
        // observe is not a testable claim. The sink is scoped to the migrations category at the one
        // test that needs it, matching EntityFrameworkChangeFeedTests.cs:136, never wired globally.
        var lines = new List<string>();
        using var database = EntityFrameworkTestDatabase.Create();
        using var context = new OutboxMinorsProbeContext(database.Options<OutboxMinorsProbeContext>(
            builder => builder
                .UseStatesmanOutboxMigrations(typeof(OutboxMigrationWithoutAttribute).Assembly)
                .LogTo(lines.Add, [DbLoggerCategory.Migrations.Name])));

        var assembly = context.GetService<IMigrationsAssembly>();

        // Still skipped -- the fix is that it is no longer skipped in SILENCE.
        Assert.DoesNotContain(
            nameof(OutboxMigrationWithoutAttribute),
            assembly.Migrations.Values.Select(value => value.Name));
        Assert.Contains(
            lines,
            line => line.Contains(nameof(RelationalEventId.MigrationAttributeMissingWarning), StringComparison.Ordinal)
                && line.Contains(nameof(OutboxMigrationWithoutAttribute), StringComparison.Ordinal));
    }

    [Fact]
    public void A_context_targeted_migration_without_a_Migration_attribute_is_logged_exactly_once_under_repeated_enumeration()
    {
        // Phase 15 final review follow-on (Task 9). Assert.Contains above proves the warning fires at
        // all, but it would still pass under a once-per-enumeration regression: a replacement assembly
        // that logged the warning on every access to Migrations would still contain the line at least
        // once. Enumerating three times through the SAME assembly instance and counting occurrences is
        // the only way to discriminate "logged once" from "logged on every access".
        var lines = new List<string>();
        using var database = EntityFrameworkTestDatabase.Create();
        using var context = new OutboxMinorsProbeContext(database.Options<OutboxMinorsProbeContext>(
            builder => builder
                .UseStatesmanOutboxMigrations(typeof(OutboxMigrationWithoutAttribute).Assembly)
                .LogTo(lines.Add, [DbLoggerCategory.Migrations.Name])));

        var assembly = context.GetService<IMigrationsAssembly>();

        _ = assembly.Migrations;
        _ = assembly.Migrations;
        _ = assembly.Migrations;

        Assert.Contains(
            lines,
            line => line.Contains(nameof(RelationalEventId.MigrationAttributeMissingWarning), StringComparison.Ordinal)
                && line.Contains(nameof(OutboxMigrationWithoutAttribute), StringComparison.Ordinal));
        Assert.Equal(
            1,
            lines.Count(line =>
                line.Contains(nameof(RelationalEventId.MigrationAttributeMissingWarning), StringComparison.Ordinal)));
    }

    private sealed class SeamCursorContext : StatesmanOutboxCursorDbContext
    {
        public SeamCursorContext(DbContextOptions<SeamCursorContext> options)
            : base(options)
        {
        }
    }

    /// <summary>
    /// Pins the baseline transaction (final review, Important 1; addendum decision 34) for the outbox
    /// twin of <c>EntityFrameworkMigrationsSeamTests.Baselining_is_atomic_when_a_history_insert_fails_partway_through</c>:
    /// two probe migrations are discovered, the second with an id longer than SQL Server's
    /// <c>__StatesmanOutboxMigrationsHistory.MigrationId</c> column, so its history INSERT fails after
    /// the first migration's INSERT has already run inside the same <c>BaselineAttemptAsync</c>
    /// transaction.
    /// </summary>
    [Fact]
    public async Task Baselining_is_atomic_when_a_history_insert_fails_partway_through()
    {
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.SelectedEngine == EntityFrameworkTestEngine.SqlServer,
            EntityFrameworkTestDatabase.SqlServerSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new OutboxBaselineTransactionProbeContext(
            database.Options<OutboxBaselineTransactionProbeContext>(
                builder => builder.UseStatesmanOutboxMigrations(
                    typeof(OutboxTransactionProbeMigrationOne).Assembly)));

        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.Contains(FirstTransactionProbeMigrationId, assembly.Migrations.Keys);
        Assert.Contains(OverlongTransactionProbeMigrationId, assembly.Migrations.Keys);

        await Assert.ThrowsAnyAsync<Exception>(() => StatesmanOutboxMigrations.BaselineAsync(context));

        Assert.False(await HistoryTableExistsAsync(context));
    }

    private static async Task<bool> HistoryTableExistsAsync(DbContext context)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @name";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = StatesmanOutboxMigrations.HistoryTableName;
        command.Parameters.Add(parameter);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    private const string FirstTransactionProbeMigrationId =
        "20260101000002_OutboxTransactionProbeOne";

    // 184 characters: longer than SQL Server's MigrationId column, so its history INSERT fails.
    private const string OverlongTransactionProbeMigrationId =
        "20260101000002_OutboxTransactionProbeTooLongForSqlServerMigrationIdColumnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

    /// <summary>The first probe migration this test discovers, applied before the overlong one.</summary>
    [DbContext(typeof(OutboxBaselineTransactionProbeContext))]
    [Migration(FirstTransactionProbeMigrationId)]
    public sealed class OutboxTransactionProbeMigrationOne : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            ArgumentNullException.ThrowIfNull(migrationBuilder);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            ArgumentNullException.ThrowIfNull(migrationBuilder);
    }

    /// <summary>The second probe migration: its id is what makes the history INSERT fail.</summary>
    [DbContext(typeof(OutboxBaselineTransactionProbeContext))]
    [Migration(OverlongTransactionProbeMigrationId)]
    public sealed class OutboxTransactionProbeMigrationTwo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            ArgumentNullException.ThrowIfNull(migrationBuilder);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            ArgumentNullException.ThrowIfNull(migrationBuilder);
    }

    // A dedicated subclass, never used outside this test, so these two probe migrations are never
    // discovered by any other seam test in this assembly.
    private sealed class OutboxBaselineTransactionProbeContext : StatesmanOutboxCursorDbContext
    {
        public OutboxBaselineTransactionProbeContext(
            DbContextOptions<OutboxBaselineTransactionProbeContext> options)
            : base(options)
        {
        }
    }
}
