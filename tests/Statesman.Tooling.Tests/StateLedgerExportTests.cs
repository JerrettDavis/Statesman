using System.Text;
using System.Text.Json;

namespace Statesman.Tooling.Tests;

public sealed class StateLedgerExportTests
{
    private static readonly StatesmanManifest Manifest = new()
    {
        Id = "app",
        Version = "1.0",
        Fingerprint = "fingerprint-app",
    };

    [Fact]
    public async Task ExportAsync_writes_header_every_retained_revision_in_partition_then_revision_order_and_a_trailer()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        var addressB = new StateAddress("app", "export/b", StatePartition.Default);
        var addressA = new StateAddress("app", "export/a", new StatePartition("tenant-1"));
        var foreign = new StateAddress("other", "export/a", StatePartition.Default);
        StateAppendResult b1 = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult a1 = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult a2 = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        await store.AppendAsync(foreign, StateWriteCondition.Absent, Commit("foreign"));

        using var output = new MemoryStream();
        StateLedgerExportSummary summary = await StateLedgerExport.ExportAsync(store, Manifest, output);

        string[] lines = Lines(output);
        Assert.Equal(5, lines.Length);

        StateLedgerExportHeader header = Deserialize<StateLedgerExportHeader>(lines[0]);
        Assert.Equal(StateLedgerExportFormat.Version, header.Format);
        Assert.Equal("app", header.Root);
        Assert.Equal("fingerprint-app", header.Fingerprint);
        Assert.Equal("memory", header.Store);

        StateLedgerExportRecord[] records = lines[1..^1].Select(Deserialize<StateLedgerExportRecord>).ToArray();
        Assert.Equal(
            new[] { ("export/a", "tenant-1", 1L), ("export/a", "tenant-1", 2L), ("export/b", "default", 1L) },
            records.Select(record => (record.Path, record.Partition, record.Revision)).ToArray());
        Assert.Equal(
            new[] { a1.Record!.GlobalPosition, a2.Record!.GlobalPosition, b1.Record!.GlobalPosition },
            records.Select(record => record.GlobalPosition).ToArray());
        Assert.Equal("a2", Encoding.UTF8.GetString(records[1].Payload!));

        StateLedgerExportTrailer trailer = Deserialize<StateLedgerExportTrailer>(lines[^1]);
        Assert.Equal(3, trailer.Records);
        Assert.Equal(2, trailer.Partitions);

