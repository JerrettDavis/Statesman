using System.Text;

namespace Statesman.Tooling.Tests;

public sealed class StateLedgerRestoreTests
{
    private static readonly StatesmanManifest Manifest = new()
    {
        Id = "app",
        Version = "1.0",
        Fingerprint = "fingerprint-app",
    };

    [Fact]
    public async Task RestoreAsync_round_trips_an_export_into_an_empty_store_preserving_revisions_and_positions()
    {
        await using var source = new InMemoryStateLedgerStore("source");
        var addressA = new StateAddress("app", "restore/a", StatePartition.Default);
        var addressB = new StateAddress("app", "restore/b", new StatePartition("tenant-1"));
        StateAppendResult a1 = await source.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult b1 = await source.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult a2 = await source.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        byte[] export = await ExportAsync(source, Manifest);

        await using var target = new InMemoryStateLedgerStore("target");
        StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export));

        Assert.Equal(3, summary.Records);
        Assert.Equal(2, summary.Partitions);
        Assert.Equal("fingerprint-app", summary.Fingerprint);

        StateRecord? headA = await target.ReadLatestAsync(addressA);
        Assert.Equal(2, headA!.Revision);
        Assert.Equal(a2.Record!.GlobalPosition, headA.GlobalPosition);
        Assert.Equal("a2", Encoding.UTF8.GetString(headA.Payload!));
        Assert.Equal(a2.Record.OccurredAt, headA.OccurredAt);

        List<StateRecord> historyA = [];
        await foreach (StateRecord record in target.ReadHistoryAsync(addressA, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            historyA.Add(record);
        }

        Assert.Equal(new[] { a1.Record!.GlobalPosition, a2.Record.GlobalPosition }, historyA.Select(record => record.GlobalPosition).ToArray());

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in target.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(
            new[] { a1.Record.GlobalPosition, b1.Record!.GlobalPosition, a2.Record.GlobalPosition },
            changes.Select(envelope => envelope.Cursor.Position).ToArray());

        StateAppendResult next = await target.AppendAsync(addressB, StateWriteCondition.AtRevision(1), Commit("b2"));
        Assert.True(next.Record!.GlobalPosition > a2.Record.GlobalPosition,
            "an append after restore must allocate above every imported position");
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_fingerprint_mismatch_without_touching_the_target()
    {
        byte[] export = await ExportSampleAsync();
        await using var target = new InMemoryStateLedgerStore("target");
        StatesmanManifest other = Manifest with { Fingerprint = "fingerprint-other" };

        StateLedgerRestoreException exception = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, other, new MemoryStream(export)));

        Assert.Contains("fingerprint-app", exception.Message);
        Assert.Contains("fingerprint-other", exception.Message);
        Assert.Empty(await PartitionsAsync(target));
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_root_mismatch_without_touching_the_target()
    {
        byte[] export = await ExportSampleAsync();
        await using var target = new InMemoryStateLedgerStore("target");
        StatesmanManifest other = Manifest with { Id = "other-app" };

        StateLedgerRestoreException exception = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, other, new MemoryStream(export)));

        Assert.Contains("root", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await PartitionsAsync(target));
    }

    [Fact]
    public async Task RestoreAsync_refuses_an_unknown_format_and_a_file_that_is_not_an_export()
    {
        byte[] export = await ExportSampleAsync();
        string text = Encoding.UTF8.GetString(export);
        string wrongVersion = text.Replace(StateLedgerExportFormat.Version, "statesman.ledger-export/v99", StringComparison.Ordinal);
        await using var target = new InMemoryStateLedgerStore("target");

        StateLedgerRestoreException versionException = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(Encoding.UTF8.GetBytes(wrongVersion))));
        Assert.Contains("statesman.ledger-export/v99", versionException.Message);

        StateLedgerRestoreException garbageException = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(Encoding.UTF8.GetBytes("not json\n"))));
        Assert.Contains("header", garbageException.Message, StringComparison.OrdinalIgnoreCase);

        StateLedgerRestoreException emptyException = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream()));
        Assert.Contains("empty", emptyException.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await PartitionsAsync(target));
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_truncated_export_without_importing_any_record()
    {
        byte[] export = await ExportSampleAsync();
        string[] lines = Encoding.UTF8.GetString(export).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string missingTrailer = string.Join('\n', lines[..^1]) + "\n";
        string missingRecord = string.Join('\n', lines.Where((_, index) => index != 1)) + "\n";
        await using var target = new InMemoryStateLedgerStore("target");

        StateLedgerRestoreException noTrailer = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(Encoding.UTF8.GetBytes(missingTrailer))));
        Assert.Contains("truncated", noTrailer.Message, StringComparison.OrdinalIgnoreCase);

        StateLedgerRestoreException countMismatch = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(Encoding.UTF8.GetBytes(missingRecord))));
        Assert.Contains("declares 3", countMismatch.Message);
        Assert.Contains("contains 2", countMismatch.Message);

        Assert.Empty(await PartitionsAsync(target));
    }

    [Fact]
    public async Task RestoreAsync_refuses_an_invalid_record_line_without_importing_any_record()
    {
        byte[] export = await ExportSampleAsync();
        string[] lines = Encoding.UTF8.GetString(export).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[1] = lines[1].Replace("\"revision\":1", "\"revision\":0", StringComparison.Ordinal);
        Assert.Contains("\"revision\":0", lines[1]);
        await using var target = new InMemoryStateLedgerStore("target");

        StateLedgerRestoreException exception = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"))));

        Assert.Contains("line 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await PartitionsAsync(target));
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_record_from_another_root_without_importing_any_record()
    {
        byte[] export = await ExportSampleAsync();
        string[] lines = Encoding.UTF8.GetString(export).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[2] = lines[2].Replace("\"root\":\"app\"", "\"root\":\"other\"", StringComparison.Ordinal);
        Assert.Contains("\"root\":\"other\"", lines[2]);
        await using var target = new InMemoryStateLedgerStore("target");

        StateLedgerRestoreException exception = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"))));

        Assert.Contains("line 3", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'other'", exception.Message);
        Assert.Empty(await PartitionsAsync(target));
    }

    [Fact]
    public async Task RestoreAsync_throws_NotSupportedException_when_the_target_cannot_import_exact_records()
    {
        byte[] export = await ExportSampleAsync();
        await using var target = new MinimalLedgerStore();

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export)));

        Assert.Contains("IStateLedgerReplica", exception.Message);
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_target_that_already_holds_the_root_unless_allowed_and_ignores_other_roots()
    {
        byte[] export = await ExportSampleAsync();
        await using var target = new InMemoryStateLedgerStore("target");
        await target.AppendAsync(new StateAddress("other", "restore/x", StatePartition.Default), StateWriteCondition.Absent, Commit("x"));

        StateLedgerRestoreSummary first = await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export));
        Assert.Equal(3, first.Records);

        StateLedgerRestoreException exception = await Assert.ThrowsAsync<StateLedgerRestoreException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export)));
        Assert.Contains("already holds", exception.Message);
        Assert.Contains("AllowNonEmptyTarget", exception.Message);

        StateLedgerRestoreSummary again = await StateLedgerRestore.RestoreAsync(
            target, Manifest, new MemoryStream(export), new StateLedgerRestoreOptions { AllowNonEmptyTarget = true });
        Assert.Equal(3, again.Records);

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in target.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public async Task RestoreFromFileAsync_round_trips_a_file_written_by_ExportToFileAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "ledger.export.jsonl");
        try
        {
            await using var source = new InMemoryStateLedgerStore("source");
            var address = new StateAddress("app", "restore/file", StatePartition.Default);
            await source.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
            await StateLedgerExport.ExportToFileAsync(source, Manifest, path);

            await using var target = new InMemoryStateLedgerStore("target");
            StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreFromFileAsync(target, Manifest, path);

            Assert.Equal(1, summary.Records);
            Assert.Equal("one", Encoding.UTF8.GetString((await target.ReadLatestAsync(address))!.Payload!));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoreAsync_leaves_the_target_partially_restored_when_the_import_throws_partway_through()
    {
        // Phase 6 parked this, and it is the one case the whole validate-first design exists to bound:
        // every other failure test in this file is a validation refusal that happens BEFORE the target
        // is contacted, so nothing pinned what a target holds after it has accepted N records and then
        // thrown. The answer is "the first N", and that is documented behaviour rather than a defect --
        // StateLedgerRestore has no transaction across a target it does not own, and every provider's
        // ImportAsync is exact and idempotent, so re-running the restore is the supported recovery.
        byte[] export = await ExportSampleAsync();
        await using var inner = new InMemoryStateLedgerStore("target");
        await using var target = new FailingImportStore(inner, throwOnCall: 3);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export)));

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, target.ImportCalls);

        // Two of the three records landed; the third is the one that threw. Pinned deliberately.
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in inner.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
    }

    /// <summary>
    /// Forwards to an inner store, counting <see cref="IStateLedgerReplica.ImportAsync"/> calls and
    /// throwing on the Nth — so a restore fails after the target has already accepted records.
    /// </summary>
    private sealed class FailingImportStore : IStateLedgerStore, IStateLedgerReplica, IPartitionCatalog
    {
        private readonly InMemoryStateLedgerStore _inner;
        private readonly int _throwOnCall;
        private int _importCalls;

        public FailingImportStore(InMemoryStateLedgerStore inner, int throwOnCall)
        {
            _inner = inner;
            _throwOnCall = throwOnCall;
        }

        public int ImportCalls => Volatile.Read(ref _importCalls);

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

        public IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
            CancellationToken cancellationToken = default) =>
            ((IPartitionCatalog)_inner).ListPartitionsAsync(cancellationToken);

        public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _importCalls) == _throwOnCall)
            {
                throw new InvalidOperationException("the target rejected this record");
            }

            return ((IStateLedgerReplica)_inner).ImportAsync(record, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<byte[]> ExportSampleAsync()
    {
        await using var source = new InMemoryStateLedgerStore("source");
        var addressA = new StateAddress("app", "restore/a", StatePartition.Default);
        var addressB = new StateAddress("app", "restore/b", StatePartition.Default);
        await source.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        await source.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        await source.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        return await ExportAsync(source, Manifest);
    }

    private static async Task<byte[]> ExportAsync(IStateLedgerStore source, StatesmanManifest manifest)
    {
        using var output = new MemoryStream();
        await StateLedgerExport.ExportAsync(source, manifest, output);
        return output.ToArray();
    }

    private static async Task<List<StatePartitionDescriptor>> PartitionsAsync(IStateLedgerStore store)
    {
        List<StatePartitionDescriptor> partitions = [];
        await foreach (StatePartitionDescriptor descriptor in ((IPartitionCatalog)store).ListPartitionsAsync())
        {
            partitions.Add(descriptor);
        }

        return partitions;
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
