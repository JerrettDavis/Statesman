using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Statesman.TestHelpers;

namespace Statesman.EntityFrameworkCore.Tests;

/// <summary>
/// What happens when a consumer turns on <c>EnableRetryOnFailure</c>, which ROADMAP 0.3 Phase 13
/// makes a supported configuration.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against a live server engine, because a retrying execution strategy only
/// exists on one. Three of the five tests — <see cref="A_transient_failure_before_the_commit_is_retried_into_exactly_one_record"/>,
/// <see cref="A_lost_commit_acknowledgement_on_acquire_returns_this_callers_own_lease"/> and
/// <see cref="A_lost_commit_acknowledgement_on_append_returns_the_documented_conflict"/> — are
/// PostgreSQL-only: they inject a transient failure, and Npgsql's <c>PostgresException</c> has a
/// public constructor where a <c>SqlException</c> would need reflection over internals no contract
/// covers. SQL Server's coverage is the whole-suite run under <c>STATESMAN_TEST_EF_RETRY</c>, which
/// proves that nothing FAILED rather than that anything RETRIED. That asymmetry is real; it is
/// stated here rather than implied away.
/// </para>
/// <para>
/// The proof that the STRATEGY retried, rather than the store's own bounded loop, is a pair of
/// counts: the outer loop creates a fresh <c>DbContext</c> per attempt and the strategy does not, so
/// one context created beside two transactions started can only be a strategy retry.
/// </para>
/// </remarks>
public sealed class EntityFrameworkRetryStrategyTests
{
    [Fact]
    public async Task Every_transactional_method_succeeds_under_a_retrying_execution_strategy()
    {
        // Before Phase 13 all four of these threw InvalidOperationException naming
        // CreateExecutionStrategy, because each opens its own transaction and a retrying strategy
        // refuses a user-initiated one. This runs on whichever server engine is configured.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.ServerEngineConfigured,
            EntityFrameworkTestDatabase.ServerEngineSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<RetryContext> options = database.OptionsWithRetryOnFailure<RetryContext>();
        await using (var setup = new RetryContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var factory = new TestDbContextFactory<RetryContext>(options, o => new RetryContext(o));
        await using var store = new EntityFrameworkStateLedgerStore<RetryContext>("database", factory);

        var address = new StateAddress("app", "retry/all", StatePartition.Default);
        StateAppendResult appended = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
        Assert.True(appended.Succeeded);

        StateAppendResult second = await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
        Assert.True(second.Succeeded);

        var imported = new StateAddress("app", "retry/imported", StatePartition.Default);
        await store.ImportAsync(Record(imported, revision: 1, position: 9_000));

        IReadOnlyDictionary<StateAddress, StateRecord?> readCommitted = await store.CaptureAsync(
            [address, imported], StateCaptureConsistency.ReadCommittedDistributed);
        Assert.Equal(2, readCommitted.Count);

        IReadOnlyDictionary<StateAddress, StateRecord?> snapshot = await store.CaptureAsync(
            [address, imported], StateCaptureConsistency.SnapshotDistributed);
        Assert.Equal(2, snapshot.Count);

        IStateLease? lease = await store.AcquireAsync("retry/lease", TimeSpan.FromMinutes(5));
        Assert.NotNull(lease);
        Assert.True(await lease!.RenewAsync(TimeSpan.FromMinutes(5)));
        await lease.DisposeAsync();

        await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

        var history = new List<long>();
        await foreach (StateRecord record in store.ReadHistoryAsync(
            address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            history.Add(record.Revision);
        }

        Assert.Equal([2L], history);
    }

    [Fact]
    public async Task A_transient_failure_before_the_commit_is_retried_into_exactly_one_record()
    {
        // The deterministic proof that something RETRIED. The interceptor throws a PostgreSQL 40001
        // on the first commit ATTEMPT, so nothing reached the server; the strategy re-invokes the
        // delegate against the same context and the replay must produce exactly one record at exactly
        // one position. Without ChangeTracker.Clear() as the delegate's first statement, the replay's
        // second Add of the same key throws instead.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.SelectedEngine == EntityFrameworkTestEngine.PostgreSql,
            EntityFrameworkTestDatabase.PostgresSkipReason);

        var interceptor = new TransientCommitInterceptor(FailureMoment.BeforeCommit);
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<RetryContext> options = database.OptionsWithRetryOnFailure<RetryContext>(
            builder => builder.AddInterceptors(interceptor));
        await using (var setup = new RetryContext(database.Options<RetryContext>()))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var factory = new CountingDbContextFactory<RetryContext>(
            new TestDbContextFactory<RetryContext>(options, o => new RetryContext(o)));
        await using var store = new EntityFrameworkStateLedgerStore<RetryContext>("database", factory);

        var address = new StateAddress("app", "retry/one", StatePartition.Default);
        StateAppendResult result = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

        Assert.True(result.Succeeded);
        Assert.True(interceptor.Thrown);

        // One context, two transactions: the store's own loop would have created a second context.
        Assert.Equal(1, factory.ContextsCreated);
        Assert.Equal(2, interceptor.TransactionsStarted);

        var positions = new List<long>();
        await foreach (StateRecord record in store.ReadHistoryAsync(
            address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            positions.Add(record.GlobalPosition);
        }

        Assert.Single(positions);

        var descriptors = new List<long>();
        await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
        {
            descriptors.Add(descriptor.LastPosition.Position);
        }

        Assert.Equal(positions, descriptors);
    }

    [Fact]
    public async Task A_lost_commit_acknowledgement_on_acquire_returns_this_callers_own_lease()
    {
        // The narrow behaviour a retrying strategy makes reachable, and the reason the lease token is
        // generated once per AcquireAsync call rather than once per attempt. The interceptor throws
        // AFTER the commit reached the server, so the lease row is durable and carries THIS caller's
        // token; the replay re-reads it, finds it live, and before Phase 13 returned null -- telling a
        // caller that holds the lease that it does not.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.SelectedEngine == EntityFrameworkTestEngine.PostgreSql,
            EntityFrameworkTestDatabase.PostgresSkipReason);

        var interceptor = new TransientCommitInterceptor(FailureMoment.AfterCommit);
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<RetryContext> options = database.OptionsWithRetryOnFailure<RetryContext>(
            builder => builder.AddInterceptors(interceptor));
        await using (var setup = new RetryContext(database.Options<RetryContext>()))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var factory = new CountingDbContextFactory<RetryContext>(
            new TestDbContextFactory<RetryContext>(options, o => new RetryContext(o)));
        await using var store = new EntityFrameworkStateLedgerStore<RetryContext>("database", factory);

        IStateLease? lease = await store.AcquireAsync("retry/lost-ack", TimeSpan.FromMinutes(5));

        Assert.True(interceptor.Thrown);
        Assert.NotNull(lease);

        // The handle is the real thing, not a placeholder: renewing it matches the row's token.
        Assert.True(await lease!.RenewAsync(TimeSpan.FromMinutes(5)));

        // And a DIFFERENT caller is still refused while that lease is live.
        var rival = new EntityFrameworkStateLedgerStore<RetryContext>(
            "rival", new TestDbContextFactory<RetryContext>(database.Options<RetryContext>(), o => new RetryContext(o)));
        await using (rival)
        {
            IStateLease? denied = await rival.AcquireAsync("retry/lost-ack", TimeSpan.FromMinutes(5));
            Assert.Null(denied);
        }

        // Release the row so the per-test database drops cleanly.
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task A_lost_commit_acknowledgement_on_append_returns_the_documented_conflict()
    {
        // The other half of the lost-acknowledgement contract, and the one that must NOT change:
        // docs/providers/index.md already documents that a lost acknowledgement makes the retry
        // re-read the head, find the record it just wrote, and return Conflict carrying it. The
        // execution strategy replays the delegate exactly where the internal retry used to, so the
        // documented outcome is unchanged -- proven here rather than asserted in prose.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.SelectedEngine == EntityFrameworkTestEngine.PostgreSql,
            EntityFrameworkTestDatabase.PostgresSkipReason);

        var interceptor = new TransientCommitInterceptor(FailureMoment.AfterCommit);
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<RetryContext> options = database.OptionsWithRetryOnFailure<RetryContext>(
            builder => builder.AddInterceptors(interceptor));
        await using (var setup = new RetryContext(database.Options<RetryContext>()))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var factory = new CountingDbContextFactory<RetryContext>(
            new TestDbContextFactory<RetryContext>(options, o => new RetryContext(o)));
        await using var store = new EntityFrameworkStateLedgerStore<RetryContext>("database", factory);

        var address = new StateAddress("app", "retry/lost-append", StatePartition.Default);
        StateAppendResult result = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

        Assert.True(interceptor.Thrown);
        Assert.False(result.Succeeded);
        Assert.Null(result.Record);
        Assert.NotNull(result.Current);
        Assert.Equal(1, result.Current!.Revision);

        // Nothing duplicated, and the rolled-back replay burned no position.
        var positions = new List<long>();
        await foreach (StateRecord record in store.ReadHistoryAsync(
            address, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            positions.Add(record.GlobalPosition);
        }

        Assert.Equal([result.Current.GlobalPosition], positions);
    }

    [Fact]
    public async Task The_retry_variable_reaches_the_seam()
    {
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.ServerEngineConfigured,
            EntityFrameworkTestDatabase.ServerEngineSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        await using var context = new RetryContext(database.Options<RetryContext>());

        Assert.Equal(
            EntityFrameworkTestDatabase.RetryOnFailureRequested,
            context.Database.CreateExecutionStrategy().RetriesOnFailure);
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

    private static StateRecord Record(StateAddress address, long revision, long position) => new()
    {
        Address = address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes("imported"),
        Source = "test",
    };

    private sealed class RetryContext : StatesmanLedgerDbContext
    {
        public RetryContext(DbContextOptions<RetryContext> options)
            : base(options)
        {
        }
    }

    /// <summary>Where the injected failure lands, relative to the commit reaching the server.</summary>
    private enum FailureMoment
    {
        /// <summary>Before it: nothing was written, so a replay must succeed outright.</summary>
        BeforeCommit,

        /// <summary>After it: the write is durable and only its acknowledgement was lost.</summary>
        AfterCommit,
    }

    /// <summary>Throws one provider-transient failure at a chosen point, and counts transactions.</summary>
    private sealed class TransientCommitInterceptor : DbTransactionInterceptor
    {
        private readonly FailureMoment _moment;
        private int _thrown;
        private int _transactionsStarted;

        public TransientCommitInterceptor(FailureMoment moment) => _moment = moment;

        public bool Thrown => Volatile.Read(ref _thrown) == 1;

        public int TransactionsStarted => Volatile.Read(ref _transactionsStarted);

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _transactionsStarted);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (_moment == FailureMoment.BeforeCommit)
            {
                ThrowOnce();
            }

            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_moment == FailureMoment.AfterCommit)
            {
                ThrowOnce();
            }

            return Task.CompletedTask;
        }

        private void ThrowOnce()
        {
            if (Interlocked.CompareExchange(ref _thrown, 1, 0) != 0)
            {
                return;
            }

            // 40001 is a PostgreSQL serialization failure. NpgsqlRetryingExecutionStrategy classifies
            // it as transient, so the strategy re-invokes the delegate; DbException.IsTransient also
            // reports true for it, which is why the store's delegate rolls back and RETHROWS rather
            // than looping -- the strategy has to get first refusal or this test proves nothing.
            throw new PostgresException("injected serialization failure", "ERROR", "ERROR", "40001");
        }
    }

    /// <summary>Counts contexts handed out, which is what separates a strategy retry from a loop retry.</summary>
    private sealed class CountingDbContextFactory<TContext> : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        private readonly IDbContextFactory<TContext> _inner;
        private int _created;

        public CountingDbContextFactory(IDbContextFactory<TContext> inner) => _inner = inner;

        public int ContextsCreated => Volatile.Read(ref _created);

        public TContext CreateDbContext()
        {
            Interlocked.Increment(ref _created);
            return _inner.CreateDbContext();
        }

        public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _created);
            return _inner.CreateDbContextAsync(cancellationToken);
        }
    }
}
