namespace Statesman.Outbox.Tests;

public sealed class OutboxLeaseTests
{
    private static OutboxOptions Options(bool requireLease = true, int batchSize = 100) => new()
    {
        OutboxId = "test",
        StoreName = "leased",
        Root = "app",
        BatchSize = batchSize,
        RequireLease = requireLease,
    };

    private static async Task SeedAsync(LeasedLedgerStore store, params long[] positions)
    {
        long revision = 0;
        foreach (long position in positions)
        {
            revision++;
            await store.ImportAsync(OutboxTestRecords.Record(position, revision));
        }
    }

    [Fact]
    public async Task A_store_without_a_lease_provider_is_refused_by_name_when_a_lease_is_required()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options();
        options.StoreName = "memory";

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options));

        Assert.Contains("IStateLeaseProvider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("memory", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OutboxOptions.RequireLease), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Require_lease_false_runs_unleased_and_reports_the_degradation()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1, 2);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options(requireLease: false);
        options.StoreName = "memory";
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.True(dispatcher.RunningWithoutLease);
        Assert.Equal(2, result.Published);
    }

    [Fact]
    public async Task A_leased_store_reports_no_degradation_and_names_its_lease()
    {
        await using var store = new LeasedLedgerStore();
        await using var sink = new InMemoryStateChangeSink();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), Options());

        Assert.False(dispatcher.RunningWithoutLease);
        Assert.Equal("app:leased:outbox:test", dispatcher.LeaseId);
    }

    [Fact]
    public async Task A_dispatcher_that_cannot_acquire_the_lease_publishes_nothing()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());
        store.Leases.RefuseAcquire = true;

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.LeaseUnavailable, result.Outcome);
        Assert.Equal(0, result.Published);
        Assert.Empty(sink.Published);
        Assert.Null(await cursors.ReadAsync("test"));
        Assert.Equal("app:leased:outbox:test", store.Leases.LastRequestedLeaseId);
    }

    [Fact]
    public async Task A_second_dispatcher_publishes_nothing_while_the_first_holds_the_lease()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var firstSink = new InMemoryStateChangeSink();
        await using var secondSink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        var second = new StateChangeDispatcher(store, secondSink, cursors, Options());

        // The first dispatcher's sink runs the second dispatcher from inside the protected window,
        // so the assertion is about what is true *during* the critical section, not afterwards.
        OutboxDispatchResult? nested = null;
        await using var firstSinkProbe = new CallbackSink(firstSink, async () =>
            nested = await second.DispatchOnceAsync());
        var first = new StateChangeDispatcher(store, firstSinkProbe, cursors, Options());

        OutboxDispatchResult result = await first.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Published);
        Assert.NotNull(nested);
        Assert.Equal(OutboxDispatchOutcome.LeaseUnavailable, nested!.Outcome);
        Assert.Empty(secondSink.Published);
    }

    [Fact]
    public async Task The_lease_is_held_after_the_cycle_ends_and_released_on_request()
    {
        // Phase 10 inverts what this test used to assert. Before, the dispatcher acquired and
        // released inside one DispatchOnceAsync call, so HeldCount was 0 afterwards; lease traffic
        // therefore scaled with the number of cycles, which under hint-driven dispatch means it
        // scaled with write volume. The dispatcher is now a persistent leader: it holds the lease
        // between cycles and releases it when asked or when disposed.
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1);
        await using var sink = new InMemoryStateChangeSink();
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), Options());

        await dispatcher.DispatchOnceAsync();

        Assert.Equal(1, store.Leases.HeldCount);

        await dispatcher.ReleaseLeaseAsync();

        Assert.Equal(0, store.Leases.HeldCount);

        // Idempotent: a second release is a no-op, which is what lets the hosted worker call it from
        // a finally, from a catch, and from DisposeAsync without counting.
        await dispatcher.ReleaseLeaseAsync();
        Assert.Equal(0, store.Leases.HeldCount);
    }

    [Fact]
    public async Task A_lease_lost_mid_run_stops_before_the_next_batch_without_advancing_past_it()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        OutboxOptions options = Options(batchSize: 1);
        options.LeaseRenewInterval = TimeSpan.Zero;
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);
        store.Leases.RefuseRenew = true;

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.LeaseLost, result.Outcome);
        Assert.Equal(1, result.Published);
        Assert.Equal(1, store.Leases.RenewCalls);
        Assert.Equal(1, (await cursors.ReadAsync("test"))!.Value.Position);
        Assert.Equal(0, store.Leases.HeldCount);
    }

    [Fact]
    public async Task The_lease_is_renewed_between_batches_and_the_run_continues()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options(batchSize: 1);
        options.LeaseRenewInterval = TimeSpan.Zero;
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Published);
        Assert.Equal(2, store.Leases.RenewCalls);
    }

    [Fact]
    public async Task The_lease_is_renewed_periodically_under_a_nonzero_interval_not_once_per_batch()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, Enumerable.Range(1, 25).Select(i => (long)i).ToArray());
        await using var sink = new DelayingStateChangeSink(TimeSpan.FromMilliseconds(10));
        OutboxOptions options = Options(batchSize: 1);
        options.LeaseRenewInterval = TimeSpan.FromMilliseconds(50);
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, result.Outcome);
        Assert.Equal(25, result.Published);
        Assert.True(
            store.Leases.RenewCalls > 0,
            $"expected at least one renewal across a ~250ms drain with a 50ms renewal interval, got {store.Leases.RenewCalls}.");
        Assert.True(
            store.Leases.RenewCalls < 25,
            $"expected renewal to be spaced by the interval rather than firing once per batch, got {store.Leases.RenewCalls} renewals for 25 batches.");
    }

    [Fact]
    public async Task A_poison_batch_is_skipped_even_when_new_records_keep_extending_the_batch_end_cursor()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1);
        await using var sink = new FailingStateChangeSink(failures: int.MaxValue);
        var cursors = new InMemoryOutboxCursorStore();
        OutboxOptions options = Options();
        options.SkipPoisonAfterAttempts = 2;
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchOnceAsync());

        // A new record lands between attempts, extending the batch's END cursor from 1 to 2. If
        // the poison counter were keyed on that end cursor it would reset to 1 here and never reach
        // the limit while the store keeps taking writes. Keyed on the stable START cursor instead,
        // this is attempt 2 and the limit is reached.
        await store.ImportAsync(OutboxTestRecords.Record(2, revision: 2));

        OutboxDispatchResult second = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, second.Outcome);
        Assert.Equal(0, second.Published);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, (await cursors.ReadAsync("test"))!.Value.Position);
    }

    [Fact]
    public async Task Without_a_skip_limit_a_poison_batch_blocks_the_cursor_indefinitely()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2);
        await using var sink = new FailingStateChangeSink(failures: int.MaxValue);
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchOnceAsync());
        }

        Assert.Null(await cursors.ReadAsync("test"));
        Assert.Equal(3, sink.Attempts);
    }

    [Fact]
    public async Task A_poison_batch_is_skipped_once_the_configured_attempt_limit_is_reached()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2);
        await using var sink = new FailingStateChangeSink(failures: int.MaxValue);
        var cursors = new InMemoryOutboxCursorStore();
        OutboxOptions options = Options();
        options.SkipPoisonAfterAttempts = 2;
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchOnceAsync());
        OutboxDispatchResult second = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, second.Outcome);
        Assert.Equal(0, second.Published);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, (await cursors.ReadAsync("test"))!.Value.Position);
        Assert.IsType<InvalidOperationException>(dispatcher.LastSkippedError);
    }

    [Fact]
    public async Task The_lease_is_acquired_once_and_held_across_cycles()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), Options());

        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();

        // Exact, not a range. On the pre-Phase-10 dispatcher this reads 3.
        Assert.Equal(1, store.Leases.AcquireCalls);
        Assert.Equal(1, store.Leases.HeldCount);
    }

    [Fact]
    public async Task Releasing_the_lease_lets_a_second_dispatcher_take_it()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1);
        await using var firstSink = new InMemoryStateChangeSink();
        await using var secondSink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        await using var first = new StateChangeDispatcher(store, firstSink, cursors, Options());
        await using var second = new StateChangeDispatcher(store, secondSink, cursors, Options());

        await first.DispatchOnceAsync();
        Assert.Equal(OutboxDispatchOutcome.LeaseUnavailable, (await second.DispatchOnceAsync()).Outcome);

        await first.ReleaseLeaseAsync();

        Assert.Equal(0, store.Leases.HeldCount);
        Assert.Equal(OutboxDispatchOutcome.Completed, (await second.DispatchOnceAsync()).Outcome);
    }

    [Fact]
    public async Task A_held_lease_is_renewed_at_the_top_of_every_later_cycle()
    {
        // Exact. Cycle 1 acquires (a fresh lease needs no renewal); cycles 2 and 3 renew at the top
        // because LeaseRenewInterval is zero. A dispatcher that re-acquires per cycle reads 0
        // renewals and 3 acquires; one that never renews a held lease reads 0 renewals and would
        // silently drop the lease at LeaseTtl in production.
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options();
        options.LeaseRenewInterval = TimeSpan.Zero;
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), options);

        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();

        Assert.Equal(1, store.Leases.AcquireCalls);
        Assert.Equal(2, store.Leases.RenewCalls);
    }

    [Fact]
    public async Task The_renewal_clock_survives_a_cycle_boundary()
    {
        // The subtle bug this task must not ship. DrainAsync must seed its renewal clock from the
        // dispatcher's field and write the threaded value back; an implementation that stamps a
        // fresh Stopwatch timestamp per cycle and writes THAT back re-stamps the clock every cycle,
        // never satisfies the interval, and silently drops the lease at LeaseTtl.
        //
        // This is the one assertion in this task that is not exact, and the reason is structural:
        // StillHeldAsync measures the interval with Stopwatch, which no TimeProvider can advance, and
        // LeaseRenewInterval = TimeSpan.Zero makes every check fire regardless of what the clock
        // says -- so the correct and the broken implementation are indistinguishable at zero. The
        // margins are chosen so neither direction is a coin flip: two 600 ms gaps against a
        // 1000 ms interval means the correct implementation must renew on cycle 3 (1200 ms since
        // acquisition) and the broken one must not (600 ms since cycle 2 began), and a cycle over an
        // empty in-memory feed costs well under a millisecond, so the broken implementation would
        // need a 400 ms cycle to pass by accident.
        await using var store = new LeasedLedgerStore();
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options();
        options.LeaseRenewInterval = TimeSpan.FromMilliseconds(1000);
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), options);

        await dispatcher.DispatchOnceAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        await dispatcher.DispatchOnceAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        await dispatcher.DispatchOnceAsync();

        Assert.Equal(1, store.Leases.AcquireCalls);
        Assert.True(
            store.Leases.RenewCalls >= 1,
            $"expected the renewal clock to be measured from acquisition rather than from each cycle's start, so a 1000 ms interval fires across two 600 ms gaps; got {store.Leases.RenewCalls} renewals.");
    }

    [Fact]
    public async Task A_leader_with_the_default_renew_interval_does_not_renew_on_every_cycle()
    {
        // The Phase 7 rule: the shipped default has to be exercised, not only a test-shortened
        // value. LeaseRenewInterval left null is LeaseTtl / 3 = 10 seconds at the default 30-second
        // TTL, so three back-to-back cycles must renew zero times -- and must still acquire once.
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options();
        Assert.Null(options.LeaseRenewInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.EffectiveLeaseRenewInterval);
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), options);

        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();

        Assert.Equal(1, store.Leases.AcquireCalls);
        Assert.Equal(0, store.Leases.RenewCalls);
    }

    [Fact]
    public async Task A_refused_renewal_between_cycles_drops_the_handle_and_reacquires()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options();
        options.LeaseRenewInterval = TimeSpan.Zero;
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), options);

        await dispatcher.DispatchOnceAsync();
        store.Leases.RefuseRenew = true;

        // A refused renewal makes the handle worthless, so the cycle drops it and acquires once.
        OutboxDispatchResult second = await dispatcher.DispatchOnceAsync();
        Assert.Equal(OutboxDispatchOutcome.Completed, second.Outcome);
        Assert.Equal(2, store.Leases.AcquireCalls);
        Assert.Equal(1, store.Leases.HeldCount);

        // With the acquire refused too, the cycle reports standby and leaks no handle.
        store.Leases.RefuseAcquire = true;
        OutboxDispatchResult third = await dispatcher.DispatchOnceAsync();
        Assert.Equal(OutboxDispatchOutcome.LeaseUnavailable, third.Outcome);
        Assert.Equal(0, store.Leases.HeldCount);
    }

    /// <summary>A sink that takes a fixed delay per batch, to make renewal cadence observable.</summary>
    private sealed class DelayingStateChangeSink : IStateChangeSink
    {
        private readonly TimeSpan _delay;

        public DelayingStateChangeSink(TimeSpan delay) => _delay = delay;

        public string Name => "delaying";

        public async ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default) =>
            await Task.Delay(_delay, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Wraps a sink and runs a callback from inside <see cref="PublishAsync"/>.</summary>
    private sealed class CallbackSink : IStateChangeSink
    {
        private readonly IStateChangeSink _inner;
        private readonly Func<Task> _duringPublish;

        public CallbackSink(IStateChangeSink inner, Func<Task> duringPublish)
        {
            _inner = inner;
            _duringPublish = duringPublish;
        }

        public string Name => _inner.Name;

        public async ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
        {
            await _duringPublish();
            await _inner.PublishAsync(batch, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
