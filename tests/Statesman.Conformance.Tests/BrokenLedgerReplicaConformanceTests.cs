using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="LedgerReplicaConformanceTests"/>'s import-is-exact
/// assertion. A conformance suite's own mechanism is discrimination: if its assertions pass against a
/// store that does not implement the contract, they prove nothing about the stores that do. The shared
/// assertion is therefore run here twice — against a deliberately wrong double, which must fail, and
/// against a deliberately correct one, which must pass. The second half matters as much as the first: a
/// suite that failed against everything would be as useless as one that passed against everything.
/// </summary>
public sealed class BrokenLedgerReplicaConformanceTests
{
    [Fact]
    public async Task The_import_is_exact_assertion_fails_against_a_replica_that_reallocates_the_position()
    {
        await using var store = new PositionReallocatingReplicaStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            LedgerReplicaConformanceTests.AssertImportIsExactAsync(store, store));
    }

    [Fact]
    public async Task The_import_is_exact_assertion_passes_against_a_replica_that_preserves_the_position()
    {
        await using var store = new InMemoryStateLedgerStore("correct-replica", TimeProvider.System);

        await LedgerReplicaConformanceTests.AssertImportIsExactAsync(store, store);
    }

    /// <summary>
    /// Implements both <see cref="IStateLedgerStore"/> and <see cref="IStateLedgerReplica"/>, forwarding
    /// every member to an inner in-memory store except <see cref="ImportAsync"/>, which ignores the
    /// record's own <see cref="StateRecord.GlobalPosition"/> and lets the inner store allocate its own
    /// instead. Deliberately wrong: this is the exact defect the shared assertion exists to catch — an
    /// import target that does not preserve the position an authoritative ledger already assigned.
    /// </summary>
    private sealed class PositionReallocatingReplicaStore : IStateLedgerStore, IStateLedgerReplica
    {
        private readonly InMemoryStateLedgerStore _inner = new("position-reallocating", TimeProvider.System);

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

        public async ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            // Deliberately ignores record.GlobalPosition and appends unconditionally, letting the
            // inner store allocate whatever position comes next instead of preserving the one the
            // imported record already carries.
            await _inner.AppendAsync(
                record.Address,
                StateWriteCondition.Any,
                new StateCommit
                {
                    Operation = record.Operation,
                    Status = record.Status,
                    ValueType = record.ValueType,
                    SchemaVersion = record.SchemaVersion,
                    Payload = record.Payload,
                    Source = record.Source,
                },
                cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
