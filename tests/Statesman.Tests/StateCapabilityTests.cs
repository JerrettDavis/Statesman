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
}