        Assert.Equal("app", summary.Root);
        Assert.Equal("fingerprint-app", summary.Fingerprint);
        Assert.Equal(3, summary.Records);
        Assert.Equal(2, summary.Partitions);
    }

    [Fact]
    public async Task ExportAsync_includes_more_revisions_than_the_default_history_page()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        var address = new StateAddress("app", "export/long", StatePartition.Default);
        await store.AppendAsync(address, StateWriteCondition.Absent, Commit("0"));
        for (int revision = 1; revision < 130; revision++)
        {
            await store.AppendAsync(address, StateWriteCondition.AtRevision(revision), Commit(revision.ToString()));
        }

        using var output = new MemoryStream();
        StateLedgerExportSummary summary = await StateLedgerExport.ExportAsync(store, Manifest, output);

        Assert.Equal(130, summary.Records);
        Assert.Equal(132, Lines(output).Length);
    }

    [Fact]
    public async Task ExportAsync_round_trips_every_record_field_through_the_export_record()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        var address = new StateAddress("app", "export/fields", new StatePartition("p"));
        var commit = new StateCommit
        {
            Operation = StateOperation.Faulted,
            Status = StateStatus.Faulted,
            ValueType = "Sample.Type",
            SchemaVersion = 3,
            Payload = null,
            FreshUntil = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero),
            ServeUntil = new DateTimeOffset(2026, 9, 4, 11, 0, 0, TimeSpan.Zero),
            Source = "loader",
            CorrelationId = "corr-1",
            CausationId = "cause-1",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["region"] = "eu" },
            Error = new StateError("E1", "boom", "System.Exception", "detail", IsTransient: true),
        };
        StateAppendResult appended = await store.AppendAsync(address, StateWriteCondition.Absent, commit);
        StateRecord original = appended.Record!;

        using var output = new MemoryStream();
        await StateLedgerExport.ExportAsync(store, Manifest, output);

        StateRecord restored = Deserialize<StateLedgerExportRecord>(Lines(output)[1]).ToRecord();
        Assert.Equal(original.Address, restored.Address);
        Assert.Equal(original.Revision, restored.Revision);
        Assert.Equal(original.GlobalPosition, restored.GlobalPosition);
        Assert.Equal(original.OccurredAt, restored.OccurredAt);
        Assert.Equal(original.Operation, restored.Operation);
        Assert.Equal(original.Status, restored.Status);
        Assert.Equal(original.ValueType, restored.ValueType);
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Null(restored.Payload);
        Assert.Equal(original.FreshUntil, restored.FreshUntil);
        Assert.Equal(original.ServeUntil, restored.ServeUntil);
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.CorrelationId, restored.CorrelationId);
        Assert.Equal(original.CausationId, restored.CausationId);
        Assert.Equal("eu", restored.Metadata["REGION"]);
        Assert.Equal(original.Error, restored.Error);
    }

    [Fact]
    public async Task ExportAsync_throws_NotSupportedException_when_the_source_has_no_partition_catalog()
    {
        await using var store = new MinimalLedgerStore();
        using var output = new MemoryStream();

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await StateLedgerExport.ExportAsync(store, Manifest, output));

        Assert.Contains("IPartitionCatalog", exception.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ExportToFileAsync_creates_the_directory_and_writes_the_export()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "nested", "ledger.export.jsonl");
        try
        {
            await using var store = new InMemoryStateLedgerStore("memory");
            await store.AppendAsync(new StateAddress("app", "export/file", StatePartition.Default), StateWriteCondition.Absent, Commit("one"));

            StateLedgerExportSummary summary = await StateLedgerExport.ExportToFileAsync(store, Manifest, path);

            Assert.Equal(1, summary.Records);
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(StateLedgerExportFormat.Version, Deserialize<StateLedgerExportHeader>(lines[0]).Format);
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
    public async Task ExportToFileAsync_leaves_the_previous_file_intact_when_the_export_fails()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "ledger.export.jsonl");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "previous backup\n");
            await using var store = new MinimalLedgerStore();

            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await StateLedgerExport.ExportToFileAsync(store, Manifest, path));

            Assert.Equal("previous backup\n", await File.ReadAllTextAsync(path));
            Assert.False(File.Exists(path + ".tmp"));
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
    public async Task ExportToFileAsync_leaves_no_file_at_the_destination_when_cancelled_after_the_header()
    {
        // Phase 6 parked this. ExportAsync writes the header, then checks the token once per record
        // (StateLedgerExport.cs:103-115), and ExportToFileAsync writes to path + ".tmp" and deletes it
        // on any exception (:146-166) -- so cancelling after the header must leave NEITHER file. The
        // existing ExportToFileAsync_leaves_the_previous_file_intact_when_the_export_fails covers the
        // failure case against a destination that already held a file; this is the cancellation twin
        // against a destination that held nothing.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "ledger.export.jsonl");
        try
        {
            await using var inner = new InMemoryStateLedgerStore("memory");
            await inner.AppendAsync(
                new StateAddress("app", "export/cancel", StatePartition.Default),
                StateWriteCondition.Absent,
                Commit("one"));

            using var cancellation = new CancellationTokenSource();

            // Cancels as the first history record is about to be read, which is after the header has
            // been written and before any record line has. Deterministic: no timer, no sleep.
            await using var source = new CancellingHistoryStore(inner, cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await StateLedgerExport.ExportToFileAsync(source, Manifest, path, options: null, cancellation.Token));

            Assert.False(File.Exists(path), "a cancelled export must not leave a partial file at the destination");
            Assert.False(File.Exists(path + ".tmp"), "a cancelled export must not leave its temporary file behind");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Forwards to an inner store, cancelling the supplied source the first time history is
    /// enumerated — so cancellation lands after the export's header and before its first record.
    /// </summary>
    private sealed class CancellingHistoryStore : IStateLedgerStore, IPartitionCatalog
    {
        private readonly InMemoryStateLedgerStore _inner;
        private readonly CancellationTokenSource _cancellation;

        public CancellingHistoryStore(InMemoryStateLedgerStore inner, CancellationTokenSource cancellation)
        {
            _inner = inner;
            _cancellation = cancellation;
        }

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default)
        {
            _cancellation.Cancel();
            return _inner.ReadHistoryAsync(address, options, cancellationToken);
        }

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string[] Lines(MemoryStream output) =>
        Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static T Deserialize<T>(string line) =>
        JsonSerializer.Deserialize<T>(line, StateLedgerExportFormat.Json)!;

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
