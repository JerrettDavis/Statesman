using Microsoft.EntityFrameworkCore;

namespace Statesman.Outbox.EntityFrameworkCore.PostgreSql;

/// <summary>Opts a Statesman outbox cursor context into this package's shipped PostgreSQL migration.</summary>
public static class StatesmanOutboxPostgreSqlMigrationsExtensions
{
    /// <summary>
    /// Applies this package's shipped PostgreSQL migration to a <c>StatesmanOutboxCursorDbContext</c> or any
    /// subclass of it.
    /// </summary>
    /// <remarks>
    /// Call it <b>after</b> <c>UseNpgsql</c>, which selects the provider this reads its options from:
    /// <c>options.UseNpgsql(connectionString).UseStatesmanOutboxPostgreSqlMigrations()</c>. It sets the
    /// migrations assembly, sets the Statesman history table, and installs the subclass-tolerant
    /// migrations assembly a shipped migration needs to reach a derived context at all.
    /// </remarks>
    /// <param name="builder">The options builder, with <c>UseNpgsql</c> already called.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No relational provider has been selected yet.</exception>
    public static DbContextOptionsBuilder UseStatesmanOutboxPostgreSqlMigrations(
        this DbContextOptionsBuilder builder) =>
        builder.UseStatesmanOutboxMigrations(
            typeof(StatesmanOutboxPostgreSqlMigrationsExtensions).Assembly);
}
