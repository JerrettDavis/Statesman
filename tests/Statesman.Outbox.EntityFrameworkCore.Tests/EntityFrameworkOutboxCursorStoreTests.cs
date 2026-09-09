using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Statesman.Outbox.EntityFrameworkCore;
using Statesman.Outbox.Tests;

namespace Statesman.Outbox.EntityFrameworkCore.Tests;

/// <summary>
/// The shared <see cref="IOutboxCursorStore"/> conformance suite, run against the Entity Framework Core
/// cursor store over a file-backed SQLite database. Every test gets a fresh instance of this class and
/// therefore its own database file; <c>CreateStore</c> hands out a fresh store whose contexts each open
/// their own connection to that same file, which is what makes the suite's "read it back through a
/// fresh handle" assertions meaningful.
/// </summary>
/// <remarks>
/// A file, not one shared open <c>:memory:</c> connection: this suite's concurrent tests
/// (<c>Concurrent_writers_on_separate_instances_converge_on_the_maximum</c>,
/// <c>Reads_concurrent_with_writes_do_not_throw</c>) issue commands from multiple contexts at once, and
/// Microsoft.Data.Sqlite does not support two concurrent commands on one connection object -- the same
/// reason <c>EntityFrameworkChangeFeedTests.cs</c>'s own concurrent test uses a file rather than the
/// shared in-memory connection every other test in that file uses.
/// </remarks>
public sealed class EntityFrameworkOutboxCursorStoreTests : OutboxCursorStoreConformanceTests, IDisposable
{
    private readonly string _databaseFile;
    private readonly CursorContextFactory _factory;

    public EntityFrameworkOutboxCursorStoreTests()
    {
        _databaseFile = Path.Combine(
            Path.GetTempPath(),
            "statesman-outbox-cursor-ef-tests",
            Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(_databaseFile)!);
        DbContextOptions<CursorContext> options = new DbContextOptionsBuilder<CursorContext>()
            .UseSqlite($"Data Source={_databaseFile}")
            .Options;
        _factory = new CursorContextFactory(options);
        using CursorContext context = _factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    protected override IOutboxCursorStore CreateStore() =>
        new EntityFrameworkOutboxCursorStore<CursorContext>(_factory);

    [Fact]
    public async Task Writes_arriving_in_descending_order_still_leave_the_maximum_stored()
    {
        // The break-the-mechanism test. Every conformance write that races is ascending or unordered,
        // and a last-write-wins implementation can pass those under a favourable interleaving.
        // Descending and sequential, it cannot: deleting the "WHERE Position < @new" predicate makes
        // this read 1 rather than 64, deterministically, with no concurrency involved at all.
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        IOutboxCursorStore store = CreateStore();

        for (int position = 64; position >= 1; position--)
        {
            await store.WriteAsync(outboxId, new StateChangeCursor(position));
        }

        Assert.Equal(64, (await CreateStore().ReadAsync(outboxId))!.Value.Position);
    }

    [Fact]
    public async Task Two_writers_racing_the_first_insert_both_succeed_and_the_maximum_wins()
    {
        // The DbUpdateException retry path: only one of the two inserts can create the row, and the
        // loser must fall back to the conditional update rather than surfacing a primary-key
        // violation. Deterministic decision logic driven by a factory that fails the first insert,
        // following the two-phase technique in
        // tests/Statesman.EntityFrameworkCore.Tests/EntityFrameworkLeaseProviderTests.cs:183-235 --
        // no SQLite race is reproduced, which is the point.
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        var factory = new FirstInsertFailsContextFactory(_factory);
        IOutboxCursorStore store = new EntityFrameworkOutboxCursorStore<CursorContext>(factory);

        // Seed the row behind the store's back, so the store's own insert is the one that loses.
        await CreateStore().WriteAsync(outboxId, new StateChangeCursor(10));

        await store.WriteAsync(outboxId, new StateChangeCursor(20));

        Assert.Equal(20, (await CreateStore().ReadAsync(outboxId))!.Value.Position);
        Assert.True(factory.InsertFailed, "the test did not actually exercise the first-insert retry path.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
        {
            File.Delete(_databaseFile);
        }
    }

    private sealed class CursorContext : StatesmanOutboxCursorDbContext
    {
        private readonly Exception? _throwOnSaveChanges;

        public CursorContext(DbContextOptions<CursorContext> options, Exception? throwOnSaveChanges = null)
            : base(options)
        {
            _throwOnSaveChanges = throwOnSaveChanges;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_throwOnSaveChanges is not null)
            {
                throw _throwOnSaveChanges;
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class CursorContextFactory : IDbContextFactory<CursorContext>
    {
        private readonly DbContextOptions<CursorContext> _options;

        public CursorContextFactory(DbContextOptions<CursorContext> options) => _options = options;

        public DbContextOptions<CursorContext> Options => _options;

        public CursorContext CreateDbContext() => new(_options);

        public Task<CursorContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Wraps a working factory and makes the FIRST context's <c>SaveChangesAsync</c> throw
    /// <see cref="DbUpdateException"/>, as a lost first-insert race does. Every later context works,
    /// so the store's retry sees a database that already holds the row.
    /// </summary>
    private sealed class FirstInsertFailsContextFactory : IDbContextFactory<CursorContext>
    {
        private readonly CursorContextFactory _inner;
        private int _callCount;

        public FirstInsertFailsContextFactory(CursorContextFactory inner) => _inner = inner;

        public bool InsertFailed => Volatile.Read(ref _callCount) > 0;

        public CursorContext CreateDbContext() =>
            Interlocked.Increment(ref _callCount) == 1
                ? new CursorContext(_inner.Options, new DbUpdateException("Simulated first-insert race loss."))
                : _inner.CreateDbContext();

        public Task<CursorContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
