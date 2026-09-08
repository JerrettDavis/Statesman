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

            // A thousand hints must not become a thousand cycles, and must not block the caller
            // raising them: the worker's sink is a capacity-1 DropWrite channel.
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
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected condition was never observed within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
