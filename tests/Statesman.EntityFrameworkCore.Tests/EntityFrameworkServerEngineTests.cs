using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Statesman.TestHelpers;

namespace Statesman.EntityFrameworkCore.Tests;

/// <summary>
/// The behaviours only a live server engine can show, and the reason ROADMAP 0.3 Phase 12 built a
/// SQL Server and PostgreSQL test job before fixing any of them.
/// </summary>
public sealed class EntityFrameworkServerEngineTests
{
    [Fact]
    public async Task Concurrent_appends_to_different_addresses_all_succeed()
    {
        // NOT gated on a server engine, deliberately. On SQLite, IsolationLevel.Serializable opens
        // as BEGIN IMMEDIATE -- a database-wide write lock -- so eight appends for eight different
        // addresses queue and never collide: this test has always passed there and proves nothing
        // there. On a server engine before this task's fix they collide, and the collision is NOT
        // where the pre-Phase-12 spec said it was. Under serializable isolation SQL Server takes a
        // key-range shared lock on the gap that would contain each head's key, and on a sparse index
        // both addresses fall in the SAME gap; each append then needs an insert-intent lock on that
        // range for its INSERT INTO StatesmanHeads, neither can convert while the other holds the
        // shared range lock, and one is chosen as the deadlock victim. PostgreSQL's serializable
        // snapshot isolation instead detects the read/write dependency on the one global-position
        // row and aborts the second appender with 40001, without it ever waiting.
        //
        // So: GREEN on SQLite unchanged, RED on BOTH server engines before the fix. That three-way
        // discrimination is the evidence, and it is recorded per engine in the phase ledger.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(
            EntityFrameworkTestConcurrency.ConcurrentTransactions);
        TestDbContextFactory<ServerEngineContext> factory =
            await database.CreateFactoryAsync<ServerEngineContext>(options => new ServerEngineContext(options));
        await using var store = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory);

        const int writers = 8;
        StateAddress[] addresses = [.. Enumerable.Range(0, writers)
            .Select(index => new StateAddress("app", $"race/{index}", StatePartition.Default))];

        // One gun, eight runners: every append is created before any of them starts, so they contend
        // for the sequence row rather than running one after another.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<StateAppendResult>[] appends = [.. addresses.Select(address => Task.Run(async () =>
        {
            await start.Task;
            return await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
        }))];

        start.SetResult();
        StateAppendResult[] results = await Task.WhenAll(appends);

        Assert.All(results, result => Assert.True(result.Succeeded));

        // Positions come from one row, so eight successful appends must hold eight distinct
        // positions. A "fix" that swallowed the failure and reused a position would satisfy the
        // assertion above and fail this one.
        Assert.Equal(writers, results.Select(result => result.Record!.GlobalPosition).Distinct().Count());
    }

    [Fact]
    public async Task A_SnapshotDistributed_capture_does_not_block_a_concurrent_writer()
    {
        // The reason to change a shipped enum member's behaviour, expressed as an observation.
        // CaptureAsync opens one transaction and reads each address in turn. Under
        // IsolationLevel.Snapshot on SQL Server the capture reads a version snapshot taken at its
        // start, so a writer committing mid-capture is neither blocked by it nor visible to it.
        // Under Serializable -- the shipped mapping before Phase 12 -- SQL Server takes key-range
        // shared locks instead, and a range taken for one address does not necessarily cover
        // another, so a concurrent write to a DIFFERENT address is neither blocked nor excluded:
        // the capture goes on to read that address and returns the POST-write revision. That is a
        // torn multi-address view under the name of a point-in-time snapshot, which is the defect
        // this test pins.
        //
        // The discrimination is on SQL SERVER. PostgreSQL's Serializable is snapshot-based, so this
        // passes there both before and after the change: RepeatableRead is a cheaper way to get the
        // same observable behaviour, not a behaviour fix. Say so in the report rather than counting
        // PostgreSQL as a second proof.
        //
        // Gated on a server engine: on SQLite the capture holds BEGIN IMMEDIATE and the writer waits
        // it out, which is correct behaviour for that provider and would fail this test.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.ServerEngineConfigured,
            EntityFrameworkTestDatabase.ServerEngineSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(
            EntityFrameworkTestConcurrency.ConcurrentTransactions);
        TestDbContextFactory<ServerEngineContext> plain =
            await database.CreateFactoryAsync<ServerEngineContext>(options => new ServerEngineContext(options));
        await using var writer = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", plain);

        var addressA = new StateAddress("app", "snapshot/a", StatePartition.Default);
        var addressB = new StateAddress("app", "snapshot/b", StatePartition.Default);
        await writer.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        await writer.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

        // The writer runs on a pool thread rather than inline, so a blocked write cannot deadlock the
        // capture that is waiting for it -- it fails the assertion after the window instead of
        // hanging the suite. The write is awaited at the end either way, so no task is abandoned.
        Task<StateAppendResult>? write = null;
        bool wroteDuringCapture = false;
        var interceptor = new AfterFirstReadInterceptor(async () =>
        {
            write = Task.Run(() =>
                writer.AppendAsync(addressB, StateWriteCondition.AtRevision(1), Commit("b2")).AsTask());
            await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5)));
            wroteDuringCapture = write.IsCompletedSuccessfully;
        });

        // A SECOND options set over the SAME database: the interceptor must see only the capture's
        // commands, or the write it starts would re-enter it.
        var intercepted = new TestDbContextFactory<ServerEngineContext>(
            database.Options<ServerEngineContext>(builder => builder.AddInterceptors(interceptor)),
            options => new ServerEngineContext(options));
        await using var reader = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", intercepted);

        IReadOnlyDictionary<StateAddress, StateRecord?> captured = await reader.CaptureAsync(
            [addressA, addressB], StateCaptureConsistency.SnapshotDistributed);

        Assert.NotNull(write);
        StateAppendResult writeResult = await write!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(writeResult.Succeeded);

        Assert.True(
            wroteDuringCapture,
            "the concurrent write did not complete while the capture was open, so the capture is still blocking writers.");

        // The other half, and the one that actually catches the shipped mapping: a snapshot is a
        // point in time. The capture must NOT see the revision that landed after it started.
        Assert.Equal(1L, captured[addressB]!.Revision);
        Assert.Equal(1L, captured[addressA]!.Revision);
    }

    [Fact]
    public async Task PostgreSQL_stores_a_timestamp_to_the_microsecond_and_no_finer()
    {
        // The documented limitation, pinned in both directions rather than described. PostgreSQL's
        // timestamptz is a microsecond count; a .NET tick is 100 ns. So an OccurredAt whose tick
        // count is not a whole number of microseconds comes back with its last digit gone -- and
        // comes back changed by EXACTLY that and nothing more, which is the half that would matter
        // if a future Npgsql release changed the mapping. FreshUntil, ServeUntil and the lease
        // table's ExpiresAt carry the same truncation; nothing in this codebase depends on finer
        // resolution than a microsecond (see docs/providers/index.md).
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.SelectedEngine == EntityFrameworkTestEngine.PostgreSql,
            EntityFrameworkTestDatabase.PostgresSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<ServerEngineContext> factory =
            await database.CreateFactoryAsync<ServerEngineContext>(options => new ServerEngineContext(options));

        // Deliberately not a whole number of microseconds: the last tick digit is 7.
        DateTimeOffset stamped = new DateTimeOffset(2026, 9, 10, 19, 37, 8, TimeSpan.Zero).AddTicks(1_252_657);
        await using var store = new EntityFrameworkStateLedgerStore<ServerEngineContext>(
            "database", factory, new FixedTimeProvider(stamped));

        var address = new StateAddress("app", "precision/one", StatePartition.Default);
        StateAppendResult appended = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
        Assert.Equal(stamped, appended.Record!.OccurredAt);

        StateRecord? readBack = await store.ReadLatestAsync(address);
        Assert.NotNull(readBack);
        Assert.NotEqual(stamped, readBack!.OccurredAt);
        Assert.Equal(stamped.AddTicks(-(stamped.Ticks % TimeSpan.TicksPerMicrosecond)), readBack.OccurredAt);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task An_import_and_a_concurrent_append_do_not_deadlock()
    {
        // The lock-order inversion 4804bb8 introduced, as an observation. AppendAsync takes the one
        // global-position row's exclusive lock first and holds it to commit; ImportAsync (before
        // this fix) reads records and heads first, under serializable isolation, and only reaches
        // the sequence row at the end. Two writers, two resources, opposite orders. The paused
        // append parks holding the sequence row; the import then takes the head's read lock and
        // waits for the sequence row; releasing the append makes it wait for the head. On SQL Server
        // that is a deadlock and the import -- which has no retry -- is the victim. On PostgreSQL it
        // is also a deadlock, but the APPEND's retry absorbs it, so the only symptom is a burned
        // position: the append comes back at 501 instead of 2. That is why the position assertion at
        // the end is not decoration.
        //
        // Gated on a server engine: SQLite's BEGIN IMMEDIATE serializes the two writers outright, so
        // this passes there before and after and proves nothing.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.ServerEngineConfigured,
            EntityFrameworkTestDatabase.ServerEngineSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(
            EntityFrameworkTestConcurrency.ConcurrentTransactions);
        TestDbContextFactory<ServerEngineContext> factory =
            await database.CreateFactoryAsync<ServerEngineContext>(options => new ServerEngineContext(options));

        var address = new StateAddress("app", "import-race/a", StatePartition.Default);

        await using var seed = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory);
        StateAppendResult first = await seed.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
        Assert.True(first.Succeeded);

        // A dedicated store for the paused writer, so the seeding appends above do not consume the
        // clock's single pause. AppendAsync reads the clock once, for OccurredAt, after it has taken
        // the sequence row and read the head and before it writes anything -- which is exactly the
        // window this test needs.
        var clock = new PausingTimeProvider(pauseOnCall: 1);
        await using var paused = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory, clock);
        await using var importer = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory);

        StateRecord reimport = first.Record! with { GlobalPosition = 500 };

        Task<StateAppendResult> append = Task.Run(() =>
            paused.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2")).AsTask());
        Task import;
        try
        {
            await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));

            import = Task.Run(() => importer.ImportAsync(reimport).AsTask());
            await Task.WhenAny(import, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            clock.Release();
        }

        StateAppendResult appended = await append.WaitAsync(TimeSpan.FromSeconds(30));
        await import.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(appended.Succeeded);
        Assert.Equal(2L, appended.Record!.Revision);

        StateRecord? head = await seed.ReadLatestAsync(address);
        Assert.Equal(2L, head!.Revision);

        // The position half. After the fix the import cannot start until the append commits, so the
        // append holds position 2, the import advances the sequence to 500, and the next append is
        // 501. Before the fix on PostgreSQL the append silently retried and took 501 itself, so this
        // read 502 -- the only visible trace of a deadlock that threw nothing.
        var other = new StateAddress("app", "import-race/b", StatePartition.Default);
        StateAppendResult after = await seed.AppendAsync(other, StateWriteCondition.Absent, Commit("v1"));
        Assert.Equal(501L, after.Record!.GlobalPosition);
    }

    [Fact]
    public async Task An_import_below_the_current_position_still_serializes_against_an_append()
    {
        // The half of the fix that is easy to get wrong. The import's max-advance is a CONDITIONAL
        // update, so it is fair to ask whether it still locks the row when the incoming position is
        // lower and nothing advances. It does: the conditional lives in the SET clause as a
        // CASE WHEN on all three providers, never in the WHERE, so the row is written -- and
        // therefore exclusively locked -- either way. Here the import's position (1) is below the
        // current sequence value (2), so no advance happens, and the paused append must still get 3.
        //
        // This one discriminates on SQL SERVER only. Before the fix, PostgreSQL's import reads the
        // sequence row under MVCC without blocking, finds no advance is needed, and never writes it,
        // so no second resource exists to form a cycle with; SQL Server's serializable READ of that
        // row still needs a lock the append holds, so it deadlocks anyway. Record the PostgreSQL
        // pass as the expected non-result rather than counting it as a second proof.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.ServerEngineConfigured,
            EntityFrameworkTestDatabase.ServerEngineSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(
            EntityFrameworkTestConcurrency.ConcurrentTransactions);
        TestDbContextFactory<ServerEngineContext> factory =
            await database.CreateFactoryAsync<ServerEngineContext>(options => new ServerEngineContext(options));

        var address = new StateAddress("app", "import-low/a", StatePartition.Default);
        await using var seed = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory);
        StateAppendResult first = await seed.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
        StateAppendResult second = await seed.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
        Assert.Equal(2L, second.Record!.GlobalPosition);

        var clock = new PausingTimeProvider(pauseOnCall: 1);
        await using var paused = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory, clock);
        await using var importer = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory);

        Task<StateAppendResult> append = Task.Run(() =>
            paused.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("v3")).AsTask());
        Task import;
        try
        {
            await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));
            import = Task.Run(() => importer.ImportAsync(first.Record!).AsTask());
            await Task.WhenAny(import, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            clock.Release();
        }

        StateAppendResult appended = await append.WaitAsync(TimeSpan.FromSeconds(30));
        await import.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(appended.Succeeded);
        Assert.Equal(3L, appended.Record!.GlobalPosition);

        // The sequence did not go backwards to 1.
        var other = new StateAddress("app", "import-low/b", StatePartition.Default);
        StateAppendResult after = await seed.AppendAsync(other, StateWriteCondition.Absent, Commit("v1"));
        Assert.Equal(4L, after.Record!.GlobalPosition);
    }

    [Fact]
    public async Task An_import_survives_a_prune_that_removes_the_record_it_was_replacing()
    {
        // The one thing Serializable was supposed to be giving ImportAsync, pinned so that lowering
        // the import to ReadCommitted is a measured trade rather than an assumption. The interceptor
        // prunes the address the instant the import has read the record it is about to replace, so
        // the replace lands on a row that no longer exists.
        //
        // Serializable did NOT actually protect this. Measured on c6262d7: PostgreSQL failed the
        // import outright with "40001: could not serialize access due to concurrent delete", and SQL
        // Server blocked the prune for the full 30-second command timeout. ReadCommitted plus the
        // DbUpdateConcurrencyException retry turns the same race into a re-read that finds the row
        // gone and re-inserts it, which is what an exact, idempotent import should do.
        //
        // The imported record must DIFFER from the stored one -- hence the changed GlobalPosition.
        // With an identical record, Entity Framework Core detects no modification, emits no UPDATE,
        // and the race is never exercised: the test passes without testing anything.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.ServerEngineConfigured,
            EntityFrameworkTestDatabase.ServerEngineSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(
            EntityFrameworkTestConcurrency.ConcurrentTransactions);
        TestDbContextFactory<ServerEngineContext> factory =
            await database.CreateFactoryAsync<ServerEngineContext>(options => new ServerEngineContext(options));

        var address = new StateAddress("app", "import-prune/a", StatePartition.Default);
        await using var store = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory);
        StateAppendResult first = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
        await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
        await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("v3"));

        var pruned = false;
        var interceptor = new AfterFirstReadInterceptor(async () =>
        {
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });
            pruned = true;
        });
        var intercepted = new TestDbContextFactory<ServerEngineContext>(
            database.Options<ServerEngineContext>(builder => builder.AddInterceptors(interceptor)),
            options => new ServerEngineContext(options));
        await using var importer = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", intercepted);

        await importer.ImportAsync(first.Record! with { GlobalPosition = 400 });

        Assert.True(pruned);
        List<StateRecord> history = [];
        await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            history.Add(record);
        }

        Assert.Contains(history, record => record.Revision == 1 && record.GlobalPosition == 400);
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private sealed class ServerEngineContext : StatesmanLedgerDbContext
    {
        public ServerEngineContext(DbContextOptions<ServerEngineContext> options)
            : base(options)
        {
        }
    }

    /// <summary>Runs a callback once, immediately after the first reader this context executes.</summary>
    private sealed class AfterFirstReadInterceptor : DbCommandInterceptor
    {
        private readonly Func<Task> _afterFirstRead;
        private int _reads;

        public AfterFirstReadInterceptor(Func<Task> afterFirstRead) => _afterFirstRead = afterFirstRead;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                await _afterFirstRead();
            }

            return result;
        }
    }
}
