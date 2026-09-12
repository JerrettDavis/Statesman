using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Statesman.Outbox.EntityFrameworkCore.PostgreSql;

/// <summary>
/// The context <c>dotnet ef</c> builds this package's migration against.
/// </summary>
/// <remarks>
/// <para>
/// Typed to the <b>base</b> <c>StatesmanOutboxCursorDbContext</c>, deliberately: that is what stamps
/// <c>[DbContext(typeof(StatesmanOutboxCursorDbContext))]</c> onto the generated migration and snapshot, and
/// a migration stamped for a subclass would serve nobody.
/// </para>
/// <para>
/// The connection string is never opened. <c>dotnet ef</c> needs a provider selected so it knows which
/// type mappings to generate, not a reachable database, and this package's migration is generated and
/// checked entirely offline.
/// </para>
/// <para>
/// It goes through the same shipped extension a consumer calls, so the authoring path and the
/// consuming path cannot drift apart.
/// </para>
/// </remarks>
public sealed class StatesmanOutboxPostgreSqlDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<StatesmanOutboxCursorDbContext>
{
    /// <inheritdoc />
    public StatesmanOutboxCursorDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<StatesmanOutboxCursorDbContext>();
        builder.UseNpgsql("Host=statesman-design-time;Database=statesman-design-time");
        builder.UseStatesmanOutboxPostgreSqlMigrations();
        return new StatesmanOutboxCursorDbContext(builder.Options);
    }
}
