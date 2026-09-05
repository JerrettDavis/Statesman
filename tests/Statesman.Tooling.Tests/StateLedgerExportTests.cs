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
