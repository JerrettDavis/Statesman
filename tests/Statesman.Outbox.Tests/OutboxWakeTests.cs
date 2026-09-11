using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Statesman.Testing;

namespace Statesman.Outbox.Tests;

public sealed class OutboxWakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Far longer than any test here runs, so a passing test cannot be explained by a timer tick.
    private static readonly TimeSpan NoTimerCanFire = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_hint_dispatches_long_before_the_poll_interval_elapses()
    {
        // FAILS on the pre-change worker: it waits only on a 30-second timer, so the sink signal
        // never arrives inside the 10-second assertion window.
        await using var store = new NotifyingLedgerStore();
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions
        {
            OutboxId = "wake",
            StoreName = store.Name,
            PollInterval = NoTimerCanFire,
        };
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // The subscription is lazy, so the record and the hint must both come after the worker
            // has actually begun enumerating. Signalling first would be a legitimately missed hint
            // and this test would then be waiting on the 30-second timer -- the vacuous version.
            await store.Subscribed.WaitAsync(Timeout);
            await store.ImportAsync(OutboxTestRecords.Record(position: 1));
            store.Signal();

            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, sink.PublishedCount);
    }

    [Fact]
    public async Task A_store_without_a_notifier_still_dispatches_on_the_default_poll_interval()
    {
        // The Phase 7 rule: the fallback runs at its shipped default, not at a test-shortened one.
        // LeasedLedgerStore has a change feed and a lease provider and deliberately no notifier.
        await using var store = new LeasedLedgerStore();
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions { OutboxId = "poll", StoreName = store.Name };
        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);

        await store.ImportAsync(OutboxTestRecords.Record(position: 1));
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        Assert.Null(dispatcher.ChangeNotifier);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, sink.PublishedCount);
    }

    [Fact]
    public async Task A_burst_of_hints_is_coalesced_and_never_runs_two_cycles_at_once()
    {
        await using var store = new NotifyingLedgerStore();
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1, holdFirstPublish: true);
        var options = new OutboxOptions
        {
            OutboxId = "burst",
            StoreName = store.Name,
            PollInterval = NoTimerCanFire,
        };
        var cursors = new CountingOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, new ManualTimeProvider());

        int cyclesBeforeBurst;
        int cyclesAfterBurst;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);
            await store.ImportAsync(OutboxTestRecords.Record(position: 1));

            // One hint starts a cycle, and the sink holds that cycle open inside its publish call.
            // Everything raised from here until the release lands while a cycle is provably
            // running, which is the only arrangement in which coalescing has an exact answer: the
            // worker's capacity-1 channel keeps one pending wake and drops the rest. Raising the
            // burst against an idle worker instead makes the cycle count a race between the pump
            // and the loop, and a fast machine legitimately runs dozens of cycles that way.
            store.Signal();
            await sink.FirstPublishStarted.WaitAsync(Timeout);
            cyclesBeforeBurst = cursors.ReadCalls;

            // Signalling itself cannot block regardless of the worker's behaviour -- it writes to
            // this double's own unbounded channel, not the worker's capacity-1 one -- so the only
            // observable evidence of coalescing is how many times the worker cycles afterwards.
            for (int i = 0; i < 1000; i++)
            {
                store.Signal();
            }

            // Every hint has been taken by the pump before the held cycle is allowed to finish.
            await WaitUntilAsync(() => store.PendingHints == 0, Timeout);
            sink.ReleasePublish();
            await sink.Reached.WaitAsync(Timeout);

            // The one pending wake becomes exactly one follow-up cycle. Wait for it, then give a
            // non-coalescing worker time to show the rest of its thousand. This is a HINT-path
            // window, not a clock wait: a wrong implementation misbehaves in real time and no
            // virtual clock makes that instantaneous. What the virtual clock does buy is that no
            // poll tick can fire in it by construction, rather than because PollInterval happens to
            // be 30 seconds.
            await WaitUntilAsync(() => cursors.ReadCalls > cyclesBeforeBurst, Timeout);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            cyclesAfterBurst = cursors.ReadCalls;
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // PublishedCount and MaxConcurrentPublishes cannot discriminate a coalescing worker from a
        // sequential one here: one record exists, the cursor advances after the first successful
        // publish, and the dispatcher's own lease serialises every cycle regardless of the wake
        // path -- so both would read 1 even if all 1000 hints ran as 1000 separate cycles.
        // CountingOutboxCursorStore.ReadCalls is the observable that actually counts cycles: the
        // dispatcher reads the cursor exactly once per cycle. FakeLeaseProvider.AcquireCalls used to
        // serve this purpose and no longer can -- a persistent leader acquires once and holds, so it
        // would read 0 here no matter how many cycles ran. A non-coalescing worker runs one cycle per
        // hint (~1000 extra reads); a coalescing one runs exactly one extra. Two is tolerated only
        // for the last hint the pump had already taken from the store but not yet written to the
        // wake channel at the moment of release.
        Assert.InRange(cyclesAfterBurst - cyclesBeforeBurst, 1, 2);
        Assert.Equal(1, sink.PublishedCount);
        Assert.Equal(1, sink.MaxConcurrentPublishes);
    }

    [Fact]
    public async Task A_standby_worker_that_cannot_take_the_lease_does_not_acquire_once_per_hint()
    {
        await using var store = new NotifyingLedgerStore();
        store.Leases.RefuseAcquire = true;
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions
        {
            OutboxId = "standby",
            StoreName = store.Name,
            PollInterval = NoTimerCanFire,
        };
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, new ManualTimeProvider());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);
            await store.ImportAsync(OutboxTestRecords.Record(position: 1));
            for (int i = 0; i < 50; i++)
            {
                store.Signal();
            }

            await WaitUntilAsync(() => store.Leases.AcquireCalls >= 1, Timeout);

            // A generous window in which a worker that kept honouring hints would burn through the
            // other 49. This is a HINT-path window, not a clock wait: a wrong implementation
            // misbehaves in real time and no virtual clock makes that instantaneous. What the
            // virtual clock does buy is that no poll tick can fire in it by construction, rather
            // than because PollInterval happens to be 30 seconds.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // One acquire from the first hint. A second is tolerated only for a hint that arrived while
        // that first cycle was still running and had not yet set the standby flag. Fifty hints must
        // never mean fifty lease round trips.
        Assert.InRange(store.Leases.AcquireCalls, 1, 2);
        Assert.Equal(0, sink.PublishedCount);
    }

    [Fact]
    public async Task A_standby_worker_takes_over_on_the_poll_interval_once_the_lease_frees_up()
    {
        // The other half of the standby ruling: falling back to the timer must not mean never
        // running again.
        await using var store = new NotifyingLedgerStore();
        store.Leases.RefuseAcquire = true;
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions
        {
            OutboxId = "takeover",
            StoreName = store.Name,
            PollInterval = TimeSpan.FromMilliseconds(200),
        };
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);
            await store.ImportAsync(OutboxTestRecords.Record(position: 1));
            store.Signal();
            await WaitUntilAsync(() => store.Leases.AcquireCalls >= 1, Timeout);

            store.Leases.RefuseAcquire = false;
            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, sink.PublishedCount);
    }

    [Fact]
    public async Task A_notifier_that_throws_degrades_to_timer_only_and_keeps_dispatching()
    {
        // The pump's non-cancellation catch: a provider-side failure, not a caller cancellation and
        // not a clean store disposal. A short real interval keeps the test fast; PollInterval is
        // what must pick the record up, since the wake path is now dead.
        await using var store = new NotifyingLedgerStore();
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions
        {
            OutboxId = "fault",
            StoreName = store.Name,
            PollInterval = TimeSpan.FromMilliseconds(250),
        };
        var cursors = new CountingOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);

            // One hint proves the pump was alive before the fault; the fault itself ends the
            // subscription with an exception rather than cleanly.
            store.Signal();
            store.Fault(new InvalidOperationException("simulated provider failure"));

            // The enumerator's finally block is the only thing that completes this, so it is the
            // proof that the pump actually disarmed rather than the timer merely covering for a
            // wake path still limping along. Mirrors the clean-end sibling below.
            await store.Unsubscribed.WaitAsync(Timeout);

            // Imported after the fault, so only a timer tick -- never the dead wake path -- can
            // dispatch it. If the worker died or spun instead of degrading, this times out or the
            // acquire-count assertion below catches the spin.
            await store.ImportAsync(OutboxTestRecords.Record(position: 1));
            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, sink.PublishedCount);

        // A worker that mishandled the fault by re-arming a dead wake path in a tight loop would run
        // far more than a handful of CYCLES in this window; a healthy one polls at most a few times
        // over the up-to-10-second wait at a 250 ms interval, plus one on shutdown. Counted through
        // the cursor store rather than through lease acquires, which a persistent leader performs
        // once regardless of how many cycles it runs.
        Assert.True(
            cursors.ReadCalls < 1000,
            $"expected a handful of poll-driven cycles, not a spin: got {cursors.ReadCalls}.");
    }

    [Fact]
    public async Task Disposing_the_store_ends_the_subscription_cleanly_and_the_worker_keeps_polling()
    {
        // The other pump end condition: the store itself is disposed (the documented "ends when the
        // store is disposed" contract), not cancelled and not faulted. The disarm path must not
        // throw, and the worker must keep ticking on the timer afterwards rather than getting stuck.
        var store = new NotifyingLedgerStore();
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions
        {
            OutboxId = "clean-end",
            StoreName = store.Name,
            PollInterval = TimeSpan.FromMilliseconds(200),
        };
        var cursors = new CountingOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);

            int cyclesBeforeDispose = cursors.ReadCalls;
            await store.DisposeAsync();

            // The pump's await foreach must fall out normally here, not throw -- that is what
            // "ends cleanly" means -- and Unsubscribed only completes from that finally block.
            await store.Unsubscribed.WaitAsync(Timeout);

            // The disarm path (wakeArmed = false; continue;) must not stop the timer side: the
            // worker keeps cycling on every subsequent tick regardless of the store's own disposal.
            await WaitUntilAsync(() => cursors.ReadCalls > cyclesBeforeDispose, Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disposing_the_worker_tears_down_the_notifier_subscription()
    {
        // FAILS on the pre-change worker, whose DisposeAsync disposes only the sink: the assertion
        // is that the subscription is ALREADY finished when DisposeAsync returns, not merely that
        // it finishes eventually once the base class cancels the stopping token.
        await using var store = new NotifyingLedgerStore();
        var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions
        {
            OutboxId = "teardown",
            StoreName = store.Name,
            PollInterval = NoTimerCanFire,
        };
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        await store.Subscribed.WaitAsync(Timeout);

        await worker.DisposeAsync();

        Assert.True(
            store.Unsubscribed.IsCompleted,
            "DisposeAsync returned while the change-notification subscription was still running.");
    }

    [Fact]
    public async Task Two_replicas_under_a_thousand_hinted_writes_take_the_lease_a_handful_of_times()
    {
        // The Phase 9 Important, closed. Two workers over one store with the same OutboxId share one
        // lease id; the timer can never fire, so every cycle either worker runs is hint-driven. On
        // the pre-Phase-10 dispatcher the leader acquired and released once per cycle and this
        // reads 1000+ -- the in-process analogue of the 2300-acquires-per-5s measurement the outbox
        // guide used to publish. With a persistent leader it is two: the leader's one acquire, and
        // the loser's one refused attempt before it settles into standby.
        await using var store = new NotifyingLedgerStore();
        await using var firstSink = new OverlapDetectingStateChangeSink(expected: 1000);
        await using var secondSink = new OverlapDetectingStateChangeSink(expected: 1000);
        var cursors = new InMemoryOutboxCursorStore();
        var options = new OutboxOptions
        {
            OutboxId = "replicas",
            StoreName = store.Name,
            PollInterval = NoTimerCanFire,
        };

        var first = new StateChangeDispatcher(store, firstSink, cursors, options);
        var second = new StateChangeDispatcher(store, secondSink, cursors, options);
        var firstWorker = new StatesmanOutboxHostedService(
            first, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);
        var secondWorker = new StatesmanOutboxHostedService(
            second, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await firstWorker.StartAsync(CancellationToken.None);
        await secondWorker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);
            for (int position = 1; position <= 1000; position++)
            {
                await store.ImportAsync(OutboxTestRecords.Record(position, revision: position));
                store.Signal();
            }

            // NotifyingLedgerStore fans every hint out to each subscriber's own channel, so the
            // leader's pump sees every one of the 1000 hints regardless of what the standby's pump
            // does with its copy -- the final import's hint alone guarantees the leader learns the
            // feed has more to read.
            await WaitUntilAsync(
                () => firstSink.PublishedCount + secondSink.PublishedCount >= 1000,
                Timeout);
        }
        finally
        {
            await firstWorker.StopAsync(CancellationToken.None);
            await secondWorker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1000, firstSink.PublishedCount + secondSink.PublishedCount);
        Assert.InRange(store.Leases.AcquireCalls, 1, 3);
        Assert.Equal(0, store.Leases.HeldCount);
    }

    [Fact]
    public async Task A_failed_cycle_releases_the_lease_before_the_backoff_delay_elapses()
    {
        // Minor 7 (final review): StatesmanOutboxHostedService.cs:~207 releases the lease BEFORE the
        // backoff delay, not after, so a stalled worker is not holding the lease while doing nothing --
        // the invariant OutboxOptions.MaxRetryDelay's own validation message states. Nothing exercised
        // it; the nearest test above asserts HeldCount == 0 only after a clean stop, not after a failed
        // cycle. The sink here always throws, so the worker's first cycle fails and enters backoff.
        //
        // Phase 12: the clock is virtual. The backoff is Task.Delay(..., _timeProvider, ...), so it
        // does not elapse until this test advances past MinRetryDelay -- which turns "the delay is
        // still outstanding" from a 1.2-second real-time bet into a fact, and removes 1.2 seconds of
        // waiting. Total virtual time advanced below is at most 1.5 s, comfortably under the 2 s
        // MinRetryDelay.
        var clock = new ManualTimeProvider();
        await using var store = new LeasedLedgerStore();
        await using var sink = new FailingStateChangeSink(failures: int.MaxValue);
        var options = new OutboxOptions
        {
            OutboxId = "backoff-release",
            StoreName = store.Name,
            PollInterval = TimeSpan.FromMilliseconds(50),
            MinRetryDelay = TimeSpan.FromSeconds(2),
            MaxRetryDelay = TimeSpan.FromSeconds(5),
        };
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, clock);

        await store.ImportAsync(OutboxTestRecords.Record(position: 1));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // LeasedLedgerStore implements no IStateChangeNotifier, so the worker's wake path is
            // never armed and it waits for a poll tick before its first cycle. StartAsync returns
            // before ExecuteAsync has created that timer, and a timer created after an Advance takes
            // its due time from the advanced clock -- so the tick is nudged until the cycle actually
            // happens, in PollInterval steps whose total stays far below MinRetryDelay.
            for (int step = 0; step < 10 && sink.Attempts == 0; step++)
            {
                clock.Advance(options.PollInterval);
                await Task.Delay(20);
            }

            Assert.True(sink.Attempts >= 1, "the worker never ran its first dispatch cycle.");

            // The backoff (at least MinRetryDelay, 2 s of virtual time) is outstanding for the whole
            // window below, because nothing advances the clock past it. A lease still held here
            // means the release did not happen before the delay.
            await WaitUntilAsync(() => store.Leases.HeldCount == 0, Timeout);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(0, store.Leases.HeldCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException("The expected condition was never observed within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
