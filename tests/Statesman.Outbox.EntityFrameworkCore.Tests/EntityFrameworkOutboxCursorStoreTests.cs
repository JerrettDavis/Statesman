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
        // position (30) than the store is writing (20). AnyAsync (in WriteAsync's own catch block)
        // runs before the rival row exists, so it is not what resolves this case; the retry's own
        // conditional ExecuteUpdateAsync is what matches zero rows -- 30 is not less than 20 -- and
        // must leave 30 in place rather than throwing or overwriting it.
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        var factory = new FirstInsertFailsContextFactory(_factory, outboxId, rivalPosition: 30);
        IOutboxCursorStore store = new EntityFrameworkOutboxCursorStore<CursorContext>(factory);

        await store.WriteAsync(outboxId, new StateChangeCursor(20));

        Assert.Equal(30, (await CreateStore().ReadAsync(outboxId))!.Value.Position);
        Assert.True(factory.InsertFailed, "the test did not actually exercise the first-insert retry path.");
    }

    [Fact]
    public async Task A_first_insert_failure_that_is_not_a_race_is_rethrown_rather_than_swallowed()
    {
        // Minor 5 (final review): before this fix, a DbUpdateException at the first insert was always
        // treated as a race and swallowed once the retry's conditional update matched zero rows -- but
        // zero rows is also exactly what "the row still does not exist" looks like. This double throws
        // WITHOUT landing any rival row, so there is genuinely no race: WriteAsync must rethrow the
        // original DbUpdateException rather than return as though the write succeeded, and the cursor
        // must remain unwritten.
        string outboxId = $"outbox-{Guid.NewGuid():N}";
        var factory = new FirstInsertFailsWithoutRaceContextFactory(_factory);
        IOutboxCursorStore store = new EntityFrameworkOutboxCursorStore<CursorContext>(factory);

        DbUpdateException thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.WriteAsync(outboxId, new StateChangeCursor(20)).AsTask());

        Assert.Equal("Simulated non-race insert failure.", thrown.Message);
        Assert.Null(await CreateStore().ReadAsync(outboxId));
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
        private readonly string _failureMessage;

        public CursorContext(
            DbContextOptions<CursorContext> options,
            Func<CancellationToken, Task>? injectRaceBeforeThrow = null,
            string failureMessage = "Simulated first-insert race loss.")
            : base(options)
        {
            _injectRaceBeforeThrow = injectRaceBeforeThrow;
            _failureMessage = failureMessage;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_injectRaceBeforeThrow is not null)
            {
                // Land the rival row through a separate connection first, so the exception thrown
                // below reflects a database that genuinely already holds a colliding row -- not a
                // scripted failure against an empty table. (Not every caller lands a rival row --
                // FirstInsertFailsWithoutRaceContextFactory below passes a no-op, for the non-race case.)
                await _injectRaceBeforeThrow(cancellationToken);
                throw new DbUpdateException(_failureMessage);
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

    /// <summary>
    /// Wraps a working factory and makes the FIRST context's <c>SaveChangesAsync</c> throw
    /// <see cref="DbUpdateException"/> with NO rival row landed anywhere -- a non-race insert failure
    /// (a constraint or conversion error, say), the case
    /// <see cref="EntityFrameworkOutboxCursorStore{TContext}"/> must rethrow rather than swallow. Every
    /// later context this factory hands out works normally.
    /// </summary>
    private sealed class FirstInsertFailsWithoutRaceContextFactory : IDbContextFactory<CursorContext>
    {
        private readonly CursorContextFactory _inner;
        private int _callCount;

        public FirstInsertFailsWithoutRaceContextFactory(CursorContextFactory inner) => _inner = inner;

        public CursorContext CreateDbContext() =>
            Interlocked.Increment(ref _callCount) == 1
                ? new CursorContext(_inner.Options, NoRivalRowThenThrowAsync, "Simulated non-race insert failure.")
                : _inner.CreateDbContext();

        public Task<CursorContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        private static Task NoRivalRowThenThrowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
