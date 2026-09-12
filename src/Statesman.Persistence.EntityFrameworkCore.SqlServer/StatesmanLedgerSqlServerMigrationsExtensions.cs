using Microsoft.EntityFrameworkCore;

namespace Statesman.Persistence.EntityFrameworkCore.SqlServer;

/// <summary>Opts a Statesman ledger context into this package's shipped SQL Server migration.</summary>
public static class StatesmanLedgerSqlServerMigrationsExtensions
{
    /// <summary>
    /// Applies this package's shipped SQL Server migration to a <c>StatesmanLedgerDbContext</c> or any
    /// subclass of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it <b>after</b> <c>UseSqlServer</c>, which selects the provider this reads its options
    /// from: <c>options.UseSqlServer(connectionString).UseStatesmanLedgerSqlServerMigrations()</c>. It
    /// sets the migrations assembly, sets the Statesman history table, and installs the
    /// subclass-tolerant migrations assembly a shipped migration needs to reach a derived context at
    /// all.
    /// </para>
    /// <para>
    /// SQL Server emits a 900-byte clustered-index-key warning when creating <c>StatesmanHeads</c> and
    /// <c>StatesmanRecords</c>: the shipped composite key's declared maximum exceeds that limit, which
    /// is a documented and boundary-tested limitation rather than a defect. See
    /// <c>docs/providers/entity-framework-core-migrations.md</c>. <c>EnsureCreated</c> emits the same
    /// warning today.
    /// </para>
    /// </remarks>
    /// <param name="builder">The options builder, with <c>UseSqlServer</c> already called.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No relational provider has been selected yet.</exception>
    public static DbContextOptionsBuilder UseStatesmanLedgerSqlServerMigrations(
        this DbContextOptionsBuilder builder) =>
        builder.UseStatesmanLedgerMigrations(
            typeof(StatesmanLedgerSqlServerMigrationsExtensions).Assembly);
}
