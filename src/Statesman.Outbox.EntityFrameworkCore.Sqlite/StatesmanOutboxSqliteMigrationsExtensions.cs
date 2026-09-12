using Microsoft.EntityFrameworkCore;

namespace Statesman.Outbox.EntityFrameworkCore.Sqlite;

/// <summary>Opts a Statesman outbox cursor context into this package's shipped SQLite migration.</summary>
public static class StatesmanOutboxSqliteMigrationsExtensions
{
    /// <summary>
    /// Applies this package's shipped SQLite migration to a <c>StatesmanOutboxCursorDbContext</c> or any
    /// subclass of it.
    /// </summary>
    /// <remarks>
    /// Call it <b>after</b> <c>UseSqlite</c>, which selects the provider this reads its options from:
    /// <c>options.UseSqlite(connectionString).UseStatesmanOutboxSqliteMigrations()</c>. It sets the
    /// migrations assembly, sets the Statesman history table, and installs the subclass-tolerant
    /// migrations assembly a shipped migration needs to reach a derived context at all.
    /// </remarks>
    /// <param name="builder">The options builder, with <c>UseSqlite</c> already called.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No relational provider has been selected yet.</exception>
    public static DbContextOptionsBuilder UseStatesmanOutboxSqliteMigrations(
        this DbContextOptionsBuilder builder) =>
        builder.UseStatesmanOutboxMigrations(
            typeof(StatesmanOutboxSqliteMigrationsExtensions).Assembly);
}
