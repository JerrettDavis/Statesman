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

    /// <summary>
    /// Pins the limitation documented in docs/providers/index.md and deferred to Phase 13
    /// (task3-brief-v2.md, "Deferred to Phase 13"): a retrying execution strategy refuses to run
    /// inside a user-initiated transaction, and every transactional method on this store opens one
    /// itself, so all of them fail today. When Phase 13 routes these methods through
    /// <c>context.Database.CreateExecutionStrategy().ExecuteAsync(…)</c>, this test goes red first.
    /// </summary>
    [Fact]
    public async Task A_retrying_execution_strategy_is_rejected_by_every_transactional_method_today()
    {
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.ServerEngineConfigured,
            EntityFrameworkTestDatabase.ServerEngineSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<ServerEngineContext> options = database.OptionsWithRetryOnFailure<ServerEngineContext>();
        await using (var setup = new ServerEngineContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var factory = new TestDbContextFactory<ServerEngineContext>(options, o => new ServerEngineContext(o));
        await using var store = new EntityFrameworkStateLedgerStore<ServerEngineContext>("database", factory);

        var address = new StateAddress("app", "retry/one", StatePartition.Default);
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1")));

        Assert.Contains("CreateExecutionStrategy", exception.Message);
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
