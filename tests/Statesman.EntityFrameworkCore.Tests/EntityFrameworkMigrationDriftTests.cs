using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Statesman.Persistence.EntityFrameworkCore.PostgreSql;
using Statesman.Persistence.EntityFrameworkCore.SqlServer;
using Statesman.Persistence.EntityFrameworkCore.Sqlite;

namespace Statesman.EntityFrameworkCore.Tests;

/// <summary>
/// Fails when <see cref="StatesmanLedgerDbContext"/>'s model no longer matches a shipped migration.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the first edit to <c>OnModelCreating</c> silently ships a schema the migrations do
/// not produce, and the consumer finds out at a query.
/// </para>
/// <para>
/// <b>No database is contacted.</b> <c>HasPendingModelChanges()</c> is a design-time diff of the
/// context's model against the shipped <c>IMigrationsAssembly.ModelSnapshot</c>; the connection
/// strings below are deliberately unreachable, and if one of these tests ever needs a live engine,
/// something has changed that must be understood rather than worked around.
/// </para>
/// <para>
/// The first two assertions in each case are load-bearing, not decoration. A missing snapshot makes
/// <c>HasPendingModelChanges()</c> return <see langword="true"/> for a reason that has nothing to do
/// with drift, and an empty migration set would let the third assertion pass vacuously -- so a gate
/// without them can fail for the wrong reason and pass for the wrong reason.
/// </para>
/// </remarks>
public sealed class EntityFrameworkMigrationDriftTests
{
    [Fact]
    public void The_sqlite_migration_matches_the_ledger_model() =>
        AssertNoDrift(builder => builder
            .UseSqlite("Data Source=statesman-drift-check.db")
            .UseStatesmanLedgerSqliteMigrations());

    [Fact]
    public void The_sql_server_migration_matches_the_ledger_model() =>
        AssertNoDrift(builder => builder
            .UseSqlServer("Server=statesman-drift-check;Database=statesman-drift-check")
            .UseStatesmanLedgerSqlServerMigrations());

    [Fact]
    public void The_postgresql_migration_matches_the_ledger_model() =>
        AssertNoDrift(builder => builder
            .UseNpgsql("Host=statesman-drift-check;Database=statesman-drift-check")
            .UseStatesmanLedgerPostgreSqlMigrations());

    private static void AssertNoDrift(Action<DbContextOptionsBuilder<StatesmanLedgerDbContext>> configure)
    {
        var builder = new DbContextOptionsBuilder<StatesmanLedgerDbContext>();
        configure(builder);
        using var context = new StatesmanLedgerDbContext(builder.Options);

        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.NotEmpty(assembly.Migrations);
        Assert.NotNull(assembly.ModelSnapshot);
        Assert.False(
            context.Database.HasPendingModelChanges(),
            "StatesmanLedgerDbContext's model has drifted from this package's shipped migration. "
            + "Add a migration to ALL SIX packages -- three engines times two contexts -- with "
            + "`dotnet tool restore && dotnet ef migrations add <Name> --project <package> --output-dir Migrations`. "
            + "Never edit or remove a shipped migration: its id is a permanent public contract.");
    }
}
