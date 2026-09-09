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
    public async Task Two_writers_racing_the_first_insert_both_succeed_and_the_higher_position_wins()
    {
        // The DbUpdateException retry path, case 1: the store's own insert loses the race to a rival
        // that lands a LOWER position (10) than the store is writing (20). No row exists before this
        // test runs -- the rival is created by the injected failure itself, mid-SaveChangesAsync, so
        // the collision is genuine: the store's insert really does lose to a row that did not exist
        // when WriteAsync started. The retry's conditional update must then raise 10 to 20.
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        var factory = new FirstInsertFailsContextFactory(_factory, outboxId, rivalPosition: 10);
        IOutboxCursorStore store = new EntityFrameworkOutboxCursorStore<CursorContext>(factory);

        await store.WriteAsync(outboxId, new StateChangeCursor(20));

        Assert.Equal(20, (await CreateStore().ReadAsync(outboxId))!.Value.Position);
        Assert.True(factory.InsertFailed, "the test did not actually exercise the first-insert retry path.");
    }

    [Fact]
    public async Task Two_writers_racing_the_first_insert_both_succeed_and_a_higher_rival_is_not_overwritten()
    {
        // The DbUpdateException retry path, case 2: the rival that wins the race lands a HIGHER
        // position (30) than the store is writing (20). The retry's conditional update then matches
        // zero rows -- 30 is not less than 20 -- and WriteAsync's own AnyAsync no-op branch, not the
        // ExecuteUpdateAsync path, is what must leave 30 in place rather than throwing or overwriting it.
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        var factory = new FirstInsertFailsContextFactory(_factory, outboxId, rivalPosition: 30);
        IOutboxCursorStore store = new EntityFrameworkOutboxCursorStore<CursorContext>(factory);

        await store.WriteAsync(outboxId, new StateChangeCursor(20));

        Assert.Equal(30, (await CreateStore().ReadAsync(outboxId))!.Value.Position);
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
        private readonly Func<CancellationToken, Task>? _injectRaceBeforeThrow;

        public CursorContext(DbContextOptions<CursorContext> options, Func<CancellationToken, Task>? injectRaceBeforeThrow = null)
            : base(options)
        {
            _injectRaceBeforeThrow = injectRaceBeforeThrow;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_injectRaceBeforeThrow is not null)
            {
                // Land the rival row through a separate connection first, so the exception thrown
                // below reflects a database that genuinely already holds a colliding row -- not a
                // scripted failure against an empty table.
                await _injectRaceBeforeThrow(cancellationToken);
                throw new DbUpdateException("Simulated first-insert race loss.");
            }

            return await base.SaveChangesAsync(cancellationToken);
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
    /// Wraps a working factory and makes the FIRST context's <c>SaveChangesAsync</c> insert a rival
    /// row for the outbox id given to this factory's constructor (through a separate, real context) and then throw
    /// <see cref="DbUpdateException"/>, reproducing a lost first-insert race: by the time the store's
    /// exception handler runs, the row genuinely exists, landed by someone else, mid-flight. Every
    /// later context this factory hands out works normally, so the store's retry sees that database.
    /// </summary>
    private sealed class FirstInsertFailsContextFactory : IDbContextFactory<CursorContext>
    {
        private readonly CursorContextFactory _inner;
        private readonly string _outboxId;
        private readonly long _rivalPosition;
        private int _callCount;
        private volatile bool _insertFailed;

        public FirstInsertFailsContextFactory(CursorContextFactory inner, string outboxId, long rivalPosition)
        {
            _inner = inner;
            _outboxId = outboxId;
            _rivalPosition = rivalPosition;
        }

        /// <summary>True once the injected throw actually happened, after the rival row landed for real.</summary>
        public bool InsertFailed => _insertFailed;

        public CursorContext CreateDbContext() =>
            Interlocked.Increment(ref _callCount) == 1
                ? new CursorContext(_inner.Options, InjectRivalRowThenThrowAsync)
                : _inner.CreateDbContext();

        public Task<CursorContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        private async Task InjectRivalRowThenThrowAsync(CancellationToken cancellationToken)
        {
            await using CursorContext rival = _inner.CreateDbContext();
            rival.StatesmanOutboxCursors.Add(new StatesmanOutboxCursorEntity
            {
                OutboxId = _outboxId,
                Position = _rivalPosition,
            });
            await rival.SaveChangesAsync(cancellationToken);

            // Only now, with the rival row genuinely committed, did the race actually happen.
            _insertFailed = true;
        }
    }
}
