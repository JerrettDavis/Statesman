using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

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
        await using var sink = new OverlapDetectingStateChangeSink(expected: 1);
        var options = new OutboxOptions
        {
            OutboxId = "burst",
            StoreName = store.Name,
            PollInterval = NoTimerCanFire,
        };
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);
            await store.ImportAsync(OutboxTestRecords.Record(position: 1));

            // A thousand hints raised back-to-back must not become a thousand cycles. Signalling
            // itself cannot block regardless of the worker's behaviour -- it writes to this
            // double's own unbounded channel, not the worker's capacity-1 one -- so the only
            // observable evidence of coalescing is how many times the worker actually cycled.
            for (int i = 0; i < 1000; i++)
            {
                store.Signal();
            }

            await sink.Reached.WaitAsync(Timeout);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // PublishedCount and MaxConcurrentPublishes cannot discriminate a coalescing worker from a
        // sequential one here: one record exists, the cursor advances after the first successful
        // publish, and DispatchOnceAsync's own lease serialises every cycle regardless of the wake
        // path -- so both would read 1 even if all 1000 hints ran as 1000 separate cycles.
        // AcquireCalls is the one observable in this test that actually counts cycles: a
        // non-coalescing worker would run roughly one cycle per hint (~1000 acquires); a
        // coalescing one collapses the burst to at most a couple of extra cycles beyond the first.
        // The upper bound is loose enough to absorb scheduling jitter (an extra hint landing just
        // after a cycle already committed to running) while still failing loudly on anything
        // resembling per-hint dispatch.
        Assert.InRange(store.Leases.AcquireCalls, 1, 5);
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
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

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
            // other 49. No timer tick can fire in it.
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
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
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

        // A worker that mishandled the fault by re-arming a dead wake path in a tight loop would
        // run far more than a handful of cycles in this window; a healthy one polls at most a few
        // times over the up-to-10-second wait at a 250 ms interval, plus one on shutdown.
        Assert.True(
            store.Leases.AcquireCalls < 1000,
            $"expected a handful of poll-driven acquires, not a spin: got {store.Leases.AcquireCalls}.");
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
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher, options, NullLogger<StatesmanOutboxHostedService>.Instance, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Subscribed.WaitAsync(Timeout);

            int acquiresBeforeDispose = store.Leases.AcquireCalls;
            await store.DisposeAsync();

            // The pump's await foreach must fall out normally here, not throw -- that is what
            // "ends cleanly" means -- and Unsubscribed only completes from that finally block.
            await store.Unsubscribed.WaitAsync(Timeout);

            // The disarm path (wakeArmed = false; continue;) must not stop the timer side: the
            // worker keeps calling AcquireAsync on every subsequent tick regardless of the store's
            // own disposal, because DispatchOnceAsync only asks Leases, which is unaffected by it.
            await WaitUntilAsync(() => store.Leases.AcquireCalls > acquiresBeforeDispose, Timeout);
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
