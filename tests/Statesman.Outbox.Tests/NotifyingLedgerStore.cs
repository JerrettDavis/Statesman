using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Statesman.Outbox.Tests;

/// <summary>
/// An in-memory store whose change hints and lease outcomes a test drives by hand. The hint channel
/// is deliberately unbounded and never coalesces: the coalescing under test belongs to the hosted
/// worker's own capacity-1 channel, and a double that coalesced too would hide it.
/// </summary>
internal sealed class NotifyingLedgerStore
    : IStateLedgerStore, IStateChangeFeed, IStateLedgerReplica, IStateLeaseProvider, IStateChangeNotifier
{
    private readonly InMemoryStateLedgerStore _inner;
    private readonly Channel<StateChangeNotification> _hints = Channel.CreateUnbounded<StateChangeNotification>();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _unsubscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NotifyingLedgerStore(string name = "notifying") => _inner = new InMemoryStateLedgerStore(name);

    /// <summary>The lease outcomes this store reports, so a test can put the worker into standby.</summary>
    public FakeLeaseProvider Leases { get; } = new();

    /// <summary>Completes once the worker's subscription has actually begun enumerating.</summary>
    public Task Subscribed => _subscribed.Task;

    /// <summary>Completes once that subscription has been torn down.</summary>
    public Task Unsubscribed => _unsubscribed.Task;

    /// <summary>Raises one hint. Never blocks, and never drops.</summary>
    public void Signal() => _hints.Writer.TryWrite(default);

    /// <summary>
    /// Ends the subscription with a non-cancellation exception, as a live notifier could after a
    /// provider-side failure -- distinct from both a caller cancellation and <see cref="DisposeAsync"/>'s
    /// clean end. Any hint already raised is still yielded first; the exception surfaces only once
    /// the enumerator asks for the next one.
    /// </summary>
    public void Fault(Exception exception) => _hints.Writer.TryComplete(exception);

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

    public IAsyncEnumerable<StateChangeEnvelope> ReadAsync(StateChangeCursor? from, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(from, cancellationToken);

    public ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Leases.AcquireAsync(leaseId, ttl, cancellationToken);

    public async IAsyncEnumerable<StateChangeNotification> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _subscribed.TrySetResult();
        try
        {
            await foreach (StateChangeNotification hint in
                _hints.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return hint;
            }
        }
        finally
        {
            _unsubscribed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Every real provider ends a live subscription cleanly -- no exception -- once the store
        // itself is disposed. Completing the hint channel here (rather than only disposing _inner)
        // is what lets a test exercise that clean-end path instead of only cancellation or a fault.
        _hints.Writer.TryComplete();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
