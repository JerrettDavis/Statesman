using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-feed conformance suite, run against the Entity Framework Core provider over a
/// file-backed SQLite database.
/// </summary>
/// <remarks>
/// A file, not <c>Data Source=:memory:</c>: the in-flight test needs two concurrent transactions,
/// and the shared-single-connection fixture the other Entity Framework Core tests use cannot host
/// them. With a file, the second writer blocks on <c>BEGIN IMMEDIATE</c> until the first commits,
/// which is the serialization point under test.
/// </remarks>
public sealed class EntityFrameworkChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    // AppendAsync's only clock read is OccurredAt, inside the open serializable transaction and
    // after sequence.Value++ has allocated the position.
    protected override int PauseCallIndex => 1;

    protected override async ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock)
    {
        string file = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var options = new DbContextOptionsBuilder<ConformanceContext>()
            .UseSqlite($"Data Source={file}")
            .Options;
        var factory = new ConformanceContextFactory(options);
        await using (ConformanceContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<ConformanceContext>("database", factory, clock);
        return new ConformanceStore
        {
            Store = store,
            Feed = store,
            Cleanup = () =>
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                return ValueTask.CompletedTask;
            },
        };
    }

    private sealed class ConformanceContext : StatesmanLedgerDbContext
    {
        public ConformanceContext(DbContextOptions<ConformanceContext> options)
            : base(options)
        {
        }
    }

    private sealed class ConformanceContextFactory : IDbContextFactory<ConformanceContext>
    {
        private readonly DbContextOptions<ConformanceContext> _options;

        public ConformanceContextFactory(DbContextOptions<ConformanceContext> options)
        {
            _options = options;
        }

        public ConformanceContext CreateDbContext() => new(_options);

        public Task<ConformanceContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
