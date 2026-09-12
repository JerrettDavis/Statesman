using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Statesman.Persistence.EntityFrameworkCore.Sqlite;

/// <summary>
/// The context <c>dotnet ef</c> builds this package's migration against.
/// </summary>
/// <remarks>
/// <para>
/// Typed to the <b>base</b> <c>StatesmanLedgerDbContext</c>, deliberately: that is what stamps
/// <c>[DbContext(typeof(StatesmanLedgerDbContext))]</c> onto the generated migration and snapshot, and
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
public sealed class StatesmanLedgerSqliteDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<StatesmanLedgerDbContext>
{
    /// <inheritdoc />
    public StatesmanLedgerDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<StatesmanLedgerDbContext>();
        builder.UseSqlite("Data Source=statesman-design-time.db");
        builder.UseStatesmanLedgerSqliteMigrations();
        return new StatesmanLedgerDbContext(builder.Options);
    }
}
