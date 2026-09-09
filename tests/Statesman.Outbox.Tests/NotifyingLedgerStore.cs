using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Statesman.Outbox.Tests;

/// <summary>
/// An in-memory store whose change hints and lease outcomes a test drives by hand. Hint delivery
/// fans out to every subscriber independently -- one unbounded channel per subscription, exactly
/// the topology <see cref="InMemoryStateLedgerStore.SubscribeAsync"/> and Redis pub/sub both use --
/// rather than one shared channel that turns <see cref="Signal"/> into a work queue where two
/// subscribers steal hints from each other. That was the Phase 10 ubuntu bug: with two replicas
/// racing one shared channel, whichever hints landed on the standby's pump were gone for the
/// leader, which could stall a handful of records short of the total with no further hint to
/// recover it. Each subscriber's channel stays unbounded and never coalesces on its own: the
/// coalescing under test belongs to the hosted worker's own capacity-1 channel
/// (<c>StatesmanOutboxHostedService._wake</c>), and a double that coalesced too -- as production's
/// bounded, <c>DropWrite</c> channel does -- would hide a regression there instead of failing the
/// burst-coalescing test.
/// </summary>
internal sealed class NotifyingLedgerStore
    : IStateLedgerStore, IStateChangeFeed, IStateLedgerReplica, IStateLeaseProvider, IStateChangeNotifier
{
    private readonly InMemoryStateLedgerStore _inner;
    private readonly ConcurrentDictionary<Guid, Channel<StateChangeNotification>> _subscribers = new();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _unsubscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NotifyingLedgerStore(string name = "notifying") => _inner = new InMemoryStateLedgerStore(name);

    /// <summary>The lease outcomes this store reports, so a test can put the worker into standby.</summary>
    public FakeLeaseProvider Leases { get; } = new();

    /// <summary>Completes once the worker's subscription has actually begun enumerating.</summary>
    public Task Subscribed => _subscribed.Task;

    /// <summary>Completes once that subscription has been torn down.</summary>
    public Task Unsubscribed => _unsubscribed.Task;

    /// <summary>Raises one hint to every active subscriber. Never blocks, and never drops.</summary>
    public void Signal()
    {
        foreach (Channel<StateChangeNotification> channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(default);
        }
    }

    /// <summary>Hints raised, across every subscriber, that a pump has not yet taken from this store.</summary>
    public int PendingHints => _subscribers.Values.Sum(channel => channel.Reader.Count);

    /// <summary>
    /// Ends every active subscription with a non-cancellation exception, as a live notifier could
    /// after a provider-side failure -- distinct from both a caller cancellation and
    /// <see cref="DisposeAsync"/>'s clean end. Any hint already raised is still yielded first; the
    /// exception surfaces only once each enumerator asks for its next one.
    /// </summary>
    public void Fault(Exception exception)
    {
        foreach (Channel<StateChangeNotification> channel in _subscribers.Values)
        {
            channel.Writer.TryComplete(exception);
        }
    }

    public string Name => _inner.Name;

    public ValueTask<StateRecord?> ReadLatestAsync(StateAddress address, CancellationToken cancellationToken = default) =>
        _inner.ReadLatestAsync(address, cancellationToken);

    public IAsyncEnumerable<StateRecord> ReadHistoryAsync(StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
        _inner.ReadHistoryAsync(address, options, cancellationToken);

    public ValueTask<StateAppendResult> AppendAsync(StateAddress address, StateWriteCondition condition, StateCommit commit, CancellationToken cancellationToken = default) =>
        _inner.AppendAsync(address, condition, commit, cancellationToken);

    public ValueTask PruneAsync(StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
        _inner.PruneAsync(address, policy, cancellationToken);

    public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default) =>
        _inner.ImportAsync(record, cancellationToken);

    public IAsyncEnumerable<StateChangeEnvelope> ReadAsync(StateChangeCursor? from, StateChangeReadOptions options, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(from, options, cancellationToken);

    public ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Leases.AcquireAsync(leaseId, ttl, cancellationToken);

    public async IAsyncEnumerable<StateChangeNotification> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        Channel<StateChangeNotification> channel = Channel.CreateUnbounded<StateChangeNotification>();

        // Registered before the first await, mirroring InMemoryStateLedgerStore.SubscribeAsync: an
        // async iterator body runs synchronously up to its first suspension, so this subscription
        // exists -- and Signal can already reach it -- as soon as the caller's first
        // MoveNextAsync has returned.
        _subscribers[id] = channel;
        _subscribed.TrySetResult();
        try
        {
            await foreach (StateChangeNotification hint in
                channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return hint;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
            _unsubscribed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Every real provider ends a live subscription cleanly -- no exception -- once the store
        // itself is disposed. Completing every subscriber's channel here (rather than only
        // disposing _inner) is what lets a test exercise that clean-end path instead of only
        // cancellation or a fault.
        foreach (Channel<StateChangeNotification> channel in _subscribers.Values)
        {
            channel.Writer.TryComplete();
        }

        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
