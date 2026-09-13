using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="ImportRejectionConformanceTests"/>'s import-refusal
/// assertion. A conformance suite's own mechanism is discrimination: if its assertions pass against a
/// store that does not implement the contract, they prove nothing about the stores that do. The shared
/// assertion is therefore run here twice — against a deliberately wrong double, which must fail, and
/// against a deliberately correct one, which must pass. The second half matters as much as the first: a
/// suite that failed against everything would be as useless as one that passed against everything.
/// </summary>
public sealed class BrokenImportRejectionConformanceTests
{
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
    /// every member to an inner in-memory store except <see cref="ImportAsync"/>, which skips
    /// <see cref="StateRecord.Validate"/> and replays the record as an unconditional
    /// <see cref="AppendAsync"/>. Deliberately wrong: it accepts a record the contract requires it to
    /// refuse. It does <b>not</b> write the damaged record verbatim — an append re-derives revision and
    /// global position from the inner store, so what lands is a fresh record carrying the damaged one's
    /// operation, status, value type, schema version, payload and source. That is enough to fail the
    /// shared assertion, whose discriminating half reads the address back and requires it to be absent.
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
