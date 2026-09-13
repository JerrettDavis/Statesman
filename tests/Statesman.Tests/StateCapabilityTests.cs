using System.Runtime.CompilerServices;

namespace Statesman.Tests;

public sealed class StateCapabilityTests
{
    [Fact]
    public void TryGetCapability_returns_true_when_the_store_implements_the_capability()
    {
        var store = new InMemoryStateLedgerStore();

        bool found = store.TryGetCapability(out IStateLedgerReplica? replica);

        Assert.True(found);
        Assert.NotNull(replica);
        Assert.Same(store, replica);
    }

    [Fact]
    public void TryGetCapability_returns_false_when_the_store_does_not_implement_the_capability()
    {
        IStateLedgerStore store = new NonCapableStore();

        bool found = store.TryGetCapability(out IStateLedgerReplica? replica);

        Assert.False(found);
        Assert.Null(replica);
    }

    [Fact]
    public void TryGetCapability_returns_true_when_the_store_forwards_via_IStateCapabilityProvider()
    {
        var target = new InMemoryStateLedgerStore();
        IStateLedgerStore store = new ForwardingStore(target);

        bool found = store.TryGetCapability(out IStateLedgerReplica? replica);

        Assert.True(found);
        Assert.Same(target, replica);
    }

    [Fact]
    public void TryGetCapability_returns_false_when_forwarding_also_fails()
    {
        IStateLedgerStore store = new ForwardingStore(forwardTarget: null);

        bool found = store.TryGetCapability(out IStateLedgerReplica? replica);

        Assert.False(found);
        Assert.Null(replica);
    }

    [Fact]
    public void TryGetCapability_lets_a_capability_provider_decline_a_capability_it_implements_itself()
    {
        // The rule ROADMAP 0.3 Phase 17 introduced: a store that declares IStateCapabilityProvider
        // answers discovery for itself, and its "no" is final — the direct cast never runs behind it.
        // Without this, a composing store cannot honestly decline a capability its own type declares,
        // because the cast sees the declaration and returns before the provider is ever asked. That is
        // the tiered store's case exactly: it declares five capabilities it delegates to a tier that
        // may not back them. Pre-Phase-17 addendum decision 72.
        IStateLedgerStore store = new DecliningProviderStore();

        bool found = store.TryGetCapability(out IStateLedgerReplica? replica);

        Assert.False(found);
        Assert.Null(replica);
    }

    private sealed class NonCapableStore : IStateLedgerStore
    {
        public string Name => "non-capable";

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StateRecord?>(null);

        public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("NonCapableStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ForwardingStore : IStateLedgerStore, IStateCapabilityProvider
    {
        private readonly object? _forwardTarget;

        public ForwardingStore(object? forwardTarget) => _forwardTarget = forwardTarget;

        public string Name => "forwarding";

        public bool TryGetCapability(Type capabilityType, out object? capability)
        {
            capability = _forwardTarget is not null && capabilityType.IsInstanceOfType(_forwardTarget)
                ? _forwardTarget
                : null;
            return capability is not null;
        }

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StateRecord?>(null);

        public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ForwardingStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Declares <see cref="IStateLedgerReplica"/> on its own type and declines it through
    /// <see cref="IStateCapabilityProvider"/>. A composing store that cannot back what it declares.
    /// </summary>
    private sealed class DecliningProviderStore : IStateLedgerStore, IStateLedgerReplica, IStateCapabilityProvider
    {
        public string Name => "declining";

        public bool TryGetCapability(Type capabilityType, out object? capability)
        {
            capability = null;
            return false;
        }

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StateRecord?>(null);

        public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("DecliningProviderStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("DecliningProviderStore does not import.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
