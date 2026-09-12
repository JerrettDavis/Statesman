using Microsoft.EntityFrameworkCore;

namespace Statesman.Outbox.EntityFrameworkCore.SqlServer;

/// <summary>Opts a Statesman outbox cursor context into this package's shipped SQL Server migration.</summary>
public static class StatesmanOutboxSqlServerMigrationsExtensions
{
    /// <summary>
    /// Applies this package's shipped SQL Server migration to a <c>StatesmanOutboxCursorDbContext</c> or any
    /// subclass of it.
    /// </summary>
    /// <remarks>
    /// Call it <b>after</b> <c>UseSqlServer</c>, which selects the provider this reads its options
    /// from: <c>options.UseSqlServer(connectionString).UseStatesmanOutboxSqlServerMigrations()</c>. It
    /// sets the migrations assembly, sets the Statesman history table, and installs the
    /// subclass-tolerant migrations assembly a shipped migration needs to reach a derived context at
    /// all.
    /// </remarks>
    /// <param name="builder">The options builder, with <c>UseSqlServer</c> already called.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No relational provider has been selected yet.</exception>
    public static DbContextOptionsBuilder UseStatesmanOutboxSqlServerMigrations(
        this DbContextOptionsBuilder builder) =>
        builder.UseStatesmanOutboxMigrations(
            typeof(StatesmanOutboxSqlServerMigrationsExtensions).Assembly);
}
