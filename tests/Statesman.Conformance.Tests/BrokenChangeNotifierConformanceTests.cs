using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="ChangeNotifierConformanceTests"/>'s hint-delivery
/// assertion. A conformance suite's own mechanism is discrimination: if its assertions pass against a
/// store that does not implement the contract, they prove nothing about the stores that do. The shared
/// assertion is therefore run here twice — against a deliberately wrong double, which must fail, and
/// against a deliberately correct one, which must pass. The second half matters as much as the first: a
/// suite that failed against everything would be as useless as one that passed against everything.
/// </summary>
public sealed class BrokenChangeNotifierConformanceTests
{
    [Fact]
    public async Task The_change_notifier_assertion_fails_against_a_store_that_never_publishes_a_hint()
    {
        await using var store = new NeverNotifyingStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            ChangeNotifierConformanceTests.AssertAnAppendSignalsASubscriberAsync(store, store));
    }

    [Fact]
    public async Task The_change_notifier_assertion_passes_against_a_store_that_publishes_a_hint()
    {
        await using var store = new InMemoryStateLedgerStore("correct-notifier", TimeProvider.System);

        await ChangeNotifierConformanceTests.AssertAnAppendSignalsASubscriberAsync(store, store);
    }

    /// <summary>
    /// Implements both <see cref="IStateLedgerStore"/> and <see cref="IStateChangeNotifier"/>,
    /// forwarding every store member to an inner in-memory store but never publishing a hint from
    /// <see cref="SubscribeAsync"/> — it only ever ends when the caller's own token is cancelled.
    /// Deliberately wrong: this is the exact defect the shared assertion exists to catch, a notifier
    /// that advertises the capability but never signals a live subscriber.
    /// </summary>
    private sealed class NeverNotifyingStore : IStateLedgerStore, IStateChangeNotifier
    {
        private readonly InMemoryStateLedgerStore _inner = new("never-notifying", TimeProvider.System);

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public async IAsyncEnumerable<StateChangeNotification> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Deliberately never yields a hint; the sequence only ends via the caller's own token.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
