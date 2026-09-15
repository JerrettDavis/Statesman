using System.Text;

namespace Statesman.Tooling.Tests;

public sealed class SerializerEnvelopeExportTests
{
    private static readonly StatesmanManifest Manifest = new()
    {
        Id = "app",
        Version = "1.0",
        Fingerprint = "fingerprint-app",
    };

    private static readonly StateAddress Address = new("app", "envelope/a", StatePartition.Default);

    private static readonly StateEnvelope Envelope = new()
    {
        ContentType = "application/json",
        SerializerId = "statesman.json/v1",
        Fingerprint = "fingerprint-app",
    };

    [Fact]
    public async Task An_export_and_a_restore_carry_an_envelope_verbatim()
    {
        // Restore validates the header's fingerprint and nothing about a record's envelope: the
        // envelope is carried, not checked. This fact is what says the carrying happens at all, on
        // both the write side of the export line and the read side of the restore.
        var source = new InMemoryStateLedgerStore("source", TimeProvider.System);
        Assert.True((await source.AppendAsync(
            Address,
            StateWriteCondition.Absent,
            new StateCommit
            {
                Operation = StateOperation.Set,
                Status = StateStatus.Ready,
                ValueType = "System.String",
                SchemaVersion = 1,
                Payload = Encoding.UTF8.GetBytes("one"),
                Envelope = Envelope,
                Source = "test",
            },
            TestContext.Current.CancellationToken)).Succeeded);

        using var destination = new MemoryStream();
        await StateLedgerExport.ExportAsync(
            source,
            Manifest,
            destination,
            cancellationToken: TestContext.Current.CancellationToken);
        string[] lines = Encoding.UTF8.GetString(destination.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // The record line is the middle one, between the header and the trailer, and it carries the
        // envelope as a nested object rather than as flattened columns.
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"envelope\":{\"contentType\":\"application/json\"", lines[1], StringComparison.Ordinal);

        var target = new InMemoryStateLedgerStore("target", TimeProvider.System);
        StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreAsync(
            target,
            Manifest,
            new MemoryStream(destination.ToArray()),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Records);
        StateRecord? restored = await target.ReadLatestAsync(Address, TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(Envelope, restored.Envelope);
    }

    [Fact]
    public async Task A_record_without_an_envelope_writes_no_envelope_key_and_restores_as_null()
    {
        // The other half: nothing about the export line changes for a record that carries no
        // envelope, which is why StateLedgerExportFormat.Version does not bump for this release.
        var source = new InMemoryStateLedgerStore("source", TimeProvider.System);
        Assert.True((await source.AppendAsync(
            Address,
            StateWriteCondition.Absent,
            new StateCommit
            {
                Operation = StateOperation.Set,
                Status = StateStatus.Ready,
                ValueType = "System.String",
                SchemaVersion = 1,
                Payload = Encoding.UTF8.GetBytes("one"),
                Source = "test",
            },
            TestContext.Current.CancellationToken)).Succeeded);

        using var destination = new MemoryStream();
        await StateLedgerExport.ExportAsync(
            source,
            Manifest,
            destination,
            cancellationToken: TestContext.Current.CancellationToken);
        string text = Encoding.UTF8.GetString(destination.ToArray());

        // The property name, not the bare word: this fixture's own state path is "envelope/a", so a
        // search for "envelope" alone matches the path and never discriminates anything.
        Assert.DoesNotContain("\"envelope\":", text, StringComparison.Ordinal);

        var target = new InMemoryStateLedgerStore("target", TimeProvider.System);
        await StateLedgerRestore.RestoreAsync(
            target,
            Manifest,
            new MemoryStream(destination.ToArray()),
            cancellationToken: TestContext.Current.CancellationToken);

        StateRecord? restored = await target.ReadLatestAsync(Address, TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Null(restored.Envelope);
    }
}
