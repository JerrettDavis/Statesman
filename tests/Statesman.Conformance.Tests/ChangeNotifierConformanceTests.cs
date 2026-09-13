namespace Statesman.Conformance.Tests;

/// <summary>
/// The change-hint contract every <see cref="IStateChangeNotifier"/> implementation shares, run once
/// per provider by a subclass. Capability presence is answered by
/// <see cref="StateCapabilityExtensions.TryGetCapability{TCapability}"/> at the test site, never by a
/// per-provider boolean: per <c>docs/architecture/capabilities.md</c> the filesystem and Entity
/// Framework Core providers have no notifier at all, and skip every fact here with an honest "No"
/// rather than a silent pass.
/// </summary>
public abstract class ChangeNotifierConformanceTests
{
    /// <summary>How long a fact waits for a hint before treating its absence as a contract failure.</summary>
    public static readonly TimeSpan HintTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often the append loop below checks whether a hint has arrived yet.</summary>
    private static readonly TimeSpan HintRetryInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that an append made after a subscriber's first <c>MoveNextAsync</c> has started
    /// produces a hint within a bounded window. Public and static so the negative test in this
    /// project can run it against a double that never publishes a hint, and require it to fail.
    /// </summary>
    /// <param name="store">The store to append through.</param>
    /// <param name="notifier">The notifier capability under test.</param>
    public static async Task AssertAnAppendSignalsASubscriberAsync(IStateLedgerStore store, IStateChangeNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notifier);
        using var subscriptionCts = new CancellationTokenSource();
        IAsyncEnumerator<StateChangeNotification> hints =
            notifier.SubscribeAsync(subscriptionCts.Token).GetAsyncEnumerator(subscriptionCts.Token);
        try
        {
            // Subscribing is lazy (Ledger.cs): for a provider whose SubscribeAsync suspends
            // synchronously up to its first genuine await, the subscription already exists by the
            // time this call returns even though the task it hands back is still pending -- so an
            // append made right after is never a race against subscribing. Redis's subscribe is a
            // real network round trip though, and PUBLISH has no backlog: a hint sent before the
            // SUBSCRIBE lands on the server is gone for good. Rather than guess a settle time --
            // widening a timing bound this mechanism does not name -- this keeps appending fresh
            // revisions, gated on the pending read completing, until it does or the bounded window
            // below runs out.
            Task<bool> pending = hints.MoveNextAsync().AsTask();
            var address = new StateAddress("app", $"conformance/notify-append-{Guid.NewGuid():N}", StatePartition.Default);
            DateTime deadline = DateTime.UtcNow + HintTimeout;
            long revision = 0;

            while (!pending.IsCompleted && DateTime.UtcNow < deadline)
            {
                revision++;
                StateWriteCondition condition = revision == 1
                    ? StateWriteCondition.Absent
                    : StateWriteCondition.AtRevision(revision - 1);
                await store.AppendAsync(address, condition, Commit($"v{revision}"));
                await Task.WhenAny(pending, Task.Delay(HintRetryInterval));
            }

            if (!pending.IsCompleted)
            {
                // A hint that has not arrived inside the window is a failure of the contract, and
                // the window is what stops this hanging forever -- never the assertion itself.
                await subscriptionCts.CancelAsync();
                try
                {
                    await pending;
                }
                catch (OperationCanceledException)
                {
                }

                Assert.Fail("No hint arrived from a live subscriber within the bounded window.");
            }

            Assert.True(await pending, "The subscription ended without ever yielding a hint.");
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_append_signals_a_live_subscriber()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateChangeNotifier? notifier), "This provider has no change notifier.");

        await AssertAnAppendSignalsASubscriberAsync(store.Store, notifier!);
    }

    [Fact]
    public async Task Disposing_the_store_ends_every_live_subscription()
    {
        ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateChangeNotifier? notifier), "This provider has no change notifier.");

        using var cts = new CancellationTokenSource(HintTimeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            notifier!.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> pending = hints.MoveNextAsync();

        await store.DisposeAsync();

        // False, not a hang and not a throw: disposing the store completes every live subscriber.
        Assert.False(await pending.AsTask().WaitAsync(HintTimeout));
        await hints.DisposeAsync();
    }

    [Fact]
    public async Task A_consumer_that_ignores_every_hint_still_reads_every_record()
    {
        // The contract's own escape clause (Ledger.cs: "losing every hint costs latency and nothing
        // else"), and the assertion a provider is most likely to break by making a hint carry data
        // the feed itself depends on.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateChangeNotifier? _), "This provider has no change notifier.");

        var address = new StateAddress("app", $"conformance/notify-ignored-{Guid.NewGuid():N}", StatePartition.Default);
        for (long revision = 1; revision <= 25; revision++)
        {
            StateWriteCondition condition = revision == 1
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(revision - 1);
            Assert.True((await store.Store.AppendAsync(address, condition, Commit($"v{revision}"))).Succeeded);
        }

        List<StateChangeEnvelope> changes = await DrainAsync(store.Feed, address);

        Assert.Equal(25, changes.Count);
    }

    private static async Task<List<StateChangeEnvelope>> DrainAsync(IStateChangeFeed feed, StateAddress address)
    {
        var changes = new List<StateChangeEnvelope>();
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            if (envelope.Record.Address.Canonical == address.Canonical)
            {
                changes.Add(envelope);
            }
        }

        return changes;
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
