using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proofs for this project's shared suites. A conformance suite's own
/// mechanism is discrimination: if its assertions pass against a store that does not implement the
/// contract, they prove nothing about the stores that do. Every shared assertion that can be reduced
/// to a reusable method is therefore run here twice — against a deliberately wrong double, which must
/// fail, and against a deliberately correct one, which must pass. The second half matters as much as
/// the first: a suite that failed against everything would be as useless as one that passed against
/// everything.
/// </summary>
public sealed class BrokenStoreConformanceTests
{
    [Fact]
    public async Task The_exclusive_creation_assertion_fails_against_a_store_that_ignores_its_write_condition()
    {
        await using var store = new UnconditionalStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            LedgerWriteConformanceTests.AssertExclusiveCreationAsync(store));
    }

    [Fact]
    public async Task The_exclusive_creation_assertion_passes_against_a_store_that_honours_it()
    {
        await using var store = new InMemoryStateLedgerStore("correct", TimeProvider.System);

        await LedgerWriteConformanceTests.AssertExclusiveCreationAsync(store);
    }

    /// <summary>
    /// Appends unconditionally, ignoring <see cref="StateWriteCondition"/> entirely, so every racer
    /// wins. Deliberately wrong: this is the exact defect the shared assertion exists to catch.
    /// </summary>
    private sealed class UnconditionalStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner = new("unconditional", TimeProvider.System);

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
            _inner.AppendAsync(address, StateWriteCondition.Any, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Fact]
    public async Task The_cancellation_assertion_fails_against_a_store_that_swallows_its_token()
    {
        await using var store = new TokenSwallowingStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            CancellationConformanceTests.AssertAppendHonoursCancellationAsync(store));
    }

    [Fact]
    public async Task The_cancellation_assertion_passes_against_a_store_that_honours_its_token()
    {
        await using var store = new InMemoryStateLedgerStore("correct-cancellation", TimeProvider.System);

        await CancellationConformanceTests.AssertAppendHonoursCancellationAsync(store);
    }

    /// <summary>
    /// Drops the caller's token on the floor and appends anyway. Deliberately wrong: this is the
    /// shape of the Phase 15 Redis catalog defect, moved onto the write path.
    /// </summary>
    private sealed class TokenSwallowingStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner = new("swallowing", TimeProvider.System);

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, CancellationToken.None);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, CancellationToken.None);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, CancellationToken.None);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, CancellationToken.None);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Fact]
    public async Task The_import_rejection_assertion_fails_against_a_replica_that_skips_validation()
    {
        await using var store = new ValidationSkippingStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            ImportRejectionConformanceTests.AssertInvalidImportIsRefusedAsync(store, store));
    }

    [Fact]
    public async Task The_import_rejection_assertion_passes_against_a_replica_that_validates()
    {
        await using var store = new InMemoryStateLedgerStore("correct-import", TimeProvider.System);

        await ImportRejectionConformanceTests.AssertInvalidImportIsRefusedAsync(store, store);
    }

    /// <summary>
    /// Implements both <see cref="IStateLedgerStore"/> and <see cref="IStateLedgerReplica"/>, forwarding
    /// every member to an inner in-memory store except <see cref="ImportAsync"/>, which writes through
    /// an unconditional append instead of calling <see cref="StateRecord.Validate"/> first. Deliberately
    /// wrong: this is the exact defect the shared assertion exists to catch — an import target that
    /// skips validation and writes a damaged record anyway.
    /// </summary>
    private sealed class ValidationSkippingStore : IStateLedgerStore, IStateLedgerReplica
    {
        private readonly InMemoryStateLedgerStore _inner = new("validation-skipping", TimeProvider.System);

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

            // Deliberately skips StateRecord.Validate and writes the damaged record through anyway.
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
